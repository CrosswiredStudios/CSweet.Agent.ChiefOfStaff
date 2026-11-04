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
        @{ draft = $Draft; assets = @($Assets); html_url = 'https://github.com/example/agent/releases/tag/v1.0.0' } | ConvertTo-Json -Depth 5 -Compress
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
        (Reply 'api *' $emptyDraft), (Reply 'release upload *' 'uploaded'),
        (Reply 'api *' $readyDraft), (Reply 'release edit *--draft=false*' 'published'),
        (Reply 'api *' $published))

    Run-Scenario 'Create 500 with committed draft resumes without duplicate create' @(
        (Reply 'api *' 'HTTP 404' 1), (Reply 'api *--paginate --slurp' '[[]]'), (Reply 'release create *' 'HTTP 500' 1),
        (Reply 'api *' $emptyDraft), (Reply 'release upload *' 'uploaded'),
        (Reply 'api *' $readyDraft), (Reply 'release edit *' 'published'), (Reply 'api *' $published))

    Run-Scenario 'Upload 502 with one committed asset uploads only the missing checksum' @(
        (Reply 'api *' $emptyDraft), (Reply 'release upload *' 'HTTP 502' 1),
        (Reply 'api *' (ReleaseJson $true @($assets[0]))), (Reply 'release upload *agent.csab.sha256' 'uploaded'),
        (Reply 'api *' $readyDraft), (Reply 'release edit *' 'published'), (Reply 'api *' $published))
    if (($global:csweetReleaseTestCalls | Where-Object { $_ -like 'release upload *' })[1] -match 'agent.csab ') { throw 'Existing bundle was uploaded twice.' }

    Run-Scenario 'Publish 500 with committed publication is verified' @(
        (Reply 'api *' $readyDraft), (Reply 'release edit *' 'HTTP 500' 1), (Reply 'api *' $published))

    Run-Scenario 'Existing complete release performs no writes' @((Reply 'api *' $published))
    $untaggedDraft = $readyDraft | ConvertFrom-Json
    $untaggedDraft | Add-Member -NotePropertyName tag_name -NotePropertyValue 'v1.0.0'
    $draftPages = '[[' + ($untaggedDraft | ConvertTo-Json -Depth 5 -Compress) + ']]'
    Run-Scenario 'Draft without a Git tag is discovered before creating anything' @(
        (Reply 'api *' 'HTTP 404' 1), (Reply 'api *--paginate --slurp' $draftPages),
        (Reply 'release edit *' 'published'), (Reply 'api *' $published))
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
        (Reply 'api *' $readyDraft), (Reply 'release edit *--draft=false --prerelease --latest=false' 'published'),
        (Reply 'api *' $published)) '' '1.0.0-beta.1'
    Write-Host 'All 12 release recovery scenarios passed.'
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
