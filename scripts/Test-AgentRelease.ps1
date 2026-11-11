$ErrorActionPreference = 'Stop'
$publisher = Join-Path $PSScriptRoot 'Publish-AgentRelease.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('csweet-release-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $testRoot 'releases') -Force | Out-Null
Push-Location $testRoot
try {
    Set-Content -LiteralPath 'agent.csab' -Value 'test payload' -NoNewline
    Set-Content -LiteralPath 'agent.csab.sha256' -Value 'test checksum' -NoNewline
    Set-Content -LiteralPath 'releases/1.0.0.md' -Value '# 1.0.0'
    Set-Content -LiteralPath 'releases/1.0.0-beta.1.md' -Value '# 1.0.0-beta.1'
    $assets = @('agent.csab', 'agent.csab.sha256') | ForEach-Object {
        @{ name = $_; size = (Get-Item -LiteralPath $_).Length; state = 'uploaded';
           digest = 'sha256:' + (Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant() }
    }
    function Reply($Command, $Output, $Code = 0) { @{ Command = $Command; Output = $Output; Code = $Code } }
    function ReleaseJson($Draft, $Assets) {
        @{ id = 101; tag_name = 'v1.0.0'; target_commitish = 'abc123'; name = 'Chief of Staff 1.0.0'; body = '# 1.0.0';
           prerelease = $false; draft = $Draft; assets = @($Assets); html_url = 'https://github.com/example/agent/releases/tag/v1.0.0' } | ConvertTo-Json -Depth 5 -Compress
    }
    $emptyDraft = ReleaseJson $true @()
    $readyDraft = ReleaseJson $true $assets
    $published = ReleaseJson $false $assets
    function gh {
        $command = $args -join ' '
        $global:csweetReleaseTestCalls.Add($command)
        if ($global:csweetReleaseTestReplies.Count -eq 0) { throw "Unexpected gh command: $command" }
        $reply = $global:csweetReleaseTestReplies.Dequeue()
        if ($command -notlike $reply.Command) { throw "Expected '$($reply.Command)', received '$command'." }
        $global:LASTEXITCODE = $reply.Code
        $reply.Output
    }
    function Start-Sleep { param($Seconds) $global:csweetReleaseTestDelays.Add($Seconds) }
    function Run-Scenario($Name, $Responses, $ExpectedError = '', $Version = '1.0.0') {
        $global:csweetReleaseTestReplies = [Collections.Generic.Queue[object]]::new()
        foreach ($response in $Responses) { $global:csweetReleaseTestReplies.Enqueue($response) }
        $global:csweetReleaseTestCalls = [Collections.Generic.List[string]]::new()
        $global:csweetReleaseTestDelays = [Collections.Generic.List[int]]::new()
        $caught = ''
        try {
            & $publisher -Repository example/agent -Tag "v$Version" -Version $Version -Target abc123 -Bundle agent.csab -MaximumAttempts 3
        }
        catch { $caught = $_.Exception.Message }
        if ($ExpectedError -and $caught -notlike "*$ExpectedError*") { throw "${Name}: expected '$ExpectedError', received '$caught'" }
        if (-not $ExpectedError -and $caught) { throw "${Name}: $caught" }
        if ($global:csweetReleaseTestReplies.Count) { throw "${Name}: unconsumed responses" }
        Write-Host "PASS: $Name"
    }

    Run-Scenario 'Lookup 500 retries without creating a release' @(
        (Reply 'api *' 'HTTP 500' 1), (Reply 'api *' $published))
    if ($global:csweetReleaseTestDelays.Count -ne 1 -or $global:csweetReleaseTestDelays[0] -ne 2) { throw 'Expected bounded retry delay.' }

    Run-Scenario 'Missing release creates a draft, uploads, verifies, then publishes' @(
        (Reply 'api *' 'gh: Not Found (HTTP 404)' 1), (Reply 'api *--paginate --slurp' '[[]]'), (Reply 'release create *--draft*' 'created'),
        (Reply 'api *' $emptyDraft),
        (Reply 'api --method POST https://uploads.github.com/repos/example/agent/releases/101/assets?name=agent.csab --header * --input *' 'uploaded'),
        (Reply 'api --method POST https://uploads.github.com/repos/example/agent/releases/101/assets?name=agent.csab.sha256 --header * --input *' 'uploaded'),
        (Reply 'api repos/example/agent/releases/101' $readyDraft),
        (Reply 'api --method PATCH repos/example/agent/releases/101 -F draft=false' 'published'),
        (Reply 'api repos/example/agent/releases/101' $published))

    Run-Scenario 'Create 500 with committed draft resumes without duplicate create' @(
        (Reply 'api *' 'HTTP 404' 1), (Reply 'api *--paginate --slurp' '[[]]'), (Reply 'release create *' 'HTTP 500' 1),
        (Reply 'api *' $emptyDraft), (Reply 'api --method POST https://uploads.github.com/*name=agent.csab --header *' 'uploaded'),
        (Reply 'api --method POST https://uploads.github.com/*name=agent.csab.sha256 --header *' 'uploaded'),
        (Reply 'api repos/example/agent/releases/101' $readyDraft), (Reply 'api --method PATCH *' 'published'),
        (Reply 'api repos/example/agent/releases/101' $published))

    Run-Scenario 'Upload 502 with one committed asset uploads only the missing checksum' @(
        (Reply 'api *' $emptyDraft), (Reply 'api --method POST https://uploads.github.com/*name=agent.csab --header *' 'HTTP 502' 1),
        (Reply 'api repos/example/agent/releases/101' (ReleaseJson $true @($assets[0]))),
        (Reply 'api --method POST https://uploads.github.com/*name=agent.csab.sha256 --header *' 'uploaded'),
        (Reply 'api repos/example/agent/releases/101' $readyDraft), (Reply 'api --method PATCH *' 'published'),
        (Reply 'api repos/example/agent/releases/101' $published))
    if (($global:csweetReleaseTestCalls | Where-Object { $_ -like '*name=agent.csab --header *' }).Count -ne 1) { throw 'Existing bundle was uploaded twice.' }

    Run-Scenario 'Publish 500 with committed publication is verified' @(
        (Reply 'api *' $readyDraft), (Reply 'api --method PATCH *' 'HTTP 500' 1), (Reply 'api repos/example/agent/releases/101' $published))

    Run-Scenario 'Existing complete release performs no writes' @((Reply 'api *' $published))
    $untaggedDraft = $readyDraft | ConvertFrom-Json
    $draftPages = '[[' + ($untaggedDraft | ConvertTo-Json -Depth 5 -Compress) + ']]'
    Run-Scenario 'Draft without a Git tag is discovered before creating anything' @(
        (Reply 'api *' 'HTTP 404' 1), (Reply 'api *--paginate --slurp' $draftPages),
        (Reply 'api --method PATCH repos/example/agent/releases/101 -F draft=false' 'published'),
        (Reply 'api repos/example/agent/releases/101' $published))

    $duplicate = $emptyDraft | ConvertFrom-Json
    $duplicate.id = 102
    $duplicateJson = $duplicate | ConvertTo-Json -Depth 5 -Compress
    # Include another tag and split across pages to cover authenticated pagination.
    $otherTag = $emptyDraft | ConvertFrom-Json
    $otherTag.tag_name = 'v0.9.0'
    $duplicatePages = '[[' + $duplicateJson + ',' + ($otherTag | ConvertTo-Json -Depth 5 -Compress) + '],[' + $emptyDraft + ']]'
    Run-Scenario 'Retried create leaves two empty drafts; oldest ID receives all writes' @(
        (Reply 'api *' 'HTTP 404' 1), (Reply 'api *--paginate --slurp' $duplicatePages),
        (Reply 'api --method POST https://uploads.github.com/*releases/101/assets?name=agent.csab --header *' 'uploaded'),
        (Reply 'api --method POST https://uploads.github.com/*releases/101/assets?name=agent.csab.sha256 --header *' 'uploaded'),
        (Reply 'api repos/example/agent/releases/101' $readyDraft),
        (Reply 'api --method PATCH repos/example/agent/releases/101 -F draft=false' 'published'),
        (Reply 'api repos/example/agent/releases/101' $published))

    Run-Scenario 'Create 500 with duplicate committed drafts resumes by ID' @(
        (Reply 'api *' 'HTTP 404' 1), (Reply 'api *--paginate --slurp' '[[]]'),
        (Reply 'release create *' 'HTTP 500' 1),
        (Reply 'api *' 'HTTP 404' 1), (Reply 'api *--paginate --slurp' $duplicatePages),
        (Reply 'api --method POST https://uploads.github.com/*releases/101/assets?name=agent.csab --header *' 'uploaded'),
        (Reply 'api --method POST https://uploads.github.com/*releases/101/assets?name=agent.csab.sha256 --header *' 'uploaded'),
        (Reply 'api repos/example/agent/releases/101' $readyDraft), (Reply 'api --method PATCH *' 'published'),
        (Reply 'api repos/example/agent/releases/101' $published))

    $partialDuplicate = $duplicateJson | ConvertFrom-Json
    $partialDuplicate.assets = @($assets[0])
    $partialPages = '[[' + $emptyDraft + ',' + ($partialDuplicate | ConvertTo-Json -Depth 5 -Compress) + ']]'
    $readyDuplicate = $readyDraft | ConvertFrom-Json
    $readyDuplicate.id = 102
    $publishedDuplicate = $published | ConvertFrom-Json
    $publishedDuplicate.id = 102
    Run-Scenario 'Rerun resumes the sole partial draft alongside an empty duplicate' @(
        (Reply 'api *' 'HTTP 404' 1), (Reply 'api *--paginate --slurp' $partialPages),
        (Reply 'api --method POST https://uploads.github.com/*releases/102/assets?name=agent.csab.sha256 --header *' 'uploaded'),
        (Reply 'api repos/example/agent/releases/102' ($readyDuplicate | ConvertTo-Json -Depth 5 -Compress)),
        (Reply 'api --method PATCH repos/example/agent/releases/102 -F draft=false' 'published'),
        (Reply 'api repos/example/agent/releases/102' ($publishedDuplicate | ConvertTo-Json -Depth 5 -Compress)))

    $bothPopulated = '[[' + $readyDraft + ',' + ($partialDuplicate | ConvertTo-Json -Depth 5 -Compress) + ']]'
    Run-Scenario 'Two populated drafts require review without writes' @(
        (Reply 'api *' 'HTTP 404' 1), (Reply 'api *--paginate --slurp' $bothPopulated)) 'conflicting metadata or assets'

    $partialDuplicate = $partialDuplicate | ConvertTo-Json -Depth 5 | ConvertFrom-Json
    $partialDuplicate.assets[0].digest = 'sha256:different'
    $mismatchedPages = '[[' + $emptyDraft + ',' + ($partialDuplicate | ConvertTo-Json -Depth 5 -Compress) + ']]'
    Run-Scenario 'Sole populated duplicate with mismatched content requires review' @(
        (Reply 'api *' 'HTTP 404' 1), (Reply 'api *--paginate --slurp' $mismatchedPages)) 'refusing to replace'

    foreach ($conflict in @('target_commitish', 'body', 'name', 'prerelease', 'draft')) {
        $different = $duplicateJson | ConvertFrom-Json
        switch ($conflict) {
            target_commitish { $different.target_commitish = 'different' }
            body { $different.body = 'different notes' }
            name { $different.name = 'different title' }
            prerelease { $different.prerelease = $true }
            draft { $different.draft = $false }
        }
        $conflictPages = '[[' + $emptyDraft + ',' + ($different | ConvertTo-Json -Depth 5 -Compress) + ']]'
        Run-Scenario "Duplicate drafts with conflicting $conflict require review without writes" @(
            (Reply 'api *' 'HTTP 404' 1), (Reply 'api *--paginate --slurp' $conflictPages)) 'conflicting metadata or assets'
    }
    $wrongTarget = $readyDraft | ConvertFrom-Json
    $wrongTarget.target_commitish = 'different'
    Run-Scenario 'Single draft targeting another commit is preserved' @(
        (Reply 'api *' ($wrongTarget | ConvertTo-Json -Depth 5 -Compress))) 'different commit'
    Run-Scenario 'Authentication failure does not create a release' @((Reply 'api *' 'HTTP 403' 1)) 'HTTP 403'
    Run-Scenario 'Repeated 503 errors stop at the retry limit' @(
        (Reply 'api *' 'HTTP 503' 1), (Reply 'api *' 'HTTP 503' 1), (Reply 'api *' 'HTTP 503' 1)) 'retry limit'
    if ($global:csweetReleaseTestDelays.Count -ne 2) { throw 'Retry count exceeded the configured limit.' }

    Run-Scenario 'Published incomplete release is preserved and reported' @(
        (Reply 'api *' (ReleaseJson $false @($assets[0])))) 'missing required assets'
    $mismatched = @(@{ name = 'agent.csab'; size = $assets[0].size; state = 'uploaded'; digest = 'sha256:different' })
    Run-Scenario 'Mismatched draft asset is never overwritten' @(
        (Reply 'api *' (ReleaseJson $true $mismatched))) 'refusing to replace'

    Run-Scenario 'Prerelease remains excluded from latest' @(
        (Reply 'api *' 'HTTP 404' 1), (Reply 'api *--paginate --slurp' '[[]]'), (Reply 'release create *--draft --prerelease' 'created'),
        (Reply 'api *' $readyDraft),
        (Reply 'api --method PATCH repos/example/agent/releases/101 -F draft=false -F prerelease=true -f make_latest=false' 'published'),
        (Reply 'api repos/example/agent/releases/101' $published)) '' '1.0.0-beta.1'
    Write-Host 'All release recovery scenarios passed.'
}
finally {
    Pop-Location
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    $temporaryParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedTestRoot.StartsWith($temporaryParent, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedTestRoot) -notlike 'csweet-release-tests-*') {
        throw 'Test cleanup target is outside the expected temporary directory.'
    }
    Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    Remove-Variable csweetReleaseTestReplies,csweetReleaseTestCalls,csweetReleaseTestDelays -Scope Global -ErrorAction SilentlyContinue
}
