#Requires -Version 5.1
<#
Gears of War 3 PC (Project Jacinto v1.1.1) - Multi Patch Tool: installation audit

Read-only. It does not change a single game file. It answers, with evidence:
  1. Which files did the patch change?       (every file in _files\Backups compared with the file in the game folder)
  2. Are the tool's own files installed?      (_files\*.upk / *.gear / *.dll / GearGame.u compared with the installed copy)
  3. Do the settings in the ini files, the launchers and the localization files match what the installer writes?
  4. Is anything wrong (a file the patch should have changed but did not, a value that crashes the game ...)?

Result levels:  OK = as expected   INFO = for your information   WARN = probably a problem or an incomplete install   FAIL = wrong / missing

Usage:   Gears3_Multipatch_Verify.bat            (or)   powershell -ExecutionPolicy Bypass -File Gears3_Multipatch_Verify.ps1 [-GameDir "D:\Games\Gears 3"] [-SkipBackupDiff] [-NoPause]
Output:  the console and VerifyReport_<date>.txt next to this script. Exit code 0 = no FAIL, 1 = at least one FAIL, 2 = could not run.
#>
param(
    [string]$GameDir,
    [switch]$SkipBackupDiff,   # skip step 1 (hashing every backed up file can take a minute on a big install)
    [switch]$NoPause
)

$ErrorActionPreference = 'Continue'
$ScriptDir = if ($PSScriptRoot) { $PSScriptRoot } elseif ($env:GOW3_MULTIPATCH_ROOT) { $env:GOW3_MULTIPATCH_ROOT } else { (Get-Location).Path }
# This script lives in _files\verify (older packages: a verify folder next to the tool): the tool folder is the first folder upwards
# that contains _files (with the backups) or Gears3_Multipatch_Tool.ps1
$ToolDir = $ScriptDir
$probe = $ScriptDir
for ($i = 0; $i -lt 3 -and $probe; $i++) {
    if ((Test-Path -LiteralPath (Join-Path $probe "_files")) -or (Test-Path -LiteralPath (Join-Path $probe "Gears3_Multipatch_Tool.ps1"))) { $ToolDir = $probe; break }
    $probe = Split-Path -Parent $probe
}
$IsWin = ($PSVersionTable.PSEdition -ne 'Core') -or $IsWindows

# Load precompiled helper DLL to prevent dynamic csc.exe compilation
$HelperDll = Join-Path $ToolDir '_files\GOW3Helper.dll'
if (Test-Path -LiteralPath $HelperDll) {
    try { [System.Reflection.Assembly]::LoadFrom($HelperDll) | Out-Null } catch { }
}

# ---------------------------------------------------------------------------------------------- result bookkeeping
$script:Results = New-Object System.Collections.Generic.List[object]
$script:Area = ''
$colors = @{ OK = 'Green'; INFO = 'Gray'; WARN = 'Yellow'; FAIL = 'Red' }

function Section($Title) {
    $script:Area = $Title
    Write-Host ""
    Write-Host ("=" * 72) -ForegroundColor DarkCyan
    Write-Host "  $Title" -ForegroundColor Cyan
    Write-Host ("=" * 72) -ForegroundColor DarkCyan
}
function Add-Result([string]$Level, [string]$Message) {
    $script:Results.Add([pscustomobject]@{ Level = $Level; Area = $script:Area; Message = $Message })
    Write-Host ("  [{0,-4}] {1}" -f $Level, $Message) -ForegroundColor $colors[$Level]
}

# ---------------------------------------------------------------------------------------------- helpers
function Get-LongPath($Path) {
    if ($IsWin -and $Path -match '^[A-Za-z]:\\' -and $Path.Length -ge 240) { return '\\?\' + $Path }
    return $Path
}
function Get-Sha256($Path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $fs = [System.IO.File]::OpenRead((Get-LongPath $Path))
        try { return ([BitConverter]::ToString($sha.ComputeHash($fs))).Replace('-', '') } finally { $fs.Dispose() }
    }
    finally { $sha.Dispose() }
}
function Get-Sha256Bytes([byte[]]$Bytes) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-', '') } finally { $sha.Dispose() }
}
function Format-Size($n) { if ($n -ge 1MB) { '{0:N1} MB' -f ($n / 1MB) } elseif ($n -ge 1KB) { '{0:N1} KB' -f ($n / 1KB) } else { "$n B" } }
function HexToBytes($Hex) {
    $b = New-Object byte[] ($Hex.Length / 2)
    for ($i = 0; $i -lt $b.Length; $i++) { $b[$i] = [Convert]::ToByte($Hex.Substring($i * 2, 2), 16) }
    return , $b
}
function Read-TextAuto($Path) {
    # INT localization files are plain ASCII, ESM / ITA ... are UTF-16 with a BOM; ini files are ASCII / UTF-8
    $b = [System.IO.File]::ReadAllBytes((Get-LongPath $Path))
    if ($b.Length -ge 2 -and $b[0] -eq 0xFF -and $b[1] -eq 0xFE) { return [System.Text.Encoding]::Unicode.GetString($b).TrimStart([char]0xFEFF) }
    return [System.Text.Encoding]::UTF8.GetString($b).TrimStart([char]0xFEFF)
}
function Get-IniValues($Text, $Key) {
    return , @([regex]::Matches($Text, '(?m)^' + [regex]::Escape($Key) + '=([^\r\n]*)') | ForEach-Object { $_.Groups[1].Value.Trim() })
}
function Get-SectionBody($Text, $Section) {
    $m = [regex]::Match($Text, '(?ms)^\[' + [regex]::Escape($Section) + '\]\r?\n(.*?)(?=^\[[^\]\r\n]+\]\r?$|\z)')
    if ($m.Success) { return $m.Groups[1].Value }
    return $null
}
function Get-SectionValue($Text, $Section, $Key) {
    $body = Get-SectionBody $Text $Section
    if ($null -eq $body) { return $null }
    $m = [regex]::Match($body, '(?m)^' + [regex]::Escape($Key) + '=([^\r\n]*)')
    if ($m.Success) { return $m.Groups[1].Value.Trim() }
    return $null
}
function Same($a, $b) { return ([string]$a).Trim() -ieq ([string]$b).Trim() }

# Aggregates one setting over several files: one OK line when every file agrees, one WARN/FAIL line naming the files that do not
function Report-Setting([string]$Label, $Findings, [string]$Level = 'WARN', [string]$Hint = '') {
    # $Findings: array of @{ File = <path>; Actual = <value or $null>; Expected = <value> }
    $bad = @($Findings | Where-Object { $null -ne $_.Actual -and -not (Same $_.Actual $_.Expected) })
    $missing = @($Findings | Where-Object { $null -eq $_.Actual })
    if (@($Findings).Count -eq 0) { return }
    if ($bad.Count -eq 0 -and $missing.Count -eq 0) { Add-Result 'OK' "$Label = $(@($Findings)[0].Expected) ($(@($Findings).Count) file(s))"; return }
    if ($bad.Count -eq 0) { Add-Result 'INFO' "$Label : key not present in $($missing.Count) of $(@($Findings).Count) file(s) ($(( $missing | ForEach-Object { Split-Path -Leaf $_.File }) -join ', '))"; return }
    foreach ($f in $bad) { Add-Result $Level "$Label in $(Split-Path -Leaf $f.File): found '$($f.Actual)', expected '$($f.Expected)'$(if ($Hint) { ' - ' + $Hint })" }
}

# Tiny C# helper: count / replace byte patterns in a 30 MB package without minutes of PowerShell looping
if (-not ('GOW3Verify' -as [type])) {
    Add-Type -TypeDefinition @"
public static class GOW3Verify
{
    public static int Count(byte[] d, byte[] n)
    {
        int c = 0, max = d.Length - n.Length;
        for (int i = 0; i <= max; i++) { int j = 0; while (j < n.Length && d[i + j] == n[j]) j++; if (j == n.Length) { c++; i += n.Length - 1; } }
        return c;
    }
    public static int[] Diff(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return null;
        int bytes = 0, runs = 0; bool inRun = false;
        for (int i = 0; i < a.Length; i++) { if (a[i] != b[i]) { bytes++; if (!inRun) { runs++; inRun = true; } } else inRun = false; }
        return new int[] { bytes, runs };
    }
    public static int Replace(byte[] d, byte[] n, byte[] r)
    {
        int c = 0, max = d.Length - n.Length;
        for (int i = 0; i <= max; i++) { int j = 0; while (j < n.Length && d[i + j] == n[j]) j++; if (j == n.Length) { System.Array.Copy(r, 0, d, i, r.Length); c++; i += n.Length - 1; } }
        return c;
    }
}
"@
}

# ---------------------------------------------------------------------------------------------- locate everything
function Test-GameFolder($Path) { return [bool]($Path -and (Test-Path -LiteralPath (Join-Path $Path 'Binaries\Win64\GearGame.exe'))) }

if (-not $GameDir) {
    $cur = $ToolDir
    for ($i = 0; $i -lt 5 -and $cur -and -not $GameDir; $i++) {
        if (Test-GameFolder $cur) { $GameDir = $cur; break }
        foreach ($child in (Get-ChildItem -LiteralPath $cur -Directory -ErrorAction SilentlyContinue)) { if (Test-GameFolder $child.FullName) { $GameDir = $child.FullName; break } }
        $cur = Split-Path -Parent $cur
    }
}
if (-not $GameDir) {
    $typed = (Read-Host "Game folder path (the one that contains Binaries, Engine, GearGame)").Trim().Trim('"')
    if (Test-GameFolder $typed) { $GameDir = $typed }
}
if (-not (Test-GameFolder $GameDir)) {
    Write-Host "  [FAIL] Game folder (Binaries\Win64\GearGame.exe) not found. Run again with -GameDir `"<path>`"." -ForegroundColor Red
    if (-not $NoPause) { Read-Host "Press ENTER to exit" | Out-Null }
    exit 2
}
$GameDir = (Resolve-Path -LiteralPath $GameDir).Path
$BinariesDir = Join-Path $GameDir 'Binaries\Win64'
$ConfigDir = Join-Path $GameDir 'GearGame\Config'
$docDir = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'My Games\Gears of War 3\GearGame\Config'

$FilesDir = if (Test-Path -LiteralPath (Join-Path $ToolDir '_files')) { Join-Path $ToolDir '_files' } elseif (Test-Path -LiteralPath (Join-Path $ToolDir 'GearGame.u')) { $ToolDir } else { $null }
$bakCandidates = @((Join-Path $ToolDir '_files\Backups'), (Join-Path $GameDir 'Gears3_Multipatch_Tool\_files\Backups'))
$BackupRoot = $bakCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
$settingsCandidates = @()
if ($BackupRoot) { $settingsCandidates += (Join-Path (Split-Path -Parent $BackupRoot) 'graphics.json') }
$settingsCandidates += (Join-Path $ToolDir '_files\graphics.json')
$GfxFile = $settingsCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

# ---------------------------------------------------------------------------------------------- 0. environment
Section 'ENVIRONMENT'
$toolVersion = '?'
$toolPs1 = Join-Path $ToolDir 'Gears3_Multipatch_Tool.ps1'
if (Test-Path -LiteralPath $toolPs1) {
    $vm = [regex]::Match([System.IO.File]::ReadAllText($toolPs1), '(?m)^\$CURRENT_VERSION\s*=\s*"([^"]+)"')
    if ($vm.Success) { $toolVersion = $vm.Groups[1].Value }
}
Add-Result 'INFO' "Audit started $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), PowerShell $($PSVersionTable.PSVersion), tool script version $toolVersion"
Add-Result 'INFO' "Game folder: $GameDir"
if ($FilesDir) { Add-Result 'OK' "Tool data files folder: $FilesDir" } else { Add-Result 'WARN' "Tool data files (_files folder) not found next to this script: the payload check is skipped." }
if ($BackupRoot) {
    $bakCount = @(Get-ChildItem -LiteralPath (Get-LongPath $BackupRoot) -Recurse -File -ErrorAction SilentlyContinue).Count
    Add-Result 'OK' "Backup folder: $BackupRoot ($bakCount file(s))"
}
else {
    Add-Result 'FAIL' "No backup folder found (looked in: $($bakCandidates -join ' ; ')). The installer has either never run on this game folder or its backups were deleted: Uninstall cannot work and step 1 below is skipped."
}

# Settings the installer applied (graphics.json = what the main menu 3. Game Settings saves). Defaults are what the installer assumes without the file.
$gs = @{ PlayerName = ''; Language = 'INT'; Resolution = 'native'; Fullscreen = $true; Fov = 0; FovAllModes = $false; MouseSens = ''
         Subtitles = $true; SubtitleSize = 'big'; VSync = $false; RefreshRate = 0; FpsLimit = 0
         GraphicsQuality = 'high'; ShadowQuality = 'high'; TextureQuality = 'high'; FoliageQuality = 'high'
         Anisotropy = 16; AntiAliasing = 'off'; AmbientOcclusion = $true; DriveType = 'ssd'
         DisableRunShake = $true; ForceWarfareFonts = $false; Disabled = @() }
$customFont = $false   # set after graphics.json is read: languages with their own fonts (Russian, Korean, Japanese, Chinese ...) keep their font packages
$effectIds = @{
    MotionBlur = @('MotionBlur', 'MotionBlurPause'); DepthOfField = @('DepthOfField'); Bloom = @('Bloom'); LensFlares = @('LensFlares')
    LightShafts = @('bAllowLightShafts'); Distortion = @('Distortion', 'FilteredDistortion'); RadialBlur = @('AllowRadialBlur')
    Subsurface = @('AllowSubsurfaceScattering'); SeparateTranslucency = @('bAllowSeparateTranslucency')
}
if ($GfxFile) {
    try {
        $o = Get-Content -LiteralPath $GfxFile -Raw | ConvertFrom-Json
        foreach ($k in @($gs.Keys)) {
            if ($null -eq $o.$k) { continue }
            if ($k -eq 'Disabled') { $gs.Disabled = @($o.Disabled | Where-Object { $effectIds.ContainsKey([string]$_) }) }
            elseif ($gs[$k] -is [bool]) { $gs[$k] = [bool]$o.$k }
            elseif ($gs[$k] -is [int]) { $gs[$k] = [int]$o.$k }
            else { $gs[$k] = ([string]$o.$k).Trim() }
        }
        Add-Result 'OK' "Saved Game Settings read from $GfxFile (language $($gs.Language), resolution $($gs.Resolution), VSync $($gs.VSync), $($gs.Disabled.Count) effect(s) disabled)"
    }
    catch { Add-Result 'WARN' "graphics.json could not be read ($($_.Exception.Message)); the installer defaults are assumed." }
}
else { Add-Result 'INFO' "graphics.json not found: the installer defaults are assumed (English, native resolution, fullscreen, VSync off, run camera shake removed, all effects on)." }

$specialGlyphLangs = @('RUS', 'CHN', 'CHT', 'ZHO', 'CHI', 'JPN', 'JAP', 'KOR', 'ARA', 'PER', 'FAR', 'HEB', 'THA')
$customFont = ($specialGlyphLangs -contains ([string]$gs.Language).ToUpper()) -and (-not [bool]$gs.ForceWarfareFonts)
if ($customFont) { Add-Result 'INFO' "Language $($gs.Language) uses special character fonts: WarfareFonts installation was skipped (font checks skipped)." }
if ($IsWin) {
    $dxMissing = @('D3DCOMPILER_43.dll', 'd3dx9_43.dll') | Where-Object { -not (Test-Path -LiteralPath (Join-Path $env:windir "System32\$_")) -and -not (Test-Path -LiteralPath (Join-Path $BinariesDir $_)) }
    if ($dxMissing) { Add-Result 'FAIL' "DirectX June 2010 runtime missing ($($dxMissing -join ', ')): the game can stop at start with 'Fatal error! DwTriovizCreatePixelShader'. Install Microsoft's DirectX End-User Runtime." }
    else { Add-Result 'OK' "DirectX runtime DLLs present (D3DCOMPILER_43.dll, d3dx9_43.dll)" }
}

# ---------------------------------------------------------------------------------------------- 1. backups vs game files
$bakDiff = @{ Modified = 0; Same = 0; Missing = 0 }
if ($BackupRoot -and -not $SkipBackupDiff) {
    Section '1. WHAT THE PATCH CHANGED (every backed up file compared with the file in the game folder)'
    $bakRootLong = Get-LongPath $BackupRoot
    $baks = @(Get-ChildItem -LiteralPath $bakRootLong -Recurse -File -ErrorAction SilentlyContinue)
    $idx = 0
    $unchangedPk = @()
    foreach ($bak in $baks) {
        $idx++
        if ($idx % 25 -eq 0) { Write-Progress -Activity 'Comparing backups' -Status "$idx / $($baks.Count)" -PercentComplete (100 * $idx / $baks.Count) }
        $rel = $bak.FullName.Substring($bakRootLong.Length).TrimStart('\', '/')
        $target = $null
        if ($IsWin) { if ($rel -match '^([A-Za-z])[\\/](.*)') { $target = $matches[1] + ':\' + $matches[2] } }
        else { $target = '/' + $rel }
        if (-not $target) { Add-Result 'INFO' "Backup with an unknown layout skipped: $rel"; continue }
        $targetLong = Get-LongPath $target
        $ext = [System.IO.Path]::GetExtension($target).ToLower()
        if (-not (Test-Path -LiteralPath $targetLong)) { $bakDiff.Missing++; Add-Result 'FAIL' "MISSING  $target (it was backed up, but the file is gone)"; continue }
        $tLen = (Get-Item -LiteralPath $targetLong).Length
        $changed = $true
        if ($tLen -eq $bak.Length) { $changed = (Get-Sha256 $bak.FullName) -ne (Get-Sha256 $target) }
        if ($changed) {
            $bakDiff.Modified++
            $sizeNote = if ($tLen -eq $bak.Length) { "same size $(Format-Size $tLen)" } else { "$(Format-Size $bak.Length) -> $(Format-Size $tLen)" }
            Add-Result 'OK' "CHANGED  $target ($sizeNote)"
        }
        else {
            $bakDiff.Same++
            if ($ext -in @('.ini', '.bat', '.sh', '.int', '.esm', '.ita')) { Add-Result 'INFO' "UNCHANGED $target (backed up; the patch left it as it was - normal when the wanted values were already in the file)" }
            elseif ($ext -in @('.upk', '.bik')) { $unchangedPk += $target.Substring($GameDir.Length).TrimStart('\', '/') }
            elseif ($ext -eq '.u') { Add-Result 'INFO' "UNCHANGED $target (identical to its backup; section 2 shows whether it is the tool's file)" }
            elseif ($ext -eq '.exe') { Add-Result 'WARN' "UNCHANGED $target (identical to its backup: the engine binary patches of step 8, e.g. HUD scaling, are not in it - or the backup was taken from an already patched exe)" }
            else { Add-Result 'WARN' "UNCHANGED $target (backed up, but identical to the backup: the patch did not change it, or it was restored/replaced by the original afterwards)" }
        }
    }
    Write-Progress -Activity 'Comparing backups' -Completed
    if ($unchangedPk.Count -gt 0) { Add-Result 'INFO' "$($unchangedPk.Count) backed up package(s) are identical to their backup (the installer backs up a whole group, e.g. all GFx UI packages, and changes only the ones it needs): $($unchangedPk -join ' ; ')" }
    Add-Result 'INFO' "Summary: $($bakDiff.Modified) changed, $($bakDiff.Same) identical to their backup, $($bakDiff.Missing) missing, $($baks.Count) backed up in total"
}
elseif ($SkipBackupDiff) { Section '1. WHAT THE PATCH CHANGED'; Add-Result 'INFO' 'Skipped (-SkipBackupDiff).' }

# ---------------------------------------------------------------------------------------------- 2. payload files
Section '2. THE TOOL''S OWN FILES (_files compared with the installed copy)'
$kbVariant = $null     # $true = Keyboard & Mouse GearGame.u, $false = gamepad, $null = unknown
if ($FilesDir) {
    $payload = @(Get-ChildItem -LiteralPath $FilesDir -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -in @('.upk', '.gear', '.u', '.bik', '.dll') -and $_.Name -ne 'GOW3Helper.dll' })
    # name -> installed paths (one pass over the content folders)
    $wanted = @{}
    foreach ($p in $payload) { if ($p.Name -ne 'GearGame.u' -and $p.Extension -ne '.dll') { $wanted[$p.Name.ToLower()] = $true } }
    $found = @{}
    foreach ($root in @((Join-Path $GameDir 'GearGame\Content'), (Join-Path $GameDir 'GearGame\Movies'))) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        foreach ($f in [System.IO.Directory]::EnumerateFiles($root, '*', [System.IO.SearchOption]::AllDirectories)) {
            $n = [System.IO.Path]::GetFileName($f).ToLower()
            if ($wanted.ContainsKey($n)) { if (-not $found.ContainsKey($n)) { $found[$n] = New-Object System.Collections.Generic.List[string] }; $found[$n].Add($f) }
        }
    }

    # GearGame.u: gamepad variant = the file as shipped, Keyboard & Mouse variant = shipped + the 4 name patches of installer step 3 / 6
    $uSrc = Join-Path $FilesDir 'GearGame.u'
    $uDst = Join-Path $GameDir 'GearGame\Script\GearGame.u'
    if (Test-Path -LiteralPath $uSrc) {
        if (-not (Test-Path -LiteralPath $uDst)) { Add-Result 'FAIL' "GearGame.u is missing in GearGame\Script" }
        else {
            $shipped = [System.IO.File]::ReadAllBytes($uSrc)
            $pairs = @(
                @('07000000546573744D6500', '0700000045736361706500'),   # name table: TestMe -> Escape
                @('2113BD000000000000', '212BB0000000000000'),            # XboxTypeS_B -> Escape
                @('2123BD000000000000', '21B04A000000000000'),            # XboxTypeS_Start -> Enter
                @('2124BD000000000000', '21E744000000000000')             # XboxTypeS_X -> E (Players / Friends and other X buttons)
            )
            $kb = [byte[]]$shipped.Clone()
            $counts = @()
            foreach ($pr in $pairs) { $counts += [GOW3Verify]::Replace($kb, (HexToBytes $pr[0]), (HexToBytes $pr[1])) }
            $shaShip = Get-Sha256Bytes $shipped; $shaKb = Get-Sha256Bytes $kb; $shaCur = Get-Sha256 $uDst
            $curBytes = [System.IO.File]::ReadAllBytes($uDst)
            # variant: the original XboxTypeS_X name constant is gone and the 'E' one is in = Keyboard & Mouse patches applied
            $cOrig = [GOW3Verify]::Count($curBytes, (HexToBytes $pairs[3][0])); $cNew = [GOW3Verify]::Count($curBytes, (HexToBytes $pairs[3][1]))
            if ($cNew -gt 0 -and $cOrig -eq 0) { $kbVariant = $true } elseif ($cOrig -gt 0 -and $cNew -eq 0) { $kbVariant = $false }
            $variantName = if ($kbVariant -eq $true) { 'Keyboard & Mouse variant' } elseif ($kbVariant -eq $false) { 'gamepad variant' } else { 'variant not identified' }
            if ($shaCur -eq $shaKb -or $shaCur -eq $shaShip) { Add-Result 'OK' "GearGame.u is exactly the tool's file ($variantName; keyboard name patches in the shipped file: $($counts -join ' + '))" }
            else {
                $ref = if ($kbVariant -eq $true) { $kb } else { $shipped }
                $d = [GOW3Verify]::Diff($curBytes, $ref)
                if ($null -eq $d) { Add-Result 'FAIL' "GearGame.u has a different size ($(Format-Size $curBytes.Length)) than the tool's file ($(Format-Size $ref.Length)): it was replaced by another build or damaged. Run the installer again." }
                elseif ($d[1] -le 400) { Add-Result 'INFO' "GearGame.u ($variantName): same size as the tool's file, $($d[0]) byte(s) in $($d[1]) place(s) differ. Expected: later installer steps (co-op, pause menu, dev leftovers ...) patch the installed GearGame.u in place." }
                else { Add-Result 'WARN' "GearGame.u ($variantName): $($d[0]) byte(s) in $($d[1]) place(s) differ from the tool's file; that is more than the installer's in-place patches. Another mod may have changed it." }
            }
        }
    }

    $corpser = @('corpserrestor_audio.upk', 'corpserrestor_battleaudio.upk', 'corpserrestor_interwavefx.upk', 'corpserrestor_pillarchunks.upk')
    $corpserState = @{}
    foreach ($p in ($payload | Sort-Object Name)) {
        if ($p.Name -eq 'GearGame.u') { continue }
        $lname = $p.Name.ToLower()
        $paths = @()
        if ($p.Extension -eq '.dll') { $dst = Join-Path $BinariesDir $p.Name; if (Test-Path -LiteralPath $dst) { $paths = @($dst) } }
        elseif ($found.ContainsKey($lname)) { $paths = @($found[$lname]) }
        $shaSrc = Get-Sha256 $p.FullName
        $match = $null; $anyPath = $null
        foreach ($path in $paths) { $anyPath = $path; if ((Get-Sha256 $path) -eq $shaSrc) { $match = $path; break } }

        # expectations that depend on what the user chose
        $expected = $true; $why = ''
        if ($lname -eq 'warfarefonts2x.upk') { $expected = ($gs.SubtitleSize -eq 'big' -and -not $customFont); $why = $(if ($customFont) { "language $($gs.Language) uses its own fonts" } else { "subtitle size '$($gs.SubtitleSize)'" }) }
        elseif ($lname -eq 'warfarefonts.upk') { $expected = ($gs.SubtitleSize -eq 'normal' -and -not $customFont); $why = $(if ($customFont) { "language $($gs.Language) uses its own fonts" } else { "subtitle size '$($gs.SubtitleSize)'" }) }
        elseif ($lname -eq 'warfare_hud.upk') { $expected = ($kbVariant -eq $true); $why = 'Keyboard & Mouse icons are only installed with the Keyboard & Mouse input choice' }
        elseif ($p.Name -ieq 'wsock32.dll' -or $p.Name -ieq 'steam_api64.dll') { $expected = $false; $why = 'Steam online co-op (JacintoNet) is optional' }

        if ($match) {
            Add-Result 'OK' "$($p.Name): installed and identical ($match)"
            if ($corpser -contains $lname) { $corpserState[$lname] = 'ok' }
        }
        elseif ($anyPath) {
            $jn = ($p.Name -ieq 'wsock32.dll') -and [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($anyPath)).Contains('JacintoNet %s')
            if ($jn) { Add-Result 'OK' "$($p.Name): installed, a JacintoNet build that differs from the packaged one ($anyPath)" }
            elseif (-not $expected) { Add-Result 'INFO' "$($p.Name): installed copy differs from the tool's file; not expected to be the tool's file here ($why)" }
            else { Add-Result 'WARN' "$($p.Name): the installed file differs from the tool's file ($anyPath). Another mod or pack replaced it, or the install is older." }
            if ($corpser -contains $lname) { $corpserState[$lname] = 'differs' }
        }
        else {
            if ($corpser -contains $lname) { $corpserState[$lname] = 'missing' }
            elseif (-not $expected) { Add-Result 'INFO' "$($p.Name): not installed ($why)" }
            elseif ($p.Extension -eq '.gear') { Add-Result 'WARN' "$($p.Name): no map with this name found in GearGame\Content" }
            else { Add-Result 'WARN' "$($p.Name): not found in the game folder (the tool ships it, so the install is incomplete)" }
        }
    }
    if ($corpserState.Count -gt 0) {
        $states = @($corpserState.Values | Select-Object -Unique)
        if ($states.Count -gt 1 -or ($states[0] -ne 'ok' -and $states[0] -ne 'missing')) {
            Add-Result 'FAIL' "Mother Corpser restoration is incomplete or mixed ($(($corpserState.GetEnumerator() | Sort-Object Name | ForEach-Object { $_.Name + '=' + $_.Value }) -join ', ')). Its maps and packages must be installed together or not at all: this state can crash the boss fight."
        }
        elseif ($states[0] -eq 'missing') { Add-Result 'INFO' 'Mother Corpser restoration: not installed (its 4 packages are absent)' }
    }
}

# ---------------------------------------------------------------------------------------------- 3. engine inis
Section '3. ENGINE SETTINGS (GearEngine.ini / DefaultEngine.ini / BaseEngine.ini / Documents GearEngine.ini)'
$engineInis = @((Join-Path $ConfigDir 'GearEngine.ini'), (Join-Path $ConfigDir 'DefaultEngine.ini'), (Join-Path $GameDir 'Engine\Config\BaseEngine.ini'), (Join-Path $docDir 'GearEngine.ini')) | Where-Object { Test-Path -LiteralPath $_ }
$engText = @{}
foreach ($f in $engineInis) { $engText[$f] = Read-TextAuto $f }
if ($engineInis.Count -eq 0) { Add-Result 'FAIL' 'No engine ini file found.' }
else {
    Add-Result 'INFO' "Files checked: $(($engineInis | ForEach-Object { $_.Substring($GameDir.Length).TrimStart('\') -replace [regex]::Escape($docDir), 'Documents\...' }) -join ' ; ')"

    # resolution: a fixed choice must match; native just has to be the same everywhere
    $resX = $null; $resY = $null
    if ($gs.Resolution -match '^(\d+)x(\d+)$') { $resX = [int]$matches[1]; $resY = [int]$matches[2] }
    $fX = @(); $fY = @()
    foreach ($f in $engineInis) {
        $vx = Get-IniValues $engText[$f] 'ResX'; $vy = Get-IniValues $engText[$f] 'ResY'
        if ($vx.Count -gt 0) { $fX += @{ File = $f; Actual = $vx[0]; Expected = $(if ($resX) { "$resX" } else { $vx[0] }) } }
        if ($vy.Count -gt 0) { $fY += @{ File = $f; Actual = $vy[0]; Expected = $(if ($resY) { "$resY" } else { $vy[0] }) } }
    }
    Report-Setting 'ResX' $fX 'FAIL'; Report-Setting 'ResY' $fY 'FAIL'
    if (-not $resX -and $fX.Count -gt 0) {
        $allX = @($engineInis | ForEach-Object { (Get-IniValues $engText[$_] 'ResX') | Select-Object -First 1 } | Where-Object { $_ } | Select-Object -Unique)
        if ($allX.Count -gt 1) { Add-Result 'WARN' "Native resolution chosen but ResX differs between files ($($allX -join ', '))" }
        $resX = [int]$allX[0]; $resY = [int]((Get-IniValues $engText[$engineInis[0]] 'ResY') | Select-Object -First 1)
    }

    $simple = @(
        @{ L = 'Fullscreen'; K = 'Fullscreen'; E = $(if ($gs.Fullscreen) { 'True' } else { 'False' }) },
        @{ L = 'UseVsync (VSync)'; K = 'UseVsync'; E = $(if ($gs.VSync) { 'True' } else { 'False' }) },
        @{ L = 'Language'; K = 'Language'; E = $gs.Language },
        @{ L = 'LanguageExt'; K = 'LanguageExt'; E = $gs.Language },
        @{ L = 'bUseTaskPerfTracking (18 s start delay fix)'; K = 'bUseTaskPerfTracking'; E = 'FALSE' },
        @{ L = 'bUseTaskPerfMemDatabase (18 s start delay fix)'; K = 'bUseTaskPerfMemDatabase'; E = 'FALSE' }
    )
    if ($resX -and $resY) { $simple += @{ L = 'AspectRatioAxisConstraint'; K = 'AspectRatioAxisConstraint'; E = $(if ($resX / $resY -ge 2.0) { 'AspectRatio_MaintainYFOV' } else { 'AspectRatio_MaintainXFOV' }) } }
    foreach ($s in $simple) {
        $fi = @()
        foreach ($f in $engineInis) {
            $vals = Get-IniValues $engText[$f] $s.K
            if ($vals.Count -eq 0) { $fi += @{ File = $f; Actual = $null; Expected = $s.E }; continue }
            $badVal = $vals | Where-Object { -not (Same $_ $s.E) } | Select-Object -First 1
            $fi += @{ File = $f; Actual = $(if ($badVal) { $badVal } else { $vals[0] }); Expected = $s.E }
        }
        $lvl = if ($s.K -in @('Language', 'LanguageExt', 'UseVsync', 'Fullscreen')) { 'FAIL' } else { 'WARN' }
        Report-Setting $s.L $fi $lvl
    }

    # fullscreen refresh rate (0 = the game picks the mode)
    $rrFind = @()
    foreach ($f in $engineInis) { foreach ($k in 'MinAllowableRefreshRate', 'MaxAllowableRefreshRate') { $rrFind += @{ File = $f; Actual = (Get-SectionValue $engText[$f] 'WinDrv.WindowsClient' $k); Expected = "$($gs.RefreshRate)" } } }
    Report-Setting 'Fullscreen refresh rate (Min/MaxAllowableRefreshRate)' $rrFind 'WARN'

    # post-process effects
    $fxFind = @{}
    foreach ($id in $effectIds.Keys) {
        $want = if ($gs.Disabled -contains $id) { 'False' } else { 'True' }
        foreach ($k in $effectIds[$id]) {
            foreach ($f in $engineInis) {
                $v = Get-SectionValue $engText[$f] 'SystemSettings' $k
                if (-not $fxFind.ContainsKey($id)) { $fxFind[$id] = @() }
                $fxFind[$id] += @{ File = $f; Actual = $v; Expected = $want; Key = $k }
            }
        }
    }
    foreach ($id in ($effectIds.Keys | Sort-Object)) {
        $bad = @($fxFind[$id] | Where-Object { $null -ne $_.Actual -and -not (Same $_.Actual $_.Expected) })
        $want = if ($gs.Disabled -contains $id) { 'disabled' } else { 'enabled (original)' }
        if ($bad.Count -eq 0) { Add-Result 'OK' "Post-process effect $id is $want" }
        else { Add-Result 'WARN' "Post-process effect $id should be $want but $(($bad | ForEach-Object { (Split-Path -Leaf $_.File) + ':' + $_.Key + '=' + $_.Actual }) -join ', ')" }
    }

    # network
    foreach ($f in $engineInis) {
        $t = $engText[$f]
        if ($t -match '(?m)^Port=1000\s*$') { Add-Result 'WARN' "$(Split-Path -Leaf $f): [URL] Port=1000 (privileged port on Linux / SteamOS; the installer changes it to 7777)" }
        if ($t -match '(?m)^NetworkDevice="?OnlineSubsystemLive\.IpNetDriverLive') { Add-Result 'INFO' "$(Split-Path -Leaf $f): NetworkDevice still OnlineSubsystemLive.IpNetDriverLive (logs 'Failed to find object' at each map load, harmless; the installer switches it to IpDrv.TcpNetDriver)" }
    }

    # subtitles
    $subFind = @()
    foreach ($f in $engineInis) {
        $v = Get-SectionValue $engText[$f] 'Engine.Engine' 'bSubtitlesForcedOff'
        if ($null -ne $v) { $subFind += @{ File = $f; Actual = $v; Expected = $(if ($gs.Subtitles) { 'FALSE' } else { 'TRUE' }) } }
    }
    Report-Setting 'bSubtitlesForcedOff' $subFind 'WARN'
    if ($gs.SubtitleSize -ne 'default' -and -not $customFont) {
        $fontName = if ($gs.SubtitleSize -eq 'big') { 'WarfareFonts2x' } else { 'WarfareFonts' }
        $fFind = @()
        foreach ($f in $engineInis) { if ((Split-Path -Leaf $f) -ne 'BaseEngine.ini') { $v = Get-IniValues $engText[$f] 'SubtitleFontName'; if ($v.Count -gt 0) { $fFind += @{ File = $f; Actual = $v[0]; Expected = "$fontName.Fonts.WarfareFonts_Euro20" } } } }
        Report-Setting 'SubtitleFontName' $fFind 'WARN'
    }
}

# ---------------------------------------------------------------------------------------------- 4. textures
Section '4. TEXTURE STREAMING AND SIZE CAPS (crash guard)'
$stock = [ordered]@{ World = @(1024, 0); WorldNormalMap = @(1024, 0); WorldSpecular = @(512, 1); Character = @(1024, 0); CharacterNormalMap = @(1024, 0); CharacterSpecular = @(512, 0)
                     Weapon = @(1024, 0); WeaponNormalMap = @(1024, 0); WeaponSpecular = @(512, 0); Vehicle = @(1024, 0); VehicleNormalMap = @(1024, 0); VehicleSpecular = @(512, 1) }
$phase = @{}
foreach ($n in 1, 2) {
    $pf = Join-Path $GameDir "GearGame\Content\HD_TexturePack_Phase$n.version"
    $ok = $false
    if (Test-Path -LiteralPath $pf) { try { $ok = ([version]((Get-Content -LiteralPath $pf -TotalCount 1).Trim()) -ge [version]'1.3') } catch { $ok = $false } }
    $phase[$n] = $ok
}
$legacy = Test-Path -LiteralPath (Join-Path $GameDir 'GearGame\Content\HD_TexturePack.version')
Add-Result 'INFO' "HD Textures Pack markers: Phase 1 $(if ($phase[1]) { 'v1.3+' } else { 'not installed' }), Phase 2 $(if ($phase[2]) { 'v1.3+' } else { 'not installed' })$(if ($legacy) { ', old HD_TexturePack.version (v1.2) present: it does not allow raised caps' })"
foreach ($f in $engineInis) {
    if ((Split-Path -Leaf $f) -eq 'BaseEngine.ini') { continue }   # the engine's base defaults; GearEngine.ini / DefaultEngine.ini override them
    $txt = $engText[$f]
    $sysBody = Get-SectionBody $txt 'SystemSettings'
    if ($null -eq $sysBody) { continue }
    $raised = @()
    foreach ($g in $stock.Keys) {
        $m = [regex]::Match($sysBody, '(?m)^TEXTUREGROUP_' + $g + '=\(([^\r\n]*)\)')
        if (-not $m.Success) { continue }
        $max = [regex]::Match($m.Groups[1].Value, 'MaxLODSize=(\d+)'); $bias = [regex]::Match($m.Groups[1].Value, 'LODBias=(-?\d+)')
        if (-not $max.Success) { continue }
        $safe = if ($g -like 'World*') { $phase[1] -and $phase[2] } else { $phase[1] }
        if ([int]$max.Groups[1].Value -gt $stock[$g][0] -and -not $safe) { $raised += "$g MaxLODSize=$($max.Groups[1].Value) (stock $($stock[$g][0]))" }
        elseif ($bias.Success -and [int]$bias.Groups[1].Value -lt $stock[$g][1] -and -not $safe) { $raised += "$g LODBias=$($bias.Groups[1].Value) (stock $($stock[$g][1]))" }
    }
    if ($raised.Count -gt 0) { Add-Result 'FAIL' "$(Split-Path -Leaf $f): texture caps above the stock values without the HD pack phase that supports them: $($raised -join '; '). This is the cause of 'Assertion failed: Offset != INDEX_NONE' crashes. Run the installer again (or Uninstall)." }
    else { Add-Result 'OK' "$(Split-Path -Leaf $f): no texture group cap above what the installed packages support" }
    $pool = Get-SectionValue $txt 'TextureStreaming' 'PoolSize'
    if ($pool) { Add-Result 'OK' "$(Split-Path -Leaf $f): [TextureStreaming] PoolSize=$pool" }
    elseif ((Split-Path -Leaf $f) -ne 'BaseEngine.ini') { Add-Result 'WARN' "$(Split-Path -Leaf $f): [TextureStreaming] PoolSize not set (the Texture Streaming step did not run)" }
}

$expOnlyStream = if ($gs.DriveType -eq 'hdd') { 'True' } else { 'False' }
$expMipFade = if ($gs.DriveType -eq 'hdd') { 'True' } else { 'False' }
$streamFind = @(); $fadeFind = @()
foreach ($f in $engineInis) {
    if ((Split-Path -Leaf $f) -eq 'BaseEngine.ini') { continue }
    $txt = $engText[$f]
    $os = Get-SectionValue $txt 'SystemSettings' 'OnlyStreamInTextures'
    if ($null -ne $os) { $streamFind += @{ File = $f; Actual = $os; Expected = $expOnlyStream } }
    $mf = Get-SectionValue $txt 'Engine.Engine' 'bEnableMipLevelFading'
    if ($null -ne $mf) { $fadeFind += @{ File = $f; Actual = $mf; Expected = $expMipFade } }
}
Report-Setting "OnlyStreamInTextures ($(if ($gs.DriveType -eq 'hdd') { 'HDD anti-popin' } else { 'SSD stream-out' }))" $streamFind 'WARN'
Report-Setting "bEnableMipLevelFading ($(if ($gs.DriveType -eq 'hdd') { 'HDD mip cross-fade' } else { 'SSD instant' }))" $fadeFind 'WARN'

# ---------------------------------------------------------------------------------------------- 5. camera / pawn / input / mouse / name
Section '5. CAMERA, RUN SHAKE, INPUT, MOUSE, PLAYER NAME'
$camInis = @((Join-Path $ConfigDir 'GearCamera.ini'), (Join-Path $ConfigDir 'DefaultCamera.ini'), (Join-Path $docDir 'GearCamera.ini')) | Where-Object { Test-Path -LiteralPath $_ }
$fov = if ($gs.Fov -ge 50 -and $gs.Fov -le 130) { $gs.Fov } elseif ($resX -and $resY -and $resX / $resY -ge 2.0) { 65 } else { 80 }
$bf = @(); $fa = @()
foreach ($f in $camInis) {
    $t = Read-TextAuto $f
    $v = Get-IniValues $t 'BaseFOV'; if ($v.Count -gt 0) { $bf += @{ File = $f; Actual = $v[0]; Expected = "$fov" } }
    if (-not $gs.FovAllModes) { $d = Get-SectionValue $t 'GearGame.GameplayCam_Default' 'FOVAngle'; if ($null -ne $d) { $fa += @{ File = $f; Actual = $d; Expected = "$fov" } } }
}
Report-Setting 'BaseFOV' $bf 'WARN' "FOV $fov expected for this resolution / setting"
Report-Setting 'GameplayCam_Default FOVAngle' $fa 'WARN'

$stockShake = @{ 'GearGame.GearPawn' = '0.6f'; 'GearGameContent.GearPawn_LocustKantusArmor' = '0.45f' }
$shake = @()
foreach ($dir in @($ConfigDir, $docDir)) {
    foreach ($name in @('GearPawn.ini', 'DefaultPawn.ini', 'GearPawnMP.ini', 'DefaultPawnMP.ini')) {
        $f = Join-Path $dir $name
        if (-not (Test-Path -LiteralPath $f)) { continue }
        $t = Read-TextAuto $f
        foreach ($sec in $stockShake.Keys) {
            $v = Get-SectionValue $t $sec 'RoadieRunShakyCamDampeningFactor'
            if ($null -ne $v) { $shake += @{ File = "$f [$sec]"; Actual = $v; Expected = $(if ($gs.DisableRunShake) { '0.0f' } else { $stockShake[$sec] }) } }
        }
    }
}
Report-Setting "RoadieRunShakyCamDampeningFactor ($(if ($gs.DisableRunShake) { 'shake removed' } else { 'original shake' }))" $shake 'WARN'

if ($gs.MouseSens -ne '') {
    $ms = @()
    foreach ($f in @((Join-Path $ConfigDir 'DefaultInput.ini'), (Join-Path $ConfigDir 'GearInput.ini'), (Join-Path $docDir 'GearInput.ini'), (Join-Path $GameDir 'Engine\Config\BaseInput.ini'))) {
        if (Test-Path -LiteralPath $f) { $ms += @{ File = $f; Actual = (Get-SectionValue (Read-TextAuto $f) 'Engine.PlayerInput' 'MouseSensitivity'); Expected = $gs.MouseSens } }
    }
    Report-Setting 'MouseSensitivity' $ms 'WARN'
}
if ($gs.PlayerName -ne '') {
    $sera = Join-Path $BinariesDir 'sera.ini'
    if (Test-Path -LiteralPath $sera) { Report-Setting 'player_name (sera.ini)' @(@{ File = $sera; Actual = ((Get-IniValues (Read-TextAuto $sera) 'player_name') | Select-Object -First 1); Expected = $gs.PlayerName }) 'WARN' }
    else { Add-Result 'WARN' 'sera.ini not found: the player name could not be set' }
}

# keyboard & mouse UI keys: only meaningful with the Keyboard & Mouse GearGame.u variant
$inputInis = @((Join-Path $ConfigDir 'GearInput.ini'), (Join-Path $ConfigDir 'DefaultInput.ini'), (Join-Path $docDir 'GearInput.ini')) | Where-Object { Test-Path -LiteralPath $_ }
if ($kbVariant -eq $true) {
    foreach ($f in $inputInis) {
        $t = Read-TextAuto $f
        $km = Get-SectionBody $t 'GFxUI.KeyMap'
        if ($null -eq $km) { Add-Result 'INFO' "$(Split-Path -Leaf $f): no [GFxUI.KeyMap] section"; continue }
        # Enter / Escape lines in [GFxUI.KeyMap] are written by the installer, but the game rewrites GearInput.ini at start and exit and may drop them;
        # Space / Enter / Esc work in the menus without them (tested in game), what matters is FullKeyboard=1.
        if ($km -match '(?m)^FullKeyboard=1') { Add-Result 'OK' "$(Split-Path -Leaf $f): [GFxUI.KeyMap] FullKeyboard=1 (keyboard works in the menus)" }
        elseif ($km -match '(?m)^FullKeyboard=0') { Add-Result 'WARN' "$(Split-Path -Leaf $f): FullKeyboard=0 (the installer sets 1; menus may not take keyboard input)" }
        else { Add-Result 'WARN' "$(Split-Path -Leaf $f): [GFxUI.KeyMap] has no FullKeyboard=1 line (the installer sets it)" }
        $miss = @('Enter=GAMEPAD_A', 'Escape=GAMEPAD_B') | Where-Object { $km -notmatch ('(?m)^' + [regex]::Escape($_) + '\s*$') }
        if ($miss) { Add-Result 'INFO' "$(Split-Path -Leaf $f): [GFxUI.KeyMap] has no $($miss -join ', ') line (the game drops it when it rewrites the ini; Enter / Esc still work)" }
    }
}
else { Add-Result 'INFO' "Keyboard & Mouse menu keys not checked (GearGame.u is not the Keyboard & Mouse variant, or was not identified)." }

# saved keybinds (Keybind Editor)
$kbFile = if ($FilesDir) { Join-Path $FilesDir 'keybinds.json' } else { $null }
if ($kbFile -and (Test-Path -LiteralPath $kbFile)) {
    try {
        $kb = Get-Content -LiteralPath $kbFile -Raw | ConvertFrom-Json
        $primary = Join-Path $ConfigDir 'GearInput.ini'
        if (Test-Path -LiteralPath $primary) {
            $t = Read-TextAuto $primary; $bad = @(); $n = 0
            foreach ($p in $kb.PSObject.Properties) {
                $n++
                if ($t -notmatch ('(?m)^[ \t]*[\.\+]?Bindings=\(Name="' + [regex]::Escape([string]$p.Value) + '",Command="' + [regex]::Escape($p.Name) + '"')) { $bad += "$($p.Name)=$($p.Value)" }
            }
            if ($bad.Count -gt 0) { Add-Result 'WARN' "Saved keybinds not found in GearInput.ini: $($bad -join ', ')" } else { Add-Result 'OK' "All $n saved custom keybind(s) are in GearInput.ini" }
        }
    }
    catch { Add-Result 'WARN' "keybinds.json could not be read: $($_.Exception.Message)" }
}

# ---------------------------------------------------------------------------------------------- 6. localization
Section '6. LOCALIZATION LINES (raw "?INT?..." texts in the UI)'
$locRoot = Join-Path $GameDir 'GearGame\Localization'
$poiNeeded = @('CHAR_POI_T', 'CHAR_POI_TE', 'Pipe_POI_1', 'Pipe_POI_2', 'Pipe_POI_3', 'RUSTLUNG_POI_BUTTON', 'RUSTLUNG_POI_SILVERBACK')
$langDirs = @(Get-ChildItem -LiteralPath $locRoot -Directory -ErrorAction SilentlyContinue)
if ($langDirs.Count -eq 0) { Add-Result 'FAIL' 'GearGame\Localization has no language folder.' }
foreach ($ld in $langDirs) {
    $poi = Get-ChildItem -LiteralPath $ld.FullName -File -Filter 'level_poi.*' -ErrorAction SilentlyContinue | Where-Object { $_.Extension -notmatch 'bak' } | Select-Object -First 1
    if ($poi) {
        $t = Read-TextAuto $poi.FullName
        $body = Get-SectionBody $t 'POIS'
        if ($null -eq $body) { Add-Result 'FAIL' "[$($ld.Name)] $($poi.Name): no [POIS] section" }
        else {
            $miss = $poiNeeded | Where-Object { $body -notmatch ('(?mi)^' + [regex]::Escape($_) + '=') }
            if ($miss) { Add-Result 'FAIL' "[$($ld.Name)] $($poi.Name): [POIS] lacks $($miss -join ', ') (the UI shows raw ?$($ld.Name)?level_poi.POIS...? text)" }
            else { Add-Result 'OK' "[$($ld.Name)] point of interest names present ($($poiNeeded.Count) added keys)" }
        }
    }
    $gg = Get-ChildItem -LiteralPath $ld.FullName -File -ErrorAction SilentlyContinue | Where-Object { $_.BaseName -ieq 'GearGame' -and $_.Extension -notmatch 'bak' } | Select-Object -First 1
    if ($gg) {
        $t = Read-TextAuto $gg.FullName
        $miss = @()
        if ($t -notmatch '(?m)^TUT_ACTIVERELOAD_BUTTON1=') { $miss += 'TUT_ACTIVERELOAD_BUTTON1' }
        if ($t -notmatch '(?m)^TUT_ACTIVERELOAD_BUTTON2=') { $miss += 'TUT_ACTIVERELOAD_BUTTON2' }
        $lobby = Get-SectionBody $t 'GearUISceneFELobby_Party'
        foreach ($k in 'GameType_Desc', 'MapSelectType_Desc', 'WeapSwap_Desc') { if ($null -eq $lobby -or $lobby -notmatch ('(?m)^' + $k + '=')) { $miss += "GearUISceneFELobby_Party.$k" } }
        if ($miss) { Add-Result 'FAIL' "[$($ld.Name)] $($gg.Name) lacks: $($miss -join ', ')" } else { Add-Result 'OK' "[$($ld.Name)] $($gg.Name): tutorial and lobby texts present" }
    }
    $ob = Get-ChildItem -LiteralPath $ld.FullName -File -Filter 'level_objectives.*' -ErrorAction SilentlyContinue | Where-Object { $_.Extension -notmatch 'bak' } | Select-Object -First 1
    if ($ob) {
        $t = Read-TextAuto $ob.FullName
        $n = @([regex]::Matches($t, '(?m)^[A-Za-z0-9_]+=')).Count
        if ($t -match '--- lines the maps ask for') { Add-Result 'OK' "[$($ld.Name)] $($ob.Name): $n objective lines, missing-objective block present" }
        else { Add-Result 'INFO' "[$($ld.Name)] $($ob.Name): $n objective lines, no block added by the tool" }
    }
}

# ---------------------------------------------------------------------------------------------- 7. launchers
Section '7. LAUNCHERS'
$fsFlag = $(if ($gs.Fullscreen) { '-FULLSCREEN' } else { '-WINDOWED' }) + $(if ($gs.VSync) { ' -VSYNC' } else { ' -NOVSYNC' })
if ($resX -and $resY) {
    $args1 = "$fsFlag -ResX=$resX -ResY=$resY -language=$($gs.Language)"
    foreach ($l in @((Join-Path $GameDir 'Gears3_Start_Game.bat'), (Join-Path $BinariesDir 'StartCampaign.bat'), (Join-Path $GameDir 'Gears3_Start_Game.sh'))) {
        if (-not (Test-Path -LiteralPath $l)) { continue }
        $t = [System.IO.File]::ReadAllText($l)
        if ($t.Contains($args1)) { Add-Result 'OK' "$(Split-Path -Leaf $l): starts the game with '$args1'" }
        else { Add-Result 'WARN' "$(Split-Path -Leaf $l): does not contain '$args1' (the launch flags override the ini: VSync / resolution / language may not match your settings)" }
    }
}
if ($IsWin) {
    foreach ($l in @('Gears3_Start_Game.bat', 'Gears3_Start_Game_Debug.bat')) { if (-not (Test-Path -LiteralPath (Join-Path $GameDir $l))) { Add-Result 'WARN' "$l not found in the game folder" } }
}
$exe = Join-Path $BinariesDir 'GearGame.exe'
if ($BackupRoot) {
    $exeBak = Join-Path $BackupRoot (($exe -replace ':', ''))
    if (Test-Path -LiteralPath (Get-LongPath $exeBak)) { Add-Result 'INFO' "GearGame.exe has a backup (engine binary patches were applied; see step 1 for whether it changed)" }
    else { Add-Result 'INFO' 'GearGame.exe has no backup: the engine binary patches (HUD scaling, checkpoint, UI) were not applied by this tool.' }
}

# ---------------------------------------------------------------------------------------------- summary + report
Section 'SUMMARY'
$cnt = @{ OK = 0; INFO = 0; WARN = 0; FAIL = 0 }
foreach ($r in $script:Results) { $cnt[$r.Level]++ }
Write-Host ("  OK: {0}   INFO: {1}   WARN: {2}   FAIL: {3}" -f $cnt.OK, $cnt.INFO, $cnt.WARN, $cnt.FAIL) -ForegroundColor $(if ($cnt.FAIL -gt 0) { 'Red' } elseif ($cnt.WARN -gt 0) { 'Yellow' } else { 'Green' })
if ($cnt.FAIL -gt 0) { Write-Host "  Things to fix first:" -ForegroundColor Red; $script:Results | Where-Object { $_.Level -eq 'FAIL' } | ForEach-Object { Write-Host "   - [$($_.Area)] $($_.Message)" -ForegroundColor Red } }
if ($cnt.WARN -gt 0) { Write-Host "  Warnings:" -ForegroundColor Yellow; $script:Results | Where-Object { $_.Level -eq 'WARN' } | ForEach-Object { Write-Host "   - [$($_.Area)] $($_.Message)" -ForegroundColor Yellow } }
if ($cnt.FAIL -eq 0 -and $cnt.WARN -eq 0) { Write-Host "  Everything the audit knows about matches. (It checks the files and settings listed above; it cannot play the game for you.)" -ForegroundColor Green }

$reportPath = Join-Path $ScriptDir ("VerifyReport_{0}.txt" -f (Get-Date -Format 'yyyyMMdd_HHmmss'))
try {
    $lines = @("Gears of War 3 Multi Patch Tool - installation audit", "Game folder: $GameDir", "Tool script version: $toolVersion", "Date: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')", "OK $($cnt.OK) / INFO $($cnt.INFO) / WARN $($cnt.WARN) / FAIL $($cnt.FAIL)", "")
    $area = ''
    foreach ($r in $script:Results) {
        if ($r.Area -ne $area) { $area = $r.Area; $lines += ''; $lines += "== $area" }
        $lines += ("[{0}] {1}" -f $r.Level, $r.Message)
    }
    [System.IO.File]::WriteAllLines($reportPath, $lines, (New-Object System.Text.UTF8Encoding($true)))
    Write-Host ""
    Write-Host "  Report saved: $reportPath" -ForegroundColor Cyan
}
catch { Write-Host "  Report could not be saved: $($_.Exception.Message)" -ForegroundColor Yellow }
if (-not $NoPause) { Write-Host ""; Read-Host "Press ENTER to exit" | Out-Null }
if ($cnt.FAIL -gt 0) { exit 1 } else { exit 0 }
