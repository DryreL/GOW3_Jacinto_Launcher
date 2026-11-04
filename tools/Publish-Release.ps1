[CmdletBinding()]
param([switch]$DryRun, [switch]$DeleteLatest)
# Puts the patch data on the "patch-data" release for CI, tags the version in the csproj, waits for the CI build and publishes the release.
# CI builds the packages and makes a draft, so no launcher sees the update until the patch data is on it. Run Build.bat first: it writes dist\multipatch.jcpatch.
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $repo
function RunGit { $out = & git @args; if ($LASTEXITCODE -ne 0) { throw "git $args failed" }; $out }
# GitHub sometimes answers a push with "Internal Server Error": a push is tried three times.
function PushGit {
    for ($try = 1; $try -le 3; $try++) {
        & git push @args
        if ($LASTEXITCODE -eq 0) { return }
        if ($try -lt 3) { Write-Host "git push failed (try $try of 3), retrying in $(10 * $try) s..."; Start-Sleep (10 * $try) }
    }
    throw "git push $($args -join ' ') failed. Run Release.bat again: it continues from here."
}
# GitHub's API sometimes answers 502 or times out on large uploads: each call is tried three times before the release stops.
function RunGh {
    for ($try = 1; $try -le 3; $try++) {
        & gh @args
        if ($LASTEXITCODE -eq 0) { return }
        if ($try -lt 3) { Write-Host "gh failed (try $try of 3), retrying in $(10 * $try) s..."; Start-Sleep (10 * $try) }
    }
    throw "gh $(($args | ForEach-Object { $_ }) -join ' ') failed"
}
# True when patch-data already holds this patch data (same checksum file and same size), so a rerun does not upload 38 MB again.
function PatchDataUploaded {
    $sum = & gh release download patch-data --pattern 'multipatch.jcpatch.sha256' --output - 2>$null
    if ($LASTEXITCODE -ne 0 -or "$sum" -notmatch $expected) { return $false }
    # Parsed here: Windows PowerShell drops the quotes inside a jq filter on the way to gh.
    $remote = (& gh release view patch-data --json assets | ConvertFrom-Json).assets | Where-Object { $_.name -eq 'multipatch.jcpatch' -and $_.state -eq 'uploaded' }
    return $remote -and $remote.size -eq (Get-Item -LiteralPath 'dist\multipatch.jcpatch').Length
}

# -DeleteLatest: removes the newest launcher release (never patch-data) with its tag, on GitHub and here, after typing the tag.
if ($DeleteLatest) {
    $latest = (& gh release list --limit 20 --json tagName,isDraft,publishedAt | ConvertFrom-Json) | Where-Object { $_.tagName -ne 'patch-data' } | Select-Object -First 1
    if (-not $latest) { throw 'No launcher release found.' }
    $doomed = $latest.tagName
    Write-Host "Latest release: $doomed (draft: $($latest.isDraft)). This deletes the release and the tag on GitHub and here."
    if ((Read-Host "Type $doomed to delete it") -ne $doomed) { Write-Host 'Nothing deleted.'; exit 0 }
    RunGh release delete $doomed --yes --cleanup-tag
    if (& git ls-remote --tags origin "refs/tags/$doomed") { PushGit origin --delete $doomed }
    if (& git tag --list $doomed) { RunGit tag -d $doomed | Out-Null }
    Write-Host "$doomed deleted. Run Release.bat again to publish it anew."
    exit 0
}

$version = ([xml](Get-Content -LiteralPath 'GoW3Jacinto_Launcher.csproj' -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'No <Version> in the csproj.' }
$tag = "v$version"

if ((RunGit rev-parse --abbrev-ref HEAD) -ne 'main') { throw 'Switch to main first.' }
$dirty = RunGit status --porcelain
if ($dirty -and -not $DryRun) { throw "Commit your changes first:`n$($dirty -join "`n")" }
$resume = $false
if (RunGit tag --list $tag) {
    $onGitHub = & git ls-remote --tags origin "refs/tags/$tag"
    if ($onGitHub -or (RunGit rev-parse "$tag^{}") -ne (RunGit rev-parse HEAD)) { throw "$tag already exists." }
    $resume = $true
    Write-Host "$tag is here but not on GitHub yet (an earlier run stopped): continuing with it."
}

# The section of this version in the changelog becomes the release notes.
$changelog = (Get-Content -LiteralPath 'changelog.txt' -Raw -Encoding UTF8) -replace "`r`n", "`n"
$section = [regex]::Match($changelog, "(?ms)^$([regex]::Escape($tag))(?: \(.*?\))?\n(.*?)(?=^v\d+\.\d+|\z)")
if (-not $section.Success) { throw "changelog.txt has no $tag section." }
$notes = Join-Path $env:TEMP "jacinto-notes-$tag.md"
[IO.File]::WriteAllText($notes, $section.Groups[1].Value.Trim(), (New-Object Text.UTF8Encoding $false))

$assets = @(Get-Item -LiteralPath 'dist\multipatch.jcpatch', 'dist\multipatch.jcpatch.sha256')
$expected = ((Get-Content -LiteralPath 'dist\multipatch.jcpatch.sha256' -Raw).Trim() -split '\s')[0]
if ((Get-FileHash -LiteralPath 'dist\multipatch.jcpatch').Hash -ne $expected) { throw 'dist\multipatch.jcpatch does not match its checksum.' }

Write-Host ""
Write-Host "  Release $tag"
Write-Host "  Commit:  $(RunGit log -1 --format='%h %s')"
foreach ($asset in $assets) { Write-Host ("  {0,-48} {1,6:0} MB  {2:dd MMM HH:mm}" -f $asset.Name, ($asset.Length / 1MB), $asset.LastWriteTime) }
Write-Host ""
if ($DryRun) { Write-Host "Dry run: nothing was pushed. Notes: $notes"; return }
if ((Read-Host "Push main, tag $tag and publish? Type yes") -cne 'yes') { Write-Host 'Stopped. Nothing was pushed.'; return }

PushGit origin main
# A prerelease: GitHub's "latest" and the launcher's update check both skip it.
if (@(& gh release list --limit 100 --json tagName --jq '.[].tagName') -notcontains 'patch-data') { RunGh release create patch-data --prerelease --latest=false --title 'Patch data for CI' --notes 'multipatch.jcpatch, read by the build workflow. Not a launcher release.' }
if (-not (PatchDataUploaded)) { RunGh release upload patch-data @($assets.FullName) --clobber } else { Write-Host 'patch-data already holds this patch data.' }
if (-not $resume) { RunGit tag -a $tag -m $tag | Out-Null }
PushGit origin $tag

Write-Host 'Waiting for the CI build...'
$sha = RunGit rev-parse HEAD
$run = $null
for ($i = 0; $i -lt 30 -and -not $run; $i++) {
    Start-Sleep 10
    $run = & gh run list --workflow build.yml --commit $sha --event push --limit 1 --json databaseId --jq '.[0].databaseId'
}
if (-not $run) { throw "CI did not start. The tag $tag is pushed: see the Actions page." }
& gh run watch $run --exit-status
if ($LASTEXITCODE -ne 0) { throw "CI failed, so $tag was not published. See: gh run view $run --log-failed" }

RunGh release upload $tag @($assets.FullName) --clobber
RunGh release edit $tag --notes-file $notes --draft=false --latest
Write-Host "Published: $(& gh release view $tag --json url --jq .url)"
