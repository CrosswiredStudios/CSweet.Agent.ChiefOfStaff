param(
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Target,
    [Parameter(Mandatory)][string]$Bundle,
    [ValidateRange(1, 8)][int]$MaximumAttempts = 5
)

$ErrorActionPreference = 'Stop'
# Native failures are inspected below, including under GitHub Actions' pwsh defaults.
$PSNativeCommandUseErrorActionPreference = $false
if ($Tag -ne "v$Version") { throw 'Release tag and version disagree.' }
$notes = "releases/$Version.md"
$expectedAssets = @($Bundle, "$Bundle.sha256") | ForEach-Object {
    $file = Get-Item -LiteralPath $_
    [pscustomobject]@{
        Path = $file.FullName
        Name = $file.Name
        Size = $file.Length
        Digest = 'sha256:' + (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
if (-not (Test-Path -LiteralPath $notes)) { throw "Missing release notes: $notes" }

function Invoke-ReleaseGh([string[]]$Arguments) {
    $output = (& gh @Arguments 2>&1 | Out-String).Trim()
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
}

function Assert-RetryableFailure($Result) {
    # Only a 404 from the exact tag lookup means absence. Authentication and
    # validation errors must remain actionable instead of causing blind writes.
    if ($Result.Output -notmatch '(?i)HTTP (429|5\d\d)\b|timeout|timed out|connection reset|connection refused|unexpected EOF|TLS handshake|temporary failure|no such host|already_exists') {
        throw "GitHub release operation failed (exit $($Result.ExitCode)): $($Result.Output)"
    }
    Write-Warning $Result.Output
}

$endpoint = "repos/$Repository/releases/tags/$([Uri]::EscapeDataString($Tag))"
$releaseEndpoint = $endpoint
$failures = 0
for ($step = 1; $step -le ($MaximumAttempts * 4); $step++) {
    $failed = $false
    # Always re-read before retrying a write: GitHub may have committed it before
    # returning a 500 or losing the response. Never overwrite existing assets.
    $lookup = Invoke-ReleaseGh @('api', $releaseEndpoint)
    $missingRelease = $false
    if ($lookup.ExitCode -ne 0 -and $lookup.Output -match '\bHTTP 404\b') {
        # A draft can precede creation of its Git tag. Discover it through the
        # authenticated list before deciding to create another release.
        $lookup = Invoke-ReleaseGh @('api', "repos/$Repository/releases?per_page=100", '--paginate', '--slurp')
        if ($lookup.ExitCode -eq 0) {
            $matchingReleases = @(foreach ($page in ($lookup.Output | ConvertFrom-Json)) {
                foreach ($candidate in $page) { if ($candidate.tag_name -ceq $Tag) { $candidate } }
            })
            if ($matchingReleases.Count -gt 1) {
                # A retried create can leave equivalent empty drafts. Prefer the
                # sole draft with assets on reruns, otherwise choose the oldest ID.
                # Preserve all drafts; conflicting metadata or assets need review.
                $first = $matchingReleases[0]
                $conflicts = @($matchingReleases | Where-Object {
                    -not $_.draft -or $_.target_commitish -cne $Target -or
                    $_.name -cne $first.name -or $_.body -cne $first.body -or
                    $_.prerelease -ne $first.prerelease
                })
                $populated = @($matchingReleases | Where-Object { @($_.assets).Count -gt 0 })
                if ($conflicts.Count -gt 0 -or $populated.Count -gt 1) {
                    throw "Multiple releases use tag $Tag with conflicting metadata or assets; stopped for review. IDs: $($matchingReleases.id -join ', ')"
                }
                if ($populated.Count -eq 1) { $matchingReleases = $populated }
                else { $matchingReleases = @($matchingReleases | Sort-Object id | Select-Object -First 1) }
                Write-Warning "Equivalent drafts use tag $Tag; resuming release ID $($matchingReleases[0].id) and preserving empty duplicates."
            }
            if ($matchingReleases.Count -eq 0) { $missingRelease = $true }
            else { $lookup.Output = $matchingReleases[0] | ConvertTo-Json -Depth 20 -Compress }
        }
    }
    if ($missingRelease) {
        $flags = @()
        if ($Version.Contains('-')) { $flags = @('--prerelease') }
        $created = Invoke-ReleaseGh (@('release', 'create', $Tag, '--repo', $Repository,
            '--target', $Target, '--title', "Chief of Staff $Version", '--notes-file', $notes, '--draft') + $flags)
        if ($created.ExitCode -ne 0) { Assert-RetryableFailure $created; $failed = $true }
    }
    elseif ($lookup.ExitCode -ne 0) { Assert-RetryableFailure $lookup; $failed = $true }
    else {
        $release = $lookup.Output | ConvertFrom-Json
        if (-not $release.id) { throw 'GitHub release response has no ID.' }
        $releaseEndpoint = "repos/$Repository/releases/$($release.id)"
        if ($release.draft -and $release.target_commitish -cne $Target) {
            throw "Draft release $($release.id) targets a different commit; stopped for review."
        }
        $missing = @()
        foreach ($expected in $expectedAssets) {
            $asset = @($release.assets | Where-Object name -CEQ $expected.Name)
            if ($asset.Count -eq 0) { $missing += $expected; continue }
            if ($asset.Count -ne 1 -or $asset[0].state -ne 'uploaded' -or $asset[0].size -ne $expected.Size -or
                ($asset[0].digest -and $asset[0].digest -ne $expected.Digest)) {
                throw "Existing asset $($expected.Name) differs from this build or is incomplete; refusing to replace it."
            }
        }
        if (-not $release.draft) {
            if ($missing.Count -gt 0) { throw 'Published release is missing required assets; refusing to modify a published release.' }
            Write-Host "Verified published release: $($release.html_url)"
            return
        }
        if ($missing.Count -gt 0) {
            foreach ($asset in $missing) {
                $uploadEndpoint = "https://uploads.github.com/$releaseEndpoint/assets?name=$([Uri]::EscapeDataString($asset.Name))"
                $uploaded = Invoke-ReleaseGh @('api', '--method', 'POST', $uploadEndpoint,
                    '--header', 'Content-Type: application/octet-stream', '--header', "Content-Length: $($asset.Size)", '--input', $asset.Path)
                if ($uploaded.ExitCode -ne 0) { Assert-RetryableFailure $uploaded; $failed = $true; break }
            }
        }
        else {
            # The previous read verified both assets before exposing the release.
            $flags = @()
            if ($Version.Contains('-')) { $flags = @('-F', 'prerelease=true', '-f', 'make_latest=false') }
            $published = Invoke-ReleaseGh (@('api', '--method', 'PATCH', $releaseEndpoint, '-F', 'draft=false') + $flags)
            if ($published.ExitCode -ne 0) { Assert-RetryableFailure $published; $failed = $true }
        }
    }
    # Successful mutations can advance immediately; back off only on API failures.
    if ($failed) {
        $failures++
        if ($failures -ge $MaximumAttempts) { break }
        Start-Sleep -Seconds ([Math]::Min(30, [Math]::Pow(2, $failures)))
    }
}
throw "GitHub release $Tag could not be verified within the retry limit. Rerun to resume the draft safely."
