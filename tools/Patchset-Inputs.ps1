# Fingerprint of what tools\Build-Patchset.ps1 makes the patch data from. Build-Patchset writes it beside the patch data;
# Build-Package and Publish-Release refuse patch data whose fingerprint no longer matches, so a fix added to the tool,
# a new JacintoNet build or a changed Build-Patchset.ps1 cannot ship in a release that still carries the old patch data
# (v0.4.1 went out without its Ghost Town fix and JacintoNet build that way).
function Get-PatchsetInputs([string]$Repo) {
    $tool = Join-Path $Repo 'multipatch\tool'
    $files = @(
        Get-Item -LiteralPath (Join-Path $Repo 'tools\Build-Patchset.ps1'), (Join-Path $tool 'Gears3_Multipatch_Tool.ps1')
        Get-ChildItem -LiteralPath (Join-Path $tool '_files') -File | Where-Object { $_.Name -notmatch '\.before_' }
        Get-ChildItem -LiteralPath (Join-Path $tool '_files\JacintoNet\bin') -File -Filter '*.dll'
    ) | Sort-Object FullName
    $lines = foreach ($file in $files) { $file.FullName.Substring($Repo.Length + 1) + ' ' + (Get-FileHash -LiteralPath $file.FullName).Hash }
    $sha = [Security.Cryptography.SHA256]::Create()
    [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($lines -join "`n")))).Replace('-', '')
}

# Throws unless the patch data was built from the current inputs.
function Assert-PatchsetCurrent([string]$Repo, [string]$Patchset) {
    $stamp = $Patchset + '.inputs'
    $built = if (Test-Path -LiteralPath $stamp) { (Get-Content -LiteralPath $stamp -Raw).Trim() } else { '' }
    $current = Get-PatchsetInputs $Repo
    if ($built -ne $current) {
        throw "The patch data is out of date: the Multipatch tool, its files, JacintoNet or Build-Patchset.ps1 changed after it was built. Run tools\Build-Patchset.ps1 -CleanGame <clean Jacinto 1.1.1> first. (A fix added only to Gears3_Multipatch_Tool.ps1 must also be added to Build-Patchset.ps1, which builds from a frozen reference copy of the tool.)"
    }
}
