[CmdletBinding()]
param([switch]$DryRun)
# Puts the patch data on the "patch-data" release for CI, tags the version in the csproj, waits for the CI build and publishes the release.
# CI builds the packages and makes a draft, so no launcher sees the update until the patch data is on it. Run Build.bat first: it writes dist\multipatch.jcpatch.
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $repo
function RunGit { $out = & git @args; if ($LASTEXITCODE -ne 0) { throw "git $args failed" }; $out }
function RunGh { & gh @args; if ($LASTEXITCODE -ne 0) { throw "gh $args failed" } }

$version = ([xml](Get-Content -LiteralPath 'GoW3Jacinto_Launcher.csproj' -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'No <Version> in the csproj.' }
$tag = "v$version"

if ((RunGit rev-parse --abbrev-ref HEAD) -ne 'main') { throw 'Switch to main first.' }
$dirty = RunGit status --porcelain
if ($dirty -and -not $DryRun) { throw "Commit your changes first:`n$($dirty -join "`n")" }
if (RunGit tag --list $tag) { throw "$tag already exists." }

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

RunGit push origin main | Out-Null
# A prerelease: GitHub's "latest" and the launcher's update check both skip it.
if (@(& gh release list --limit 100 --json tagName --jq '.[].tagName') -notcontains 'patch-data') { RunGh release create patch-data --prerelease --latest=false --title 'Patch data for CI' --notes 'multipatch.jcpatch, read by the build workflow. Not a launcher release.' }
RunGh release upload patch-data @($assets.FullName) --clobber
RunGit tag -a $tag -m $tag
RunGit push origin $tag | Out-Null

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
