[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

#Requires -Version 5.1

<#

Gears of War 3 PC (Project Jacinto v1.1.1) - Multi Patch Tool

Multi Patch Tool Developer: DryreL (with community support)

NexusMods: https://www.nexusmods.com/gearsofwar3/mods/27

All-in-one fix

#>

$ErrorActionPreference = 'Stop'

# Number of numbered steps shown in the installer headers. Bump when a new fix step is added.
$StepTotal = 37

# Launchers follow the OS: .bat on Windows, .sh on Linux / SteamOS (pwsh)
$IsWin = ($PSVersionTable.PSEdition -ne 'Core') -or $IsWindows

trap {
    Write-Host ""
    Write-Host ("=" * 66) -ForegroundColor Red
    Write-Host "  [CRITICAL ERROR] An error occurred during patch execution!" -ForegroundColor Red
    Write-Host ("=" * 66) -ForegroundColor Red
    Write-Host "  Error Message : $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "  Script Line   : Line $($_.InvocationInfo.ScriptLineNumber)" -ForegroundColor Red
    if ($_.InvocationInfo.Line) {
        Write-Host "  Failed Command: $($_.InvocationInfo.Line.Trim())" -ForegroundColor DarkRed
    }
    if ($_.Exception.InnerException) {
        Write-Host "  Inner Details : $($_.Exception.InnerException.Message)" -ForegroundColor Red
    }
    Write-Host ("=" * 66) -ForegroundColor Red
    Write-Host ""
    Read-Host "Press ENTER to exit"
    exit 1
}

# `iex` ile bir Gist'ten çağrıldığında $PSScriptRoot boş olur. Bu durumda
# başlatıcı .bat dosyasının aktardığı araç klasörünü kullan.
$ScriptDir = if ($PSScriptRoot) {
    $PSScriptRoot
}
elseif ($env:GOW3_MULTIPATCH_ROOT) {
    $env:GOW3_MULTIPATCH_ROOT
}
else {
    (Get-Location).Path
}

# Precompiled helper DLL: avoids dynamic csc.exe compilation and temporary file alerts
$HelperDll = Join-Path $ScriptDir '_files\GOW3Helper.dll'
if (Test-Path -LiteralPath $HelperDll) {
    try { [System.Reflection.Assembly]::LoadFrom($HelperDll) | Out-Null } catch { }
}

function FindGameFolder {
    $current = $ScriptDir
    for ($i = 0; $i -lt 5; $i++) {
        if (-not $current) { break }
        if (Test-Path (Join-Path $current "Binaries\Win64\GearGame.exe")) {
            return $current
        }

        # Araç klasörü oyun klasörünün yanında ise, aynı üst klasördeki
        # doğrudan alt klasörleri de kontrol et.
        foreach ($child in (Get-ChildItem -LiteralPath $current -Directory -ErrorAction SilentlyContinue)) {
            if (Test-Path (Join-Path $child.FullName "Binaries\Win64\GearGame.exe")) {
                return $child.FullName
            }
        }
        $current = Split-Path -Parent $current
    }
    return $null
}

function Test-GameFolder($Path) {
    return [bool]($Path -and (Test-Path -LiteralPath (Join-Path $Path "Binaries\Win64\GearGame.exe")))
}

# The tool may be started from anywhere (an archive, Desktop, Documents ...). When the game folder is not found
# around it, ask the user to pick it: a folder dialog on Windows, a typed path elsewhere.
function Select-GameFolder {
    Write-Host ""
    Write-Host "  [!] The game folder was not found next to the tool." -ForegroundColor Yellow
    Write-Host "      Select the Gears of War 3 folder (the one that contains Binaries, Engine and GearGame)." -ForegroundColor Yellow
    Write-Host ""
    while ($true) {
        $picked = $null
        if ($IsWin) {
            try {
                Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
                $owner = New-Object System.Windows.Forms.Form -Property @{ TopMost = $true; ShowInTaskbar = $false; StartPosition = 'CenterScreen'; Width = 0; Height = 0 }
                $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
                $dlg.Description = "Select your Gears of War 3 game folder (contains Binaries, Engine, GearGame)"
                $dlg.ShowNewFolderButton = $false
                $owner.Show()
                $res = $dlg.ShowDialog($owner)
                $owner.Close()
                if ($res -eq [System.Windows.Forms.DialogResult]::OK) { $picked = $dlg.SelectedPath }
                else { return $null }
            }
            catch { $picked = $null }
        }
        if (-not $picked) {
            $picked = (Read-Host "Game folder path (empty to quit)").Trim().Trim('"')
            if (-not $picked) { return $null }
        }
        # Accept the game root itself, a folder one level above it, or Binaries\Win64 picked by mistake.
        $up = Split-Path -Parent (Split-Path -Parent $picked)
        foreach ($cand in @($picked, $up)) {
            if (Test-GameFolder $cand) { return (Resolve-Path -LiteralPath $cand).Path }
        }
        foreach ($child in (Get-ChildItem -LiteralPath $picked -Directory -ErrorAction SilentlyContinue)) {
            if (Test-GameFolder $child.FullName) { return $child.FullName }
        }
        Write-Host "  [!] '$picked' is not a Gears of War 3 folder (Binaries\Win64\GearGame.exe not found). Try again." -ForegroundColor Red
    }
}

$GameDir = FindGameFolder
if (-not $GameDir) { $GameDir = Select-GameFolder }
if (-not $GameDir) {
    Write-Host "`n  [!] ERROR: Could not find the game folder (Binaries\Win64\GearGame.exe)." -ForegroundColor Red
    Write-Host "      Run the tool again and select the Gears of War 3 folder, or place Gears3_Multipatch_Tool inside it.`n" -ForegroundColor Yellow
    Read-Host "Press ENTER to exit"
    exit 1
}

$BinariesDir = Join-Path $GameDir "Binaries\Win64"
$ConfigDir = Join-Path $GameDir "GearGame\Config"

# Package data files (GearGame.u, Open_Logos.bik ...): normally in the _files folder, but a package whose files were
# extracted one by one into a single folder (no _files folder) works too.
$FilesDir = if (Test-Path -LiteralPath (Join-Path $ScriptDir "_files")) { Join-Path $ScriptDir "_files" }
            elseif (Test-Path -LiteralPath (Join-Path $ScriptDir "GearGame.u")) { $ScriptDir }
            else { Join-Path $ScriptDir "_files" }

# Backups must outlive this run: when the tool runs from outside the game folder (archive, Desktop ...) keep them in the game folder.
$toolInGame = $ScriptDir.TrimEnd('\', '/').StartsWith($GameDir.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)
$BackupRoot = if ($toolInGame) { Join-Path $ScriptDir "_files\Backups" } else { Join-Path $GameDir "Gears3_Multipatch_Tool\_files\Backups" }

function Get-BackupPath($File) {
    $fullPath = (Resolve-Path $File -ErrorAction SilentlyContinue).Path
    if (-not $fullPath) { $fullPath = $File }
    $driveFree = $fullPath -replace ':', ''
    return Join-Path $BackupRoot $driveFree
}

$iso88591 = [System.Text.Encoding]::GetEncoding("iso-8859-1")

function Say($Text, $Color = "Gray") {
    # A missing or invalid colour must never stop the patch process.
    if ([string]::IsNullOrWhiteSpace([string]$Color)) { $Color = "Gray" }
    if ([enum]::GetNames([System.ConsoleColor]) -notcontains [string]$Color) { $Color = "Gray" }
    Write-Host $Text -ForegroundColor $Color
}

function Header($Text) {

    Write-Host ""

    Write-Host ("=" * 66) -ForegroundColor DarkCyan

    Write-Host "  $Text" -ForegroundColor Cyan

    Write-Host ("=" * 66) -ForegroundColor DarkCyan

}

# Get-FileHash lives in Microsoft.PowerShell.Utility, which is missing / not loadable on some systems (PowerShell 4-, broken module path)
if (-not (Get-Command Get-FileHash -ErrorAction SilentlyContinue)) {
    function Get-FileHash([string]$LiteralPath, [string]$Algorithm = 'SHA256') {
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try { $h = ([BitConverter]::ToString($sha.ComputeHash([System.IO.File]::ReadAllBytes($LiteralPath)))).Replace('-', '') }
        finally { $sha.Dispose() }
        [pscustomobject]@{ Algorithm = 'SHA256'; Hash = $h; Path = $LiteralPath }
    }
}

# Startup environment check: warn (never stop) when PowerShell is too old or a cmdlet the tool uses cannot be found
function Test-PowerShellEnvironment {
    $ver = $PSVersionTable.PSVersion
    if ($ver -lt [version]'5.1') {
        Say "  WARNING: PowerShell $ver is too old (5.1 or newer needed). On Windows it comes with Windows Update (or install Windows Management Framework 5.1); Windows 10 / 11 already include it." 'Yellow'
    }
    $missing = @('Get-Content', 'Set-Content', 'Copy-Item', 'Get-ChildItem', 'Select-String', 'ConvertFrom-Json', 'ConvertTo-Json', 'Get-Date') |
        Where-Object { -not (Get-Command $_ -ErrorAction SilentlyContinue) }
    if ($missing) {
        Say "  WARNING: PowerShell commands not found: $($missing -join ', ') (PowerShell $ver)." 'Yellow'
        Say "  A PowerShell module could not be loaded. Check that the PSModulePath environment variable is not changed or empty, or run the tool in a normal Windows PowerShell window." 'Yellow'
        Say "  If the installer stops with an error, send the error text together with the output of: `$PSVersionTable" 'Yellow'
    }
}
Test-PowerShellEnvironment

function HexToBytes($Hex) {

    $b = New-Object byte[] ($Hex.Length / 2)

    for ($i = 0; $i -lt $b.Length; $i++) { $b[$i] = [Convert]::ToByte($Hex.Substring($i * 2, 2), 16) }

    return , $b

}

function HexToStr($Hex) {

    return $iso88591.GetString((HexToBytes $Hex))

}

if (-not ('GOW3SemanticByteLocator' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
public static class GOW3SemanticByteLocator
{
    public static int[] Find(byte[] data, byte[] prefix, byte[] suffix)
    {
        var hits = new List<int>();
        int first = prefix.Length;
        int last = data.Length - suffix.Length - 1;
        for (int candidate = first; candidate <= last; candidate++)
        {
            int start = candidate - prefix.Length;
            bool match = true;
            for (int j = 0; j < prefix.Length; j++)
            {
                if (data[start + j] != prefix[j]) { match = false; break; }
            }
            if (!match) continue;
            for (int j = 0; j < suffix.Length; j++)
            {
                if (data[candidate + 1 + j] != suffix[j]) { match = false; break; }
            }
            if (match) hits.Add(candidate);
        }
        return hits.ToArray();
    }

    // Like Find, but the matched value is `span` bytes wide: prefix, any `span` bytes, then suffix. Returns the value's start.
    public static int[] FindSpan(byte[] data, byte[] prefix, byte[] suffix, int span)
    {
        var hits = new List<int>();
        int last = data.Length - span - suffix.Length;
        for (int candidate = prefix.Length; candidate <= last; candidate++)
        {
            int start = candidate - prefix.Length;
            bool match = true;
            for (int j = 0; j < prefix.Length; j++)
            {
                if (data[start + j] != prefix[j]) { match = false; break; }
            }
            if (!match) continue;
            for (int j = 0; j < suffix.Length; j++)
            {
                if (data[candidate + span + j] != suffix[j]) { match = false; break; }
            }
            if (match) hits.Add(candidate);
        }
        return hits.ToArray();
    }

    public static int Replace(byte[] data, byte[] needle, byte[] replacement)
    {
        int count = 0;
        int max = data.Length - needle.Length;
        for (int i = 0; i <= max; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (data[i + j] != needle[j]) { match = false; break; }
            }
            if (match)
            {
                for (int j = 0; j < replacement.Length; j++)
                {
                    data[i + j] = replacement[j];
                }
                count++;
                i += needle.Length - 1;
            }
        }
        return count;
    }
}
"@
}

# Rewrites one 4-byte int (object link / name index) found by its serialized context. Old/New must each match exactly once.
# Returns 'applied', 'already' or 'error'. Used by the Wreckage final Brumak chapter-end fix (step 31).
function Set-IntAtContext($File, $PrefixHex, $SuffixHex, [int]$Old, [int]$New) {
    $b = [System.IO.File]::ReadAllBytes($File)
    $hits = [GOW3SemanticByteLocator]::FindSpan($b, (HexToBytes $PrefixHex), (HexToBytes $SuffixHex), 4)
    $oldHits = @($hits | Where-Object { [BitConverter]::ToInt32($b, $_) -eq $Old })
    $newHits = @($hits | Where-Object { [BitConverter]::ToInt32($b, $_) -eq $New })
    if ($oldHits.Count -eq 1 -and $newHits.Count -eq 0) {
        [BitConverter]::GetBytes($New).CopyTo($b, $oldHits[0])
        Set-ItemProperty -Path $File -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
        [System.IO.File]::WriteAllBytes($File, $b)
        return 'applied'
    }
    if ($newHits.Count -eq 1 -and $oldHits.Count -eq 0) { return 'already' }
    return 'error'
}

# Wreckage final Brumak chapter end: three link edits (PrepareMapChange moved to the COMMIT_NextLevel path), see step 31.
$BrumakEndEdits = @(
    @{ Map = 'SP_Wreckage_P.gear'; Old = 404; New = 403
       Pre = '000000000400000000000000f4000000580100000000000012000000000000008e01000000000000010000002e0100000000000012000000000000004400000000000000010000002c0100000000000051010000000000000400000000000000'
       Suf = 'f6000000000000000901000000000000' },
    @{ Map = 'SP_Wreckage_P.gear'; Old = 499; New = 405
       Pre = '9301000000000000010000002e0100000000000012000000000000004400000000000000010000002c0100000000000051010000000000000400000000000000'
       Suf = 'f60000000000000009010000000000000400000000000000000000004b010000000000002a0100000000000013020000000000000d0000000000000009000000' },
    @{ Map = 'SP_Wreckage_01_S_CombatD.gear'; Old = 384; New = 331
       Pre = 'd30000000000000045010000000000000800000000000000'
       Suf = '000000000f010000000000001b000000000000004d010000' }
)

# Wreckage Brumak death stub cines (DeathA = first Brumak, DeathB = final Brumak): no real cine exists. The stub runs CINE_ModeOn ->
# a 4 s matinee (camera pulls back from the Brumak, fades) whose ShowMessage event draws "BRUMAK DEATH STUB" mid-screen -> CINE_ModeOff.
# Edit: SeqEvent_RemoteEvent_33 (CINE_BrumakDeathX_Start) skips ModeOn/matinee/ModeOff and links straight to what followed ModeOff:
# A: ActivateRemoteEvent_6 -> SetBool_0 (SiegeBeastsAreDead -> DropDown cine), B: ActivateRemoteEvent_6 -> ActivateRemoteEvent_4
# (Unload -> COMMIT_NextLevel). Pre starts inside the event export (includes its MaxWidth), so it matches once.
$BrumakStubEdits = @(
    @{ Map = 'SP_Wreckage_Cine_BrumakDeathA.gear'; Old = 24; New = 27
       Pre = '00000000040000000000000025010000a70000000000000001000000000000008e0100000000000001000000900000000000000001000000000000004400000000000000010000008e00000000000000a2000000000000000400000000000000'
       Suf = '710000000000000085000000000000000400000000000000' },
    @{ Map = 'SP_Wreckage_Cine_BrumakDeathB.gear'; Old = 23; New = 21
       Pre = '00000000040000000000000024010000970000000000000001000000000000008e010000000000000100000082000000000000000100000000000000440000000000000001000000800000000000000092000000000000000400000000000000'
       Suf = '640000000000000078000000000000000400000000000000' }
)

# Dev-build console commands left in Kismet (step 36), one link each, both chains end there (no other links):
# - SP_Ravens2_05_S (Abandon Ship) DECK_SWEEP_ENCOUNTER.SLOW_MOTION: Delay_6 (3 s after 'slomo .9') -> ConsoleCommand_7 'slomo .8'
#   becomes -> ConsoleCommand_11 'slomo 1' (existed, unlinked). TimeDilation 0.8 otherwise lasts while GearGame_P stays loaded.
# - SP_Coast_P Level_Startup: Delay_2 -> ConsoleCommand_0 'gamma 3' ("GAMMA HACK!!!! IT IS TOO DARK!!!", default 2.2, stays for
#   the session) becomes -> Log_3 (the hack's log line only).
$DevLeftoverEdits = @(
    @{ Dir = 'SP_01_Ravens'; Map = 'SP_Ravens2_05_S.gear'; Old = 2245; New = 2243
       Pre = '009f050000ae00000000000000d900000000000000000000000000000001110000000000000015030000000000000400000000000000000000008200000000000000d9000000000000000000000000000000008300000000000000d9000000000000000000000000000000001305000000000000dc03000000000000040000000000000038000000f0040000000000000f050000000000004400000000000000e10200000000000002000000460400000000000044000000000000004400000000000000010000004404000000000000fe040000000000000400000000000000'
       Suf = 'b703000000000000dc030000000000000400000000000000' },
    @{ Dir = 'SP_13_Coast'; Map = 'SP_Coast_P.gear'; Old = 395; New = 434
       Pre = '0037e3ffff430000000000000051000000000000000000000000000000001000000000000000fd000000000000000400000000000000000000002f00000000000000510000000000000000000000000000000030000000000000005100000000000000000000000000000000b30100000000000041010000000000000400000000000000380000009f01000000000000b1010000000000002000000000000000e102000000000000020000006e0100000000000020000000000000004400000000000000010000006c01000000000000a8010000000000000400000000000000'
       Suf = '2d0100000000000041010000000000000400000000000000' }
)

# Crater (Act 4 Ch2) lift deck hole fix (step 15): context of the SeqAct_Toggle_1 -> ToggleHidden_4 link (ref 299 -> 249) in SP_Char_Tower_2_elevator_S.gear.
$LiftHoleLinkPre = '0000000000000000002701000000000000cd010000000000000400000000000000170300005c000000000000006b0000000000000000000000000000000103000000000000006f010000000000000400000000000000000000003f000000000000006b0000000000000000000000000000000040000000000000006b000000000000000000000000000000004602000000000000cd01000000000000040000000000000000000000330200000000000043020000000000001c000000000000008e0100000000000001000000f1010000000000001c00000000000000440000000000000001000000ef0100000000000039020000000000000400000000000000'
$LiftHoleLinkSuf = 'b001000000000000cd01000000000000'

# Crater lift ride (step 15): Lift.InterpData_3 Platform PosTrack, keys 20..39 s. The deck dropped 320 units in 0.5 s at 20 s (Gunker lands)
# and 384 units in 1 s at 25 s, faster than characters fall, so Marcus and Jace ended up under the deck and fell down the shaft.
# New: the deck holds at the 20 s height (1968) until 39 s, Z tangents zeroed, then climbs to the top (55 s) as before.
$LiftFlatOld = '405a5443d90100000000000011030000000000000c000000000000003703000000000000c50089bb00000000405a5443c101000000000000a20000000000000008000000000000004101000000000000c4000000000000003302000000000000ce010000000000006f0100000000000004000000000000000000a441440200000000000011030000000000000c00000000000000370300000000000000000000000080bd4000ce441d0000000000000011030000000000000c0000000000000037030000000000000000000000000000852e7fc3d90100000000000011030000000000000c0000000000000037030000000000000000000000000000852e7fc3c101000000000000a20000000000000008000000000000004101000000000000c3000000000000003302000000000000ce010000000000006f0100000000000004000000000000000000c841440200000000000011030000000000000c00000000000000370300000000000000000000000080bd4000ae441d0000000000000011030000000000000c0000000000000037030000000000000000000000000000ba2f973ed90100000000000011030000000000000c000000000000003703000000000000e02d90390000000061374bc0c101000000000000a20000000000000008000000000000004101000000000000c2000000000000003302000000000000ce010000000000006f0100000000000004000000000000000000d041440200000000000011030000000000000c00000000000000370300000000000000000000000080bd80007c441d0000000000000011030000000000000c0000000000000037030000000000007500043d000000005b9714c0d90100000000000011030000000000000c00000000000000370300000000000054e14f3a00000000b16ffa40c101000000000000a20000000000000008000000000000004101000000000000c2000000000000003302000000000000ce010000000000006f0100000000000004000000000000000000e041440200000000000011030000000000000c00000000000000370300000000000000000000000080bd400088441d0000000000000011030000000000000c0000000000000037030000000000000000000000000000398e6341d90100000000000011030000000000000c0000000000000037030000000000000000000000000000398e6341c101000000000000a20000000000000008000000000000004101000000000000c0000000000000003302000000000000ce010000000000006f0100000000000004000000000000000000f441440200000000000011030000000000000c00000000000000370300000000000000000000000080bd400086441d0000000000000011030000000000000c000000000000003703000000000000000000000000000000000000d90100000000000011030000000000000c000000000000003703000000000000000000000000000000000000c101000000000000a20000000000000008000000000000004101000000000000c3000000000000003302000000000000ce010000000000006f01000000000000040000000000000000000842440200000000000011030000000000000c00000000000000370300000000000000000000000080bd40008e441d0000000000000011030000000000000c000000000000003703000000000000000000000000000000000000d90100000000000011030000000000000c000000000000003703000000000000000000000000000000000000c101000000000000a20000000000000008000000000000004101000000000000c3000000000000003302000000000000ce010000000000006f01000000000000040000000000000000001c42440200000000000011030000000000000c00000000000000370300000000000000000000000080bd400086441d0000000000000011030000000000000c000000000000003703000000000000000000000000000000000000d90100000000000011030000000000000c0000000000000037030000000000002768933d000000008462ab3e'
$LiftFlatNew = '00000000d90100000000000011030000000000000c000000000000003703000000000000c50089bb0000000000000000c101000000000000a20000000000000008000000000000004101000000000000c4000000000000003302000000000000ce010000000000006f0100000000000004000000000000000000a441440200000000000011030000000000000c00000000000000370300000000000000000000000080bd4000f6441d0000000000000011030000000000000c000000000000003703000000000000000000000000000000000000d90100000000000011030000000000000c000000000000003703000000000000000000000000000000000000c101000000000000a20000000000000008000000000000004101000000000000c3000000000000003302000000000000ce010000000000006f0100000000000004000000000000000000c841440200000000000011030000000000000c00000000000000370300000000000000000000000080bd4000f6441d0000000000000011030000000000000c000000000000003703000000000000000000000000000000000000d90100000000000011030000000000000c000000000000003703000000000000e02d90390000000000000000c101000000000000a20000000000000008000000000000004101000000000000c2000000000000003302000000000000ce010000000000006f0100000000000004000000000000000000d041440200000000000011030000000000000c00000000000000370300000000000000000000000080bd4000f6441d0000000000000011030000000000000c0000000000000037030000000000007500043d0000000000000000d90100000000000011030000000000000c00000000000000370300000000000054e14f3a0000000000000000c101000000000000a20000000000000008000000000000004101000000000000c2000000000000003302000000000000ce010000000000006f0100000000000004000000000000000000e041440200000000000011030000000000000c00000000000000370300000000000000000000000080bd4000f6441d0000000000000011030000000000000c000000000000003703000000000000000000000000000000000000d90100000000000011030000000000000c000000000000003703000000000000000000000000000000000000c101000000000000a20000000000000008000000000000004101000000000000c0000000000000003302000000000000ce010000000000006f0100000000000004000000000000000000f441440200000000000011030000000000000c00000000000000370300000000000000000000000080bd4000f6441d0000000000000011030000000000000c000000000000003703000000000000000000000000000000000000d90100000000000011030000000000000c000000000000003703000000000000000000000000000000000000c101000000000000a20000000000000008000000000000004101000000000000c3000000000000003302000000000000ce010000000000006f01000000000000040000000000000000000842440200000000000011030000000000000c00000000000000370300000000000000000000000080bd4000f6441d0000000000000011030000000000000c000000000000003703000000000000000000000000000000000000d90100000000000011030000000000000c000000000000003703000000000000000000000000000000000000c101000000000000a20000000000000008000000000000004101000000000000c3000000000000003302000000000000ce010000000000006f01000000000000040000000000000000001c42440200000000000011030000000000000c00000000000000370300000000000000000000000080bd4000f6441d0000000000000011030000000000000c000000000000003703000000000000000000000000000000000000d90100000000000011030000000000000c0000000000000037030000000000002768933d0000000000000000'

# Crater lift (step 15): the counterweight push matinee leaves Marcus and Jace past the lift's east edge (Base = None, measured
# in game) and the lift starts right away without them. The two push-start teleports (Marcus -> PathTargetPoint_4, Jace ->
# PathTargetPoint_56, both on the deck) are moved from before the matinee (redundant there: the matinee places them itself)
# to after it. Links in the CounterWeight sequence (export ref old -> new):
#   Teleport_4 (Anya) -> Teleport_5 (Marcus)        becomes -> HolsterWeapon_1   (291 -> 253)
#   ToggleCinematicMode_1 (off) -> HolsterWeapon_0  becomes -> Teleport_5        (252 -> 291)
#   Teleport_0 (Jace) -> HolsterWeapon_1            becomes -> HolsterWeapon_0   (253 -> 252)
$LiftPushLinks = @(
    @{ Old = 291; New = 253; Pre = '8e0100000000000001000000f1010000000000001c00000000000000440000000000000001000000ef0100000000000039020000000000000400000000000000'
       Suf = 'b001000000000000cd010000000000000400000000000000000000003302000000000000ed0100000000000010030000000000000800000000000000040000004f7574005b000000000000006b0000000000000000000000000000000045000000000000006b0000000000000000000000000000000047000000000000006b00000000000000000000000000000000ef01000000000000390200000000000004000000000000000000000003000000000000006f010000000000000400000000000000000000002701000000000000cd0100000000000004000000000000006a0800005c000000000000006b000000000000000000000000000000003f000000000000006b0000000000000000000000000000000040000000000000006b000000000000000000000000000000004602000000000000cd0100000000000004000000000000000b000000330200000000000035030000000000001c00000000000000610500000000000003000000640100000000000039020000000000000400000000000000b6fffffff0010000000000001c0000000000000008000000000000000100000058010000ed0100000000' },
    @{ Old = 252; New = 291; Pre = '005f0400005c000000000000006b0000000000000000000000000000000103000000000000006f010000000000000400000000000000000000003f000000000000006b0000000000000000000000000000000040000000000000006b000000000000000000000000000000004602000000000000cd01000000000000040000000000000038000000330200000000000043020000000000001c000000000000008e0100000000000001000000f1010000000000001c00000000000000440000000000000001000000ef0100000000000039020000000000000400000000000000'
       Suf = 'b001000000000000cd01000000000000' },
    @{ Old = 253; New = 252; Pre = '8e0100000000000001000000f1010000000000001c00000000000000440000000000000001000000ef0100000000000039020000000000000400000000000000'
       Suf = 'b001000000000000cd010000000000000400000000000000000000003302000000000000ed0100000000000010030000000000000800000000000000040000004f7574005b000000000000006b0000000000000000000000000000000045000000000000006b0000000000000000000000000000000047000000000000006b00000000000000000000000000000000ef01000000000000390200000000000004000000000000000000000003000000000000006f010000000000000400000000000000000000002701000000000000cd0100000000000004000000000000006a0800005c000000000000006b000000000000000000000000000000003f000000000000006b0000000000000000000000000000000040000000000000006b000000000000000000000000000000004602000000000000cd0100000000000004000000000000000b000000330200000000000035030000000000001c00000000000000610500000000000003000000640100000000000039020000000000000400000000000000b6fffffff0010000000000001c000000000000000800000000000000010000005b010000ed0100000000' }
)
# Crater lift (step 15): DynamicBlockingVolume_3, a slab hard-attached under the lift deck, sat 7 units ABOVE the deck surface
# (its twin DynamicBlockingVolume_2 is 9 units below). Anyone who landed on the deck (teleport, matinee end, roll, stumble)
# came to rest on that slab with Base = None (measured in game: Z -7828.9 vs -7836.9 on the deck), so the lift did not carry
# them and they fell down the shaft. The slab is lowered 16 units to its twin's height (Location Z -8192 -> -8208,
# RelativeLocation Z -272 -> -288).
$LiftDbv3Old = '00000000ac00000093000000000000003902000000000000040000000000000006000000f60100000000000011030000000000000c000000000000003703000000000000008033c701004ec5000000c62e00000000000000390200000000000004000000000000004e00000059000000000000006b0000000000000000000000000000000189000000000000006b000000000000000000000000000000011b03000000000000280200000000000008000000000000002d01000000000000850200000000000011030000000000000c000000000000003703000000000000200060c3000088c3000088c3'
$LiftDbv3New = '00000000ac00000093000000000000003902000000000000040000000000000006000000f60100000000000011030000000000000c000000000000003703000000000000008033c701004ec5004000c62e00000000000000390200000000000004000000000000004e00000059000000000000006b0000000000000000000000000000000189000000000000006b000000000000000000000000000000011b03000000000000280200000000000008000000000000002d01000000000000850200000000000011030000000000000c000000000000003703000000000000200060c3000088c3000090c3'
# Crater lift (step 15), the root cause measured in game: BlockingVolume_15 in SP_Char_P.gear (persistent level, box over the east
# strip of the lift where the car is pushed) had its top at Z -7916, 4 units ABOVE the lift deck (-7920). Anyone standing there
# (the two pushers after the car push, anyone loaded from the lift checkpoint) stood on this static volume (Base = BlockingVolume_15)
# instead of the lift, so the lift rose through them and left them behind. The volume is lowered 20 units (Location Z -8040 -> -8060,
# top -7936) so it stays under the deck.
$LiftBv15Old = '92020000000000000c00000000000000c602000000000000002033c7000048c50040fbc5a102000000000000'
$LiftBv15New = '92020000000000000c00000000000000c602000000000000002033c7000048c500e0fbc5a102000000000000'
# Crater lift (step 15): the lift ride is skipped. Even with the squad on the deck the ride kept dropping Marcus and Jace through
# the deck (they ended up inside the deck collision, on the slab volume under it, Base = None, and the rising lift left them).
# When the lift checkpoint finishes (also when that checkpoint is loaded) the game now runs its own dev jump jump_09_tower2_cable
# (SP_Char_P Console_Events: teleport the squad to the cable cutting area at the top, stream the levels, fire jumped_to_09).
# SP_Char_Tower_2_elevator_S.gear: SeqAct_Checkpoint_1 Finished -> Lift.SeqAct_ActivateRemoteEvent_8 instead of starting the
# Lift sequence; that event is renamed STINGER_SCARE_1 -> CompleteObjective_5d; the original CompleteObjective_5d event fired
# before the checkpoint is renamed STINGER_SUCCESS so the jump never starts before the checkpoint is saved.
$LiftSkipElev = @(
    @{ Old = '3302000000000000c50000005c01000000000000280200000000000008000000000000000e03000002000000b1010000000000001c'
       New = '3302000000000000c50000005c0100000000000028020000000000000800000000000000d400000000000000b1010000000000001c' }
    @{ Old = '3302000000000000c80000005c0100000000000028020000000000000800000000000000d400000000000000b10100000000'
       New = '3302000000000000c80000005c01000000000000280200000000000008000000000000000f03000000000000b10100000000' }
    @{ Old = '000000003302000000000000ef010000000000003902000000000000040000000000000044010000b001000000000000cd01'
       New = '000000003302000000000000ef0100000000000039020000000000000400000000000000ce000000b001000000000000cd01' }
)
# SP_Char_P.gear: the CompleteObjective_5d listener (MISSION_OBJECTIVES.SeqEvent_RemoteEvent_3) now links to
# Console_Events.SeqAct_Teleport_74 input 0 (start of jump_09) instead of SeqAct_ManageObjectives_9 input 1.
$LiftSkipPOld = '09010000bb0100000000000024000000000000008e0100000000000001000000720100000000000024000000000000004400000000000000010000007001000000000000b40100000000000004000000000000005502000029010000000000003a01000000000000040000000000000001000000ab010000000000006e'
$LiftSkipPNew = '09010000bb0100000000000024000000000000008e0100000000000001000000720100000000000024000000000000004400000000000000010000007001000000000000b4010000000000000400000000000000f002000029010000000000003a01000000000000040000000000000000000000ab010000000000006e'
# Swaps one exact byte block for another of the same length. Returns 'applied', 'already' or 'error'.
function Set-BlockExact($File, $OldHex, $NewHex) {
    $b = [System.IO.File]::ReadAllBytes($File)
    $old = HexToBytes $OldHex; $new = HexToBytes $NewHex
    $oldHits = [GOW3SemanticByteLocator]::FindSpan($b, $old, [byte[]]@(), 0)
    $newHits = [GOW3SemanticByteLocator]::FindSpan($b, $new, [byte[]]@(), 0)
    if ($oldHits.Count -eq 1 -and $newHits.Count -eq 0) {
        $new.CopyTo($b, $oldHits[0] - $old.Length)
        Set-ItemProperty -Path $File -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
        try { [System.IO.File]::WriteAllBytes($File, $b) } catch { return 'error' }   # e.g. the game is running and holds the map
        return 'applied'
    }
    if ($newHits.Count -eq 1 -and $oldHits.Count -eq 0) { return 'already' }
    return 'error'
}

# Native display size. System.Windows.Forms does not exist in PowerShell on Linux / Wine-less setups, so fall back to xrandr, then 1920x1080.
function Get-NativeResolution {
    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
        $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        if ($b.Width -gt 0 -and $b.Height -gt 0) { return @($b.Width, $b.Height) }
    } catch { }
    try {
        $xr = & xrandr --current 2>$null | Where-Object { $_ -match '\*' } | Select-Object -First 1
        if ($xr -match '(\d{3,5})x(\d{3,5})') { return @([int]$matches[1], [int]$matches[2]) }
    } catch { }
    Say "  [i] Could not detect the native resolution on this system; using 1920x1080. Pick option 2-8 to choose another." 'Yellow'
    return @(1920, 1080)
}

# Triple monitor: with NVIDIA Surround / AMD Eyefinity on, Windows already reports the spanned display as the primary screen.
# Otherwise the bounding box of every monitor (virtual desktop) is used; fullscreen then still needs Surround / Eyefinity, windowed works without.
function Get-SpanResolution {
    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
        $p = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $v = [System.Windows.Forms.SystemInformation]::VirtualScreen
        if ($p.Width / $p.Height -ge 3.5) { return @($p.Width, $p.Height) }
        # Height = the shortest monitor, so the picture stays inside every screen
        $minH = ([System.Windows.Forms.Screen]::AllScreens | ForEach-Object { $_.Bounds.Height } | Measure-Object -Minimum).Minimum
        if ($v.Width -gt 0 -and $minH -gt 0) { return @($v.Width, [int]$minH) }
    } catch { }
    return Get-NativeResolution
}

# Windows MAX_PATH (260): the backup mirrors the full game path under _files\Backups, so long install / tool folder names overflow it. The \\?\ prefix lifts the limit.
function Get-LongPath($Path) {
    if ($Path -match '^[A-Za-z]:\\' -and $Path.Length -ge 240 -and (($PSVersionTable.PSEdition -ne 'Core') -or $IsWindows)) { return '\\?\' + $Path }
    return $Path
}

function Backup($File) {
    if (-not (Test-Path $File)) { return }
    $bak = Get-BackupPath $File
    $bak = Get-LongPath $bak
    if (-not (Test-Path $bak)) {
        $bakDir = Split-Path $bak
        if (-not (Test-Path $bakDir)) { New-Item -ItemType Directory -Force -Path $bakDir | Out-Null }
        Copy-Item (Get-LongPath $File) $bak
        Say "  created backup: $(Split-Path -Leaf $bak) (in _files\Backups)" 'DarkGray'
    }
}

function Restore-AllBackups($Silent = $false, $DeleteBackups = $false) {
    $restoredCount = 0
    if (Test-Path $BackupRoot) {
        $bakRoot = Get-LongPath $BackupRoot
        $allBaks = Get-ChildItem -LiteralPath $bakRoot -Recurse -File
        # Windows: backups are stored as <drive>\path (colon removed). Linux / macOS: stored as the absolute path under the backup root.
        $onWindows = ($PSVersionTable.PSEdition -ne 'Core') -or $IsWindows
        $targets = foreach ($bak in $allBaks) {
            $relPath = $bak.FullName.Substring($bakRoot.Length).TrimStart('\', '/')
            $targetPath = $null
            if ($onWindows) {
                if ($relPath -match "^([A-Za-z])[\\/](.*)") { $targetPath = $matches[1] + ":\" + $matches[2] }
            }
            elseif ($relPath) { $targetPath = '/' + $relPath }
            [pscustomobject]@{ Bak = $bak; Target = $targetPath }
        }
        # The backups keep the game folder they were made in. When the game was moved or copied since (another drive letter,
        # a copied / pre-patched install), restore into the current game folder instead of the old, possibly missing, path.
        $oldGameDir = [string]($targets | ForEach-Object {
                if ($_.Target -and $_.Target -notmatch '[\\/]My Games[\\/]' -and $_.Target -match '^(.*?)[\\/](Binaries|GearGame|Engine)[\\/]') { $matches[1] }
            } | Group-Object | Sort-Object Count -Descending | Select-Object -First 1 -ExpandProperty Name)
        $curGameDir = $GameDir.TrimEnd('\', '/')
        $rebase = $oldGameDir -and ($oldGameDir -ne $curGameDir)
        if ($rebase) { Say "  Backups were made in another game folder ($oldGameDir); restoring them into $curGameDir." 'Yellow' }
        foreach ($t in $targets) {
            $bak = $t.Bak
            $targetPath = $t.Target
            if ($targetPath -and $rebase -and $targetPath.StartsWith($oldGameDir + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
                $targetPath = $curGameDir + $targetPath.Substring($oldGameDir.Length)
            }
            if ($targetPath -and -not (Test-Path -LiteralPath ([System.IO.Path]::GetPathRoot($targetPath)))) {
                Say "  skipped (drive not found): $targetPath" 'Yellow'
                continue
            }
            if ($targetPath) {
                $targetPath = Get-LongPath $targetPath
                $targetDir = Split-Path $targetPath
                if (-not (Test-Path $targetDir)) { New-Item -ItemType Directory -Force -Path $targetDir | Out-Null }
                if (Test-Path $targetPath) { Set-ItemProperty -Path $targetPath -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue }
                Copy-Item $bak.FullName $targetPath -Force
                if (-not $Silent) { Say "  restored: $(Split-Path -Leaf $targetPath)" 'DarkGray' }
                $restoredCount++
            }
        }
        if ($DeleteBackups -and $restoredCount -gt 0) {
            Remove-Item $bakRoot -Recurse -Force
        }
    }
    return $restoredCount
}

function Get-DetectedVideoMemory {
    try {
        $controllers = @(Get-CimInstance -ClassName Win32_VideoController -ErrorAction Stop |
            Where-Object { $_.Name })
        $registryControllers = @(Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Control\Video' -ErrorAction SilentlyContinue |
            ForEach-Object { Get-ItemProperty (Join-Path $_.PSPath '0000') -ErrorAction SilentlyContinue } |
            Where-Object { $_.'HardwareInformation.AdapterString' -and $_.'HardwareInformation.qwMemorySize' })

        foreach ($controller in $controllers) {
            $memoryBytes = 0L
            $registryController = $registryControllers |
            Where-Object { $controller.Name -like "*$($_.'HardwareInformation.AdapterString')*" -or $_.'HardwareInformation.AdapterString' -like "*$($controller.Name)*" } |
            Select-Object -First 1

            if ($registryController) {
                $memoryBytes = [int64]$registryController.'HardwareInformation.qwMemorySize'
            }
            elseif ($controller.AdapterRAM -gt 0) {
                $memoryBytes = [int64]$controller.AdapterRAM
            }

            if ($memoryBytes -le 0) { continue }
            $memoryGb = [math]::Round(($memoryBytes / 1GB), 1)
            [pscustomobject]@{
                Name         = $controller.Name
                MemoryGb     = $memoryGb
                MemorySource = if ($registryController) { 'Windows display-driver registry' } else { 'WMI AdapterRAM (fallback)' }
            }
        }
    }
    catch {
        @()
    }
}

function Get-DetectedStorageDrive($Path) {
    $res = [pscustomobject]@{
        DriveType    = 'ssd'
        BusType      = ''
        FriendlyName = ''
        MediaType    = ''
        Detected     = $false
        Letter       = ''
    }
    if (-not $Path) { $Path = $GameDir }
    if ($IsWin -and $Path -match '^([A-Za-z]):') {
        $letter = $matches[1].ToUpper()
        $res.Letter = $letter
        try {
            $part = Get-Partition -DriveLetter $letter -ErrorAction Stop
            $disk = Get-Disk -Number $part.DiskNumber -ErrorAction Stop
            $res.BusType = [string]$disk.BusType
            $res.FriendlyName = [string]$disk.FriendlyName
            $pdisk = Get-PhysicalDisk -ErrorAction SilentlyContinue | Where-Object { $_.DeviceId -eq $part.DiskNumber }
            if ($pdisk) {
                $res.MediaType = [string]$pdisk.MediaType
                if ($pdisk.MediaType -match 'HDD') {
                    $res.DriveType = 'hdd'
                    $res.Detected = $true
                }
                elseif ($pdisk.MediaType -match 'SSD') {
                    $res.DriveType = 'ssd'
                    $res.Detected = $true
                }
            }
            if (-not $res.Detected) {
                if ($disk.BusType -eq 'NVMe' -or $disk.FriendlyName -match 'SSD|NVMe') {
                    $res.DriveType = 'ssd'
                    $res.Detected = $true
                }
                elseif ($disk.FriendlyName -match 'HDD|WDC|ST\d{3,}|Seagate|Hitachi|Toshiba|SpinPoint') {
                    $res.DriveType = 'hdd'
                    $res.Detected = $true
                }
            }
        }
        catch { }
    }
    return $res
}

function Set-IniSectionValue($Text, $Section, $Key, $Value) {
    $line = "$Key=$Value"
    $secEscaped = [regex]::Escape($Section)
    $keyEscaped = [regex]::Escape($Key)
    $secPattern = "(?m)^\[" + $secEscaped + "\]\r?$"
    if ($Text -match $secPattern) {
        $patternStr = "(?ms)(^\[" + $secEscaped + "\]\r?\n)(.*?)(?=(?m)^\[[^\]\r\n]+\]\r?$|\Z)"
        $secRegex = New-Object System.Text.RegularExpressions.Regex($patternStr)
        $match = $secRegex.Match($Text)
        if ($match.Success) {
            $secBody = $match.Groups[2].Value
            $keyPattern = "(?m)^" + $keyEscaped + "=[^\r\n]*"
            if ($secBody -match $keyPattern) {
                $newSecBody = [regex]::Replace($secBody, $keyPattern, [System.Text.RegularExpressions.MatchEvaluator] { param($m) $line })
            }
            else {
                $newSecBody = $secBody.TrimEnd() + "`r`n$line`r`n"
            }
            $newSectionFull = $match.Groups[1].Value + $newSecBody
            return $Text.Substring(0, $match.Index) + $newSectionFull + $Text.Substring($match.Index + $match.Length)
        }
    }
    return ($Text.TrimEnd() + "`r`n`r`n[$Section]`r`n$line`r`n")
}

function Set-IniValue($Text, $Key, $Value) {
    $line = "$Key=$Value"
    $pattern = "(?m)^" + [regex]::Escape($Key) + '=[^\r\n]*'
    if ($Text -match $pattern) {
        return [regex]::Replace($Text, $pattern, [System.Text.RegularExpressions.MatchEvaluator] { param($match) $line })
    }

    return ($Text.TrimEnd() + "`r`n$line`r`n")
}

# Stock MaxLODSize / LODBias of the gameplay texture groups (GearEngine.ini / DefaultEngine.ini of Jacinto v1.1.1)
$TextureGroupStock = [ordered]@{
    World = @(1024, 0); WorldNormalMap = @(1024, 0); WorldSpecular = @(512, 1)
    Character = @(1024, 0); CharacterNormalMap = @(1024, 0); CharacterSpecular = @(512, 0)
    Weapon = @(1024, 0); WeaponNormalMap = @(1024, 0); WeaponSpecular = @(512, 0)
    Vehicle = @(1024, 0); VehicleNormalMap = @(1024, 0); VehicleSpecular = @(512, 1)
}

function Get-HdSafeTextureGroups {
    # Stock textures have their top mip levels cooked out of the packages. A size cap above the stock value makes the engine ask for
    # them and the game crashes (Assertion failed: Offset != INDEX_NONE). HD Textures Pack v1.3+ gives every such texture a LODBias
    # and its installer writes GearGame\Content\HD_TexturePack_Phase<N>.version. Phase 1 covers the Character / Weapon / Vehicle
    # groups, Phase 1 + Phase 2 the World groups. Same rule as the pack's own HD_TexturePack_Caps.ps1.
    $content = Join-Path $GameDir 'GearGame\Content'
    $installed = foreach ($n in 1, 2) {
        $f = Join-Path $content "HD_TexturePack_Phase$n.version"
        $ok = $false
        if (Test-Path -LiteralPath $f) { try { $ok = ([version]((Get-Content -LiteralPath $f -TotalCount 1).Trim()) -ge [version]'1.3') } catch { $ok = $false } }
        $ok
    }
    $safe = @{}
    foreach ($g in $TextureGroupStock.Keys) { $safe[$g] = if ($g -like 'World*') { $installed[0] -and $installed[1] } else { $installed[0] } }
    return $safe
}

function Set-TextureGroupLimits($Text, $Safe, [int]$MaxLod, [switch]$StockOthers, [switch]$MipLinear) {
    # [SystemSettings] TEXTUREGROUP_* lines of the gameplay texture groups:
    #   safe group (HD pack phase installed): MaxLODSize = $MaxLod (VRAM profile), LODBias = 0
    #   other group: stock MaxLODSize / LODBias with -StockOthers (an older backup may still hold raised caps), else untouched
    #   MipFilter = Linear with -MipLinear (trilinear; Point snaps between mip levels and distant surfaces shimmer)
    $nl = if ($Text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $inSystem = $false
    $out = foreach ($ln in ($Text -split "\r?\n")) {
        if ($ln -match '^\s*\[') { $inSystem = ($ln.Trim() -eq '[SystemSettings]') }
        if ($inSystem -and $ln -match '^TEXTUREGROUP_(\w+)=' -and $TextureGroupStock.Contains($Matches[1])) {
            $g = $Matches[1]
            if ($Safe[$g]) {
                $ln = [regex]::Replace($ln, 'MaxLODSize=\d+', "MaxLODSize=$MaxLod")
                $ln = [regex]::Replace($ln, 'LODBias=-?\d+', 'LODBias=0')
            }
            elseif ($StockOthers) {
                $ln = [regex]::Replace($ln, 'MaxLODSize=\d+', "MaxLODSize=$($TextureGroupStock[$g][0])")
                $ln = [regex]::Replace($ln, 'LODBias=-?\d+', "LODBias=$($TextureGroupStock[$g][1])")
            }
            if ($MipLinear) { $ln = [regex]::Replace($ln, 'MipFilter=\w+', 'MipFilter=Linear', 'IgnoreCase') }
        }
        $ln
    }
    return ($out -join $nl)
}

function Apply-TextureStreamingOptimization {
    Header "23/$StepTotal Texture Streaming & Anti-Aliasing Optimization"

    Say "VRAM (video memory) is the dedicated memory on your graphics card used for textures, frame buffers, and other graphics data." 'White'
    Say "You can check it in Task Manager > Performance > GPU, or in your GPU manufacturer's control panel." 'White'
    Say "The tool will try to detect your GPU automatically, but the displayed value can be approximate on some systems." 'DarkGray'

    $detectedGpus = @(Get-DetectedVideoMemory)
    if ($detectedGpus.Count -gt 0) {
        Say "`nDetected GPU information:" 'Cyan'
        foreach ($gpu in $detectedGpus) {
            Say "  $($gpu.Name) - Dedicated GPU Memory: approximately $($gpu.MemoryGb) GB" 'Cyan'
            Say "    Detection source: $($gpu.MemorySource)" 'DarkGray'
        }
    }
    else {
        Say "`nGPU information could not be detected automatically." 'Yellow'
    }

    $detectedDrive = Get-DetectedStorageDrive $GameDir
    if ($detectedDrive.Detected) {
        Say "`nDetected storage drive for Gears of War 3:" 'Cyan'
        $driveDesc = if ($detectedDrive.FriendlyName) { "$($detectedDrive.FriendlyName) ($($detectedDrive.DriveType.ToUpper()))" } else { "Drive $($detectedDrive.Letter): ($($detectedDrive.DriveType.ToUpper()))" }
        Say "  $driveDesc" 'Cyan'
    }

    # Profile that fits the biggest detected card (the reported size is approximate: 7.9 GB counts as 8 GB). Without detection: 6 GB.
    $detectedGb = ($detectedGpus | Measure-Object -Property MemoryGb -Maximum).Maximum
    $autoChoice = if (-not $detectedGb) { '4' } elseif ($detectedGb -lt 1.5) { '1' } elseif ($detectedGb -lt 3.5) { '2' } elseif ($detectedGb -lt 5.5) { '3' } elseif ($detectedGb -lt 7.5) { '4' } else { '5' }
    $vramLabels = @{ '1' = '1 GB'; '2' = '2 GB'; '3' = '4 GB'; '4' = '6 GB'; '5' = '8 GB and more' }

    Say "`nSelect your graphics card VRAM:" 'White'
    foreach ($k in '1', '2', '3', '4', '5') {
        $tag = if ($k -eq $autoChoice -and $detectedGb) { ' [Detected - press ENTER]' } elseif ($k -eq $autoChoice) { ' [Default - press ENTER]' } else { '' }
        Say "  [$k] $($vramLabels[$k])$tag" $(if ($k -eq $autoChoice) { 'Green' } else { 'Gray' })
    }
    $vramChoice = (Read-Host "Your choice (1-5, ENTER = $($vramLabels[$autoChoice]))").Trim()
    if ($vramChoice -notmatch '^[1-5]$') { $vramChoice = $autoChoice }
    if ($detectedGb -and [int]$vramChoice -gt [int]$autoChoice) {
        # A texture pool bigger than the card makes the driver swap textures to system RAM: stutter, late textures, and with the HD
        # Textures Pack the game can run out of video memory and close.
        Say "  WARNING: $($vramLabels[$vramChoice]) is more than the detected $detectedGb GB of your graphics card." 'Yellow'
        Say "  A texture pool bigger than the card causes stutter and late textures, and with the HD Textures Pack the game can run out of video memory and close." 'Yellow'
        if ((Read-Host "Keep $($vramLabels[$vramChoice]) anyway? [y/N]").Trim() -notmatch '^(y|yes|e|evet)$') {
            $vramChoice = $autoChoice
        }
    }
    Say "  VRAM profile: $($vramLabels[$vramChoice])" 'Green'

    # Storage drive type (SSD vs Mechanical HDD)
    $gsSaved = Get-GameSettings
    $autoDriveChoice = if ($gsSaved.DriveType -eq 'hdd' -or ($detectedDrive.Detected -and $detectedDrive.DriveType -eq 'hdd')) { '2' } else { '1' }
    Say "`nSelect your storage drive type (where Gears of War 3 is installed):" 'White'
    Say "  Mechanical hard drives (HDD) suffer from slow seek times, causing delayed textures and pop-in." 'DarkGray'
    Say "  The HDD profile enables VRAM pre-caching and mip fading so textures stay in video memory and avoid disk seeking." 'DarkGray'
    Say "  [1] SSD / NVMe (Default - Fast texture streaming, stream out distant textures)$(if ($autoDriveChoice -eq '1') { ' [Detected/Default - press ENTER]' } else { '' })" $(if ($autoDriveChoice -eq '1') { 'Green' } else { 'Gray' })
    Say "  [2] HDD (Mechanical - Anti-popin pre-caching, keep textures in VRAM, smoother streaming)$(if ($autoDriveChoice -eq '2') { ' [Detected - press ENTER]' } else { '' })" $(if ($autoDriveChoice -eq '2') { 'Green' } else { 'Gray' })
    $driveInput = (Read-Host "Your choice (1-2, ENTER = $(if ($autoDriveChoice -eq '2') { 'HDD' } else { 'SSD' }))").Trim()
    $driveChoice = if ($driveInput -eq '2') { 'hdd' } elseif ($driveInput -eq '1') { 'ssd' } else { if ($autoDriveChoice -eq '2') { 'hdd' } else { 'ssd' } }
    $isHdd = ($driveChoice -eq 'hdd')
    Say "  Storage drive: $(if ($isHdd) { 'HDD (Mechanical - Anti-popin pre-caching)' } else { 'SSD / NVMe (High-speed streaming)' })" 'Green'
    if ($gsSaved.DriveType -ne $driveChoice) {
        $gsSaved.DriveType = $driveChoice
        Save-GameSettings $gsSaved
    }

    $profiles = @{
        '1' = @{ Label = '1 GB'; PoolSize = 1024; MaxLODSize = 1024; MemoryMargin = 2; HysteresisLimit = 8; MaxAnisotropy = 4; bSmoothFrameRate = 'False'; MaxSmoothedFrameRate = 300; bAllowWriteInvalidate = 'False' }
        '2' = @{ Label = '2 GB'; PoolSize = 2048; MaxLODSize = 2048; MemoryMargin = 3; HysteresisLimit = 10; MaxAnisotropy = 8; bSmoothFrameRate = 'False'; MaxSmoothedFrameRate = 300; bAllowWriteInvalidate = 'True' }
        '3' = @{ Label = '4 GB'; PoolSize = 4096; MaxLODSize = 4096; MemoryMargin = 4; HysteresisLimit = 10; MaxAnisotropy = 16; bSmoothFrameRate = 'False'; MaxSmoothedFrameRate = 300; bAllowWriteInvalidate = 'True' }
        '4' = @{ Label = '6 GB'; PoolSize = 6144; MaxLODSize = 4096; MemoryMargin = 5; HysteresisLimit = 10; MaxAnisotropy = 16; bSmoothFrameRate = 'False'; MaxSmoothedFrameRate = 300; bAllowWriteInvalidate = 'True' }
        '5' = @{ Label = '8 GB and more'; PoolSize = 8192; MaxLODSize = 4096; MemoryMargin = 5; HysteresisLimit = 10; MaxAnisotropy = 16; bSmoothFrameRate = 'False'; MaxSmoothedFrameRate = 300; bAllowWriteInvalidate = 'True' }
    }
    $profile = $profiles[$vramChoice]

    # Section-based engine configuration (Crash-safe, fully uncapped framerate & motion optimized)
    $sectionSettings = [ordered]@{
        'TextureStreaming' = [ordered]@{
            PoolSize                             = $profile.PoolSize
            MemoryMargin                         = $profile.MemoryMargin
            MemoryLoss                           = 0
            HysteresisLimit                      = $profile.HysteresisLimit
            DropMipLevelsLimit                   = $(if ($isHdd) { 24 } else { 16 })
            StopIncreasingLimit                  = $(if ($isHdd) { 16 } else { 12 })
            StopStreamingLimit                   = $(if ($isHdd) { 12 } else { 8 })
            MinEvictSize                         = 10
            MinFudgeFactor                       = 1
            FudgeFactorIncreaseRateOfChange      = 0.5
            FudgeFactorDecreaseRateOfChange      = -0.4
            MinRequestedMipsToConsider           = $(if ($isHdd) { 1 } else { 11 })
            MinTimeToGuaranteeMinMipCount        = $(if ($isHdd) { 12 } else { 2 })
            MaxTimeToGuaranteeMinMipCount        = $(if ($isHdd) { 30 } else { 12 })
            UseTextureFileCache                  = 'FALSE'
            LoadMapTimeLimit                     = $(if ($isHdd) { 60.0 } else { 20.0 })
            LightmapStreamingFactor              = 0.04
            ShadowmapStreamingFactor             = 0.04
            MaxLightmapRadius                    = 2000.0
            AllowStreamingLightmaps              = 'True'
            TextureFileCacheBulkDataAlignment    = 1
            UsePriorityStreaming                 = 'True'
            bAllowSwitchingStreamingSystem       = 'False'
            UseDynamicStreaming                  = 'True'
            bEnableAsyncDefrag                   = 'False'
            bEnableAsyncReallocation             = 'False'
            MaxDefragRelocations                 = 256
            MaxDefragDownShift                   = 128
            BoostPlayerTextures                  = '3.0'
            TemporalAAMemoryReserve              = '4.0'
            bUseBackgroundLevelStreaming         = 'True'
            TimeBetweenPurgingPendingKillObjects = 120
            MaxLODSize                           = $profile.MaxLODSize
        }
        'SystemSettings'   = [ordered]@{
            MaxAnisotropy           = $profile.MaxAnisotropy
            MaxMultiSamples         = '1'
            bAllowD3D9MSAA          = 'False'
            bAllowTemporalAA        = 'False'
            UseVsync                = $(if ($useVsync -eq 'True') { 'True' } else { 'False' })   # the Display step choice; this step runs later and used to force it off
            OneFrameThreadLag       = 'True'
            OnlyStreamInTextures    = $(if ($isHdd) { 'True' } else { 'False' })
            DynamicResolution       = 'False'
            bAllowDynamicResolution = 'False'
            ScreenPercentage        = '100.000000'
            UpscaleScreenPercentage = 'True'
            SkeletalMeshLODBias     = '0'
            StaticMeshLODBias       = '0'
            ParticleLODBias         = '0'
            DetailMode              = '2'
            MaxDrawDistanceScale    = '1.000000'
            bUseMaxQualityMode      = 'False'
        }
        'Engine.Engine'    = [ordered]@{
            bEnableMipLevelFading   = $(if ($isHdd) { 'True' } else { 'False' })
            bUseBackgroundLevelStreaming = 'True'
            bSmoothFrameRate        = 'False'
            MinSmoothedFrameRate    = '0'
            MaxSmoothedFrameRate    = '300'
            MinDesiredFrameRate     = '120.0'
            NetClientTicksPerSecond = '300'
            bAllowWriteInvalidate   = $profile.bAllowWriteInvalidate
        }
        'Engine.GameEngine' = [ordered]@{
            bSmoothFrameRate     = 'False'
            MaxSmoothedFrameRate = '300'
            MaxDeltaTime         = '0.05'
            NetServerMaxTickRate = '300'
            LanServerMaxTickRate = '300'
        }
        'IpDrv.TcpNetDriver' = [ordered]@{
            bClampListenServerTickRate = 'False'
            NetServerMaxTickRate       = '300.0'
            LanServerMaxTickRate       = '300.0'
            MaxClientRate              = '50000'
            MaxInternetClientRate      = '50000'
        }
        'OnlineSubsystemLive.IpNetDriverLive' = [ordered]@{
            NetServerMaxTickRate = '300'
            LanServerMaxTickRate = '300'
        }
        'UnrealEd.EditorEngine' = [ordered]@{
            bSmoothFrameRate     = 'False'
            MinSmoothedFrameRate = '0'
            MaxSmoothedFrameRate = '300'
        }
    }

    # The size caps are raised only for the texture groups that the installed HD Textures Pack v1.3+ phases made safe (no question:
    # an unsafe cap crashes the game, a safe one is what makes the 4K textures visible at all).
    $hdSafe = Get-HdSafeTextureGroups
    $hdRaised = @($TextureGroupStock.Keys | Where-Object { $hdSafe[$_] })
    $hdOldPack = Test-Path -LiteralPath (Join-Path $GameDir 'GearGame\Content\HD_TexturePack.version')

    $baseEngineIni = Join-Path $GameDir "Engine\Config\BaseEngine.ini"
    $streamingIniFiles = @($engineIni, $defEngineIni, $baseEngineIni, $docEngineIni) | Select-Object -Unique
    $updatedCount = 0
    foreach ($iniPath in $streamingIniFiles) {
        if (-not (Test-Path $iniPath)) { continue }
        Backup $iniPath
        $iniText = [System.IO.File]::ReadAllText($iniPath)
        foreach ($sec in $sectionSettings.Keys) {
            $kvMap = $sectionSettings[$sec]
            foreach ($k in $kvMap.Keys) {
                $iniText = Set-IniSectionValue $iniText $sec $k $kvMap[$k]
            }
        }
        # BaseEngine.ini stock caps are 4096 and are overridden by DefaultEngine.ini: only the other files get the stock values back
        $iniText = Set-TextureGroupLimits $iniText $hdSafe $profile.MaxLODSize -MipLinear -StockOthers:($iniPath -ne $baseEngineIni)
        Set-ItemProperty -Path $iniPath -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
        [System.IO.File]::WriteAllText($iniPath, $iniText)
        $updatedCount++
        Say "  updated: $iniPath" 'Green'
    }

    Say "  Texture streaming profile applied: $($profile.Label) VRAM / PoolSize=$($profile.PoolSize) MB / MaxAnisotropy=$($profile.MaxAnisotropy)x" 'Green'
    Say "  Storage drive optimization: $(if ($isHdd) { 'HDD (Mechanical - Anti-popin VRAM pre-caching, OnlyStreamInTextures=True)' } else { 'SSD / NVMe (High-speed streaming, OnlyStreamInTextures=False)' })" 'Green'
    if ($hdRaised.Count -gt 0) {
        $hdWhich = if ($hdRaised.Count -eq $TextureGroupStock.Count) { 'World, Character, Weapon and Vehicle' } else { 'Character, Weapon and Vehicle (World needs HD Textures Pack Phase 2 too)' }
        Say "  HD Textures Pack v1.3+ detected: $hdWhich texture groups use MaxLODSize=$($profile.MaxLODSize), LODBias=0. MipFilter=Linear." 'Green'
    }
    else {
        Say "  Texture group size caps stay at the stock values, MipFilter=Linear is applied." 'Green'
        if ($hdOldPack) {
            Say "  HD Textures Pack v1.2 or older detected: update it to v1.3 or newer. Older packs cannot unlock the 4K sizes safely, so the HD textures show at the stock 1024 size." 'Yellow'
        }
    }
    Say "  High framerate unlocked (Unlimited FPS, bClampListenServerTickRate=False, MaxFPS=300, D3D9MSAA=False)." 'Cyan'
    if ([int]$vramChoice -ge 4) {
        Say "  With 6 GB or more of VRAM, the HD Textures Pack should work smoothly without motion shimmering." 'Cyan'
        Say "  The HD Textures Pack can be downloaded from Nexus Mods:" 'Cyan'
        Say "  https://www.nexusmods.com/gearsofwar3/mods/28" 'Yellow'
        Say "  Use HD Textures Pack v1.3 or newer: install it before this tool, or run this installer again after installing / updating it." 'Cyan'
    }
    if ($updatedCount -eq 0) {
        Say "  No engine INI files were found, so no streaming settings were changed." 'Yellow'
    }

}

function Install-Multipatch {

    $warningsList = [System.Collections.Generic.List[string]]::new()
    # Data files shipped in the NexusMods package (_files). The Remote option only updates this script, not these files.
    $packageFilesBad = [System.Collections.Generic.List[string]]::new()
    foreach ($pf in @('GearGame.u', 'Open_Logos.bik', 'WarfareFonts.upk', 'WarfareFonts2x.upk', 'SP_Badlands_13_S.gear', 'SP_Badlands_Cine_Corpser_Outro.gear', 'SP_Badlands_Cine_EggRoom.gear', 'SP_Badlands2_P.gear', 'CorpserRestor_Audio.upk', 'CorpserRestor_BattleAudio.upk', 'CorpserRestor_InterwaveFX.upk', 'CorpserRestor_PillarChunks.upk', 'SP_Mercy_Cine_FoundPump.gear', 'SP_Char_GondolaRide.gear', 'wsock32.dll', 'steam_api64.dll')) {
        if (-not (Test-Path (Join-Path $FilesDir $pf))) { $packageFilesBad.Add($pf) }
    }
    if ($packageFilesBad.Count -gt 0) {
        Write-Host ""
        Say "  [!] Missing files in the _files folder: $($packageFilesBad -join ', ')" 'Red'
        Say "      The related fixes will be skipped. Download the complete package again from NexusMods (see the warning at the end)." 'Red'
    }

    if ((Test-Path $BackupRoot) -and (Get-ChildItem -Path $BackupRoot -Recurse -File -ErrorAction SilentlyContinue)) {
        Say "  Detected existing installation backups. Restoring original clean state before applying patches..." 'DarkCyan'
        $restored = Restore-AllBackups $true $true
        Say "  Original clean state restored ($restored files). Proceeding with fresh patch installation.`n" 'Green'
    }

    Header "Online Co-op"
    Say "Enable online co-op through Steam (invite Steam friends to your Campaign)? [Y/n] (Press ENTER for yes):" 'White'
    Say "  Steam must be running; both players need this option and the same tool version. Friends see the game as 'Spacewar'." 'DarkGray'
    Say "  Host: start the Campaign, then press F8 in game (or Shift+Tab > Friend > Invite). The friend accepts while his game is in the main menu." 'DarkGray'
    $steamCoopInput = (Read-Host "Steam online co-op").Trim()
    $steamCoop = -not ($steamCoopInput -match '^(n|no|h|hayir|hayır)$')
    if ($steamCoop) { Say "  Steam online co-op: on" 'Cyan' } else { Say "  Steam online co-op: off (LAN co-op still works: open <host IP>:7777 in the console)" 'DarkGray' }
    $offlineMode = $false
    if (-not $steamCoop) {
        Write-Host ""
        Say "Switch the Campaign to full offline mode? [y/N] (Press ENTER for no):" 'White'
        Say "  Offline mode turns the Campaign listen server off: the game no longer opens network port 7777 when a chapter starts." 'DarkGray'
        Say "  Nobody can join your Campaign any more: no LAN co-op (open <IP>:7777) and no Steam invites. Split-screen co-op on this PC still works." 'DarkGray'
        Say "  Choose it only if you always play alone or split-screen. Run the installer again and answer N to get co-op back." 'DarkGray'
        $offlineInput = (Read-Host "Full offline mode").Trim()
        $offlineMode = $offlineInput -match '^(y|yes|e|evet)$'
        if ($offlineMode) { Say "  Campaign: offline only (listen server off)" 'Cyan' } else { Say "  Campaign: listen server stays on (LAN co-op possible)" 'DarkGray' }
    }

    Header "1/$StepTotal Game Settings (Player Name, Language, Display, FOV, Mouse, Subtitles, Graphics)"
    # Chosen in the main menu (5. Game Settings) and saved in graphics.json; a reinstall restores the clean files first,
    # so they are applied again below (Apply-GameSettings, after the engine INI fixes).
    $gs = Get-GameSettings
    $res = Get-GsResolution $gs; $resX = $res[0]; $resY = $res[1]
    $selectedLang = $gs.Language
    $useVsync = if ($gs.VSync) { 'True' } else { 'False' }   # also used by the Texture Streaming step
    $disableRunShake = $gs.DisableRunShake
    foreach ($l in (Get-GameSettingsLines $gs)) { Say ("  {0,-44} {1}" -f $l[0], $l[1]) 'Cyan' }
    Say ("  {0,-44} {1}" -f 'Post-process effects disabled', $gs.Disabled.Count) 'Cyan'
    foreach ($note in (Get-GsLanguageNote $selectedLang)) { Say "  [i] $note" 'Cyan' }
    Say "  Change them in the main menu: 5. Game Settings (they apply at once, no reinstall needed)." 'DarkGray'

    Header "2/$StepTotal HUD Scaling"
    Say "HUD Scaling (Game HUD size on high resolutions)" 'White'
    Say "  The game HUD (objective text at the top left, weapon / ammo box, crosshair, squad icons ...) is drawn in fixed pixels designed for 720p." 'Gray'
    Say "  [Y] Install (recommended): the HUD scales with your resolution, so its elements look BIGGER on 1080p / 1440p / 4K (same size as on 720p, relative to the screen)." 'Gray'
    Say "  [N] Do not install: the HUD keeps its original pixel size, so the higher your screen resolution, the SMALLER the HUD elements become." 'Gray'
    $hudInput = (Read-Host "Install HUD Scaling? [Y/n] (Press ENTER for Yes)").Trim()
    $hudScale = -not ($hudInput -match '^(n|no|h|hayir|hayır)$')
    Say "  HUD Scaling: $(if ($hudScale) { 'install' } else { 'skip (original HUD size)' })" 'Cyan'



    # INI Fixes

    $engineIni = Join-Path $ConfigDir "GearEngine.ini"
    $defEngineIni = Join-Path $ConfigDir "DefaultEngine.ini"
    $baseEngineIni = Join-Path $GameDir "Engine\Config\BaseEngine.ini"
    $gameIni = Join-Path $ConfigDir "GearGame.ini"
    $inputIni = Join-Path $ConfigDir "GearInput.ini"
    $defInputIni = Join-Path $ConfigDir "DefaultInput.ini"
    $camIni = Join-Path $ConfigDir "GearCamera.ini"
    $defCamIni = Join-Path $ConfigDir "DefaultCamera.ini"

    $docDir = Join-Path ([Environment]::GetFolderPath("MyDocuments")) "My Games\Gears of War 3\GearGame\Config"
    $docEngineIni = Join-Path $docDir "GearEngine.ini"
    $docInputIni = Join-Path $docDir "GearInput.ini"
    $docGameIni = Join-Path $docDir "GearGame.ini"
    $docCamIni = Join-Path $docDir "GearCamera.ini"

    Backup $engineIni; Backup $defEngineIni; Backup $baseEngineIni; Backup $gameIni; Backup $inputIni; Backup $defInputIni; Backup $camIni; Backup $defCamIni
    Backup $docEngineIni; Backup $docInputIni; Backup $docGameIni; Backup $docCamIni

    # Engine & System Config (Apply to GearEngine.ini, DefaultEngine.ini, BaseEngine.ini, and Documents INI)

    foreach ($iniPath in @($engineIni, $defEngineIni, $baseEngineIni, $docEngineIni)) {

        if (Test-Path $iniPath) {

            $txt = [System.IO.File]::ReadAllText($iniPath)
            # Epic's internal perf database (production-db): every launch waits ~18 s for the connection to time out before loading continues.
            $txt = [regex]::Replace($txt, '(?m)^bUseTaskPerfTracking=[^\r\n]*', 'bUseTaskPerfTracking=FALSE')
            $txt = [regex]::Replace($txt, '(?m)^bUseTaskPerfMemDatabase=[^\r\n]*', 'bUseTaskPerfMemDatabase=FALSE')
            # [URL] Port=1000 is a privileged port on Linux / SteamOS (Proton): the bind fails, profile loading hangs / "connection error". 7777 works everywhere.
            $txt = [regex]::Replace($txt, '(?m)^Port=1000(?=\r?$)', 'Port=7777')
            # The exe has no Live net driver: OnlineSubsystemLive.IpNetDriverLive fails to load ('Failed to find object' at every map load)
            # and the engine falls back to IpDrv.TcpNetDriver anyway. Use it directly (same networking, no warning).
            $txt = [regex]::Replace($txt, '(?m)^NetworkDevice="?OnlineSubsystemLive\.IpNetDriverLive"?(?=\r?$)', 'NetworkDevice=IpDrv.TcpNetDriver')

            Set-ItemProperty -Path $iniPath -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue

            [System.IO.File]::WriteAllText($iniPath, $txt)

        }

    }

    # Game Settings: resolution, display mode, language, FOV, mouse sensitivity, player name, subtitles, VSync, effects, run shake, launchers
    $gsFiles = Apply-GameSettings $gs
    Say "  ok  Game Settings applied ($gsFiles file(s) checked)." 'Green'

    # High DPI

    try {

        $regPath = "HKCU:\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"

        if (-not (Test-Path $regPath)) { New-Item -Path $regPath -Force | Out-Null }

        Set-ItemProperty -Path $regPath -Name (Join-Path $BinariesDir "GearGame.exe") -Value "~ HIGHDPIAWARE" -ErrorAction SilentlyContinue

    }
    catch {}


    # The game compiles its Trioviz (3D TV) shaders at start with D3DCOMPILER_43.dll and loads d3dx9_43.dll (DirectX June 2010 runtime, not shipped
    # with the game). Without them it stops with "Fatal error! __DW_D3D9_DwTriovizCreatePixelShader".
    if ($IsWin) {
        $dxMissing = @('D3DCOMPILER_43.dll', 'd3dx9_43.dll') | Where-Object {
            -not (Test-Path (Join-Path $env:windir "System32\$_")) -and -not (Test-Path (Join-Path $BinariesDir $_))
        }
        if ($dxMissing) {
            Say "  WARNING: $($dxMissing -join ', ') not found. The game needs the DirectX June 2010 runtime; without it it can stop at start with 'Fatal error! DwTriovizCreatePixelShader'." 'Yellow'
            Say "  Install 'DirectX End-User Runtime Web Installer' from Microsoft (https://www.microsoft.com/en-us/download/details.aspx?id=35) and start the game again." 'Yellow'
            $warningsList.Add("DirectX June 2010 runtime missing: $($dxMissing -join ', ')")
        }
    }

    Header "3/$StepTotal UI Menu Input Preference (Video Settings / Credits / War Journal)"
    Say "Which input method will you primarily use?" 'White'
    Say "  [1] Keyboard & Mouse (Applies UI ESC/Enter Fix - Breaks Gamepad B in specific menus)"
    Say "  [2] Gamepad (Default UI Controls - ESC/Enter won't work in specific menus)"
    $inputPref = Read-Host "Your choice (1-2, Default: 1)"
    if ([string]::IsNullOrWhiteSpace($inputPref)) { $inputPref = '1' }

    Header "4/$StepTotal ActionScript UI Scaling Patch (Widescreen UI Fixes)"

    $upkList = @(
        'UI_FrontEnd.upk', 'UI_PauseMenu.upk', 'UI_Options.upk',
        'UI_Common.upk', 'UI_Scoreboards.upk', 'UI_Multiplayer.upk',
        'UI_InGame.upk', 'UI_WarJournal.upk', 'UI_PartyPanel.upk',
        'UI_Gearstart.upk', 'UI_Horde.upk', 'UI_Medals.upk',
        'UI_CharacterPortraits.upk', 'UI_Sounds.upk'
    )

    $patterns = @(
        @{ Search = HexToStr '7363616c654d6f6465006e6f5363616c6500616c69676e00'; Replace = HexToStr '7363616c654d6f64650073686f77416c6c00616c69676e00' },
        @{ Search = HexToStr '7363616c654d6f64650073686f77416c6c00616c69676e00'; Replace = HexToStr '7363616c654d6f64650073686f77416c6c00616c69676e00' },
        @{ Search = HexToStr '5374616765007363616c654d6f6465006e6f5363616c6500'; Replace = HexToStr '5374616765007363616c654d6f64650073686f77416c6c00' },
        @{ Search = HexToStr '5374616765001c961400007363616c654d6f646500006e6f5363616c6500'; Replace = HexToStr '5374616765001c961400007363616c654d6f6465000073686f77416c6c00' },
        @{ Search = HexToStr '1d961400007363616c654d6f646500006e6f5363616c6500'; Replace = HexToStr '1d961400007363616c654d6f6465000073686f77416c6c00' }
    )

    $patchCount = 0

    foreach ($upk in $upkList) {

        $path = Join-Path $GameDir "GearGame\Content\GFx\$upk"

        if (Test-Path $path) {

            Backup $path

            $bytes = [System.IO.File]::ReadAllBytes($path)

            $content = $iso88591.GetString($bytes)

            $changed = $false

            foreach ($p in $patterns) {

                if ($content.Contains($p.Search)) {

                    $content = $content.Replace($p.Search, $p.Replace)
                    $changed = $true
                }

            }

            if ($changed) {

                [System.IO.File]::WriteAllBytes($path, $iso88591.GetBytes($content))

                $patchCount++

                Say "  patched: $upk" 'Green'

            }

        }

    }

    if ($patchCount -eq 0) { Say "  UI UPKs were already patched." 'Yellow' }

    # Add Mouse Cursor visibility to all UI Menus (DryreL)
    foreach ($ini in @( (Join-Path $ConfigDir "DefaultUI.ini"), (Join-Path $ConfigDir "GearUI.ini") )) {
        if (Test-Path $ini) {
            Backup $ini
            $iniTxt = [System.IO.File]::ReadAllText($ini)
            if ($iniTxt -notmatch '(?m)^bEnableMouse=') {
                if ($iniTxt -match '(?m)^\[GFxUI\.GFxMoviePlayer\]\r?$') {
                    $iniTxt = [regex]::Replace($iniTxt, '(?m)^\[GFxUI\.GFxMoviePlayer\]\r?$', "[GFxUI.GFxMoviePlayer]`r`nbEnableMouse=True`r`nbCaptureMouseInput=True")
                }
                else {
                    $iniTxt += "`r`n[GFxUI.GFxMoviePlayer]`r`nbEnableMouse=True`r`nbCaptureMouseInput=True`r`n"
                }
                [System.IO.File]::WriteAllText($ini, $iniTxt)
            }
        }
    }

    # --------------------------------------------------------
    Say "  ok  Mouse cursor UI injection applied." 'Green'

    # Custom Fix for MissionFailed.swf (Stretch to 21:9)
    # --------------------------------------------------------
    $pauseMenu = Join-Path $GameDir "GearGame\Content\GFx\UI_PauseMenu.upk"
    if (Test-Path $pauseMenu) {
        $iso88591 = [System.Text.Encoding]::GetEncoding("ISO-8859-1")
        $pauseBytes = [System.IO.File]::ReadAllBytes($pauseMenu)
      
        $pat1 = HexToStr '7363616c654d6f64650073686f77416c6c00616c69676e00'
        $rep1 = HexToStr '7363616c654d6f6465006578616374466974006c69676e00'
        $pat2 = HexToStr '5374616765001c961400007363616c654d6f6465000073686f77416c6c004f'
        $rep2 = HexToStr '5374616765001c960100058f9602000801960900006578616374466974004f'
      
        $pauseBytesStr = $iso88591.GetString($pauseBytes)
      
        $idx1 = $pauseBytesStr.IndexOf($pat1)
        if ($idx1 -ge 0) {
            $idx1_2 = $pauseBytesStr.IndexOf($pat1, $idx1 + 1)
            if ($idx1_2 -ge 0) {
                $pauseBytesStr = $pauseBytesStr.Remove($idx1_2, $pat1.Length).Insert($idx1_2, $rep1)
            }
        }
      
        $idx2 = $pauseBytesStr.IndexOf($pat2)
        if ($idx2 -ge 0) {
            $idx2_2 = $pauseBytesStr.IndexOf($pat2, $idx2 + 1)
            if ($idx2_2 -ge 0) {
                $pauseBytesStr = $pauseBytesStr.Remove($idx2_2, $pat2.Length).Insert($idx2_2, $rep2)
            }
        }
      
        [System.IO.File]::WriteAllBytes($pauseMenu, $iso88591.GetBytes($pauseBytesStr))
        Say "  ok  MissionFailed.swf UI stretching fix applied." 'Green'
    }

    Header "5/$StepTotal Input and Hotkey Configurations"

    $bypassCmd = "servertravel"

    foreach ($ini in @($defInputIni, $inputIni, $docInputIni)) {

        if (Test-Path $ini) {

            $txt = [System.IO.File]::ReadAllText($ini)

            # 1. Esc bind fix for pause (refresh50)

            $escWrong = 'CloseEditorViewport \| ButtonPress Back \| OnRelease ButtonRelease Back'

            $txt = [regex]::Replace($txt, '(?m)^([ \t]*)([.+]?)Bindings=\(Name="Escape",Command="' + $escWrong + '"\)[ \t]*\r?$', '${1}${2}Bindings=(Name="Escape",Command="GBA_Pause")')

            # 2. F10 -> servertravel bypass (DryreL)
            $txt = [regex]::Replace($txt, '(?m)^[ \t]*[\.\+]?Bindings=\(Name="F10".*\r?\n?', '')
            $txt = [regex]::Replace($txt, '(?m)^[ \t]*[\.\+]?Bindings=\(Name="F9".*\r?\n?', '')
            $txt = [regex]::Replace($txt, '(?m)^[ \t]*[\.\+]?Bindings=\(Name="F1".*\r?\n?', '')
            $bindStr = "Bindings=(Name=`"F10`",Command=`"$bypassCmd`")"
            $txt = [regex]::Replace($txt, '(?m)^\[Engine\.PlayerInput\]\r?$', "[Engine.PlayerInput]`r`n$bindStr")

            # Cutscene skip: the stock SpaceBar binding is "GBA_MoveAction | OnRelease CancelCine 2.0", so letting go of Space (run /
            # roadie run) 2 s into a cutscene skipped it. The skip moves to its own key, Enter (Epic's own commented-out binding).
            $txt = [regex]::Replace($txt, '(?m)^([ \t]*[.+]?)Bindings=\(Name="(\w+)",Command="([^"]*?) \| OnRelease CancelCine 2\.0"\)', '${1}Bindings=(Name="${2}",Command="${3}")' + "`r`n" + '${1}Bindings=(Name="Enter",Command="OnRelease CancelCine 2.0")')

            # Remove the broken RightThumbStick hack from previous run (DryreL)

            $txt = [regex]::Replace($txt, '(?m)^[ \t]*[.+]?Bindings=\(Name="XboxTypeS_RightThumbStick".*\r?\n?', '')

            $txt = [regex]::Replace($txt, '(?m)^[ \t]*Enter=XboxTypeS_RightThumbStick\r?\n?', '')

            # 3. GFxUI Gamepad Map & FullKeyboard=1 (Enables ESC/Enter/Space across all UI screens) (DryreL)

            $txt = $txt -replace 'FullKeyboard=0', 'FullKeyboard=1'

            $keyMapBlock = "[GFxUI.KeyMap]`nEnter=GAMEPAD_A`nSpaceBar=GAMEPAD_A`nSpace=GAMEPAD_A`nTab=START`nEscape=GAMEPAD_B`nBackSpace=GAMEPAD_B`nG=GAMEPAD_L1`nR=GAMEPAD_R1"

            $txt = [regex]::Replace($txt, '\[GFxUI\.KeyMap\]\r?\n(Enter=GAMEPAD_A\r?\n)?(SpaceBar=.*\r?\n)?(Space=.*\r?\n)?(Tab=.*\r?\n)?(Escape=.*\r?\n)?(BackSpace=.*\r?\n)?(G=.*\r?\n)?(R=.*\r?\n)?', "$keyMapBlock`n")

            # 4. Analog Look Y (Pitch) Inversion Fix (DryreL)

            $txt = $txt -replace 'Axis aLookup Speed=0\.8', 'Axis aLookup Speed=-0.8'

            # 5. Patch InputAliases for PC (PlatformInputKeys[2]) (DryreL)

            $lines = $txt -split "`r`n"

            $newLines = @()

            foreach ($line in $lines) {

                if ($line.Trim().StartsWith('InputAliases=')) {

                    if ($line -match 'Cancel|Back|No|Leave|Stay') {

                        $line = [regex]::Replace($line, 'PlatformInputKeys\[2\]=\(InputKeyData=\(InputKeyName="[^"]+"\)', 'PlatformInputKeys[2]=(InputKeyData=(InputKeyName="Escape")')

                    }
                    elseif ($line -match 'Accept|Continue|Yes|Overwrite|SelectDevice|ChooseProfile|PlaySplitScreen|Kick|Retry') {

                        $line = [regex]::Replace($line, 'PlatformInputKeys\[2\]=\(InputKeyData=\(InputKeyName="[^"]+"\)', 'PlatformInputKeys[2]=(InputKeyData=(InputKeyName="Enter")')

                    }

                }

                $newLines += $line

            }

            $txt = $newLines -join "`r`n"

            Set-ItemProperty -Path $ini -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue

            [System.IO.File]::WriteAllText($ini, $txt)

            Say "  ok  Title Screen Bypass & UI Input fixes applied to $(Split-Path -Leaf $ini)." 'Green'

        }

    }

    # Keybinds saved with the Keybind Editor survive a reinstall (the clean ini backups are restored first)
    $savedKb = Get-SavedKeybinds
    if ($savedKb.Count -gt 0) {
        [void](Save-KbBindings $savedKb)
        Say "  ok  Saved custom keybinds re-applied ($($savedKb.Count) action(s))." 'Green'
    }

    Header "6/$StepTotal FIXES 1.0 Asset Replacements"

    $uScript = Join-Path $GameDir "GearGame\Script\GearGame.u"
    $srcU = Join-Path $FilesDir "GearGame.u"
    if (Test-Path $srcU) {
        Backup $uScript
        Set-ItemProperty -Path $uScript -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
        Copy-Item $srcU $uScript -Force
        Say "  ok  GearGame.u replaced (Debug Spam Fix, Profile Lock Fix, Brightness Screen Fix)." 'Green'
        $U_Path = Join-Path $GameDir "GearGame\Script\GearGame.u"
        # GearGame.u was just copied fresh from _files, so the Gamepad choice needs nothing restored. Older versions kept a
        # GearGame.u.bak made on the first Keyboard install and copied it back for Gamepad, which put an outdated GearGame.u
        # (without the later _files fixes) in place for every gamepad user. That stale file is removed.
        $U_Bak = $U_Path + ".bak"
        if (Test-Path $U_Bak) { Remove-Item $U_Bak -Force -ErrorAction SilentlyContinue }
    
        if ($inputPref -eq '1') {
            if (Test-Path $U_Path) {
                Set-ItemProperty -Path $U_Path -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
            
                $bytes = [System.IO.File]::ReadAllBytes($U_Path)
    
                $patches = @(
                    @{ S = HexToBytes "07000000546573744D6500"; R = HexToBytes "0700000045736361706500" },
                    @{ S = HexToBytes "2113BD000000000000"; R = HexToBytes "212BB0000000000000" },
                    @{ S = HexToBytes "2123BD000000000000"; R = HexToBytes "21B04A000000000000" },
                    # XboxTypeS_X -> E: the UI scenes only react to the X button name, a keyboard press arrives as 'E' (Players/Friends, Kick, Refresh ...)
                    @{ S = HexToBytes "2124BD000000000000"; R = HexToBytes "21E744000000000000" },
                    # Stats screens (UIScene_PersonalStats / UIScene_PostGameStats): LB/RB page switching -> G / R (the on-screen key icons).
                    # Only these four spots; GearPC.UpdateControllerConfig also names the shoulder buttons and must stay on the gamepad names.
                    @{ S = (HexToBytes ("2119BD0000000000001B1133000000000000" + "1DFFFFFFFF")); R = (HexToBytes ("21A7520000000000001B1133000000000000" + "1DFFFFFFFF")) },
                    @{ S = (HexToBytes ("211EBD0000000000001B1133000000000000" + "26")); R = (HexToBytes ("21D9950000000000001B1133000000000000" + "26")) },
                    @{ S = (HexToBytes ("2119BD000000000000161B0F33000000000000" + "28")); R = (HexToBytes ("21A752000000000000161B0F33000000000000" + "28")) },
                    @{ S = (HexToBytes ("211EBD000000000000161B0F33000000000000" + "27")); R = (HexToBytes ("21D995000000000000161B0F33000000000000" + "27")) }
                )
            
                $totalReplaced = 0
                foreach ($patch in $patches) {
                    $totalReplaced += [GOW3SemanticByteLocator]::Replace($bytes, $patch.S, $patch.R)
                }
    
                [System.IO.File]::WriteAllBytes($U_Path, $bytes)
            
                if ($totalReplaced -gt 0) {
                    Say "  ok  GearGame.u patched for Keyboard (ESC/Enter) successfully!" 'Green'
                }
                else {
                    Say "  ok  GearGame.u was already patched for Keyboard (ESC/Enter)." 'Yellow'
                }
            }
            else {
                Say "  error GearGame.u not found!" 'Red'
            }
        }
        else {
            Say "  ok  Default GearGame.u kept for Gamepad controls." 'Green'
        }

        # Cheats: AllWeapons / Loaded / UnlimitedAmmo / UnlimitedClips return early when NetMode != NM_Standalone unless 'MPCheats' was
        # typed, and the campaign always runs as a listen server (?listen, for co-op), so they silently did nothing. The guard becomes
        # NetMode > NM_ListenServer (only a co-op client is still refused): native 155 (!=) -> 151 (>), byte constant 0 -> 2.
        if (Test-Path $U_Path) {
            $bytes = [System.IO.File]::ReadAllBytes($U_Path)
            $cheatGuard = '383a191901d2eeffff0900c4eeffff0001c4eeffff090029fcffff000129fcffff383a24'
            $cheatTail = '16180d00812d015e5a00001616'
            $cheatHits = [GOW3SemanticByteLocator]::Replace($bytes, (HexToBytes ('9b' + $cheatGuard + '00' + $cheatTail)), (HexToBytes ('97' + $cheatGuard + '02' + $cheatTail)))
            if ($cheatHits -gt 0) {
                Set-ItemProperty -Path $U_Path -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
                [System.IO.File]::WriteAllBytes($U_Path, $bytes)
                Say "  ok  Ammo / weapon cheats enabled in the campaign (Loaded, AllWeapons, UnlimitedAmmo, UnlimitedClips: $cheatHits)." 'Green'
            }
            else { Say "  skip  Ammo cheat guard not found in GearGame.u." 'Yellow' }
        }


    }
    else {
        Say "  skip  _files\GearGame.u not found! Title Screen Bypass skipped." 'Yellow'
        $warningsList.Add("Title Screen Bypass: _files\GearGame.u not found")
    }

    # Keyboard & Mouse: replace the gamepad button icons shown in the game UI with keyboard key icons (Warfare_HUD.upk by Hpapa).
    # Gamepad: nothing to do, the original file is restored with the other backups at the start of every install.
    $hudDst = Join-Path $GameDir "GearGame\Content\Interface\Warfare_HUD.upk"
    if ($inputPref -eq '1') {
        $hudSrc = Join-Path $FilesDir "Warfare_HUD.upk"
        if ((Test-Path $hudSrc) -and (Test-Path $hudDst)) {
            Backup $hudDst
            Set-ItemProperty -Path $hudDst -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
            Copy-Item $hudSrc $hudDst -Force
            Say "  ok  Keyboard & Mouse button icons installed (Warfare_HUD.upk, by Hpapa)." 'Green'
        }
        else {
            Say "  skip  _files\Warfare_HUD.upk or GearGame\Content\Interface\Warfare_HUD.upk not found! Keyboard icons skipped." 'Yellow'
            $warningsList.Add("Keyboard Icons: Warfare_HUD.upk missing")
            $packageFilesBad.Add('Warfare_HUD.upk')
        }
    }
    else {
        Say "  ok  Default gamepad button icons kept." 'Green'
    }

    $srcLogo = Join-Path $FilesDir "Open_Logos.bik"
    $logoPath = Join-Path $GameDir "GearGame\Movies\Open_Logos.bik"
    if (Test-Path $srcLogo) {
        if (Test-Path $logoPath) { Backup $logoPath }
        Copy-Item $srcLogo $logoPath -Force
        Say "  ok  Epic/MGS Intro Video restored (Bink 1 video format)." 'Green'
    }
    else {
        Say "  skip  _files\Open_Logos.bik not found! Intro video skipped." 'Yellow'
        $warningsList.Add("Intro Video: _files\Open_Logos.bik not found")
    }

    Header "7/$StepTotal Subtitles"
    # Subtitle on / off and size (font package + SubtitleFontName) are Game Settings, applied in step 1/2 with Apply-GameSettings.
    Say "  ok  Subtitles $(if ($gs.Subtitles) { 'on (in-game option decides)' } else { 'forced off' }), size: $($GsSubtitleSizes[$gs.SubtitleSize])" 'Green'

    # The Jacinto HD movies were re-cut, but their subtitle timing files (GearGame\Movies\<movie>.txt: fps line, then
    # "<start frame> <end frame> <Subtitles.int key>") still hold the 2011 timing. Shift every line earlier by the measured lead-in
    # difference. FirstStart = first start frame of the stock file, so a file that is already shifted (or custom) is left alone.
    $movieSubShifts = @(
        # Measured with speech recognition against the movie dialogue; Tower_Outro (left as is) reads ~1 s late with the same method.
        @{ Movie = 'Azura_Intro'; FirstStart = 1231; Frames = 165 }      # ~6 s late
        @{ Movie = 'Wreckage_Outro'; FirstStart = 120; Frames = 45 }     # ~2.6 s late
    )
    foreach ($ms in $movieSubShifts) {
        $subTxt = Join-Path $GameDir "GearGame\Movies\$($ms.Movie).txt"
        if (-not (Test-Path -LiteralPath $subTxt)) { Say "  skip  $($ms.Movie).txt not found, subtitle timing unchanged." 'Yellow'; continue }
        $subLines = [System.IO.File]::ReadAllLines($subTxt)
        if ($subLines.Count -lt 2 -or ($subLines[1] -split '\s+')[0] -ne "$($ms.FirstStart)") { Say "  ok  $($ms.Movie) subtitle timing already adjusted." 'Green'; continue }
        Backup $subTxt
        $shifted = for ($i = 0; $i -lt $subLines.Count; $i++) {
            $p = $subLines[$i] -split '\s+'
            if ($i -gt 0 -and $p.Count -ge 3 -and $p[0] -match '^\d+$' -and $p[1] -match '^\d+$') {
                "$([Math]::Max(0, [int]$p[0] - $ms.Frames)) $([Math]::Max(0, [int]$p[1] - $ms.Frames)) $($p[2..($p.Count - 1)] -join ' ')"
            }
            else { $subLines[$i] }
        }
        Set-ItemProperty -LiteralPath $subTxt -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
        [System.IO.File]::WriteAllLines($subTxt, [string[]]$shifted, (New-Object System.Text.UTF8Encoding $false))
        Say "  ok  $($ms.Movie) subtitle timing fixed ($([Math]::Round($ms.Frames / 30, 1)) s earlier)." 'Green'
    }

    Header "8/$StepTotal Engine Binary Patches (Checkpoint, Engine UI and HUD Scaling Fixes)"

    $exe = Join-Path $BinariesDir "GearGame.exe"

    $uScript = Join-Path $GameDir "GearGame\Script\GearGame.u"

    $srcU = Join-Path $FilesDir "GearGame.u"

    if (Test-Path $exe) {

        Backup $exe

        $PATCHES = @(

            # Checkpoint Fix (refresh50)

            @{ Name = "Checkpoint Hook 1"; Offset = 0x02347662; After = 'fa464501' },

            @{ Name = "Checkpoint Hook 2"; Offset = 0x023D2E64; After = '0f1f800000' },

            @{ Name = "Checkpoint Function"; Offset = 0x0379BD60; After = '534883ec404889cb31c0488944242048894424284883bb100a0000007562488d4c242048baa27bf2440100000049b860e82c45010000004d8b004531c948b8a06a2f4001000000ffd085c07433837c24280074164889d931d24c8d44242048b8e09d3d4201000000ffd0488b4c24204885c9740c48b8e0da214001000000ffd04883c4405bc3' },

            @{ Name = "Checkpoint Save Path"; Offset = 0x04F269A2; After = '2e002e005c002e002e005c004700650061007200470061006d0065005c00530061007600650044006100740061005c004700650061007200730043006800650063006b0070006f0069006e00740030002e007300610076' },

            # Bink/XAudio2 Map Change Crash Fix (refresh50)

            @{ Name = "Bink/XAudio2 Crash Fix"; Offset = 0x004A4B30; After = 'c3' },

            # Hide the red "LIGHTING NEEDS TO BE REBUILT" debug text (maps shipped with unbuilt lighting): empty the UTF-16 string.
            @{ Name = "Lighting Rebuild Warning Off"; Offset = 0x03A6E4E8; After = '0000' },

            # Scaleform 4K/Ultrawide DepthStencil Overflow Crash Fix (DryreL)

            @{ Name = "GFx UI Filter Fix 1"; Offset = 0x02D144AC; After = '31c090' },

            @{ Name = "GFx UI Filter Fix 2"; Offset = 0x02D14764; After = '31c090' },

            # HUD Resolution Scaling (DryreL): HUD.PostRender hook in UGameViewportClient::Draw + cave in .nep tail (see _dev\hudscale_patch.py)

            @{ Name = "HUD Scaling Hook"; Offset = 0x00C150A8; After = 'e8d3a1b80290' },

            @{ Name = "HUD Scaling Code"; Offset = 0x0379EC80; After = '5356574881ec800000004889cb4889d7488bb3100500004885f60f841d0100004883be98000000000f840f010000b800003444660f6ec8f30f104674f30f5ec1b80000803f660f6ed00f2fc20f86eb0000008b4670894424608b4674894424648b8690000000894424688b86940000008944246cf30f104e70f30f5ec8f30f114e70f30f104e74f30f5ec8f30f114e74f30f2a4c2468f30f5ec8f30f2dc1898690000000f30f2a4c246cf30f5ec8f30f2dc18986940000000f57c90f114c24200f114c24300f114c24400f114c2450f30f11442420f30f11442434f30f11542448f30f1154245c488b8e98000000488d542420e8e8807efd488b034889d94889fa4531c04531c9ff9020020000488b8e98000000e877597ffd8b4424608946708b4424648946748b4424688986900000008b44246c898694000000eb15488b034889d94889fa4531c04531c9ff90200200004881c4800000005f5e5bc3' }

        )

        # HUD Scaling is optional (asked at the start). When skipped, the hook is not written; a reinstall restores the original exe first.
        if (-not $hudScale) {
            $PATCHES = @($PATCHES | Where-Object { $_.Name -notlike 'HUD Scaling*' })
            Say "  skip  HUD Scaling (you chose to keep the original HUD size)." 'Yellow'
        }

        $fs = [System.IO.File]::Open($exe, 'Open', 'ReadWrite', 'None')

        $appliedCount = 0

        try {

            foreach ($p in $PATCHES) {

                $targetLen = [int]($p.After.Length / 2)

                $fs.Position = $p.Offset

                $curBuf = New-Object byte[] $targetLen

                $fs.Read($curBuf, 0, $targetLen) | Out-Null

                $curHex = [BitConverter]::ToString($curBuf) -replace '-'

                $afterHex = $p.After.ToUpper()

                if ($curHex -ne $afterHex) {

                    $newBytes = HexToBytes $p.After

                    $fs.Position = $p.Offset

                    $fs.Write($newBytes, 0, $newBytes.Length)

                    $appliedCount++
                    Say "  ok  Applied binary patch: $($p.Name)" 'Green'

                }

            }

            $fs.Flush()

        }
        finally { $fs.Dispose() }

        if ($appliedCount -gt 0) {
            Say "  ok  Binary patches applied (Checkpoint Fix, 4K/Ultrawide UI Crash Fix$(if ($hudScale) { ' & HUD Resolution Scaling' }))." 'Green'
        }
        else {
            Say "  ok  Binary patches already up to date." 'Yellow'
        }
    }
    else {
        Say "  error GearGame.exe not found!" 'Red'
        $warningsList.Add("Engine Binary Patches: GearGame.exe not found")
    }


    Header "9/$StepTotal Missing Objective, Point of Interest and UI Text Lines Fix"


    $OBJ_LINES_MULTI = @{
        'INT' = [ordered]@{
            'BADLANDS2_OBJ_5a'  = 'Kill the hatchlings.'
            'BADLANDS2_OBJ_5b'  = 'Keep the hatchlings away from their Mother.'
            'BADLANDS2_OBJ_5c'  = 'Defeat the Mini Corpsers.'
            'BADLANDS2_OBJ_6'   = 'Avoid the Mother while she is blind.'
            'HANOVER_OBJ_4c'    = 'Use the loader to force the door open.'
            'HANOVER_OBJ_8'     = 'Follow the leader to the ammo surplus.'
            'HANOVER_OBJ_9'     = 'Move out to the roof.'
            'HOTPURSUIT_OBJ_1'  = 'Destroy the Serapede.'
            'LEVIATHAN_OBJ_1'   = 'Defeat the Leviathan.'
            'LEVIATHAN_OBJ_2'   = 'Defeat the Leviathan.'
            'LEVIATHAN_OBJ_3'   = 'Defeat the Leviathan.'
            'LEVIATHAN_OBJ_4'   = 'Throw the Ticker into the mouth of the Leviathan.'
            'MERCY_OBJ_11a'     = 'Defend against the attacking enemy forces.'
            'MERCY_OBJ_11b'     = 'Defend against the attacking enemy forces.'
            'MERCY_OBJ_11c'     = 'Defend against the attacking enemy forces.'
            'SHIPYARD_OBJ_9a'   = 'Defeat the Armored Kantus.'
            'SHIPYARD_OBJ_9b'   = 'Defeat the Armored Kantus.'
            'SHIPYARD_OBJ_10a'  = 'Use the valve control to flood the dock.'
            'SHIPYARD_OBJ_10b'  = 'Use the valve control to flood the dock.'
            'SHIPYARD_OBJ_11a'  = 'Use the access ramp to get onto the COG Destroyer.'
            'SHIPYARD_OBJ_11b'  = 'Get the engine part back to the submarine.'
            'TOWER_OBJ_6'       = 'Reach the top of the tower.'
            'TOWER_OBJ_8'       = 'Make your way to the roof.'
            'UNDERWORLD_OBJ_10' = 'Clear the Corpsers.'
            'UNDERWORLD_OBJ_12' = 'Defeat the Lambent Beserker.'
        }
        'TUR' = [ordered]@{
            'BADLANDS2_OBJ_5a'  = 'Yavruları öldür.'
            'BADLANDS2_OBJ_5b'  = 'Yavruları Annelerinden uzak tut.'
            'BADLANDS2_OBJ_5c'  = 'Mini Corpserları yen.'
            'BADLANDS2_OBJ_6'   = 'Anneden kör olduğu sırada kaçın.'
            'HANOVER_OBJ_4c'    = 'Kapıyı zorla açmak için yükleyiciyi kullan.'
            'HANOVER_OBJ_8'     = 'Lideri cephane fazlalığına kadar takip et.'
            'HANOVER_OBJ_9'     = 'Çatıya çık.'
            'HOTPURSUIT_OBJ_1'  = 'Serapede''i yok et.'
            'LEVIATHAN_OBJ_1'   = 'Leviathan''ı yen.'
            'LEVIATHAN_OBJ_2'   = 'Leviathan''ı yen.'
            'LEVIATHAN_OBJ_3'   = 'Leviathan''ı yen.'
            'LEVIATHAN_OBJ_4'   = 'Ticker''ı Leviathan''ın ağzına fırlat.'
            'MERCY_OBJ_11a'     = 'Saldıran düşman kuvvetlerine karşı savun.'
            'MERCY_OBJ_11b'     = 'Saldıran düşman kuvvetlerine karşı savun.'
            'MERCY_OBJ_11c'     = 'Saldıran düşman kuvvetlerine karşı savun.'
            'SHIPYARD_OBJ_9a'   = 'Zırhlı Kantus''u yen.'
            'SHIPYARD_OBJ_9b'   = 'Zırhlı Kantus''u yen.'
            'SHIPYARD_OBJ_10a'  = 'Rıhtımı suyla doldurmak için valf kontrolünü kullan.'
            'SHIPYARD_OBJ_10b'  = 'Rıhtımı suyla doldurmak için valf kontrolünü kullan.'
            'SHIPYARD_OBJ_11a'  = 'COG Destroyer''a binmek için erişim rampasını kullan.'
            'SHIPYARD_OBJ_11b'  = 'Motor parçasını denizaltına geri götür.'
            'TOWER_OBJ_6'       = 'Kulenin tepesine ulaş.'
            'TOWER_OBJ_8'       = 'Çatıya doğru ilerle.'
            'UNDERWORLD_OBJ_10' = 'Corpser''ları temizle.'
            'UNDERWORLD_OBJ_12' = 'Lambent Berserker''ı yen.'
        }
        'ESM' = [ordered]@{
            'BADLANDS2_OBJ_5a'  = 'Mata a las crías.'
            'BADLANDS2_OBJ_5b'  = 'Mantén a las crías alejadas de su Madre.'
            'BADLANDS2_OBJ_5c'  = 'Derrota a los Mini Corpsers.'
            'BADLANDS2_OBJ_6'   = 'Evita a la Madre mientras esté ciega.'
            'HANOVER_OBJ_4c'    = 'Usa el cargador para forzar la puerta.'
            'HANOVER_OBJ_8'     = 'Sigue al líder hasta el excedente de munición.'
            'HANOVER_OBJ_9'     = 'Sal al tejado.'
            'HOTPURSUIT_OBJ_1'  = 'Destruye al Serapede.'
            'LEVIATHAN_OBJ_1'   = 'Derrota al Leviatán.'
            'LEVIATHAN_OBJ_2'   = 'Derrota al Leviatán.'
            'LEVIATHAN_OBJ_3'   = 'Derrota al Leviatán.'
            'LEVIATHAN_OBJ_4'   = 'Lanza el Ticker a la boca del Leviatán.'
            'MERCY_OBJ_11a'     = 'Defiéndete de las fuerzas enemigas atacantes.'
            'MERCY_OBJ_11b'     = 'Defiéndete de las fuerzas enemigas atacantes.'
            'MERCY_OBJ_11c'     = 'Defiéndete de las fuerzas enemigas atacantes.'
            'SHIPYARD_OBJ_9a'   = 'Derrota al Kantus Acorazado.'
            'SHIPYARD_OBJ_9b'   = 'Derrota al Kantus Acorazado.'
            'SHIPYARD_OBJ_10a'  = 'Usa el control de la válvula para inundar el muelle.'
            'SHIPYARD_OBJ_10b'  = 'Usa el control de la válvula para inundar el muelle.'
            'SHIPYARD_OBJ_11a'  = 'Usa la rampa de acceso para subir al Destructor CGO.'
            'SHIPYARD_OBJ_11b'  = 'Lleva la pieza del motor de vuelta al submarino.'
            'TOWER_OBJ_6'       = 'Llega a la cima de la torre.'
            'TOWER_OBJ_8'       = 'Ábrete camino hasta el tejado.'
            'UNDERWORLD_OBJ_10' = 'Elimina a los Corpsers.'
            'UNDERWORLD_OBJ_12' = 'Derrota al Berserker Lambent.'
        }
        'ESN' = [ordered]@{
            'BADLANDS2_OBJ_5a'  = 'Mata a las crías.'
            'BADLANDS2_OBJ_5b'  = 'Mantén a las crías alejadas de su Madre.'
            'BADLANDS2_OBJ_5c'  = 'Derrota a los Mini Corpsers.'
            'BADLANDS2_OBJ_6'   = 'Evita a la Madre mientras esté ciega.'
            'HANOVER_OBJ_4c'    = 'Usa el cargador para forzar la puerta.'
            'HANOVER_OBJ_8'     = 'Sigue al líder hasta el excedente de munición.'
            'HANOVER_OBJ_9'     = 'Sal al tejado.'
            'HOTPURSUIT_OBJ_1'  = 'Destruye al Serapede.'
            'LEVIATHAN_OBJ_1'   = 'Derrota al Leviatán.'
            'LEVIATHAN_OBJ_2'   = 'Derrota al Leviatán.'
            'LEVIATHAN_OBJ_3'   = 'Derrota al Leviatán.'
            'LEVIATHAN_OBJ_4'   = 'Lanza el Ticker a la boca del Leviatán.'
            'MERCY_OBJ_11a'     = 'Defiéndete de las fuerzas enemigas atacantes.'
            'MERCY_OBJ_11b'     = 'Defiéndete de las fuerzas enemigas atacantes.'
            'MERCY_OBJ_11c'     = 'Defiéndete de las fuerzas enemigas atacantes.'
            'SHIPYARD_OBJ_9a'   = 'Derrota al Kantus Acorazado.'
            'SHIPYARD_OBJ_9b'   = 'Derrota al Kantus Acorazado.'
            'SHIPYARD_OBJ_10a'  = 'Usa el control de la válvula para inundar el muelle.'
            'SHIPYARD_OBJ_10b'  = 'Usa el control de la válvula para inundar el muelle.'
            'SHIPYARD_OBJ_11a'  = 'Usa la rampa de acceso para subir al Destructor CGO.'
            'SHIPYARD_OBJ_11b'  = 'Lleva la pieza del motor de vuelta al submarino.'
            'TOWER_OBJ_6'       = 'Llega a la cima de la torre.'
            'TOWER_OBJ_8'       = 'Ábrete camino hasta el tejado.'
            'UNDERWORLD_OBJ_10' = 'Elimina a los Corpsers.'
            'UNDERWORLD_OBJ_12' = 'Derrota al Berserker Lambent.'
        }
        'ITA' = [ordered]@{
            'BADLANDS2_OBJ_5a'  = 'Uccidi i piccoli.'
            'BADLANDS2_OBJ_5b'  = 'Tieni i piccoli lontani dalla Madre.'
            'BADLANDS2_OBJ_5c'  = 'Sconfiggi i Mini Corpser.'
            'BADLANDS2_OBJ_6'   = 'Evita la Madre mentre è accecata.'
            'HANOVER_OBJ_4c'    = 'Usa il caricatore per forzare la porta.'
            'HANOVER_OBJ_8'     = 'Segui il leader verso il deposito di munizioni.'
            'HANOVER_OBJ_9'     = 'Raggiungi il tetto.'
            'HOTPURSUIT_OBJ_1'  = 'Distruggi il Serapede.'
            'LEVIATHAN_OBJ_1'   = 'Sconfiggi il Leviatano.'
            'LEVIATHAN_OBJ_2'   = 'Sconfiggi il Leviatano.'
            'LEVIATHAN_OBJ_3'   = 'Sconfiggi il Leviatano.'
            'LEVIATHAN_OBJ_4'   = 'Lancia il Ticker nella bocca del Leviatano.'
            'MERCY_OBJ_11a'     = 'Difenditi dalle forze nemiche attaccanti.'
            'MERCY_OBJ_11b'     = 'Difenditi dalle forze nemiche attaccanti.'
            'MERCY_OBJ_11c'     = 'Difenditi dalle forze nemiche attaccanti.'
            'SHIPYARD_OBJ_9a'   = 'Sconfiggi il Kantus Corazzato.'
            'SHIPYARD_OBJ_9b'   = 'Sconfiggi il Kantus Corazzato.'
            'SHIPYARD_OBJ_10a'  = 'Usa il controllo della valvola per allagare il molo.'
            'SHIPYARD_OBJ_10b'  = 'Usa il controllo della valvola per allagare il molo.'
            'SHIPYARD_OBJ_11a'  = 'Usa la rampa di accesso per salire sul Destroyer COG.'
            'SHIPYARD_OBJ_11b'  = 'Riporta la parte del motore al sottomarino.'
            'TOWER_OBJ_6'       = 'Raggiungi la cima della torre.'
            'TOWER_OBJ_8'       = 'Fatti strada verso il tetto.'
            'UNDERWORLD_OBJ_10' = 'Elimina i Corpser.'
            'UNDERWORLD_OBJ_12' = 'Sconfiggi il Berserker Lambent.'
        }
        'FRA' = [ordered]@{
            'BADLANDS2_OBJ_5a'  = 'Tuez les larves.'
            'BADLANDS2_OBJ_5b'  = 'Éloignez les larves de leur Mère.'
            'BADLANDS2_OBJ_5c'  = 'Battez les Mini Corpsers.'
            'BADLANDS2_OBJ_6'   = 'Évitez la Mère pendant qu''elle est aveuglée.'
            'HANOVER_OBJ_4c'    = 'Utilisez le chargeur pour forcer la porte.'
            'HANOVER_OBJ_8'     = 'Suivez le meneur jusqu''au surplus de munitions.'
            'HANOVER_OBJ_9'     = 'Montez sur le toit.'
            'HOTPURSUIT_OBJ_1'  = 'Détruisez le Sérapède.'
            'LEVIATHAN_OBJ_1'   = 'Battez le Léviathan.'
            'LEVIATHAN_OBJ_2'   = 'Battez le Léviathan.'
            'LEVIATHAN_OBJ_3'   = 'Battez le Léviathan.'
            'LEVIATHAN_OBJ_4'   = 'Lancez le Ticker dans la bouche du Léviathan.'
            'MERCY_OBJ_11a'     = 'Défendez-vous contre les forces ennemies en attaque.'
            'MERCY_OBJ_11b'     = 'Défendez-vous contre les forces ennemies en attaque.'
            'MERCY_OBJ_11c'     = 'Défendez-vous contre les forces ennemies en attaque.'
            'SHIPYARD_OBJ_9a'   = 'Battez le Kantus Blindé.'
            'SHIPYARD_OBJ_9b'   = 'Battez le Kantus Blindé.'
            'SHIPYARD_OBJ_10a'  = 'Utilisez la valve de contrôle pour inonder le quai.'
            'SHIPYARD_OBJ_10b'  = 'Utilisez la valve de contrôle pour inonder le quai.'
            'SHIPYARD_OBJ_11a'  = 'Utilisez la rampe d''accès pour monter sur le Destroyer de la CGU.'
            'SHIPYARD_OBJ_11b'  = 'Rapportez la pièce de moteur au sous-marin.'
            'TOWER_OBJ_6'       = 'Atteignez le sommet de la tour.'
            'TOWER_OBJ_8'       = 'Frayez-vous un chemin jusqu''au toit.'
            'UNDERWORLD_OBJ_10' = 'Éliminez les Corpsers.'
            'UNDERWORLD_OBJ_12' = 'Battez le Berserker Lambent.'
        }
        'DEU' = [ordered]@{
            'BADLANDS2_OBJ_5a'  = 'Töte die Schlüpflinge.'
            'BADLANDS2_OBJ_5b'  = 'Halte die Schlüpflinge von ihrer Mutter fern.'
            'BADLANDS2_OBJ_5c'  = 'Besiege die Mini-Corpser.'
            'BADLANDS2_OBJ_6'   = 'Weiche der Mutter aus, solange sie blind ist.'
            'HANOVER_OBJ_4c'    = 'Benutze den Lader, um die Tür aufzubrechen.'
            'HANOVER_OBJ_8'     = 'Folge dem Anführer zum Munitionsdepot.'
            'HANOVER_OBJ_9'     = 'Begib dich auf das Dach.'
            'HOTPURSUIT_OBJ_1'  = 'Zerstöre die Serapede.'
            'LEVIATHAN_OBJ_1'   = 'Besiege den Leviathan.'
            'LEVIATHAN_OBJ_2'   = 'Besiege den Leviathan.'
            'LEVIATHAN_OBJ_3'   = 'Besiege den Leviathan.'
            'LEVIATHAN_OBJ_4'   = 'Wirf den Ticker in das Maul des Leviathans.'
            'MERCY_OBJ_11a'     = 'Verteidige gegen die angreifenden feindlichen Streitkräfte.'
            'MERCY_OBJ_11b'     = 'Verteidige gegen die angreifenden feindlichen Streitkräfte.'
            'MERCY_OBJ_11c'     = 'Verteidige gegen die angreifenden feindlichen Streitkräfte.'
            'SHIPYARD_OBJ_9a'   = 'Besiege den Gepanzerter Kantus.'
            'SHIPYARD_OBJ_9b'   = 'Besiege den Gepanzerter Kantus.'
            'SHIPYARD_OBJ_10a'  = 'Benutze die Ventilsteuerung, um das Dock zu fluten.'
            'SHIPYARD_OBJ_10b'  = 'Benutze die Ventilsteuerung, um das Dock zu fluten.'
            'SHIPYARD_OBJ_11a'  = 'Benutze die Zugangsrampe, um auf den KOR-Zerstörer zu gelangen.'
            'SHIPYARD_OBJ_11b'  = 'Bring das Motorteil zurück zum U-Boot.'
            'TOWER_OBJ_6'       = 'Erreiche die Spitze des Turms.'
            'TOWER_OBJ_8'       = 'Bahne dir deinen Weg zum Dach.'
            'UNDERWORLD_OBJ_10' = 'Beseitige die Corpser.'
            'UNDERWORLD_OBJ_12' = 'Besiege den Lambent-Berserker.'
        }
        'RUS' = [ordered]@{
            'BADLANDS2_OBJ_5a'  = 'Уничтожьте детенышей.'
            'BADLANDS2_OBJ_5b'  = 'Не подпускайте детенышей к их Матери.'
            'BADLANDS2_OBJ_5c'  = 'Победите мини-корпсеров.'
            'BADLANDS2_OBJ_6'   = 'Избегайте Мать, пока она ослеплена.'
            'HANOVER_OBJ_4c'    = 'Используйте погрузчик, чтобы выломать дверь.'
            'HANOVER_OBJ_8'     = 'Следуйте за лидером к запасам боеприпасов.'
            'HANOVER_OBJ_9'     = 'Выбирайтесь на крышу.'
            'HOTPURSUIT_OBJ_1'  = 'Уничтожьте серапеда.'
            'LEVIATHAN_OBJ_1'   = 'Победите Левиафана.'
            'LEVIATHAN_OBJ_2'   = 'Победите Левиафана.'
            'LEVIATHAN_OBJ_3'   = 'Победите Левиафана.'
            'LEVIATHAN_OBJ_4'   = 'Бросьте тикера в пасть Левиафана.'
            'MERCY_OBJ_11a'     = 'Защищайтесь от атакующих сил врага.'
            'MERCY_OBJ_11b'     = 'Защищайтесь от атакующих сил врага.'
            'MERCY_OBJ_11c'     = 'Защищайтесь от атакующих сил врага.'
            'SHIPYARD_OBJ_9a'   = 'Победите бронированного кантуса.'
            'SHIPYARD_OBJ_9b'   = 'Победите бронированного кантуса.'
            'SHIPYARD_OBJ_10a'  = 'Используйте клапан, чтобы затопить док.'
            'SHIPYARD_OBJ_10b'  = 'Используйте клапан, чтобы затопить док.'
            'SHIPYARD_OBJ_11a'  = 'Используйте рампу, чтобы подняться на эсминец Коалиции.'
            'SHIPYARD_OBJ_11b'  = 'Верните деталь двигателя на подводную лодку.'
            'TOWER_OBJ_6'       = 'Доберитесь до вершины башни.'
            'TOWER_OBJ_8'       = 'Проберитесь на крышу.'
            'UNDERWORLD_OBJ_10' = 'Уничтожьте корпсеров.'
            'UNDERWORLD_OBJ_12' = 'Победите светящегося берсерка.'
        }
        'POR' = [ordered]@{
            'BADLANDS2_OBJ_5a'  = 'Mate os filhotes.'
            'BADLANDS2_OBJ_5b'  = 'Mantenha os filhotes longe de sua Mãe.'
            'BADLANDS2_OBJ_5c'  = 'Derrote os Mini Corpsers.'
            'BADLANDS2_OBJ_6'   = 'Evite a Mãe enquanto ela estiver cega.'
            'HANOVER_OBJ_4c'    = 'Use a carregadeira para forçar a porta.'
            'HANOVER_OBJ_8'     = 'Siga o líder até o depósito de munição.'
            'HANOVER_OBJ_9'     = 'Vá para o telhado.'
            'HOTPURSUIT_OBJ_1'  = 'Destrua a Serapede.'
            'LEVIATHAN_OBJ_1'   = 'Derrote o Leviatã.'
            'LEVIATHAN_OBJ_2'   = 'Derrote o Leviatã.'
            'LEVIATHAN_OBJ_3'   = 'Derrote o Leviatã.'
            'LEVIATHAN_OBJ_4'   = 'Jogue o Ticker na boca do Leviatã.'
            'MERCY_OBJ_11a'     = 'Defenda-se das forças inimigas atacantes.'
            'MERCY_OBJ_11b'     = 'Defenda-se das forças inimigas atacantes.'
            'MERCY_OBJ_11c'     = 'Defenda-se das forças inimigas atacantes.'
            'SHIPYARD_OBJ_9a'   = 'Derrote o Kantus Blindado.'
            'SHIPYARD_OBJ_9b'   = 'Derrote o Kantus Blindado.'
            'SHIPYARD_OBJ_10a'  = 'Use a válvula de controle para inundar a doca.'
            'SHIPYARD_OBJ_10b'  = 'Use a válvula de controle para inundar a doca.'
            'SHIPYARD_OBJ_11a'  = 'Use a rampa de acesso para subir no Contratorpedeiro da CGO.'
            'SHIPYARD_OBJ_11b'  = 'Leve a peça do motor de volta ao submarino.'
            'TOWER_OBJ_6'       = 'Alcance o topo da torre.'
            'TOWER_OBJ_8'       = 'Abra caminho até o telhado.'
            'UNDERWORLD_OBJ_10' = 'Elimine os Corpsers.'
            'UNDERWORLD_OBJ_12' = 'Derrote o Berserker Lambent.'
        }
        'PTB' = [ordered]@{
            'BADLANDS2_OBJ_5a'  = 'Mate os filhotes.'
            'BADLANDS2_OBJ_5b'  = 'Mantenha os filhotes longe de sua Mãe.'
            'BADLANDS2_OBJ_5c'  = 'Derrote os Mini Corpsers.'
            'BADLANDS2_OBJ_6'   = 'Evite a Mãe enquanto ela estiver cega.'
            'HANOVER_OBJ_4c'    = 'Use a carregadeira para forçar a porta.'
            'HANOVER_OBJ_8'     = 'Siga o líder até o depósito de munição.'
            'HANOVER_OBJ_9'     = 'Vá para o telhado.'
            'HOTPURSUIT_OBJ_1'  = 'Destrua a Serapede.'
            'LEVIATHAN_OBJ_1'   = 'Derrote o Leviatã.'
            'LEVIATHAN_OBJ_2'   = 'Derrote o Leviatã.'
            'LEVIATHAN_OBJ_3'   = 'Derrote o Leviatã.'
            'LEVIATHAN_OBJ_4'   = 'Jogue o Ticker na boca do Leviatã.'
            'MERCY_OBJ_11a'     = 'Defenda-se das forças inimigas atacantes.'
            'MERCY_OBJ_11b'     = 'Defenda-se das forças inimigas atacantes.'
            'MERCY_OBJ_11c'     = 'Defenda-se das forças inimigas atacantes.'
            'SHIPYARD_OBJ_9a'   = 'Derrote o Kantus Blindado.'
            'SHIPYARD_OBJ_9b'   = 'Derrote o Kantus Blindado.'
            'SHIPYARD_OBJ_10a'  = 'Use a válvula de controle para inundar a doca.'
            'SHIPYARD_OBJ_10b'  = 'Use a válvula de controle para inundar a doca.'
            'SHIPYARD_OBJ_11a'  = 'Use a rampa de acesso para subir no Contratorpedeiro da CGO.'
            'SHIPYARD_OBJ_11b'  = 'Leve a peça do motor de volta ao submarino.'
            'TOWER_OBJ_6'       = 'Alcance o topo da torre.'
            'TOWER_OBJ_8'       = 'Abra caminho até o telhado.'
            'UNDERWORLD_OBJ_10' = 'Elimine os Corpsers.'
            'UNDERWORLD_OBJ_12' = 'Derrote o Berserker Lambent.'
        }
        'POL' = [ordered]@{
            'BADLANDS2_OBJ_5a'  = 'Zabij pisklęta.'
            'BADLANDS2_OBJ_5b'  = 'Trzymaj pisklęta z dala od ich Matki.'
            'BADLANDS2_OBJ_5c'  = 'Pokonaj mini-korpsery.'
            'BADLANDS2_OBJ_6'   = 'Unikaj Matki, gdy jest oślepiona.'
            'HANOVER_OBJ_4c'    = 'Użyj ładowarki, aby wyważyć drzwi.'
            'HANOVER_OBJ_8'     = 'Podążaj za dowódcą do magazynu amunicji.'
            'HANOVER_OBJ_9'     = 'Wyjdź na dach.'
            'HOTPURSUIT_OBJ_1'  = 'Zniszcz serapeda.'
            'LEVIATHAN_OBJ_1'   = 'Pokonaj Lewiatana.'
            'LEVIATHAN_OBJ_2'   = 'Pokonaj Lewiatana.'
            'LEVIATHAN_OBJ_3'   = 'Pokonaj Lewiatana.'
            'LEVIATHAN_OBJ_4'   = 'Rzuć tickera w paszczę Lewiatana.'
            'MERCY_OBJ_11a'     = 'Broń się przed atakującymi siłami wroga.'
            'MERCY_OBJ_11b'     = 'Broń się przed atakującymi siłami wroga.'
            'MERCY_OBJ_11c'     = 'Broń się przed atakującymi siłami wroga.'
            'SHIPYARD_OBJ_9a'   = 'Pokonaj opancerzonego kantusa.'
            'SHIPYARD_OBJ_9b'   = 'Pokonaj opancerzonego kantusa.'
            'SHIPYARD_OBJ_10a'  = 'Użyj zaworu, aby zalać dok.'
            'SHIPYARD_OBJ_10b'  = 'Użyj zaworu, aby zalać dok.'
            'SHIPYARD_OBJ_11a'  = 'Użyj rampy, aby dostać się na niszczyciel Koalicji.'
            'SHIPYARD_OBJ_11b'  = 'Zanieś część silnika z powrotem do łodzi podwodnej.'
            'TOWER_OBJ_6'       = 'Dotrzyj na szczyt wieży.'
            'TOWER_OBJ_8'       = 'Przedostań się na dach.'
            'UNDERWORLD_OBJ_10' = 'Wyeliminuj korpsery.'
            'UNDERWORLD_OBJ_12' = 'Pokonaj lambenckiego berserkera.'
        }
    }

    $locDir = Join-Path $GameDir "GearGame\Localization"
    $objFiles = Get-ChildItem -Path $locDir -Recurse -Filter "level_objectives.*" -ErrorAction SilentlyContinue | Where-Object { -not $_.Name.EndsWith(".bak_Multipatch") }

    if (-not $objFiles) {
        Say "  error level_objectives files not found, skipped" 'Yellow'
        $warningsList.Add("Missing Objectives: level_objectives files not found")
    }
    else {
        foreach ($fileItem in $objFiles) {
            $objIni = $fileItem.FullName
            $langCode = $fileItem.Directory.Name
            $upperLangCode = $langCode.ToUpper()
        
            $currentObjLines = $OBJ_LINES_MULTI[$upperLangCode]
            if ($null -eq $currentObjLines) {
                $currentObjLines = $OBJ_LINES_MULTI['INT']
            }

            $text_obj = [System.IO.File]::ReadAllText($objIni, [System.Text.Encoding]::Unicode)
            $lines = [System.Collections.Generic.List[string]]($text_obj -split "`r`n")

            $present = @{}
            foreach ($l in $lines) {
                $s = $l.Trim()
                if ($s -and -not $s.StartsWith('[')) {
                    $splits = $s.Split('=')
                    if ($splits.Count -ge 2) {
                        $present[$splits[0].Trim().ToLower()] = $true
                    }
                }
            }

            $toAdd = @()
            foreach ($k in $currentObjLines.Keys) { if (-not $present[$k.ToLower()]) { $toAdd += $k } }

            if ($toAdd.Count -eq 0) {
                Say "  ok  [$langCode] Every objective line is already present in $($fileItem.Name)." 'Yellow'
            }
            else {
                $start = -1
                for ($i = 0; $i -lt $lines.Count; $i++) {
                    if ($lines[$i].Trim().TrimStart([char]0xFEFF).ToUpper() -eq '[OBJECTIVES]') { $start = $i; break }
                }
                if ($start -lt 0) {
                    Say "  error [$langCode] could not find the [OBJECTIVES] section in $($fileItem.Name)" 'Yellow'
                    $warningsList.Add("[$langCode] [OBJECTIVES] section not found in $($fileItem.Name)")
                }
                else {
                    $end = $lines.Count
                    for ($i = $start + 1; $i -lt $lines.Count; $i++) {
                        if ($lines[$i].Trim().StartsWith('[')) { $end = $i; break }
                    }
                    while ($end -gt $start + 1 -and $lines[$end - 1].Trim() -eq '') { $end-- }

                    Backup $objIni
                    Set-ItemProperty -Path $objIni -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue

                    $block = @('; --- lines the maps ask for and that no language of this build had ---')
                    foreach ($k in $toAdd) { $block += "$k=$($currentObjLines[$k])" }
                    $lines.InsertRange($end, [string[]]$block)

                    [System.IO.File]::WriteAllText($objIni, ($lines -join "`r`n"), [System.Text.Encoding]::Unicode)
                    Say "  ok  [$langCode] $($toAdd.Count) missing objective line(s) added to $($fileItem.Name)" 'Green'
                }
            }
        }
    }

    # Point of interest names (Q key / "look at" prompts): the maps ask level_poi.<lang> [POIS] for tags that no language of this build has, so the
    # game shows the raw key (e.g. "?INT?level_poi.POIS.CHAR_POI_T?" under the cable cutscene of Act 1 Chapter 3 / Char).
    $POI_LINES = [ordered]@{
        'CHAR_POI_T'            = 'Tower'
        'CHAR_POI_TE'           = 'Tower Entrance'
        'Pipe_POI_1'            = 'Pipe'
        'Pipe_POI_2'            = 'Pipe'
        'Pipe_POI_3'            = 'Pipe'
        'RUSTLUNG_POI_BUTTON'   = 'Button'
        'RUSTLUNG_POI_SILVERBACK' = 'Silverback'
    }
    $poiFiles = Get-ChildItem -Path $locDir -Recurse -Filter "level_poi.*" -ErrorAction SilentlyContinue | Where-Object { -not $_.Name.EndsWith(".bak_Multipatch") }
    if (-not $poiFiles) {
        Say "  error level_poi files not found, skipped" 'Yellow'
        $warningsList.Add("Missing POI names: level_poi files not found")
    }
    else {
        foreach ($fileItem in $poiFiles) {
            $poiIni = $fileItem.FullName
            $langCode = $fileItem.Directory.Name
            $lines = [System.Collections.Generic.List[string]]([System.IO.File]::ReadAllText($poiIni, [System.Text.Encoding]::Unicode) -split "`r?`n")
            # The file has two sections, [POIS] (names looked up by the maps) and [MAPLOCS]: the lines must go to the end of [POIS], not the end of the file
            $poiStart = -1
            for ($i = 0; $i -lt $lines.Count; $i++) {
                if ($lines[$i].Trim().TrimStart([char]0xFEFF) -ieq '[POIS]') { $poiStart = $i; break }
            }
            if ($poiStart -lt 0) {
                Say "  error [$langCode] could not find the [POIS] section in $($fileItem.Name)" 'Yellow'
                $warningsList.Add("[$langCode] [POIS] section not found in $($fileItem.Name)")
                continue
            }
            $poiEnd = $lines.Count
            for ($i = $poiStart + 1; $i -lt $lines.Count; $i++) {
                if ($lines[$i].Trim().StartsWith('[')) { $poiEnd = $i; break }
            }
            $present = @{}
            for ($i = $poiStart + 1; $i -lt $poiEnd; $i++) {
                $s = $lines[$i].Trim()
                if ($s -and $s.Contains('=')) { $present[$s.Split('=')[0].Trim().ToLower()] = $true }
            }
            $toAdd = @($POI_LINES.Keys | Where-Object { -not $present[$_.ToLower()] })
            if ($toAdd.Count -eq 0) {
                Say "  ok  [$langCode] Every point of interest name is already present in $($fileItem.Name)." 'Yellow'
                continue
            }
            Backup $poiIni
            Set-ItemProperty -Path $poiIni -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
            while ($poiEnd -gt $poiStart + 1 -and $lines[$poiEnd - 1].Trim() -eq '') { $poiEnd-- }
            $lines.InsertRange($poiEnd, [string[]]@($toAdd | ForEach-Object { "$_=$($POI_LINES[$_])" }))
            [System.IO.File]::WriteAllText($poiIni, (($lines -join "`r`n") + "`r`n"), [System.Text.Encoding]::Unicode)
            Say "  ok  [$langCode] $($toAdd.Count) missing point of interest name(s) added to $($fileItem.Name)" 'Green'
        }
    }

    # GearGame.<lang>: strings the game and its UI ini files ask for that no language of this build has (the game shows the raw key, e.g.
    # "?INT?GearGame.TUTORIALS.TUT_ACTIVERELOAD_BUTTON1?"). Appended as extra sections at the end of the file (the game merges sections with the same name).
    #  - TUT_ACTIVERELOAD_BUTTON1 / 2: the button labels of the Active Reload tutorial (the reload button icon)
    #  - GearUISceneFELobby_Party.GameType_Desc / MapSelectType_Desc / WeapSwap_Desc: lobby descriptions referenced by GearUI.ini; the same texts exist under
    #    [UIScene_FrontEnd] in every language, so they are copied from there
    $gameLocFiles = Get-ChildItem -Path $locDir -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.BaseName -ieq 'GearGame' -and $_.Extension -ne '.bak_Multipatch' -and $_.Extension -notmatch 'bak' }
    foreach ($fileItem in $gameLocFiles) {
        $gl = $fileItem.FullName
        # INT is plain ASCII, ESM / ITA are UTF-16 with a BOM: read and write each file in its own encoding
        $glBytes = [System.IO.File]::ReadAllBytes($gl)
        $glUtf16 = ($glBytes.Length -ge 2 -and $glBytes[0] -eq 0xFF -and $glBytes[1] -eq 0xFE)
        $glEnc = if ($glUtf16) { [System.Text.Encoding]::Unicode } else { New-Object System.Text.UTF8Encoding($false) }
        $glText = $glEnc.GetString($glBytes).TrimStart([char]0xFEFF)
        $glNl = if ($glText.Contains("`r`n")) { "`r`n" } else { "`n" }
        $lines = [System.Collections.Generic.List[string]]($glText -split "`r?`n")
        $extra = New-Object System.Collections.Generic.List[string]
        if (-not ($lines | Where-Object { $_.TrimStart([char]0xFEFF) -match '^TUT_ACTIVERELOAD_BUTTON1=' })) {
            $extra.AddRange([string[]]@('[TUTORIALS]', 'TUT_ACTIVERELOAD_BUTTON1=<Controller:GBA_Reload>', 'TUT_ACTIVERELOAD_BUTTON2=<Controller:GBA_Reload>', ''))
        }
        if (-not ($lines | Where-Object { $_.Trim().TrimStart([char]0xFEFF) -ieq '[GearUISceneFELobby_Party]' })) {
            $lobby = @()
            $inFE = $false
            foreach ($l in $lines) {
                $s = $l.Trim().TrimStart([char]0xFEFF)
                if ($s.StartsWith('[')) { $inFE = ($s -ieq '[UIScene_FrontEnd]'); continue }
                if ($inFE -and $s -match '^(GameType_Desc|MapSelectType_Desc|WeapSwap_Desc)=' -and -not ($lobby | Where-Object { $_.StartsWith($Matches[1] + '=') })) { $lobby += $s }
            }
            if ($lobby.Count -gt 0) { $extra.Add('[GearUISceneFELobby_Party]'); $extra.AddRange([string[]]$lobby); $extra.Add('') }
        }
        if ($extra.Count -eq 0) {
            Say "  ok  [$($fileItem.Directory.Name)] GearGame lines are already present in $($fileItem.Name)." 'Yellow'
            continue
        }
        Backup $gl
        Set-ItemProperty -Path $gl -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
        while ($lines.Count -gt 0 -and $lines[$lines.Count - 1].Trim() -eq '') { $lines.RemoveAt($lines.Count - 1) }
        $lines.Add('')
        $lines.AddRange($extra)
        [System.IO.File]::WriteAllText($gl, (($lines -join $glNl).TrimEnd() + $glNl), $glEnc)
        Say "  ok  [$($fileItem.Directory.Name)] missing GearGame text added to $($fileItem.Name)" 'Green'
    }

    Header "10/$StepTotal Act 3 to Act 4 Transition (Objective Failed Fix)"
    $Act3FixMap = Join-Path $GameDir 'GearGame\Content\Maps\SP\SP_09_Mercy\SP_Mercy_01_S_Showdown.gear'
    if (Test-Path $Act3FixMap) {
        $MapBytes = [System.IO.File]::ReadAllBytes($Act3FixMap)
        $Prefix = [byte[]]@(0x4F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x57, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00)
        $Suffix = [byte[]]@(0x4E, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x57, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00)
        $Hits = [GOW3SemanticByteLocator]::Find($MapBytes, $Prefix, $Suffix)
        if ($Hits.Count -eq 1) {
            if ($MapBytes[$Hits[0]] -eq 0x01) {
                $MapBytes[$Hits[0]] = 0x00
                Set-ItemProperty -Path $Act3FixMap -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
                [System.IO.File]::WriteAllBytes($Act3FixMap, $MapBytes)
                Say "  ok  Act 3 transition fix applied." 'Green'
            }
            elseif ($MapBytes[$Hits[0]] -eq 0x00) {
                Say "  ok  Act 3 transition fix is already applied." 'DarkGray'
            }
            else {
                Say "  error  Act 3 transition fix target has unexpected value. Skipped." 'Yellow'
                $warningsList.Add("Act 3 Transition Fix: Target byte has unexpected value")
            }
        }
        else {
            Say "  error  Act 3 transition fix target not found exactly once. Skipped." 'Yellow'
            $warningsList.Add("Act 3 Transition Fix: Target not found or ambiguous")
        }
    }
    else {
        Say "  error  SP_Mercy_01_S_Showdown.gear not found. Skipped." 'Yellow'
        $warningsList.Add("Act 3 Transition Fix: Map file not found")
    }

    Header "11/$StepTotal AI Debug On-Screen Text Removal"

    $GearGameU = Join-Path $GameDir "GearGame\Script\GearGame.u"
    if (Test-Path $GearGameU) {
        Backup $GearGameU
        # Silence on-screen AI debug text ("... has not done anything interesting ... not allowed to delete him!").
        # GearAI.ShouldPrintStaleWarning() gates the on-screen MessagePlayer call; its body is 'return TRUE' (04 27).
        # Swapping the single token to EX_False (04 28) keeps the bytecode stream valid (same shape as GearAI_Jack's override).
        # Do NOT shorten/rewrite bytecode: the engine parses it token by token and crashes on a malformed stream.
        $bytes = [System.IO.File]::ReadAllBytes($GearGameU)
        $staleOrig = HexToBytes "3406000037be00000d000000090000000427043a8d3e000053"
        $staleFix  = HexToBytes "3406000037be00000d000000090000000428043a8d3e000053"
        $n = [GOW3SemanticByteLocator]::Replace($bytes, $staleOrig, $staleFix)
        if ($n -eq 1) {
            Set-ItemProperty -Path $GearGameU -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
            [System.IO.File]::WriteAllBytes($GearGameU, $bytes)
            Say "  ok  AI debug on-screen text disabled (GearAI.ShouldPrintStaleWarning)." 'Green'
        }
        elseif ($iso88591.GetString($bytes).Contains((HexToStr "3406000037be00000d000000090000000428043a8d3e000053"))) {
            Say "  ok  AI debug on-screen text already disabled." 'DarkGray'
        }
        else {
            Say "  skip  GearAI.ShouldPrintStaleWarning not found exactly once. AI debug text patch skipped." 'Yellow'
            $warningsList.Add("AI Debug Text: GearAI.ShouldPrintStaleWarning bytecode not found")
        }
    }
    else {
        Say "  skip  GearGame.u not found. AI debug text patch skipped." 'Yellow'
        $warningsList.Add("AI Debug Text: GearGame.u not found")
    }

    Header "12/$StepTotal Final Boss (Tempest) Soft-Lock Fix"
    # TEMPEST_MOVE_CONTROLS.SeqVar_Int_0 (IntValue 3 -> 2): the hop-count landing fallback now also works in Stage 2.
    # Without it the boss only lands via mouth-damage events, which can stop firing after the first Hammer of Dawn hit.
    $TempestMap = Join-Path $GameDir 'GearGame\Content\Maps\SP\SP_15_Tower\SP_Rooftop_01b_S.gear'
    if (Test-Path $TempestMap) {
        $TBytes = [System.IO.File]::ReadAllBytes($TempestMap)
        $TPrefix = HexToBytes 'b902000000000000b7020000000000000400000000000000'
        $TSuffix = HexToBytes '0000008e03000000000000b702000000000000040000000000000001000000c4030000000000008d030000000000000400000000000000c21400009003000000000000b7020000000000000400000000000000c0ffffff9103000000000000b702000000000000040000000000000078f9ffff'
        $THits = [GOW3SemanticByteLocator]::Find($TBytes, $TPrefix, $TSuffix)
        if ($THits.Count -eq 1) {
            if ($TBytes[$THits[0]] -eq 0x03) {
                $TBytes[$THits[0]] = 0x02
                Set-ItemProperty -Path $TempestMap -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
                [System.IO.File]::WriteAllBytes($TempestMap, $TBytes)
                Say "  ok  Tempest boss landing fallback fixed (Stage 2 no longer soft-locks)." 'Green'
            }
            elseif ($TBytes[$THits[0]] -eq 0x02) {
                Say "  ok  Tempest boss fix is already applied." 'DarkGray'
            }
            else {
                Say "  error  Tempest boss fix target has unexpected value. Skipped." 'Yellow'
                $warningsList.Add("Tempest Boss Fix: Target byte has unexpected value")
            }
        }
        else {
            Say "  error  Tempest boss fix target not found exactly once. Skipped." 'Yellow'
            $warningsList.Add("Tempest Boss Fix: Target not found or ambiguous")
        }
    }
    else {
        Say "  error  SP_Rooftop_01b_S.gear not found. Skipped." 'Yellow'
        $warningsList.Add("Tempest Boss Fix: Map file not found")
    }

    # Checkpoint reload fix: the Kismet 'SET MOUTH HEALTH' action only runs at fight start, and its values live on the pawn (TotalMouthHealth /
    # MouthHealth), which the checkpoint does not store. After a reload the new Tempest has 0 mouth health, so mouth hits never fire the
    # 'took mouth damage' event and it hovers forever. FLIGHT_BEHAVIOR.START_MOUTH_HEALTH (fired on every hop) now also runs the mouth health
    # reset: its direct link to AttachToEvent_1 is re-pointed to Delay_2 -> SET MOUTH HEALTH (the attach still happens through Gate_3). Same-size link edit.
    if (Test-Path $TempestMap) {
        $TBytes = [System.IO.File]::ReadAllBytes($TempestMap)
        $TcpPre = HexToBytes 'ec020000000000008d030000000000000400000000000000'
        $TcpSuf = HexToBytes '9502000000000000b7020000000000000400000000000000000000008203000000000000ec020000000000008d030000000000000400000000000000970f0000'
        $TcpHits = [GOW3SemanticByteLocator]::FindSpan($TBytes, $TcpPre, $TcpSuf, 4)
        if ($TcpHits.Count -eq 1) {
            $TcpCur = [System.BitConverter]::ToString($TBytes, $TcpHits[0], 4)
            if ($TcpCur -eq 'F4-0D-00-00') {
                $TcpNew = HexToBytes '690e0000'
                for ($k = 0; $k -lt 4; $k++) { $TBytes[$TcpHits[0] + $k] = $TcpNew[$k] }
                Set-ItemProperty -Path $TempestMap -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
                [System.IO.File]::WriteAllBytes($TempestMap, $TBytes)
                Say "  ok  Tempest boss checkpoint-reload fix applied (mouth health is restored after a checkpoint load)." 'Green'
            }
            elseif ($TcpCur -eq '69-0E-00-00') {
                Say "  ok  Tempest boss checkpoint-reload fix is already applied." 'DarkGray'
            }
            else {
                Say "  error  Tempest checkpoint-reload fix target has unexpected value. Skipped." 'Yellow'
                $warningsList.Add("Tempest Checkpoint Fix: Target has unexpected value")
            }
        }
        else {
            Say "  error  Tempest checkpoint-reload fix target not found exactly once. Skipped." 'Yellow'
            $warningsList.Add("Tempest Checkpoint Fix: Target not found or ambiguous")
        }
    }

    # Boss death crash: in Jacinto, SP_Rooftop_P starts the end movie on 'BeginCinematic QueenDeath' (fired by TEMPEST_STAGE_4 'Finished',
    # 1 s after the death) and unloads every sublevel at once, but KILL_TEMPEST (removes the boss) only fired 0.1 s later from the level
    # being unloaded, so it never ran: the live boss kept ticking against unloaded flight nodes / Kismet and the game crashed. STAGE_4 now
    # kills the boss first and finishes 0.5 s later: Delay_18 -> KILL_TEMPEST (was FinishSequence), Delay_0 -> FinishSequence (was
    # KILL_TEMPEST), Delay_0 Duration 0.1 -> 0.5. Same-size edits.
    if (Test-Path $TempestMap) {
        $TBytes = [System.IO.File]::ReadAllBytes($TempestMap)
        $TdLinkPre = '210300000000000002000000ee020000000000003100000000000000840000000000000002000000ec020000000000008d030000000000000400000000000000'
        $TdLinkSuf = '9502000000000000b7020000000000000400000000000000000000008203000000000000ec020000000000008d030000000000000400000000000000'
        $TdEdits = @(
            @{ Pre = $TdLinkPre; Suf = $TdLinkSuf + 'b00e0000'; Old = '240f0000'; New = 'a40d0000' },   # Delay_18 link 0
            @{ Pre = $TdLinkPre; Suf = $TdLinkSuf + 'a60d0000'; Old = 'a40d0000'; New = '240f0000' },   # Delay_0 link 0
            @{ Pre = '000000003d0e0000ca0100000000000023020000000000000400000000000000'; Suf = '96020000000000003100000000000000e70300000000000003000000ea020000'; Old = 'cdcccc3d'; New = '0000003f' }   # Delay_0 Duration
        )
        $TdState = foreach ($ed in $TdEdits) {
            $h = [GOW3SemanticByteLocator]::FindSpan($TBytes, (HexToBytes $ed.Pre), (HexToBytes $ed.Suf), 4)
            $cur = if ($h.Count -eq 1) { ([System.BitConverter]::ToString($TBytes, $h[0], 4) -replace '-', '').ToLower() } else { $null }
            [pscustomobject]@{ Pos = $(if ($h.Count -eq 1) { $h[0] } else { -1 }); Cur = $cur; Edit = $ed }
        }
        if (@($TdState | Where-Object { $_.Cur -ne $_.Edit.New }).Count -eq 0) {
            Say "  ok  Tempest boss death crash fix is already applied." 'DarkGray'
        }
        elseif (@($TdState | Where-Object { $_.Cur -ne $_.Edit.Old }).Count -eq 0) {
            foreach ($st in $TdState) { $nb = HexToBytes $st.Edit.New; for ($k = 0; $k -lt 4; $k++) { $TBytes[$st.Pos + $k] = $nb[$k] } }
            Set-ItemProperty -Path $TempestMap -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
            [System.IO.File]::WriteAllBytes($TempestMap, $TBytes)
            Say "  ok  Tempest boss death crash fix applied (the boss is removed before the end movie unloads the level)." 'Green'
        }
        else {
            Say "  error  Tempest boss death crash fix targets not found or unexpected. Skipped." 'Yellow'
            $warningsList.Add("Tempest Death Crash Fix: Target not found or unexpected value")
        }
    }

    Header "13/$StepTotal Campaign Exit 'Experience' Screen Removal"
    # The post-match Personal Progression ("Experience") screen is multiplayer-only but also opens after a campaign.
    # UIScene_FrontEnd only opens it when PPSceneClass is set; clearing that default object reference (4 bytes -> 0)
    # skips the screen entirely without touching bytecode.
    $ContentU = Join-Path $GameDir "GearGame\Script\GearGameContent.u"
    if (Test-Path $ContentU) {
        $PPPrefix = HexToStr "e93700000000000041340000000000000400000000000000"
        $PPSuffix = HexToStr "d43200000000000041340000000000000400000000000000"
        $CBytes = [System.IO.File]::ReadAllBytes($ContentU)
        $CText = $iso88591.GetString($CBytes)
        $PPOld = $PPPrefix + (HexToStr "af0b0000") + $PPSuffix
        $PPNew = $PPPrefix + (HexToStr "00000000") + $PPSuffix
        $PPIdx = $CText.IndexOf($PPOld, [System.StringComparison]::Ordinal)
        if ($PPIdx -ge 0 -and $CText.IndexOf($PPOld, $PPIdx + 1, [System.StringComparison]::Ordinal) -lt 0) {
            Backup $ContentU
            $dataPos = $PPIdx + $PPPrefix.Length
            for ($k = 0; $k -lt 4; $k++) { $CBytes[$dataPos + $k] = 0 }
            Set-ItemProperty -Path $ContentU -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
            [System.IO.File]::WriteAllBytes($ContentU, $CBytes)
            Say "  ok  Campaign 'Experience' screen disabled (UIScene_FrontEndContent.PPSceneClass)." 'Green'
        }
        elseif ($CText.Contains($PPNew)) {
            Say "  ok  Campaign 'Experience' screen is already disabled." 'DarkGray'
        }
        else {
            Say "  skip  PPSceneClass default not found exactly once. Experience screen patch skipped." 'Yellow'
            $warningsList.Add("Experience Screen: PPSceneClass default not found")
        }
    }
    else {
        Say "  skip  GearGameContent.u not found. Experience screen patch skipped." 'Yellow'
        $warningsList.Add("Experience Screen: GearGameContent.u not found")
    }

    Header "14/$StepTotal Homecoming (Act 1 Ch3) Razor Wire Soft-Lock Fix"
    # The wire fence is only usable after the 'TurnOnRazor' remote event enables SeqEvt_Engage_8/9 in SP_Hanover_CoopDoor_Razor.
    # That level is streamed in hidden and remote events do not reach hidden levels, so finishing the fight before reaching the
    # fence loses the event for good ("Can't do that yet", chapter soft-lock). Both Engage events now start enabled.
    $RazorMap = Join-Path $GameDir 'GearGame\Content\Maps\SP\SP_02_Hanover\SP_Hanover_CoopDoor_Razor.gear'
    if (Test-Path $RazorMap) {
        $RBytes = [System.IO.File]::ReadAllBytes($RazorMap)
        $RHits = [GOW3SemanticByteLocator]::Find($RBytes, (HexToBytes '31000000000000003e000000000000000000000000000000'), (HexToBytes '42010000000000001e0100000000000004000000000000000401000054010000000000001400000000000000c8060000'))
        if ($RHits.Count -eq 2) {
            $RVals = @($RHits | ForEach-Object { $RBytes[$_] })
            if (($RVals | Where-Object { $_ -eq 0 }).Count -eq 2) {
                foreach ($h in $RHits) { $RBytes[$h] = 1 }
                Set-ItemProperty -Path $RazorMap -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
                [System.IO.File]::WriteAllBytes($RazorMap, $RBytes)
                Say "  ok  Razor wire fence interaction can no longer be lost (Homecoming soft-lock fixed)." 'Green'
            }
            elseif (($RVals | Where-Object { $_ -eq 1 }).Count -eq 2) {
                Say "  ok  Razor wire fix is already applied." 'DarkGray'
            }
            else {
                Say "  error  Razor wire fix targets have unexpected values. Skipped." 'Yellow'
                $warningsList.Add("Razor Wire Fix: Target bytes have unexpected values")
            }
        }
        else {
            Say "  error  Razor wire fix targets not found exactly twice. Skipped." 'Yellow'
            $warningsList.Add("Razor Wire Fix: Targets not found or ambiguous")
        }
    }
    else {
        Say "  error  SP_Hanover_CoopDoor_Razor.gear not found. Skipped." 'Yellow'
        $warningsList.Add("Razor Wire Fix: Map file not found")
    }

    Header "15/$StepTotal Crater (Act 4 Ch2) Tower 2 Elevator Fall-Through Fix"
    # When the Tower 2 lift reaches the top, Tower_2_S swaps in a static copy of the platform: it hides the moving
    # platform (SP_Char_Tower_2_elevator_S) and unloads that level. Anyone not standing on the static copy at that moment
    # (AI squadmates, sometimes the player) falls through the floor forever. Both links are neutralised so the moving
    # platform stays solid: the unload input becomes Load (level is already loaded) and the hide input becomes Show.
    $LiftSwapMap = Join-Path $GameDir 'GearGame\Content\Maps\SP\SP_10_Char\SP_Char_Tower_2_S.gear'
    $LiftMoveMap = Join-Path $GameDir 'GearGame\Content\Maps\SP\SP_10_Char\SP_Char_Tower_2_elevator_S.gear'
    $LiftTargets = @(
        @{ Path = $LiftSwapMap; Name = 'Tower_2_S (level unload)'; Orig = 1; Fixed = 0
           Prefix = '00000000020300000000000004000000000000005e040000590200000000000076020000000000000400000000000000'
           Suffix = '000000fa02000000000000960200000000000007040000000000000800000000000000040000004f7574008a00000000000000a5000000000000000000000000000000006c00000000000000a5000000000000000000000000000000006e00000000000000a5000000000000000000000000000000009802000000' },
        @{ Path = $LiftMoveMap; Name = 'Tower_2_elevator_S (platform hide)'; Orig = 0; Fixed = 1
           Prefix = '00000000390200000000000004000000000000002c010000b001000000000000cd010000000000000400000000000000'
           Suffix = '0000003302000000000000ed0100000000000010030000000000000800000000000000040000004f7574005b000000000000006b0000000000000000000000000000000045000000000000006b0000000000000000000000000000000047000000000000006b00000000000000000000000000000000ef01000000' },
        # The invisible floor plate (InterpActor_21) that fills the hole in the middle of the lift deck only blocked bullets
        # (BlockNonZeroExtent = False), so characters walked through it once the crate was gone. It now blocks pawns too.
        @{ Path = $LiftMoveMap; Name = 'Tower_2_elevator_S (middle floor plate blocks characters)'; Orig = 0; Fixed = 1
           Prefix = '000000002900000065000000000000006b000000000000000000000000000000'
           Suffix = 'e80100000000000011030000000000003a00000000000000e701000000000000' }
    )
    foreach ($LT in $LiftTargets) {
        if (-not (Test-Path $LT.Path)) {
            Say "  error  $(Split-Path -Leaf $LT.Path) not found. Skipped." 'Yellow'
            $warningsList.Add("Elevator Fix: $($LT.Name) map file not found")
            continue
        }
        $LBytes = [System.IO.File]::ReadAllBytes($LT.Path)
        $LHits = [GOW3SemanticByteLocator]::Find($LBytes, (HexToBytes $LT.Prefix), (HexToBytes $LT.Suffix))
        if ($LHits.Count -ne 1) {
            Say "  error  Elevator fix target ($($LT.Name)) not found exactly once. Skipped." 'Yellow'
            $warningsList.Add("Elevator Fix: $($LT.Name) target not found or ambiguous")
        }
        elseif ($LBytes[$LHits[0]] -eq $LT.Orig) {
            $LBytes[$LHits[0]] = $LT.Fixed
            Set-ItemProperty -Path $LT.Path -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
            [System.IO.File]::WriteAllBytes($LT.Path, $LBytes)
            Say "  ok  Elevator platform fix applied: $($LT.Name)." 'Green'
        }
        elseif ($LBytes[$LHits[0]] -eq $LT.Fixed) {
            Say "  ok  Elevator fix is already applied: $($LT.Name)." 'DarkGray'
        }
        else {
            Say "  error  Elevator fix target ($($LT.Name)) has an unexpected value. Skipped." 'Yellow'
            $warningsList.Add("Elevator Fix: $($LT.Name) target has unexpected value")
        }
    }
    # When the Gunker dies, Gunker_0 hid the crate bottom and the floor plate and then switched the plate's collision off
    # ("floor goes poof"), leaving a hole in the middle of the lift deck that players and squadmates fell through.
    # The link from SeqAct_Toggle_1 (blood / dust effect) now skips ToggleHidden_4 + ChangeCollision_0 and goes straight to the
    # sequence output (SeqAct_FinishSequence_2), so the crate bottom stays and the floor stays solid.
    if (Test-Path $LiftMoveMap) {
        $LinkRes = Set-IntAtContext $LiftMoveMap $LiftHoleLinkPre $LiftHoleLinkSuf 299 249
        if ($LinkRes -eq 'applied') { Say "  ok  Elevator deck hole after the Gunker dies is now filled (floor stays solid)." 'Green' }
        elseif ($LinkRes -eq 'already') { Say "  ok  Elevator deck hole fix is already applied." 'DarkGray' }
        else {
            Say "  error  Elevator deck hole link not found exactly once. Skipped." 'Yellow'
            $warningsList.Add("Elevator Fix: deck hole link target not found or ambiguous")
        }
    }

    if (Test-Path $LiftMoveMap) {
        $FlatRes = Set-BlockExact $LiftMoveMap $LiftFlatOld $LiftFlatNew
        if ($FlatRes -eq 'applied') { Say "  ok  Elevator ride smoothed (no sudden deck drops when the Gunker lands)." 'Green' }
        elseif ($FlatRes -eq 'already') { Say "  ok  Elevator ride smoothing is already applied." 'DarkGray' }
        else {
            Say "  error  Elevator ride keyframes not found exactly once. Skipped." 'Yellow'
            $warningsList.Add("Elevator Fix: ride keyframes not found or ambiguous")
        }
    }
    if (Test-Path $LiftMoveMap) {
        $PushRes = @(foreach ($L in $LiftPushLinks) { Set-IntAtContext $LiftMoveMap $L.Pre $L.Suf $L.Old $L.New })
        if ($PushRes -contains 'error') {
            Say "  error  Elevator push teleport links not found exactly once. Skipped." 'Yellow'
            $warningsList.Add("Elevator Fix: push teleport links not found or ambiguous")
        }
        elseif ($PushRes -contains 'applied') { Say "  ok  Marcus and Jace are put back on the lift after pushing the car." 'Green' }
        else { Say "  ok  Elevator push teleport fix is already applied." 'DarkGray' }
    }
    if (Test-Path $LiftMoveMap) {
        $SlabRes = Set-BlockExact $LiftMoveMap $LiftDbv3Old $LiftDbv3New
        if ($SlabRes -eq 'applied') { Say "  ok  Elevator: the slab under the deck no longer sits above the floor (characters stay on the lift)." 'Green' }
        elseif ($SlabRes -eq 'already') { Say "  ok  Elevator slab fix is already applied." 'DarkGray' }
        else {
            Say "  error  Elevator slab (DynamicBlockingVolume_3) not found exactly once. Skipped." 'Yellow'
            $warningsList.Add("Elevator Fix: slab under the deck not found or ambiguous")
        }
    }
    $LiftPersistMap = Join-Path $GameDir 'GearGame\Content\Maps\SP\SP_10_Char\SP_Char_P.gear'
    if (Test-Path $LiftPersistMap) {
        $Bv15Res = Set-BlockExact $LiftPersistMap $LiftBv15Old $LiftBv15New
        if ($Bv15Res -eq 'applied') { Say "  ok  Elevator: the invisible floor over the lift deck (BlockingVolume_15) is lowered, the lift now carries everyone." 'Green' }
        elseif ($Bv15Res -eq 'already') { Say "  ok  Elevator floor volume fix is already applied." 'DarkGray' }
        else {
            Say "  error  Elevator floor volume (BlockingVolume_15) not found exactly once. Skipped." 'Yellow'
            $warningsList.Add("Elevator Fix: BlockingVolume_15 not found or ambiguous")
        }
    }
    else {
        Say "  error  SP_Char_P.gear not found. Elevator floor volume fix skipped." 'Yellow'
        $warningsList.Add("Elevator Fix: SP_Char_P.gear not found")
    }
    if (Test-Path $LiftMoveMap) {
        $SkipRes = @(foreach ($S in $LiftSkipElev) { Set-BlockExact $LiftMoveMap $S.Old $S.New })
        if (Test-Path $LiftPersistMap) { $SkipRes += Set-BlockExact $LiftPersistMap $LiftSkipPOld $LiftSkipPNew }
        else { $SkipRes += 'error' }
        if ($SkipRes -contains 'error') {
            Say "  error  Elevator ride skip targets not found exactly once. Skipped." 'Yellow'
            $warningsList.Add("Elevator Fix: ride skip targets not found or ambiguous")
        }
        elseif ($SkipRes -contains 'applied') { Say "  ok  Elevator ride skipped: after the lift checkpoint the squad is moved to the top (cable cutting)." 'Green' }
        else { Say "  ok  Elevator ride skip is already applied." 'DarkGray' }
    }
    # Cable car: InterpActor_BusTram_1 clamps every pawn that steps on it (bClampedBaseEnabled, never switched off in the map) and
    # teleports anyone who walks off back inside ("Forcing ... back on clamped base" in Launch.log). The patched map adds the cable car
    # to three existing Toggle actions: off when the car is prepared for walking through, on at the button, off when the spin ends,
    # on again for the ride. The grown Toggle exports are appended to the file, so the whole map is replaced (known original only).
    # Generator: _dev\gondola_clamp_patch.py
    $CarMap = Join-Path $GameDir 'GearGame\Content\Maps\SP\SP_10_Char\SP_Char_GondolaRide.gear'
    $CarSrc = Join-Path $FilesDir 'SP_Char_GondolaRide.gear'
    $CarOrigHash = '3AF7662BF2063CB8BE59863033D76E48E7C45462EF52BAF1D94896BE8B8D42F1'
    $CarPatchedHash = '149D07FA5059800A19A526F8723041F3CF6D865EF128237B503ACF87528D209A'
    if (Test-Path $CarMap) {
        $CarCur = (Get-FileHash -LiteralPath $CarMap -Algorithm SHA256).Hash.ToUpperInvariant()
        if ($CarCur -eq $CarPatchedHash) { Say "  ok  Cable car teleport-back fix is already applied." 'DarkGray' }
        elseif ($CarCur -ne $CarOrigHash) {
            Say "  skip  SP_Char_GondolaRide.gear is not the original Jacinto v1.1.1 file (SHA256 differs). Cable car fix skipped." 'Yellow'
            $warningsList.Add("Cable Car Fix: map is not the known original file")
        }
        elseif (-not (Test-Path $CarSrc) -or (Get-FileHash -LiteralPath $CarSrc -Algorithm SHA256).Hash.ToUpperInvariant() -ne $CarPatchedHash) {
            Say "  skip  _files\SP_Char_GondolaRide.gear is missing or damaged. Cable car fix skipped." 'Yellow'
            $warningsList.Add("Cable Car Fix: _files\SP_Char_GondolaRide.gear missing or damaged")
            if (-not $packageFilesBad.Contains('SP_Char_GondolaRide.gear')) { $packageFilesBad.Add('SP_Char_GondolaRide.gear (outdated)') }
        }
        else {
            Backup $CarMap
            Set-ItemProperty -Path $CarMap -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
            Copy-Item -LiteralPath $CarSrc -Destination $CarMap -Force
            Say "  ok  Cable car: walking out of it no longer teleports you back inside." 'Green'
        }
    }
    else {
        Say "  error  SP_Char_GondolaRide.gear not found. Cable car fix skipped." 'Yellow'
        $warningsList.Add("Cable Car Fix: map file not found")
    }
    Header "16/$StepTotal Bon Voyage (Act 4) Loader Weight Plate Fix"
    # The loader weight plates (SP_OldShipyard_05_S) only opened their doors if the DRIVER pawn happened to overlap the plate
    # trigger (SeqCond_CompareObject: touching actor == driver). The driver's collision follows the vehicle and rarely updates
    # while the loader stands still, so players had to drive back and forth. The trigger output now skips that comparison and
    # goes straight to the next node (the trigger is still only enabled while the loader is driven).
    $PlateMap = Join-Path $GameDir 'GearGame\Content\Maps\SP\SP_11_OldShipyard\SP_OldShipyard_05_S.gear'
    $PlateTargets = @(
        @{ Orig = 114; Fixed = 29; Prefix = '000000000000130500000b02000000000000bb01000000000000040000000000000000000000830200000000000063010000000000000400000000000000cdcc4c3d5b0000000000000082000000000000000000000000000000008c0000000000000082000000000000000000000000000000000d02000000000000bb010000000000000400000000000000ec00000042020000000000001d000000000000002e0400000000000003000000dc010000000000001d00000000000000440000000000000001000000da0100000000000038020000000000000400000000000000'; Suffix = '030000a601000000000000bb010000000000000400000000000000000000003202000000000000d80100000000000036030000000000000c0000000000000008000000546f756368656400680000000000000082000000000000000000000000000000004f00000000000000820000000000000000000000000000' },
        @{ Orig = 116; Fixed = 113; Prefix = '000000000000140500000b02000000000000bb01000000000000040000000000000000000000830200000000000063010000000000000400000000000000cdcc4c3d5b0000000000000082000000000000000000000000000000008c0000000000000082000000000000000000000000000000000d02000000000000bb010000000000000400000000000000ec00000042020000000000001d000000000000002e0400000000000003000000dc010000000000001d00000000000000440000000000000001000000da0100000000000038020000000000000400000000000000'; Suffix = '030000a601000000000000bb010000000000000400000000000000000000003202000000000000d80100000000000036030000000000000c0000000000000008000000546f756368656400680000000000000082000000000000000000000000000000004f00000000000000820000000000000000000000000000' },
        @{ Orig = 117; Fixed = 34; Prefix = '000000000000160500000b02000000000000bb01000000000000040000000000000000000000830200000000000063010000000000000400000000000000cdcc4c3d5b0000000000000082000000000000000000000000000000008c0000000000000082000000000000000000000000000000000d02000000000000bb010000000000000400000000000000ec00000042020000000000001d000000000000002e0400000000000003000000dc010000000000001d00000000000000440000000000000001000000da0100000000000038020000000000000400000000000000'; Suffix = '030000a601000000000000bb010000000000000400000000000000000000003202000000000000d80100000000000036030000000000000c0000000000000008000000546f756368656400680000000000000082000000000000000000000000000000004f00000000000000820000000000000000000000000000' },
        @{ Orig = 115; Fixed = 112; Prefix = '000000000000150500000b02000000000000bb01000000000000040000000000000000000000830200000000000063010000000000000400000000000000cdcc4c3d5b0000000000000082000000000000000000000000000000008c0000000000000082000000000000000000000000000000000d02000000000000bb010000000000000400000000000000ec00000042020000000000001d000000000000006e0400000000000003000000dc010000000000001d00000000000000440000000000000001000000da0100000000000038020000000000000400000000000000'; Suffix = '030000a601000000000000bb010000000000000400000000000000000000003202000000000000d80100000000000036030000000000000c0000000000000008000000546f756368656400680000000000000082000000000000000000000000000000004f00000000000000820000000000000000000000000000' },
        @{ Orig = 115; Fixed = 112; Prefix = '0000000000050000000000000063010000000000000400000000000000000000003101000000000000bb010000000000000400000000000000e50700006900000000000000820000000000000000000000000000000045000000000000008200000000000000000000000000000000460000000000000082000000000000000000000000000000004602000000000000bb0100000000000004000000000000000e0000003202000000000000dc010000000000001d00000000000000440000000000000001000000da0100000000000038020000000000000400000000000000'; Suffix = '030000a601000000000000bb010000000000000400000000000000000000003202000000000000d80100000000000036030000000000000e000000000000000a000000556e546f756368656400680000000000000082000000000000000000000000000000004f0000000000000082000000000000000000000000' }
    )
    if (Test-Path $PlateMap) {
        $PBytes = [System.IO.File]::ReadAllBytes($PlateMap)
        $PHits = @(); $PBad = $false
        foreach ($PT in $PlateTargets) {
            $H = [GOW3SemanticByteLocator]::Find($PBytes, (HexToBytes $PT.Prefix), (HexToBytes $PT.Suffix))
            if ($H.Count -ne 1) { $PBad = $true; break }
            $PHits += ,@($H[0], $PT)
        }
        if ($PBad) {
            Say "  error  Loader plate fix targets not found exactly once. Skipped." 'Yellow'
            $warningsList.Add("Loader Plate Fix: Targets not found or ambiguous")
        }
        else {
            $PApplied = @($PHits | Where-Object { $PBytes[$_[0]] -eq $_[1].Fixed }).Count
            $POrig = @($PHits | Where-Object { $PBytes[$_[0]] -eq $_[1].Orig }).Count
            if ($POrig -eq $PHits.Count) {
                foreach ($PH in $PHits) { $PBytes[$PH[0]] = $PH[1].Fixed }
                Set-ItemProperty -Path $PlateMap -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
                [System.IO.File]::WriteAllBytes($PlateMap, $PBytes)
                Say "  ok  Loader weight plates now trigger as soon as the loader stands on them." 'Green'
            }
            elseif ($PApplied -eq $PHits.Count) {
                Say "  ok  Loader plate fix is already applied." 'DarkGray'
            }
            else {
                Say "  error  Loader plate fix targets have unexpected values. Skipped." 'Yellow'
                $warningsList.Add("Loader Plate Fix: Target bytes have unexpected values")
            }
        }
    }
    else {
        Say "  error  SP_OldShipyard_05_S.gear not found. Skipped." 'Yellow'
        $warningsList.Add("Loader Plate Fix: Map file not found")
    }

    Header "17/$StepTotal Bon Voyage (Act 4) Final Kantus Battle Never Starts Fix"
    # After the Kantus intro (KantusIntroOver) SP_OldShipyard_P teleports the squad and then sends the squad to Checkpoint_1 using
    # its 'Teleport Only' input. That mode never fires 'Finished', so Gate_30 and the 'CheckpointKantus_Saved' remote event (which
    # spawns the Armored Kantus in SP_OldShipyard_07_S) are never reached and the last stage stays empty. The link now uses the
    # normal save input (it still teleports the squad), which fires 'Finished'.
    $KantusMap = Join-Path $GameDir 'GearGame\Content\Maps\SP\SP_11_OldShipyard\SP_OldShipyard_P.gear'
    $KantusPrefix = '00000000a301000000000000040000000000000008030000430100000000000054010000000000000400000000000000'
    $KantusSuffix = '0000009b0100000000000073010000000000005a020000000000000800000000000000040000004f75740057000000000000006400000000000000000000000000000000490000000000000064000000000000000000000000000000004b0000000000000064000000000000000000000000000000007501000000'
    if (Test-Path $KantusMap) {
        $KBytes = [System.IO.File]::ReadAllBytes($KantusMap)
        $KHits = [GOW3SemanticByteLocator]::Find($KBytes, (HexToBytes $KantusPrefix), (HexToBytes $KantusSuffix))
        if ($KHits.Count -eq 1) {
            if ($KBytes[$KHits[0]] -eq 2) {
                $KBytes[$KHits[0]] = 0
                Set-ItemProperty -Path $KantusMap -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
                [System.IO.File]::WriteAllBytes($KantusMap, $KBytes)
                Say "  ok  Final Kantus battle now starts after the Kantus intro (last stage no longer empty)." 'Green'
            }
            elseif ($KBytes[$KHits[0]] -eq 0) {
                Say "  ok  Final Kantus battle fix is already applied." 'DarkGray'
            }
            else {
                Say "  error  Final Kantus battle fix target has an unexpected value. Skipped." 'Yellow'
                $warningsList.Add("Kantus Battle Fix: Target byte has unexpected value")
            }
        }
        else {
            Say "  error  Final Kantus battle fix target not found exactly once. Skipped." 'Yellow'
            $warningsList.Add("Kantus Battle Fix: Target not found or ambiguous")
        }
    }
    else {
        Say "  error  SP_OldShipyard_P.gear not found. Skipped." 'Yellow'
        $warningsList.Add("Kantus Battle Fix: Map file not found")
    }

    Header "18/$StepTotal Camera Shake While Running (Roadie Run) Option"
    # GearPawn.RoadieRunShakyCamDampeningFactor scales the camera-bone shake while roadie running (game default 0.6, 0.45 for the
    # armored Kantus). 0 removes the shake. Only applied when the option was accepted above; choosing N leaves the files untouched.
    if ($disableRunShake) {
        $pawnIniNames = @('GearPawn.ini', 'DefaultPawn.ini', 'GearPawnMP.ini', 'DefaultPawnMP.ini')
        $pawnDirs = @($ConfigDir, (Join-Path ([Environment]::GetFolderPath("MyDocuments")) "My Games\Gears of War 3\GearGame\Config"))
        $shakeDone = 0
        foreach ($dir in $pawnDirs) {
            foreach ($name in $pawnIniNames) {
                $ini = Join-Path $dir $name
                if (-not (Test-Path $ini)) { continue }
                Backup $ini
                $txt = [System.IO.File]::ReadAllText($ini)
                $new = $txt
                foreach ($sec in @('GearGame.GearPawn', 'GearGameContent.GearPawn_LocustKantusArmor')) {
                    if ($new -match ('(?m)^\[' + [regex]::Escape($sec) + '\]\r?$')) {
                        $new = Set-IniSectionValue $new $sec 'RoadieRunShakyCamDampeningFactor' '0.0f'
                    }
                }
                if ($new -ne $txt) {
                    Set-ItemProperty -Path $ini -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
                    [System.IO.File]::WriteAllText($ini, $new)
                    Say "  ok  Run camera shake disabled in $name." 'Green'
                    $shakeDone++
                }
                else {
                    Say "  ok  Run camera shake already disabled in $name." 'DarkGray'
                }
            }
        }
        if ($shakeDone -eq 0 -and -not (Test-Path (Join-Path $ConfigDir 'GearPawn.ini'))) {
            Say "  error  GearPawn.ini not found. Skipped." 'Yellow'
            $warningsList.Add("Run Camera Shake: GearPawn.ini not found")
        }
    }
    else {
        Say "  skip  Run camera shake kept as original (no changes)." 'DarkGray'
    }

    Header "19/$StepTotal Coast (Act 5 Ep 1) Stealth Patrol Sight Radius Fix"
    # In the first Coast patrol, six Locust guards spot the AI squadmates (Sam) within seconds and the whole patrol raises the alarm,
    # so stealth never works. The patrol sets the global sight and hearing radius to 1900 for both teams (SeqAct_AISetSightRadius_1);
    # it is lowered to 900 so the guards only notice someone who gets close. The four floats are located as one 88-byte block.
    $CoastMap = Join-Path $GameDir 'GearGame\Content\Maps\SP\SP_13_Coast\SP_Coast_01_S.gear'
    $CoastPrefix = '04000000000000002d000000d40200000000000022050000a303000000000000c4010000000000000400000000000000'
    $CoastSuffix = '250200000000000021000000000000004d01000000000000010000006c02000000000000e303000000000000070000000000000003000000496e0076000000000000008f0000000000000000000000000000000022030000000000003c020000'
    $CoastOffsets = @(0, 28, 56, 84)
    if (Test-Path $CoastMap) {
        $CBytes = [System.IO.File]::ReadAllBytes($CoastMap)
        $CHits = [GOW3SemanticByteLocator]::FindSpan($CBytes, (HexToBytes $CoastPrefix), (HexToBytes $CoastSuffix), 88)
        if ($CHits.Count -ne 1) {
            Say "  error  Coast stealth fix target not found exactly once. Skipped." 'Yellow'
            $warningsList.Add("Coast Stealth Fix: Target not found or ambiguous")
        }
        else {
            $CVals = @($CoastOffsets | ForEach-Object { [BitConverter]::ToSingle($CBytes, $CHits[0] + $_) })
            if (@($CVals | Where-Object { $_ -eq 1900.0 }).Count -eq 4) {
                foreach ($Off in $CoastOffsets) { [Array]::Copy([BitConverter]::GetBytes([single]900.0), 0, $CBytes, $CHits[0] + $Off, 4) }
                Set-ItemProperty -Path $CoastMap -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
                [System.IO.File]::WriteAllBytes($CoastMap, $CBytes)
                Say "  ok  Coast patrol sight/hearing radius lowered to 900 (stealth is possible)." 'Green'
            }
            elseif (@($CVals | Where-Object { $_ -eq 900.0 }).Count -eq 4) {
                Say "  ok  Coast stealth fix is already applied." 'DarkGray'
            }
            else {
                Say "  error  Coast stealth fix target has unexpected values. Skipped." 'Yellow'
                $warningsList.Add("Coast Stealth Fix: Target values unexpected")
            }
        }
    }
    else {
        Say "  error  SP_Coast_01_S.gear not found. Skipped." 'Yellow'
        $warningsList.Add("Coast Stealth Fix: Map file not found")
    }

    Header "20/$StepTotal Coast (Act 5 Ep 1) Guards Spot You On Spawn Fix"
    # The Coast patrol guards are built for stealth (bAutoAcquireEnemy = False), but the follow-up guards spawned by
    # EnemyAlertedPostPatrol (AIFactory_4 / _48) and the 'Alpha' spawners (AIFactory_6 / _7) in SP_Coast_01_S have bAutoAcquireEnemy
    # (and some bAutoNotifyEnemy) enabled, so they acquire an enemy the instant they spawn and open fire even when the squad
    # advanced quietly. Those flags are now disabled.
    $TurretMap = Join-Path $GameDir 'GearGame\Content\Maps\SP\SP_13_Coast\SP_Coast_01_S.gear'
    $TurretTargets = @(
        @{ Span = 1; Offsets = @(0); Prefix = '5b335d000c010000000000002100000000000000040000000000000000000000c503000000000000210000000000000004000000000000000000000040000000000000008f000000000000000000000000000000013f000000000000008f00000000000000000000000000000001f301000000000000da02000000000000040000000000000072fffffff800000000000000da02000000000000040000000000000094ffffff740200000000000021000000000000000800000000000000010000008dffffff34000000000000008f00000000000000000000000000000001d603000000000000c00200000000000008000000000000001400000000000000a6000000000000008f000000000000000000000000000000000a00000000000000c0020000000000000800000000000000d4020000000000000a03000000000000c0020000000000000800000000000000d40200000000000042000000000000008f000000000000000000000000000000'; Suffix = '45000000000000008f0000000000000000000000000000000073000000000000008f0000000000000000000000000000000074000000000000008f00000000000000000000000000000000a5000000000000008f000000000000000000000000000000009c030000000000003c020000000000000400000000000000000000002900000000000000e30300000000000004000000000000000000000063000000000000008f0000000000000000000000000000000033000000000000008f00000000000000000000000000000000d803000000000000c40100000000000004000000000000000000f0419e03000000000000b6000000000000000800000000000000930100000000000068020000000000000c03000000000000c40100000000000004000000000000000000003f61000000000000008f000000000000000000000000000000005f000000000000008f0000000000000000000000000000000039000000000000008f00000000000000' },
        @{ Span = 26; Offsets = @(0,25); Prefix = '000000000000000000000000000000d603000000000000c00200000000000008000000000000001400000000000000a6000000000000008f000000000000000000000000000000000a00000000000000c0020000000000000800000000000000d4020000000000000a03000000000000c0020000000000000800000000000000d40200000000000042000000000000008f000000000000000000000000000000'; Suffix = '73000000000000008f0000000000000000000000000000000074000000000000008f00000000000000000000000000000000a5000000000000008f000000000000000000000000000000009c030000000000003c020000000000000400000000000000000000002900000000000000e30300000000000004000000000000000000000063000000000000008f0000000000000000000000000000000133000000' },
        @{ Span = 1; Offsets = @(0); Prefix = '00000000000000000b01000000000000c401000000000000040000000000000000000000340400000000000021000000000000000c000000000000000100000032040000000000001b0400000000000021000000000000007800000000000000050000000a0000005365742031205b315d00150000005365742031205b537061776e20506f696e74735d001b0000005365742031205b537061776e2044657374696e6174696f6e735d00120000005365742031205b4d617820416c6976655d00140000005365742031205b537061776e20546f74616c5d00e50200000000000021000000000000001400000000000000010000000c000000537061776e2031205b315d000c010000000000002100000000000000040000000000000000000000c503000000000000210000000000000004000000000000000000000040000000000000008f000000000000000000000000000000013f000000000000008f00000000000000000000000000000001f301000000000000da02000000000000040000000000000072fffffff800000000000000da02000000000000040000000000000094ffffff740200000000000021000000000000000800000000000000010000008dffffff34000000000000008f00000000000000000000000000000001d603000000000000c00200000000000008000000000000000000000000000000a6000000000000008f000000000000000000000000000000000a00000000000000c0020000000000000800000000000000d4020000000000000a03000000000000c0020000000000000800000000000000d40200000000000042000000000000008f000000000000000000000000000000'; Suffix = '45000000000000008f0000000000000000000000000000000073000000000000008f0000000000000000000000000000000074000000000000008f00000000000000000000000000000000a5000000000000008f000000000000000000000000000000009c030000000000003c020000000000000400000000000000000000002900000000000000e30300000000000004000000000000000000000063000000000000008f0000000000000000000000000000000033000000000000008f00000000000000000000000000000000d803000000000000c40100000000000004000000000000000000f0419e03000000000000b6000000000000000800000000000000930100000000000068020000000000000c03000000000000c40100000000000004000000000000000000003f61000000000000008f000000000000000000000000000000005f000000000000008f0000000000000000000000000000000039000000000000008f0000000000000000000000000000000072000000000000008f000000000000000000000000000000000f02000000000000b60000000000000008000000000000008d010000000000002803000000000000c3030000000000002100000000000000040000000000000000000000fb00000000000000c40100000000000004000000000000000000000066000000000000008f000000000000000000000000000000007301000000000000c4010000000000000400000000000000000000007201000000000000c4010000000000000400000000000000000000007401000000000000c401000000000000040000000000000000000000d4020000000000000f00000000000000b6' },
        @{ Span = 26; Offsets = @(0,25); Prefix = '00000000000034000000000000008f00000000000000000000000000000001d603000000000000c00200000000000008000000000000000000000000000000a6000000000000008f000000000000000000000000000000000a00000000000000c0020000000000000800000000000000d4020000000000000a03000000000000c0020000000000000800000000000000d40200000000000042000000000000008f000000000000000000000000000000'; Suffix = '73000000000000008f0000000000000000000000000000000074000000000000008f00000000000000000000000000000000a5000000000000008f000000000000000000000000000000009c030000000000003c020000000000000400000000000000000000002900000000000000e30300000000000004000000000000000000000063000000000000008f0000000000000000000000000000000033000000000000008f0000000000000000000000' },
        @{ Span = 1; Offsets = @(0); Prefix = '00000000000000000b01000000000000c401000000000000040000000000000000000000340400000000000021000000000000000c000000000000000100000032040000000000001b0400000000000021000000000000007800000000000000050000000a0000005365742031205b315d00150000005365742031205b537061776e20506f696e74735d001b0000005365742031205b537061776e2044657374696e6174696f6e735d00120000005365742031205b4d617820416c6976655d00140000005365742031205b537061776e20546f74616c5d00e50200000000000021000000000000001400000000000000010000000c000000537061776e2031205b315d000c010000000000002100000000000000040000000000000000000000c503000000000000210000000000000004000000000000000000000040000000000000008f000000000000000000000000000000013f000000000000008f00000000000000000000000000000001f301000000000000da02000000000000040000000000000072fffffff800000000000000da02000000000000040000000000000094ffffff740200000000000021000000000000000800000000000000010000008dffffff34000000000000008f00000000000000000000000000000001d603000000000000c00200000000000008000000000000000000000000000000a6000000000000008f000000000000000000000000000000000a00000000000000c0020000000000000800000000000000d4020000000000000a03000000000000c0020000000000000800000000000000d40200000000000042000000000000008f000000000000000000000000000000'; Suffix = '45000000000000008f0000000000000000000000000000000073000000000000008f0000000000000000000000000000000074000000000000008f00000000000000000000000000000000a5000000000000008f000000000000000000000000000000009c030000000000003c020000000000000400000000000000000000002900000000000000e30300000000000004000000000000000000000063000000000000008f0000000000000000000000000000000033000000000000008f00000000000000000000000000000000d803000000000000c40100000000000004000000000000000000f0419e03000000000000b6000000000000000800000000000000930100000000000068020000000000000c03000000000000c40100000000000004000000000000000000003f61000000000000008f000000000000000000000000000000005f000000000000008f0000000000000000000000000000000039000000000000008f0000000000000000000000000000000072000000000000008f000000000000000000000000000000000f02000000000000b60000000000000008000000000000008d010000000000002803000000000000c3030000000000002100000000000000040000000000000000000000fb00000000000000c40100000000000004000000000000000000000066000000000000008f000000000000000000000000000000007301000000000000c4010000000000000400000000000000000000007201000000000000c4010000000000000400000000000000000000007401000000000000c401000000000000040000000000000000000000d402000000000000ff02000000000000b6' }
    )
    if (Test-Path $TurretMap) {
        $TBytes = [System.IO.File]::ReadAllBytes($TurretMap)
        $TList = @(); $TBad = $false
        foreach ($TT in $TurretTargets) {
            $H = [GOW3SemanticByteLocator]::FindSpan($TBytes, (HexToBytes $TT.Prefix), (HexToBytes $TT.Suffix), $TT.Span)
            if ($H.Count -ne 1) { $TBad = $true; break }
            foreach ($Off in $TT.Offsets) { $TList += ($H[0] + $Off) }
        }
        if ($TBad) {
            Say "  error  Turret guard fix targets not found exactly once. Skipped." 'Yellow'
            $warningsList.Add("Turret Guard Fix: Targets not found or ambiguous")
        }
        else {
            $TOn = @($TList | Where-Object { $TBytes[$_] -eq 1 }).Count
            $TOff = @($TList | Where-Object { $TBytes[$_] -eq 0 }).Count
            if ($TOn -eq $TList.Count) {
                foreach ($TO in $TList) { $TBytes[$TO] = 0 }
                Set-ItemProperty -Path $TurretMap -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
                [System.IO.File]::WriteAllBytes($TurretMap, $TBytes)
                Say "  ok  Coast turret guards no longer spot the squad the moment they spawn." 'Green'
            }
            elseif ($TOff -eq $TList.Count) {
                Say "  ok  Coast turret guard fix is already applied." 'DarkGray'
            }
            else {
                Say "  error  Turret guard fix targets have unexpected values. Skipped." 'Yellow'
                $warningsList.Add("Turret Guard Fix: Target bytes have unexpected values")
            }
        }
    }
    else {
        Say "  error  SP_Coast_01_S.gear not found. Skipped." 'Yellow'
        $warningsList.Add("Turret Guard Fix: Map file not found")
    }

    Header "21/$StepTotal Developer On-Screen Messages Removal"
    # Pawn.MessagePlayer and Actor.DebugMessagePlayer (Engine.u) print AI debug text on the screen for every local player
    # ("... Not in my combatzone, and failed to move to it. CLEARING COMBAT ZONE LIST!", stuck-AI warnings, ...).
    # Their foreach LocalPlayerControllers(class'PlayerController', PC) loop now iterates DebugCameraController, which never
    # exists as a local player, so nothing is printed. Same-size data change, bytecode untouched. Log lines are kept.
    $EngineU = Join-Path $GameDir 'GearGame\Script\Engine.u'
    $MsgOrig = [byte[]](0x6d, 0x13, 0x00, 0x00)    # class'PlayerController'
    $MsgFixed = [byte[]](0x0a, 0x25, 0x00, 0x00)   # class'DebugCameraController'
    $MsgTargets = @(
        @{ Prefix = '00161f2121212100164a161c8dffffff162f1c1a02000020'; Suffix = '00d40200001670001900d4020000150000000000001b6910' },
        @{ Prefix = '00ba0d0000d17e01004e000000360000002f1c1a02000020'; Suffix = '00b3420000164a001900b3420000150000000000001b6910' }
    )
    if (Test-Path $EngineU) {
        $EBytes = [System.IO.File]::ReadAllBytes($EngineU)
        $EHits = @(); $EBad = $false
        foreach ($MT in $MsgTargets) {
            $H = [GOW3SemanticByteLocator]::FindSpan($EBytes, (HexToBytes $MT.Prefix), (HexToBytes $MT.Suffix), 4)
            if ($H.Count -ne 1) { $EBad = $true; break }
            $EHits += $H[0]
        }
        if ($EBad) {
            Say "  error  Developer message targets not found exactly once. Skipped." 'Yellow'
            $warningsList.Add("Developer Messages: Targets not found or ambiguous")
        }
        else {
            $EIsOrig = @($EHits | Where-Object { ($EBytes[$_] -eq 0x6d) -and ($EBytes[$_ + 1] -eq 0x13) -and ($EBytes[$_ + 2] -eq 0) -and ($EBytes[$_ + 3] -eq 0) }).Count
            $EIsFixed = @($EHits | Where-Object { ($EBytes[$_] -eq 0x0a) -and ($EBytes[$_ + 1] -eq 0x25) -and ($EBytes[$_ + 2] -eq 0) -and ($EBytes[$_ + 3] -eq 0) }).Count
            if ($EIsOrig -eq $EHits.Count) {
                Backup $EngineU
                foreach ($EH in $EHits) { [Array]::Copy($MsgFixed, 0, $EBytes, $EH, 4) }
                Set-ItemProperty -Path $EngineU -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
                [System.IO.File]::WriteAllBytes($EngineU, $EBytes)
                Say "  ok  Developer debug text on the screen is disabled (MessagePlayer / DebugMessagePlayer)." 'Green'
            }
            elseif ($EIsFixed -eq $EHits.Count) {
                Say "  ok  Developer message fix is already applied." 'DarkGray'
            }
            else {
                Say "  error  Developer message targets have unexpected values. Skipped." 'Yellow'
                $warningsList.Add("Developer Messages: Target values unexpected")
            }
        }
    }
    else {
        Say "  error  Engine.u not found. Skipped." 'Yellow'
        $warningsList.Add("Developer Messages: Engine.u not found")
    }

    Header "22/$StepTotal Objective Failed Screen Scaling"
    # MissionFailed.swf and GameOverOptions.swf (UI_PauseMenu.upk) are laid out by a Scaleform StagePanel whose default
    # scaleMode is "noScale": on start it overwrites Stage.scaleMode, so the screen stayed 1:1 in the middle of wide windows.
    # The default string is changed to "showAll" (same length) in those two movies only; UI_Pause.swf (3rd copy) is untouched.
    $FailPkg = Join-Path $GameDir 'GearGame\Content\GFx\UI_PauseMenu.upk'
    if (Test-Path $FailPkg) {
        $FBytes = [System.IO.File]::ReadAllBytes($FailPkg)
        $FOld = [System.Text.Encoding]::ASCII.GetBytes("noScale`0ASSetPropFlags`0")
        $FNew = [System.Text.Encoding]::ASCII.GetBytes("showAll`0ASSetPropFlags`0")
        $FHits = New-Object System.Collections.Generic.List[int]
        for ($fi = 0; $fi -le $FBytes.Length - $FOld.Length; $fi++) {
            if ($FBytes[$fi] -ne 0x6e) { continue }
            $fm = $true
            for ($fj = 1; $fj -lt $FOld.Length; $fj++) { if ($FBytes[$fi + $fj] -ne $FOld[$fj]) { $fm = $false; break } }
            if ($fm) { $FHits.Add($fi) }
        }
        if ($FHits.Count -eq 3) {
            Backup $FailPkg
            foreach ($fk in 0, 1) { [Array]::Copy($FNew, 0, $FBytes, $FHits[$fk], $FNew.Length) }
            Set-ItemProperty -Path $FailPkg -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
            [System.IO.File]::WriteAllBytes($FailPkg, $FBytes)
            Say "  ok  Objective Failed screen now scales with the window (MissionFailed / GameOverOptions)." 'Green'
        }
        elseif ($FHits.Count -eq 1) {
            Say "  ok  Objective Failed scaling fix is already applied." 'DarkGray'
        }
        else {
            Say "  error  Objective Failed scaling targets not found as expected ($($FHits.Count) matches). Skipped." 'Yellow'
            $warningsList.Add("Objective Failed Scaling: Targets not found or ambiguous")
        }
    }
    else {
        Say "  error  UI_PauseMenu.upk not found. Skipped." 'Yellow'
        $warningsList.Add("Objective Failed Scaling: UI_PauseMenu.upk not found")
    }

    Apply-TextureStreamingOptimization

    Header "24/$StepTotal Insane Difficulty Never Unlocks Fix (Chapter 30 Never Completes)"
    # Insane unlocks once every chapter of all five acts is completed on one difficulty (GearProfileSettings.HasCompletedGame).
    # SP_Tower_P (Act 5 chapter 30) fires SeqAct_ChapterComplete with ChapterCompleted = 29, the same value as SP_Azura2_P, so
    # chapter index 29 is never marked in the profile and the campaign never counts as finished. It is now 30.
    $InsaneMap = Join-Path $GameDir 'GearGame\Content\Maps\SP\SP_15_Tower\SP_Tower_P.gear'
    if (Test-Path $InsaneMap) {
        $IBytes = [System.IO.File]::ReadAllBytes($InsaneMap)
        $IHits = [GOW3SemanticByteLocator]::FindSpan($IBytes, (HexToBytes '86000000000000002d010000000000000400000000000000'), (HexToBytes '1d010000000000001e000000000000004d01000000000000'), 4)
        if ($IHits.Count -eq 1) {
            if ($IBytes[$IHits[0]] -eq 29) {
                $IBytes[$IHits[0]] = 30
                Set-ItemProperty -Path $InsaneMap -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
                [System.IO.File]::WriteAllBytes($InsaneMap, $IBytes)
                Say "  ok  Chapter 30 (Tower) now counts as completed (Insane unlocks after finishing the campaign)." 'Green'
            }
            elseif ($IBytes[$IHits[0]] -eq 30) {
                Say "  ok  Insane unlock fix is already applied." 'DarkGray'
            }
            else {
                Say "  error  Insane unlock fix target has an unexpected value. Skipped." 'Yellow'
                $warningsList.Add("Insane Unlock Fix: Target byte has unexpected value")
            }
        }
        else {
            Say "  error  Insane unlock fix target not found exactly once. Skipped." 'Yellow'
            $warningsList.Add("Insane Unlock Fix: Target not found or ambiguous")
        }
    }
    else {
        Say "  error  SP_Tower_P.gear not found. Skipped." 'Yellow'
        $warningsList.Add("Insane Unlock Fix: Map file not found")
    }

    Header "25/$StepTotal On-Screen Warning Messages Removal (targeted call sites)"
    # Earlier versions made PlayerController.ClientMessage return immediately (Engine.u jump 0x53 -> 0x51). That also silenced the
    # developer key feedback ("God mode on / off", "You feel ethereal", "Badass!", ...). ClientMessage is now left untouched
    # (the old patch is reverted below) and only the automatic warning call sites in GearGame.u are removed. Each call statement
    # (1b <ClientMessage> ... 16) is overwritten with EX_Nothing (0x0b), same size, so no jump offset changes.
    $WarnEngineU = Join-Path $GameDir 'GearGame\Script\Engine.u'
    if (Test-Path $WarnEngineU) {
        $WBytes = [System.IO.File]::ReadAllBytes($WarnEngineU)
        $WHits = [GOW3SemanticByteLocator]::FindSpan($WBytes, (HexToBytes '7e0000000b0b07'), (HexToBytes '00849a383a1901d870000009008a30'), 1)
        if ($WHits.Count -eq 1) {
            if ($WBytes[$WHits[0]] -eq 0x51) {
                Backup $WarnEngineU
                $WBytes[$WHits[0]] = 0x53
                Set-ItemProperty -Path $WarnEngineU -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
                [System.IO.File]::WriteAllBytes($WarnEngineU, $WBytes)
                Say "  ok  Developer key messages restored (PlayerController.ClientMessage back to original)." 'Green'
            }
            elseif ($WBytes[$WHits[0]] -eq 0x53) {
                Say "  ok  PlayerController.ClientMessage is original (God / Ghost / Fly key messages work)." 'DarkGray'
            }
            else {
                Say "  error  ClientMessage target has an unexpected value. Skipped." 'Yellow'
                $warningsList.Add("Warning Messages: Target byte has unexpected value")
            }
        }
        else {
            Say "  error  ClientMessage target not found exactly once. Skipped." 'Yellow'
            $warningsList.Add("Warning Messages: Target not found or ambiguous")
        }
    }
    else {
        Say "  error  Engine.u not found. Skipped." 'Yellow'
        $warningsList.Add("Warning Messages: Engine.u not found")
    }

    $WarnTargets = @(
        @{ Name = 'GSM_DoorPush ALIGNTOOBJ warning'
           Pre  = '006f05010041000000000000'
           Span = '1baa290000000000001f5741524e494e473a20444f4f5220444f4553204e4f54204841564520414c49474e544f4f424a205345542050524f5045524c59004a4a16' },
        @{ Name = 'Unfriendly actor list warning'
           Pre  = '657300385600ba6500001616'
           Span = '1baa29000000000000a81f52616e206f7574206f66206672656520556e667269656e646c794163746f724c69737420656e747269657300385600ba650000164a4a16' },
        @{ Name = 'Friendly actor list warning'
           Pre  = '657300385600ba6500001616'
           Span = '1baa29000000000000a81f52616e206f7574206f66206672656520467269656e646c794163746f724c69737420656e747269657300385600ba650000164a4a16' },
        @{ Name = 'Queued speak line debug text'
           Pre  = '0008d8000032000000000000'
           Span = '1baa29000000000000a8a83856171f3a001635ecd70000f2d70000000001e4d70000164a4a16' },
        @{ Name = 'Reaver unhandled damage type text'
           Pre  = '00434601005a000000000000'
           Span = '1baa29000000000000a8a81f524944455245415645523a20556e68616e646c656420446d675479706500385719004946010009000cefffff00010cefffff163853004d460100164a4a16' }
    )
    # Revert any previously broken 0x0B byte patches in GearGame.u to protect script bytecode integrity
    $WarnGearGameU = Join-Path $GameDir 'GearGame\Script\GearGame.u'
    if (Test-Path $WarnGearGameU) {
        $WGBytes = [System.IO.File]::ReadAllBytes($WarnGearGameU)
        $needFix = $false
        foreach ($WT in $WarnTargets) {
            $o = HexToBytes ($WT.Pre + $WT.Span)
            $n = HexToBytes ($WT.Pre + ('0b' * ($WT.Span.Length / 2)))
            if ([GOW3SemanticByteLocator]::Replace(($WGBytes.Clone()), $n, $n) -eq 1) {
                [void][GOW3SemanticByteLocator]::Replace($WGBytes, $n, $o)
                $needFix = $true
            }
        }
        if ($needFix) {
            Backup $WarnGearGameU
            Set-ItemProperty -Path $WarnGearGameU -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
            [System.IO.File]::WriteAllBytes($WarnGearGameU, $WGBytes)
            Say "  ok  Restored GearGame.u bytecode integrity (resolved Bad import index crash)." 'Green'
        }
        else {
            Say "  ok  Developer key messages active (GearGame.u bytecode intact)." 'DarkGray'
        }
    }

    Header "26/$StepTotal Main Menu Quit Button (with Confirmation)"
    # The last main menu row ("Xbox LIVE Marketplace", Next=MAINMENUOP_DLC) is useless on PC: it only shows a "sign in to Xbox LIVE"
    # error. It becomes "Quit Game" and asks for confirmation (Yes / No) before closing the game.
    # No script compiler exists for this build, so two compiled UnrealScript functions in GearGame.u are rewritten in place (same
    # export size, padded with EX_Nothing; the MemorySize header field and the jump target are recomputed):
    #   UIScene_FrontEnd.OpenMarketplaceBlade   (the row's handler) now shows DisplayErrorMessage('QuitGame_Message', 'QuitGame_Title',
    #                                            'GenericYes,GenericNo', GetLP(), OnSeasonPassConfirm)
    #   UIScene_FrontEnd.OnSeasonPassConfirm    (Xbox season pass prompt, never shown on PC) now runs GetPC().ConsoleCommand("quit")
    #                                            when 'GenericYes' is chosen
    # The row label and the texts are plain INI / localization data (GearUI.ini, DefaultUI.ini, GearGameUI.* of every language).
    $QuitPatches = @(
        @{ Name = 'OpenMarketplaceBlade'
           Old = 'f501000075010000078201828282827701c42901002a161833001cd1f7ffff1901c42901000900b9f0ffff0001b9f0ffff52effaffff2a16161833001cd1f7ffff1901c42901000900baf0ffff0001baf0ffff52f0faffff2a16161849009a383a19511901c42901000900baf0ffff0001baf0ffff150055fcffff001b895e000000000000383d00022e010016383a240216161843008119511901c42901000900baf0ffff0001baf0ffff150019feffff001b2c70000000000000383d00022e010016161619511901c42901000900b9f0ffff0001b9f0ffff170009feffff001be7a4000000000000383d00022e01004a4a16142d01d9290100281901072a01000b0000000000001b0fb7000000000000271606f2011220e8e600005b00b0e60000001cbae600001f4e6565644c697665466f72446f776e6c6f616461626c65436f6e74656e74001f5369676e65644f75745f5469746c65001f47656e65726963436f6e74696e7565001b915e000000000000164a4a4a4a4a16040b53'
           New = '85010000750100001220e8e600006100b0e60000001cbae600001f5175697447616d655f4d657373616765001f5175697447616d655f5469746c65001f47656e657269635965732c47656e657269634e6f001b915e00000000000016435189000000000000000000004a4a4a4a160b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b040b53' },
        @{ Name = 'OnSeasonPassConfirm'
           Old = 'cb0000008f00000007be00fe008b2d010021435c0000000000001607be001cd1f7ffff1901c42901000900b9f0ffff0001b9f0ffff52effaffff2a1607be00973601bf290100251619511901c42901000900b9f0ffff0001b9f0ffff370009feffff001be7a4000000000000383d191b915e0000000000001609008cf0ffff00018cf0ffff4a102501bf290100160427043a892d010053'
           New = '9f0000008f000000079200fe008b2d0100214f5c000000000000160ea7e9ffff191b515f000000000000161100a7e9ffff001b692d0000000000001f71756974004a160b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0427043a892d010053' }
    )
    $QuitGearGameU = Join-Path $GameDir 'GearGame\Script\GearGame.u'
    $QuitReady = $false
    if (Test-Path $QuitGearGameU) {
        $QBytes = [System.IO.File]::ReadAllBytes($QuitGearGameU)
        $QState = @()
        foreach ($QP in $QuitPatches) {
            $QOld = HexToBytes $QP.Old; $QNew = HexToBytes $QP.New
            if ([GOW3SemanticByteLocator]::Replace(($QBytes.Clone()), $QOld, $QOld) -eq 1) { $QState += 'todo' }
            elseif ([GOW3SemanticByteLocator]::Replace(($QBytes.Clone()), $QNew, $QNew) -eq 1) { $QState += 'done' }
            else { $QState += 'bad' }
        }
        if ($QState -contains 'bad') {
            Say "  error  Quit button targets not found exactly once in GearGame.u. Skipped." 'Yellow'
            $warningsList.Add("Quit Button: GearGame.u functions not found or ambiguous")
        }
        else {
            if ($QState -contains 'todo') {
                Backup $QuitGearGameU
                for ($qi = 0; $qi -lt $QuitPatches.Count; $qi++) {
                    if ($QState[$qi] -eq 'todo') {
                        [void][GOW3SemanticByteLocator]::Replace($QBytes, (HexToBytes $QuitPatches[$qi].Old), (HexToBytes $QuitPatches[$qi].New))
                    }
                }
                Set-ItemProperty -Path $QuitGearGameU -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
                [System.IO.File]::WriteAllBytes($QuitGearGameU, $QBytes)
                Say "  ok  GearGame.u patched (quit confirmation dialog)." 'Green'
            }
            else {
                Say "  ok  Quit confirmation dialog is already patched in GearGame.u." 'DarkGray'
            }
            $QuitReady = $true
        }
    }
    else {
        Say "  error  GearGame.u not found. Skipped." 'Yellow'
        $warningsList.Add("Quit Button: GearGame.u not found")
    }

    if ($QuitReady) {
        $QuitRowOld = '(Label="DLC",Next=MAINMENUOP_DLC,bHaltProgression=true)'
        $QuitRowNew = '(Label="Quit",Next=MAINMENUOP_DLC,bHaltProgression=true)'
        foreach ($ini in @( (Join-Path $ConfigDir "DefaultUI.ini"), (Join-Path $ConfigDir "GearUI.ini") )) {
            if (-not (Test-Path $ini)) { continue }
            $iniTxt = [System.IO.File]::ReadAllText($ini)
            if ($iniTxt.Contains($QuitRowOld)) {
                Backup $ini
                Set-ItemProperty -Path $ini -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
                [System.IO.File]::WriteAllText($ini, $iniTxt.Replace($QuitRowOld, $QuitRowNew))
                Say "  ok  Main menu row changed to Quit Game in $(Split-Path -Leaf $ini)." 'Green'
            }
            elseif ($iniTxt.Contains($QuitRowNew)) {
                Say "  ok  Quit Game row is already set in $(Split-Path -Leaf $ini)." 'DarkGray'
            }
            else {
                Say "  error  Main menu DLC row not found in $(Split-Path -Leaf $ini). Skipped." 'Yellow'
                $warningsList.Add("Quit Button: main menu row not found in $(Split-Path -Leaf $ini)")
            }
        }

        $QuitTexts = @{
            'INT' = @('Quit Game', 'Exit Gears of War 3 and return to the desktop.', 'Quit Game', 'Are you sure you want to quit the game?')
            'ESM' = @('Salir del juego', 'Cierra Gears of War 3 y vuelve al escritorio.', 'Salir del juego', ([string][char]0xBF + 'Seguro que quieres salir del juego?'))
            'ITA' = @('Esci dal gioco', 'Chiudi Gears of War 3 e torna al desktop.', 'Esci dal gioco', 'Sei sicuro di voler uscire dal gioco?')
            'TUR' = @(('Oyundan ' + [char]0xC7 + [char]0x0131 + 'k'), ('Gears of War 3' + [char]0x27 + 'ten ' + [char]0xE7 + [char]0x0131 + 'k' + [char]0x0131 + 'p masa' + [char]0xFC + 'st' + [char]0xFC + 'ne d' + [char]0xF6 + 'n.'), ('Oyundan ' + [char]0xC7 + [char]0x0131 + 'k'), ('Oyundan ' + [char]0xE7 + [char]0x0131 + 'kmak istedi' + [char]0x011F + 'inizden emin misiniz?'))
        }
        $QuitLocFiles = Get-ChildItem -Path (Join-Path $GameDir 'GearGame\Localization') -Recurse -File -Filter 'geargameui.*' -ErrorAction SilentlyContinue | Where-Object { $_.Extension -notmatch 'bak' }
        foreach ($LF in $QuitLocFiles) {
            $LangCode = $LF.Directory.Name.ToUpper()
            $LBytes = [System.IO.File]::ReadAllBytes($LF.FullName)
            if ($LBytes.Length -ge 2 -and $LBytes[0] -eq 0xFF -and $LBytes[1] -eq 0xFE) { $LEnc = [System.Text.Encoding]::Unicode; $LHasBom = $true; $LTxt = $LEnc.GetString($LBytes, 2, $LBytes.Length - 2) }
            else { $LEnc = [System.Text.Encoding]::GetEncoding(28591); $LHasBom = $false; $LTxt = $LEnc.GetString($LBytes) }
            $QT = $QuitTexts[$LangCode]
            if ($null -eq $QT -or (-not $LHasBom -and $LangCode -ne 'INT')) { $QT = $QuitTexts['INT'] }
            if ($LTxt -match '(?m)^QuitGame_Message=') {
                Say "  ok  [$LangCode] Quit texts are already present in $($LF.Name)." 'DarkGray'
                continue
            }
            $LNl = if ($LTxt.Contains("`r`n")) { "`r`n" } else { "`n" }
            $LNew = $LTxt
            $mm = [regex]::Match($LNew, '(?m)^\[MainMenu\](?=\r?$)')
            if ($mm.Success) { $LNew = $LNew.Insert($mm.Index + $mm.Length, $LNl + 'Quit=' + $QT[0] + $LNl + 'TOOLTIP_Quit=' + $QT[1]) }
            $mm = [regex]::Match($LNew, '(?m)^\[MessageBoxErrorStrings\](?=\r?$)')
            if ($mm.Success) { $LNew = $LNew.Insert($mm.Index + $mm.Length, $LNl + 'QuitGame_Title=' + $QT[2] + $LNl + 'QuitGame_Message=' + $QT[3]) }
            if ($LNew -notmatch '(?m)^QuitGame_Message=' -or $LNew -notmatch '(?m)^Quit=') {
                Say "  error  [$LangCode] [MainMenu] / [MessageBoxErrorStrings] sections not found in $($LF.Name). Skipped." 'Yellow'
                $warningsList.Add("Quit Button: sections not found in $($LF.Name)")
                continue
            }
            Backup $LF.FullName
            Set-ItemProperty -Path $LF.FullName -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
            $LOut = $LEnc.GetBytes($LNew)
            if ($LHasBom) { $LOut = [byte[]](0xFF, 0xFE) + $LOut }
            [System.IO.File]::WriteAllBytes($LF.FullName, $LOut)
            Say "  ok  [$LangCode] Quit Game texts added to $($LF.Name)." 'Green'
        }
    }

    Header "27/$StepTotal Mercy (Act 3) Dom's COG Tags and Knife Hand-over Fix"
    # In the Found_Pump cinematic Dom takes his COG tag with Maria's necklace out and leaves it in the mausoleum shrine
    # (DOM_MC_17, "Maria... Baby, I'm sorry I couldn't bring you home"). The matinee group 'COG-Tags' (AnimSet Cine_Props01.Skel.Tags,
    # Mercy_Cine_05_Shot_*_Necklace_Take) is bound to Kismet variable SeqVar_Object_67, which had no actor in this build, so the
    # tags never appeared. The patched map adds a SkeletalMeshActorMAT with Cine_Props01.Skel.GOW_Cine_Props01_Tags_SK (clone of
    # Dom's matinee actor, no attachments) to Level.Actors and binds it to the variable. The package grows (new import, name, three
    # exports), so the whole file is replaced; it is only replaced when the installed map is the known original (SHA256 check).
    # The same map also fixes Marcus never receiving Dom's knife: the MarcusKnife visibility track only hid the knife at 1.0, a 'Show' key at
    # 111.81 (the moment Dom's knife is hidden) was added; its bHardAttach flag (set only on that knife) is cleared so it follows Marcus's hand.
    # Generator: _dev\mercy_tags_patch.py
    $TagsMap = Join-Path $GameDir 'GearGame\Content\Maps\SP\SP_09_Mercy\SP_Mercy_Cine_FoundPump.gear'
    $TagsSrc = Join-Path $FilesDir 'SP_Mercy_Cine_FoundPump.gear'
    $TagsOldPatchedHashes = @('E6F2BBF49DB49FAD3A8E040A4B67EAB26AD7B3E1BBDE418572B20DF3E4D79557', 'EA996AE5C59789DBB2876DF29A133FEE3238B719F9583FC99E98015386DCBE03', '7D07396D06A29BCC22FE156B6736C8AFC08F682EB614C6039ADA0AE236B91E02', '8244CAB169E157BD76D8485507910316F4F308393232085F04487E657D051471', '7CBAC45F206582AE4DC91B4F063FD2DB2D26BCFF26ADA19EFB373AE1D79DBE18', '261922B93FC6E361FE259790939DAF24367C9660BAD1104679D0D0A0D48D0392')   # earlier builds of this fix (tags only / tags + knife), updated in place
    $TagsOrigHash = 'A329705F445917F78E01C239E1B8616FCE3E8F137E404AA106E6ACD386B27A07'
    $TagsPatchedHash = '716FE26E2588F798AF7EE21EFCD67DC5010FE5FA28FE52473C214681AD06B5FE'
    if (Test-Path $TagsMap) {
        $TagsCur = (Get-FileHash -LiteralPath $TagsMap -Algorithm SHA256).Hash.ToUpperInvariant()
        if ($TagsCur -eq $TagsPatchedHash) {
            Say "  ok  Dom's COG tags scene fix is already applied." 'DarkGray'
        }
        elseif ($TagsCur -ne $TagsOrigHash -and $TagsOldPatchedHashes -notcontains $TagsCur) {
            Say "  skip  SP_Mercy_Cine_FoundPump.gear is not the original Jacinto v1.1.1 file (SHA256 differs). Dom's COG tags fix skipped." 'Yellow'
            $warningsList.Add("Dom COG Tags Fix: map is not the known original file")
        }
        elseif (-not (Test-Path $TagsSrc) -or (Get-FileHash -LiteralPath $TagsSrc -Algorithm SHA256).Hash.ToUpperInvariant() -ne $TagsPatchedHash) {
            Say "  skip  _files\SP_Mercy_Cine_FoundPump.gear is missing or damaged. Dom's COG tags fix skipped." 'Yellow'
            $warningsList.Add("Dom COG Tags Fix: _files\SP_Mercy_Cine_FoundPump.gear missing or damaged")
            if (-not $packageFilesBad.Contains('SP_Mercy_Cine_FoundPump.gear')) { $packageFilesBad.Add('SP_Mercy_Cine_FoundPump.gear (outdated)') }
        }
        else {
            Backup $TagsMap
            Set-ItemProperty -Path $TagsMap -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
            Copy-Item -LiteralPath $TagsSrc -Destination $TagsMap -Force
            Say "  ok  Dom's COG tags / Maria necklace added to the Mercy mausoleum cinematic." 'Green'
        }
    }
    else {
        Say "  error  SP_Mercy_Cine_FoundPump.gear not found. Skipped." 'Yellow'
        $warningsList.Add("Dom COG Tags Fix: map file not found")
    }

    Header "28/$StepTotal Mother Corpser Encounter Restoration (Act 2 Chapter 4)"
    # Complete replacement of the earlier Mother Corpser fixes (map patch, stand-in patch, GearGame.u AI byte patches). Contributed restoration of the
    # retail encounter: egg chamber waves, per-eye pressure waves, three MAMASTOMP ceiling intermissions, four collapsing pillars, the timed escape,
    # objective 9, outro hand-off to SP_Badlands_14_S, plus the Mother eye-hit script fixes (GearPawn.DoDamageEffects, GearPawn_LocustMomCorpserBase
    # .GetHitBoneIndex / .ShouldTakeDamage, already part of _files\GearGame.u).
    # Files (all in _files; installed only when EVERY file passes the checks below, because the maps reference the four CorpserRestor_*.upk packages):
    #   maps  SP_Badlands_13_S / SP_Badlands_Cine_Corpser_Outro / SP_Badlands_Cine_EggRoom / SP_Badlands2_P   -> GearGame\Content\Maps\SP\SP_05_Badlands
    #   upk   CorpserRestor_Audio / _BattleAudio / _InterwaveFX / _PillarChunks                              -> GearGame\Content (new files)
    # A map is replaced only when it is the original Jacinto v1.1.1 file or an earlier build of our own fix (hashes below). The old AI byte patches
    # (Hide / NoHide / Mad / Cringe / PickAttackType) are gone: the new map expects the original AI functions.
    # NOTE: the game restores the level logic (Kismet) from the checkpoint save, so a fight already in progress keeps the old logic: start the chapter again.
    $CpMapDir = Join-Path $GameDir 'GearGame\Content\Maps\SP\SP_05_Badlands'
    $CpContentDir = Join-Path $GameDir 'GearGame\Content'
    $CpOld13S = @('10D06FAA4E444329C385DEDFF0D4D61DCB4D633D20CB4B63A0A73063A69890EF', 'F585B24E5C77CEF5A7244B5691939F7138F5E4511F87302365CE0B192FDA9DA2',
        '3084B1928E73E84F65BFCE34C435213D5059A338289F0CF2DA5E0FC935D281B0', '941417F3669EFA52C064920A1B9EEE7C1DC00A7FFC52417C55ED54561F9F7D96', '4FD8CC37B1E943ABCB6D5968122504B50B61CEB2A2C6241469ED784ED415E6A0',
        'A8BA4E0F11E842D82460F1AB40E4C7045085624A7DA2065846F19C6F34A18DFD', 'BC901D50A2773B071654D31C63C5980ABD16F7580D1DEAA5D1EDC30DC544E674', '79A10FD25F8F4ECD102C9B3D45347760DF33D3902C84798CC9619FEA8A4AFA7C',
        '1D7BCDBF617A4409529B501CDDAFE30CF2206179BFE8696224CFB3B593F99F73', '8D40B2AEB518E35617B3DD6E4AC823465F745EAAC8261D5D20C0C34D9A02C614', '818ADC989017EF3176DB3431FD495BA1B8024C46B2698A35230C5A26DA77FE80',
        '4FCD6133E056A406CD9B1F66D8B16ABBF112CBC37F4D6B3A6C29B20DDE423C5A', 'D4C1C985BE9F3D8FC267C509ECC5BE0ADAF690F777D9077B29014C97EF2F57FF', '58D3D3C6EAD121107EAC0C0F4A717553CF7427FC030AE482D6BAD67CDD8F11F9',
        '090719B26ADB3FB54AE94B111C83C46E3D47345E015C96AF5BE4331C78730ABB', '8C79D34DEBE1B4C2501C638A66DEB1CA0417DDCC3B340FE1105B259C4EBBBBCB', '2B4C70D4B052A844FC07BB5F070DF65D4DC7C7798508CEA3EC74AAB28BEFA3F2',
        '6B1BA69D98CF2216C042EE3615261E3B860A7AC1DD69C9B6F324E812D93FCFDC')
    $CpFiles = @(
        @{ Name = 'SP_Badlands_13_S.gear'; Dir = $CpMapDir; New = '82F33460C81EF1EC43394C357C6C5C2F0277E6752F5102181033A2334D625DDA'; Old = $CpOld13S },
        @{ Name = 'SP_Badlands_Cine_Corpser_Outro.gear'; Dir = $CpMapDir; New = '92DE4BD7A4AD639C236463B4FC3C319988B5B4E1384E3ADA1969CF66F2A2B33A'
           Old = @('8E0BF1A05EC8DF085D4237AD57869059848F751AAAF52C8B5BE00CE8DAE3896E', 'B11AACF8F2FCCA65F54BBCBB998352B1AA0109CA65AE6763958F2B16B2E61FCA') },
        @{ Name = 'SP_Badlands_Cine_EggRoom.gear'; Dir = $CpMapDir; New = 'C1F90CCFAC8BF2A124132BC8D3D389E89604E434700B38A586BCBDBAA1640C2E'
           Old = @('AF5285643EA0D6966C96D758567B38BB0D750A0DAA19C0AF522064B4EB2E7113') },
        @{ Name = 'SP_Badlands2_P.gear'; Dir = $CpMapDir; New = 'F75FD33180DE363D218D5648565F52B80D1E0D57D9357831B7CA43F8D342CA7E'
           Old = @('59E0F7DED4E2BB157F79FF2B6DD34FF54217F3B760DDC28063A307CE5749A988') },
        @{ Name = 'CorpserRestor_Audio.upk'; Dir = $CpContentDir; New = 'C0ABCD32058C179DB7D0D73A5E0E292BB252872B958E103A0580F3704C97E293'; Old = @() },
        @{ Name = 'CorpserRestor_BattleAudio.upk'; Dir = $CpContentDir; New = 'BFC82658DBBEF8ED44D409845B5D6F922795D13138C292F24D820BAECFA18A9B'; Old = @() },
        @{ Name = 'CorpserRestor_InterwaveFX.upk'; Dir = $CpContentDir; New = '8D0F0E5E643263C815730A2CA1D0A55E859111F0C0133C2548DB8EEE87326CEE'; Old = @() },
        @{ Name = 'CorpserRestor_PillarChunks.upk'; Dir = $CpContentDir; New = '3245CB1A8C0843BDDD24BBC46A778ADD5BFEFC008C43819DE7239A6F9A2BFD90'; Old = @() }
    )
    $CpProblems = [System.Collections.Generic.List[string]]::new()
    $CpCopy = [System.Collections.Generic.List[hashtable]]::new()
    $CpSourceBad = $false
    foreach ($cf in $CpFiles) {
        $cfSrc = Join-Path $FilesDir $cf.Name
        $cfDst = Join-Path $cf.Dir $cf.Name
        if (-not (Test-Path -LiteralPath $cfSrc) -or (Get-FileHash -LiteralPath $cfSrc -Algorithm SHA256).Hash.ToUpperInvariant() -ne $cf.New) {
            $CpProblems.Add("_files\$($cf.Name) is missing or damaged"); $CpSourceBad = $true; continue
        }
        if (Test-Path -LiteralPath $cfDst) {
            $cfCur = (Get-FileHash -LiteralPath $cfDst -Algorithm SHA256).Hash.ToUpperInvariant()
            if ($cfCur -eq $cf.New) { continue }
            if ($cf.Old.Count -eq 0 -or ($cf.Old -contains $cfCur)) { $CpCopy.Add(@{ Src = $cfSrc; Dst = $cfDst; Name = $cf.Name }) }
            else { $CpProblems.Add("$($cf.Name) is not the original Jacinto v1.1.1 file (SHA256 differs)") }
        }
        elseif ($cf.Old.Count -eq 0) { $CpCopy.Add(@{ Src = $cfSrc; Dst = $cfDst; Name = $cf.Name }) }
        else { $CpProblems.Add("$($cf.Name) not found in the game folder") }
    }
    # the maps expect the Mother eye-hit script fixes: the shipped GearGame.u (copied in step 6) must contain the patched GearPawn.DoDamageEffects
    $CpScript = Join-Path $GameDir 'GearGame\Script\GearGame.u'
    if (Test-Path -LiteralPath $CpScript) {
        $CpUBytes = [System.IO.File]::ReadAllBytes($CpScript)
        $CpMarker = HexToBytes '070d02722e92c10000172a16'
        if ([GOW3SemanticByteLocator]::Replace(($CpUBytes.Clone()), $CpMarker, $CpMarker) -ne 1) { $CpProblems.Add("GearGame.u does not contain the Mother Corpser script fixes (use the GearGame.u of this package)") }
        $CpUBytes = $null
    }
    else { $CpProblems.Add("GearGame.u not found") }

    if ($CpProblems.Count -gt 0) {
        foreach ($cpp in $CpProblems) { Say "  skip  $cpp" 'Yellow' }
        Say "  skip  Mother Corpser restoration skipped (all of its files are installed together or not at all)." 'Yellow'
        $warningsList.Add("Mother Corpser Restoration: " + ($CpProblems -join '; '))
        if ($CpSourceBad -and -not $packageFilesBad.Contains('Mother Corpser files')) { $packageFilesBad.Add('Mother Corpser files (outdated)') }
    }
    elseif ($CpCopy.Count -eq 0) {
        Say "  ok  Mother Corpser encounter restoration is already installed." 'DarkGray'
    }
    else {
        foreach ($cc in $CpCopy) {
            Backup $cc.Dst
            if (Test-Path -LiteralPath $cc.Dst) { Set-ItemProperty -LiteralPath $cc.Dst -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue }
            Copy-Item -LiteralPath $cc.Src -Destination $cc.Dst -Force
            Say "  ok  $($cc.Name)" 'DarkGray'
        }
        Say "  ok  Mother Corpser encounter restored ($($CpCopy.Count) files)." 'Green'
        # A checkpoint saved while the boss room (13_S) was loaded holds the OLD level logic (Kismet); loading it with the restored map crashes the game
        # ("CurrentIndex+Count<=CompressedData.Num()" in UCheckpoint::LoadData). Only on the FIRST install of the restoration (the CorpserRestor_Audio.upk
        # did not exist yet) such a save is moved to _files\CheckpointStash (outside the backups, so a later re-install never puts it back or touches the
        # new saves). Re-installs and fresh installs without such a save leave the checkpoints alone. Uninstall: see Uninstall-Multipatch.
        $CpFirstInstall = (@($CpCopy | Where-Object { $_.Name -eq 'CorpserRestor_Audio.upk' })).Count -gt 0
        $CpSaveDir = Join-Path $GameDir 'GearGame\SaveData'
        if ($CpFirstInstall -and (Test-Path -LiteralPath $CpSaveDir)) {
            $CpStash = Join-Path (Split-Path $BackupRoot) 'CheckpointStash'
            foreach ($cps in @(Get-ChildItem -LiteralPath $CpSaveDir -Filter 'GearsCheckpoint*.sav' -File -ErrorAction SilentlyContinue)) {
                if ($iso88591.GetString([System.IO.File]::ReadAllBytes($cps.FullName)).Contains('SP_Badlands_13_S')) {
                    New-Item -ItemType Directory -Force -Path $CpStash | Out-Null
                    Move-Item -LiteralPath $cps.FullName -Destination (Join-Path $CpStash $cps.Name) -Force
                    Say "  ok  Old Mother Corpser checkpoint ($($cps.Name)) set aside: it is incompatible with the restored fight. Pick Act 2 Chapter 4 again." 'Yellow'
                }
            }
        }
    }

    Header "29/$StepTotal Debug Launcher (Gears3_Start_Game_Debug.bat / .sh)"
    # Creates / refreshes Gears3_Start_Game_Debug.bat next to Gears3_Start_Game.bat. It starts the game with -log -forcelogflush (a log console
    # opens next to the game), reuses the resolution / window / language arguments of Gears3_Start_Game.bat and keeps a timestamped copy of
    # Launch.log in DebugLogs\ when the game exits.
    if (-not $IsWin) {
        $DebugShPath = Join-Path $GameDir 'Gears3_Start_Game_Debug.sh'
        $DebugShText = @'
#!/bin/sh
# Debug launcher (Linux / SteamOS): starts the game with -log and keeps a timestamped copy of Launch.log in DebugLogs/
DIR="$(cd "$(dirname "$0")" && pwd)"
ARGS=$(sed -n 's/^ARGS="\(.*\)"$/\1/p' "$DIR/Gears3_Start_Game.sh")
[ -z "$ARGS" ] && ARGS="-WINDOWED -language=INT"
echo "Starting GearGame.exe $ARGS -log -forcelogflush"
cd "$DIR/Binaries/Win64" || exit 1
if command -v wine >/dev/null 2>&1; then
    wine GearGame.exe $ARGS -log -forcelogflush || {
        echo "If the log shows 'DwTriovizCreatePixelShader': install the DirectX DLLs: protontricks <AppID> d3dcompiler_43 d3dx9_43 (or winetricks)"
    }
else
    echo "wine not found. Add GearGame.exe as a non-Steam game (Proton) with launch options: $ARGS -log -forcelogflush"
    exit 1
fi
mkdir -p "$DIR/DebugLogs"
TS=$(date +%Y%m%d_%H%M%S)
cp "$DIR/GearGame/Logs/Launch.log" "$DIR/DebugLogs/Launch_$TS.log" && echo "Log saved: DebugLogs/Launch_$TS.log"
'@
        [System.IO.File]::WriteAllText($DebugShPath, (($DebugShText -replace "`r`n", "`n").TrimEnd() + "`n"), (New-Object System.Text.UTF8Encoding($false)))
        & chmod +x $DebugShPath
        Say "  ok  Gears3_Start_Game_Debug.sh created." 'Green'
    }
    else {
    $DebugBatPath = Join-Path $GameDir 'Gears3_Start_Game_Debug.bat'
    $DebugBatText = @'
@echo off
title Gears of War 3 Jacinto - DEBUG (log)
rem Reuses resolution / language / window mode from Gears3_Start_Game.bat
set "ARGS=-WINDOWED -language=INT"
for /f "usebackq tokens=*" %%L in (`findstr /i /c:"GearGame.exe -" "%~dp0Gears3_Start_Game.bat"`) do set "LINE=%%L"
if defined LINE set "ARGS=%LINE:*GearGame.exe =%"
echo Starting GearGame.exe %ARGS% -log -forcelogflush
echo A log console window opens next to the game. Close the game normally when done.
cd /d "%~dp0Binaries\Win64"
GearGame.exe %ARGS% -log -forcelogflush
rem Keep a timestamped copy of the log (the game rotates Launch.log on next start)
for /f %%T in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd_HHmmss"') do set "TS=%%T"
if not exist "%~dp0DebugLogs" mkdir "%~dp0DebugLogs"
copy /y "%~dp0GearGame\Logs\Launch.log" "%~dp0DebugLogs\Launch_%TS%.log" >nul
echo Log saved: DebugLogs\Launch_%TS%.log
pause
'@
    $DebugBatText = ($DebugBatText -replace "`r`n", "`n") -replace "`n", "`r`n"
    $DebugBatCur = if (Test-Path $DebugBatPath) { [System.IO.File]::ReadAllText($DebugBatPath) } else { $null }
    if ($DebugBatCur -and (($DebugBatCur.TrimEnd() -replace "`r`n", "`n") -eq ($DebugBatText.TrimEnd() -replace "`r`n", "`n"))) {
        Say "  ok  Gears3_Start_Game_Debug.bat is already up to date." 'DarkGray'
    }
    else {
        try {
            if ($DebugBatCur) { Backup $DebugBatPath }
            [System.IO.File]::WriteAllText($DebugBatPath, $DebugBatText + "`r`n", (New-Object System.Text.ASCIIEncoding))
            Say "  ok  Gears3_Start_Game_Debug.bat created." 'Green'
        }
        catch {
            Say "  error  Could not write Gears3_Start_Game_Debug.bat: $($_.Exception.Message)" 'Yellow'
            $warningsList.Add("Debug Launcher: could not write Gears3_Start_Game_Debug.bat")
        }
    }
    }
    [void](Update-LauncherFiles (Get-GameSettings))   # the debug launchers carry the same startup parameters as the start launcher

    Header "30/$StepTotal Campaign Network (co-op listen server on port 7777, or offline mode)"
    # The Campaign URL (GearMenuGame.BuildCampaignURL) ends with "?listen", so a started Campaign accepts co-op players (LAN: open <IP>:7777,
    # Steam: JacintoNet invites). Up to v0.3.3 this step could change it to "?nolist" (offline-only) against the 'Connection Lost' error on
    # Linux / Proton; that error came from the privileged default port 1000, which the INI step now sets to 7777 for everybody. GearGame.u is
    # copied fresh from _files in step 6, so this step only makes sure no "?nolist" is left (e.g. a GearGame.u from an older install).
    $ListenOld = '1f3f6c6f6164636865636b706f696e7400160ed0e9ffff614200d0a500001f3f6c697374656e0016'
    $ListenNew = '1f3f6c6f6164636865636b706f696e7400160ed0e9ffff614200d0a500001f3f6e6f6c6973740016'
    $ListenGearGameU = Join-Path $GameDir 'GearGame\Script\GearGame.u'
    # Full offline mode (asked after the Steam question, only when Steam co-op is off): "?listen" -> "?nolist", no listen server at all.
    if ((Test-Path $ListenGearGameU) -and $offlineMode) {
        $LBytes = [System.IO.File]::ReadAllBytes($ListenGearGameU)
        $LOld = HexToBytes $ListenOld; $LNew = HexToBytes $ListenNew
        if ([GOW3SemanticByteLocator]::Replace(($LBytes.Clone()), $LNew, $LNew) -eq 1) { Say "  ok  Campaign is already offline-only (no listen server)." 'DarkGray' }
        elseif ([GOW3SemanticByteLocator]::Replace(($LBytes.Clone()), $LOld, $LOld) -eq 1) {
            Backup $ListenGearGameU
            [void][GOW3SemanticByteLocator]::Replace($LBytes, $LOld, $LNew)
            Set-ItemProperty -Path $ListenGearGameU -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
            [System.IO.File]::WriteAllBytes($ListenGearGameU, $LBytes)
            Say "  ok  Campaign set to offline only (listen server off, nobody can join)." 'Green'
        }
        else {
            Say "  error  Campaign URL not found exactly once in GearGame.u. Offline mode skipped." 'Yellow'
            $warningsList.Add("Campaign Network: offline mode target not found")
        }
    }
    elseif (Test-Path $ListenGearGameU) {
        $LBytes = [System.IO.File]::ReadAllBytes($ListenGearGameU)
        $LOld = HexToBytes $ListenOld; $LNew = HexToBytes $ListenNew
        if ([GOW3SemanticByteLocator]::Replace(($LBytes.Clone()), $LNew, $LNew) -eq 1) {
            Backup $ListenGearGameU
            [void][GOW3SemanticByteLocator]::Replace($LBytes, $LNew, $LOld)
            Set-ItemProperty -Path $ListenGearGameU -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
            [System.IO.File]::WriteAllBytes($ListenGearGameU, $LBytes)
            Say "  ok  Campaign listen server restored (was offline-only)." 'Green'
        }
        else {
            Say "  ok  Campaign accepts co-op players (listen server on port 7777)." 'DarkGray'
        }
    }
    else {
        Say "  error  GearGame.u not found. Skipped." 'Yellow'
        $warningsList.Add("Campaign Network: GearGame.u not found")
    }

    Header "31/$StepTotal Wreckage (Act 2) Brumak Fights: Chapter End After a Checkpoint Reload, Placeholder Death Scenes"
    # The chapter-end transition (SP_Wreckage_P Transition_to_Next_Level) needs PREPARE_NextLevel (sent once by CombatD at the Brumak
    # intro, BEFORE its checkpoint) and COMMIT_NextLevel (sent by the Brumak death cine). A checkpoint reload restores the streamed levels
    # but not the pending PrepareMapChange, so after dying once in the fight the commit waits forever (no outro, soft-lock).
    # Fix (3 same-size link edits): COMMIT_NextLevel now runs the PrepareMapChange itself, its Finished goes straight to
    # ChapterComplete/CommitMapChange, and CombatD's early PREPARE_NextLevel call is renamed to None (it would double-fire Finished).
    $WreckDir = Join-Path $GameDir 'GearGame\Content\Maps\SP\SP_04_Wreckage'
    $BrumakRes = @()
    $BrumakOk = (Test-Path (Join-Path $WreckDir 'SP_Wreckage_P.gear')) -and (Test-Path (Join-Path $WreckDir 'SP_Wreckage_01_S_CombatD.gear'))
    if (-not $BrumakOk) {
        Say "  error  SP_Wreckage_P.gear / SP_Wreckage_01_S_CombatD.gear not found. Skipped." 'Yellow'
        $warningsList.Add("Wreckage Brumak Chapter End Fix: Map file not found")
    }
    else {
        foreach ($E in $BrumakEndEdits) {
            # CombatD (last edit) is only changed once both SP_Wreckage_P edits are in place; otherwise the prepare would be lost.
            if ($E.Map -like '*CombatD*' -and ($BrumakRes -contains 'error')) { $BrumakRes += 'error'; continue }
            $BrumakRes += (Set-IntAtContext (Join-Path $WreckDir $E.Map) $E.Pre $E.Suf $E.Old $E.New)
        }
        if ($BrumakRes -contains 'error') {
            Say "  error  Wreckage Brumak fix targets not found exactly once or unexpected values. Skipped." 'Yellow'
            $warningsList.Add("Wreckage Brumak Chapter End Fix: Targets not found or ambiguous")
        }
        elseif ($BrumakRes -contains 'applied') {
            Say "  ok  Final Brumak chapter end now works after a checkpoint reload." 'Green'
        }
        else {
            Say "  ok  Wreckage Brumak chapter end fix is already applied." 'DarkGray'
        }
    }
    $StubRes = @(foreach ($E in $BrumakStubEdits) {
        $F = Join-Path $WreckDir $E.Map
        if (Test-Path $F) { Set-IntAtContext $F $E.Pre $E.Suf $E.Old $E.New } else { 'error' }
    })
    if ($StubRes -contains 'error') {
        Say "  error  Brumak death placeholder targets not found exactly once or unexpected values. Skipped." 'Yellow'
        $warningsList.Add("Wreckage Brumak Death Placeholders: Targets not found or ambiguous")
    }
    elseif ($StubRes -contains 'applied') {
        Say "  ok  Brumak death placeholders (camera pull-back + 'BRUMAK DEATH STUB' text) removed." 'Green'
    }
    else {
        Say "  ok  Brumak death placeholder removal is already applied." 'DarkGray'
    }

    Header "32/$StepTotal Co-op: Player 2 / Joining Players Use the Host's Difficulty"
    # Player 2's difficulty is re-read from Player 2's profile on every map load (GearPC.UpdateDifficultySetting, profile id 47), but
    # the campaign launch never writes that profile, so the choice on the difficulty screen is lost and Player 2 is always Normal.
    # GearPC.ServerSetDifficulty: its Arcade block ("one difficulty for all players") is replaced by a block of the same size
    # (106 bytes disk / 166 memory, jump targets 0x0147 / 0x0148 / 0x0149 unchanged):
    #     foreach LocalPlayerControllers(class'GearPC', OtherPC)
    #         if (OtherPC != self && OtherPC.IsPrimaryPlayer())
    #             DiffSettings = GearPRI(OtherPC.PlayerReplicationInfo).Difficulty;
    # LocalPlayerControllers only lists the host's own players, so: host Player 1 keeps its choice, the split-screen Player 2
    # and every online player who joins the host's Campaign get the host's difficulty (their own profile pick is ignored).
    # Independent of the list order (an earlier version relied on it and failed in game). Two filler statements
    # (read OtherPC, 3x EX_Nothing) keep the size. Host changing difficulty in the pause menu restarts the chapter, which re-syncs everybody.
    $CoopDiffOld = '074901772e5d8700001901c4eeffff0900ecedffff0001ecedffff2a162f1c6bf7ffff20dd70000000ab6900001648010744017700ab69000017160f00ae690000192e1a8f00001900ab6900000900a4eeffff0001a4eeffff0900e18d00000001e18d00000648013130'
    $CoopDiffNew = '00ab6900000b0b0b2f1c6bf7ffff20dd70000000ab690000164801074701827700ab69000017161820001900ab6900000a0083fdffff001ccaf3ffff16160f00ae690000192e1a8f00001900ab6900000900a4eeffff0001a4eeffff0900e18d00000001e18d00003130'
    $CoopDiffGearGameU = Join-Path $GameDir 'GearGame\Script\GearGame.u'
    if (Test-Path $CoopDiffGearGameU) {
        $CBytes = [System.IO.File]::ReadAllBytes($CoopDiffGearGameU)
        $COld = HexToBytes $CoopDiffOld; $CNew = HexToBytes $CoopDiffNew
        if ([GOW3SemanticByteLocator]::Replace(($CBytes.Clone()), $CNew, $CNew) -eq 1) {
            Say "  ok  Co-op players already use the host's difficulty." 'DarkGray'
        }
        elseif ([GOW3SemanticByteLocator]::Replace(($CBytes.Clone()), $COld, $COld) -eq 1) {
            Backup $CoopDiffGearGameU
            [void][GOW3SemanticByteLocator]::Replace($CBytes, $COld, $CNew)
            Set-ItemProperty -Path $CoopDiffGearGameU -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
            [System.IO.File]::WriteAllBytes($CoopDiffGearGameU, $CBytes)
            Say "  ok  Split-screen Player 2 and online co-op players now play on the host's difficulty." 'Green'
        }
        else {
            Say "  error  Co-op difficulty target not found exactly once in GearGame.u. Skipped." 'Yellow'
            $warningsList.Add("Co-op Player 2 Difficulty: GearGame.u target not found or ambiguous")
        }
    }
    else {
        Say "  error  GearGame.u not found. Skipped." 'Yellow'
        $warningsList.Add("Co-op Player 2 Difficulty: GearGame.u not found")
    }

    Header "33/$StepTotal Split-screen Co-op: Second Controller Joins, Controller 1 Stays Player 1"
    # The PC online subsystem only knows user 0 (log: "Invalid user index (1)"): only controller 0 has a saved profile.
    # a) UIScene_FrontEnd.UpdatePlayersAndControllers (main menu open + every controller plug / unplug) only created a player for a
    #    signed-in controller, so controller 2 never joined when controller 1 pressed Start. The "if (bIsSignedIn)" JumpIfNot target
    #    is moved from the guest branch (0x04c3) to its own next statement (0x0458): every connected controller gets a player.
    # b) The controller that pressed Start on the title screen was made the primary player (Player 1, owner of the campaign save and
    #    of the difficulty). With controller 2 that is the profile the PC version cannot read or save: difficulty always Normal,
    #    chapter progress lost. Both BecomePrimaryPlayer calls now get an index <= 0, which BecomePrimaryPlayer ignores:
    #    Callback_TransitionFromStart passes local i (still 0) instead of PlayerIndex, UnregisteredInput passes
    #    PendingActivatedControllerId (-1 / 0 on the title screen) instead of ControllerId. Controller 0 always stays Player 1.
    # All edits keep the size (2-byte jump target, local / instance variable refs).
    $CoopJoinEdits = @(
        @{ Old = '07cf04812d00ab2d01001607c3042d00a62d0100'; New = '07cf04812d00ab2d0100160758042d00a62d0100' },
        @{ Old = '1ba91200000000000000de2c010016'; New = '1ba91200000000000000d62c010016' },
        @{ Old = '1ba91200000000000000802a010016'; New = '1ba91200000000000001c029010016' }
    )
    $CoopJoinGearGameU = Join-Path $GameDir 'GearGame\Script\GearGame.u'
    if (Test-Path $CoopJoinGearGameU) {
        $JBytes = [System.IO.File]::ReadAllBytes($CoopJoinGearGameU)
        $JState = @()
        foreach ($E in $CoopJoinEdits) {
            $JOld = HexToBytes $E.Old; $JNew = HexToBytes $E.New
            if ([GOW3SemanticByteLocator]::Replace(($JBytes.Clone()), $JNew, $JNew) -eq 1) { $JState += 'done' }
            elseif ([GOW3SemanticByteLocator]::Replace(($JBytes.Clone()), $JOld, $JOld) -eq 1) { $JState += 'todo' }
            else { $JState += 'error' }
        }
        if ($JState -contains 'error') {
            Say "  error  Co-op controller targets not found exactly once in GearGame.u. Skipped." 'Yellow'
            $warningsList.Add("Co-op Controller Join: GearGame.u targets not found or ambiguous")
        }
        elseif ($JState -contains 'todo') {
            Backup $CoopJoinGearGameU
            for ($k = 0; $k -lt $CoopJoinEdits.Count; $k++) {
                if ($JState[$k] -eq 'todo') { [void][GOW3SemanticByteLocator]::Replace($JBytes, (HexToBytes $CoopJoinEdits[$k].Old), (HexToBytes $CoopJoinEdits[$k].New)) }
            }
            Set-ItemProperty -Path $CoopJoinGearGameU -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
            [System.IO.File]::WriteAllBytes($CoopJoinGearGameU, $JBytes)
            Say "  ok  Every connected controller joins in the main menu; controller 1 always stays Player 1." 'Green'
        }
        else {
            Say "  ok  Co-op controller join fix is already applied." 'DarkGray'
        }
    }
    else {
        Say "  error  GearGame.u not found. Skipped." 'Yellow'
        $warningsList.Add("Co-op Controller Join: GearGame.u not found")
    }

    Header "34/$StepTotal Steam Online Co-op (JacintoNet)"
    # JacintoNet (source: _dev\JacintoNet) is a wsock32.dll proxy: GearGame.exe imports WSOCK32.dll, so the copy next to it is loaded at start.
    # Every export behaves like the system wsock32 (same ordinals, forwarded to ws2_32 / mswsock). It starts Steam as Spacewar (480), owns a
    # friends-only lobby (F8 / overlay invites, version check) and carries the game traffic to friends over Steam P2P (virtual IPs 10.x.y.z),
    # so no port forwarding / VPN is needed. steam_api64.dll is the Steamworks redistributable. Without the option both files are removed
    # (only when they are ours; a different wsock32.dll from another mod is left alone). A replaced foreign file is backed up / restored.
    foreach ($jf in @('wsock32.dll', 'steam_api64.dll')) {
        $JnSrc = Join-Path $FilesDir $jf
        $JnDst = Join-Path $BinariesDir $jf
        $JnOurs = Test-JacintoNetFile $JnSrc $JnDst
        $JnSame = $JnOurs -and ((Get-FileHash -LiteralPath $JnSrc).Hash -eq (Get-FileHash -LiteralPath $JnDst).Hash)
        if ($steamCoop) {
            if (-not (Test-Path -LiteralPath $JnSrc)) {
                Say "  error  _files\$jf not found. Steam co-op skipped." 'Yellow'
                $warningsList.Add("Steam Online Co-op: _files\$jf not found")
            }
            elseif ($JnSame) {
                Say "  ok  $jf is already installed." 'DarkGray'
            }
            else {
                if (-not $JnOurs) { Backup $JnDst }        # an older JacintoNet build is simply replaced, not kept as the original
                if (Test-Path -LiteralPath $JnDst) { Set-ItemProperty -LiteralPath $JnDst -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue }
                Copy-Item -LiteralPath $JnSrc -Destination $JnDst -Force
                Say "  ok  $jf installed." 'Green'
            }
        }
        elseif ($JnOurs) {
            Remove-Item -LiteralPath $JnDst -Force
            Say "  removed: $jf (Steam co-op not selected)" 'DarkGray'
        }
    }
    if ($steamCoop) {
        Say "  Start the game from Steam (add Binaries\Win64\GearGame.exe as a non-Steam game) so the Steam overlay works for invites." 'Cyan'
        Say "  Host: start the Campaign, then press F8. Log: GearGame\Logs\JacintoNet.log" 'Cyan'
    }

    # Same-size GearGame.u edits: every Old must be found exactly once (or its New already applied).
    $ApplyGearGameEdits = {
        param([string]$Name, $Edits, [string]$OkText)
        $U = Join-Path $GameDir 'GearGame\Script\GearGame.u'
        if (-not (Test-Path $U)) { Say "  error  GearGame.u not found. Skipped." 'Yellow'; $warningsList.Add("${Name}: GearGame.u not found"); return }
        $UB = [System.IO.File]::ReadAllBytes($U)
        $St = @(foreach ($E in $Edits) {
            if ([GOW3SemanticByteLocator]::Replace(($UB.Clone()), (HexToBytes $E.New), (HexToBytes $E.New)) -eq 1) { 'done' }
            elseif ([GOW3SemanticByteLocator]::Replace(($UB.Clone()), (HexToBytes $E.Old), (HexToBytes $E.Old)) -eq 1) { 'todo' }
            else { 'error' }
        })
        if ($St -contains 'error') { Say "  error  $Name targets not found exactly once in GearGame.u. Skipped." 'Yellow'; $warningsList.Add("${Name}: GearGame.u targets not found or ambiguous"); return }
        if (-not ($St -contains 'todo')) { Say "  ok  $Name fix is already applied." 'DarkGray'; return }
        Backup $U
        for ($k = 0; $k -lt $Edits.Count; $k++) {
            if ($St[$k] -eq 'todo') { [void][GOW3SemanticByteLocator]::Replace($UB, (HexToBytes $Edits[$k].Old), (HexToBytes $Edits[$k].New)) }
        }
        Set-ItemProperty -Path $U -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
        [System.IO.File]::WriteAllBytes($U, $UB)
        Say "  ok  $OkText" 'Green'
    }

    Header "35/$StepTotal Pause Menu: Mouse Clicks Behind Pop-ups"
    # Every GFx movie gets the mouse click, also the pause menu hidden behind a pop-up (e.g. "Return to main menu?"). A click on the
    # pop-up selected the pause menu item under the cursor: every click opened one more Options screen (one Esc each), and Options
    # screens left open after "Leave" kept the keyboard / gamepad in the main menu (menu looked frozen).
    # a) UIScene_Pause.ApplyPriorityEffect (native calls it when a higher movie hides / blurs the pause menu, and again when it is
    #    gone) now stores the hidden state in bMenuIsFadingOut: bMenuIsFadingOut = Blur || Hidden; Super(false, bMenuIsFadingOut).
    # b) OpenOptionsMenu and ChangeDifficulty only run while the pause menu is not hidden: "if (OptionsMenuClass != None)" /
    #    "if (DifficultyPopup != None)" -> "if (!bMenuIsFadingOut)". The other items open the same pop-up, which is not duplicated.
    & $ApplyGearGameEdits 'Pause Menu Mouse' @(
        @{ Old = '142d00e52e0100842d00e62e0100180b002d00e52e010016142d00e62e0100281c83f1ffff2d00e62e01002d00e52e010016'
           New = '142d01384d0100842d00e62e0100180b002d00e52e010016142d00e62e0100281c83f1ffff2d00e62e01002d01384d010016' },
        @{ Old = '07800077013d4e01002a16'; New = '078000812d01384d010016' },
        @{ Old = '070d037701da5401002a16'; New = '070d03812d01384d010016' }
    ) 'Clicks on a pop-up no longer open Options behind it.'

    Header "36/$StepTotal Dev Build Leftovers: Abandon Ship Slow Motion, Coast Gamma Hack, Xbox LIVE Screens"
    # Stats & Awards screens that wait for Xbox LIVE (nothing answers on PC), GearGame.u, same-size edits, for every control choice:
    # a) Player Stats, "VIEW GAMER CARD": Space / Enter froze the game: an endless loop in the screen's own ActionScript (call stack of the frozen game).
    #    The only menu item is passed to the SWF as unselectable (CanViewGamerCard() is false without an Xbox LIVE id) and the list's "find the next
    #    selectable item" loop (wrapping list, a do-while over _dataProvider[i].unselectable) never ends when no item can be selected.
    #    UIScene_PersonalStats.CanViewGamerCard now returns true (04 82 -> 04 27), so the item can be selected, and ViewGamercard() returns at once
    #    (the "if (OnlineSub != None)" JumpIfNot 07 -> Jump 06 to the end, same size; the old test stays behind as a harmless statement - the package
    #    bytecode is parsed completely at load, so no token may be cut: a Return with the old bytes behind it crashed at start-up, "Bad export index"),
    #    so selecting it does nothing (no Xbox LIVE call).
    # b) UIScene_WarJournal.Callback_OnLeaderboardCategorySelected: the "if (!IsLoggedIn(...))" test is now always true (JumpIfNot -> 3 x Nothing,
    #    the test still runs as a statement), so the sign-in message box appears instead of Leaderboard_RefreshStats(), which waited for an Xbox LIVE
    #    stats read forever ("READING LEADERBOARDS...", bReadingLeaderboards = TRUE blocks all input of the screen).
    & $ApplyGearGameEdits 'Xbox LIVE Screens' @(
        @{ Old = '04821B096800000000000016180B001B0868000000000000161604' + '3AEF320100'
           New = '04271B096800000000000016180B001B0868000000000000161604' + '3AEF320100' },
        @{ Old = '0f00ed320100122051fbffff0a0087eeffff001c49f6ffff1607390177'
           New = '0f00ed320100122051fbffff0a0087eeffff001c49f6ffff1606390177' },
        @{ Old = '0765028112204afaffff2a00f8fcffff001c93f2ffff191b915e0000000000001609008cf0ffff00'
           New = '0b0b0b8112204afaffff2a00f8fcffff001c93f2ffff191b915e0000000000001609008cf0ffff00' }
        ,
        # c) UIScene_PostGameStats.CheckAndPromptForLivePermissions: "GetLoginStatus(...) < LS_LoggedIn" -> "< 3" (byte const 02 -> 03), always true, so the
        #    sign-in message appears (and the Beast page shows "leaderboards unavailable") instead of PerformLeaderboardRead(), which opens the
        #    input-blocking COG spin until an Xbox LIVE stats read that never answers (also guards Callback_PlayerSelected / Challenge).
        @{ Old = '383a24021616075501812d00e8340100'
           New = '383a24031616075501812d00e8340100' }
    ) 'Player Stats: Gamer Card item no longer freezes the screen; Leaderboards (Stats & Awards and post-game) show the sign-in message instead of waiting for Xbox LIVE.'
    # See $DevLeftoverEdits. Each map is independent; a map that does not match is skipped with a warning.
    $SpDir = Join-Path $GameDir 'GearGame\Content\Maps\SP'
    $DevRes = @(foreach ($E in $DevLeftoverEdits) {
        $F = Join-Path (Join-Path $SpDir $E.Dir) $E.Map
        $R = if (Test-Path $F) { Set-IntAtContext $F $E.Pre $E.Suf $E.Old $E.New } else { 'error' }
        if ($R -eq 'error') { $warningsList.Add("Dev Build Leftovers: $($E.Map) not found or unexpected content") }
        $R
    })
    if ($DevRes -contains 'error') {
        Say "  error  Some dev build leftover targets were not found exactly once. Those maps were skipped." 'Yellow'
    }
    if ($DevRes -contains 'applied') {
        Say "  ok  Abandon Ship slow motion now returns to normal speed; Coast no longer forces gamma 3." 'Green'
    }
    elseif ($DevRes -notcontains 'error') {
        Say "  ok  Dev build leftover fixes are already applied." 'DarkGray'
    }

    Header "37/$StepTotal Campaign & Chapter Select DLC Placeholder Removal"
    # The campaign act list and campaign game type menu contained unfinished DLC placeholder buttons:
    # 1) In Select Chapter (Act Select), "VI: DLC" loaded chapters 31-35 (DLC 1..5) with missing map files (SP_Bank_P etc.).
    #    Removing [Act6 GearCampaignActData] from GearGame.ini and DefaultGame.ini collapses this placeholder act entirely.
    # 2) In the Campaign game type menu (Standard / Arcade / CampaignDLC), the "CampaignDLC" entry ("Coming Soon") is removed
    #    so the menu cleanly displays only playable options.
    $DlcPatched = $false
    foreach ($ini in @( (Join-Path $ConfigDir "DefaultGame.ini"), (Join-Path $ConfigDir "GearGame.ini") )) {
        if (-not (Test-Path $ini)) { continue }
        $txt = [System.IO.File]::ReadAllText($ini)
        if ($txt -match '(?m)^\[Act6 GearCampaignActData\]') {
            Backup $ini
            $newTxt = [regex]::Replace($txt, '(?m)^\[Act6 GearCampaignActData\](?:\r?\n(?![\[\r\n]).*)*\r?\n?', '')
            Set-ItemProperty -Path $ini -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
            [System.IO.File]::WriteAllText($ini, $newTxt)
            $DlcPatched = $true
        }
    }
    $DlcUiOld = ',(Label="CampaignDLC",Next=MAINMENUOP_None,bIsDisabled=true)'
    foreach ($ini in @( (Join-Path $ConfigDir "DefaultUI.ini"), (Join-Path $ConfigDir "GearUI.ini") )) {
        if (-not (Test-Path $ini)) { continue }
        $txt = [System.IO.File]::ReadAllText($ini)
        if ($txt.Contains($DlcUiOld)) {
            Backup $ini
            $newTxt = $txt.Replace($DlcUiOld, '')
            Set-ItemProperty -Path $ini -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
            [System.IO.File]::WriteAllText($ini, $newTxt)
            $DlcPatched = $true
        }
    }
    if ($DlcPatched) {
        Say "  ok  DLC placeholder buttons collapsed (Act 6 in Select Chapter and CampaignDLC in Campaign menu)." 'Green'
    }
    else {
        Say "  ok  DLC placeholder buttons are already collapsed." 'DarkGray'
    }

    Header "Installation Complete!"

    if ($warningsList.Count -gt 0) {
        Say "  [!] Some patches were skipped or completed with warnings:" 'Yellow'
        foreach ($w in $warningsList) {
            Say "      - $w" 'Yellow'
        }
    }
    else {
        Say "  All patches applied successfully." 'Green'
    }
    if ($packageFilesBad.Count -gt 0) {
        Write-Host ""
        Write-Host ("=" * 66) -ForegroundColor Red
        Say "  [!] PACKAGE FILES MISSING OR OUTDATED: $($packageFilesBad -join ', ')" 'Red'
        Say "      Updating only the script (the Remote option) is NOT enough when package files are missing or outdated." 'Red'
        Say "      This version needs up-to-date data files from the _files folder (e.g. the Badlands Mother Corpser map)." 'Red'
        Say "      Please download the complete Multi Patch Tool package again from NexusMods," 'Red'
        Say "      replace the whole tool folder, then run the installer again:" 'Red'
        Say "      https://www.nexusmods.com/gearsofwar3/mods/27" 'Yellow'
        Write-Host ("=" * 66) -ForegroundColor Red
    }

    Write-Host ""

    Say "What would you like to do next?" 'White'

    $launchExt = if ($IsWin) { 'bat' } else { 'sh' }
    Say "  [1] Launch Game (Gears3_Start_Game.$launchExt)"

    Say "  [2] Launch Game with Debug Log (Gears3_Start_Game_Debug.$launchExt)"

    Say "  [3] Return to Menu"

    $postOpt = Read-Host "Your choice (1-3, Press ENTER to Launch Game)"
    if ([string]::IsNullOrWhiteSpace($postOpt)) { $postOpt = '1' }
    if ($postOpt -eq '3') { Clear-Host; return }   # back to the main menu, at the top of the screen

    if ($postOpt -eq '1') {

        $batPath = Join-Path $GameDir "Gears3_Start_Game.$launchExt"

        if (Test-Path $batPath) {

            if ($IsWin) { Start-Process -FilePath $batPath } else { Start-Process -FilePath 'sh' -ArgumentList "`"$batPath`"" }

            exit 0

        }
        else {

            Say "  error: Gears3_Start_Game.$launchExt not found!" 'Red'

            $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')

        }

    }

    elseif ($postOpt -eq '2') {

        $dbgPath = Join-Path $GameDir "Gears3_Start_Game_Debug.$launchExt"

        if (Test-Path $dbgPath) {

            if ($IsWin) { Start-Process -FilePath $dbgPath } else { Start-Process -FilePath 'sh' -ArgumentList "`"$dbgPath`"" }

            exit 0

        }
        else {

            Say "  error: Gears3_Start_Game_Debug.$launchExt not found!" 'Red'

            $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')

        }

    }

}

function Uninstall-Multipatch {
    Header "Uninstalling & Reverting Changes"
    $count = Restore-AllBackups $false $true

    # The restored engine inis can be older than the installed HD Textures Pack phases (or hold caps of a removed pack): apply the
    # pack's size cap rule again, so no cap stays above what the installed packages support.
    $hdSafe = Get-HdSafeTextureGroups
    foreach ($ini in @((Join-Path $ConfigDir 'GearEngine.ini'), (Join-Path $ConfigDir 'DefaultEngine.ini'), (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'My Games\Gears of War 3\GearGame\Config\GearEngine.ini'))) {
        if (-not (Test-Path -LiteralPath $ini)) { continue }
        $capsOld = [System.IO.File]::ReadAllText($ini)
        $capsNew = Set-TextureGroupLimits $capsOld $hdSafe 4096 -StockOthers
        if ($capsNew -ne $capsOld) {
            Set-ItemProperty -LiteralPath $ini -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
            [System.IO.File]::WriteAllText($ini, $capsNew)
            Say "  texture size caps matched to the installed HD Textures Pack: $(Split-Path -Leaf $ini)" 'DarkGray'
        }
    }

    # JacintoNet (Steam co-op): remove our wsock32.dll / steam_api64.dll (a foreign file that was backed up is already restored and differs).
    foreach ($jf in @('wsock32.dll', 'steam_api64.dll')) {
        $JnSrc = Join-Path $FilesDir $jf; $JnDst = Join-Path $BinariesDir $jf
        if (Test-JacintoNetFile $JnSrc $JnDst) {
            Remove-Item -LiteralPath $JnDst -Force
            Say "  removed: $jf" 'DarkGray'
            $count++
        }
    }

    foreach ($CpUpk in @('CorpserRestor_Audio.upk', 'CorpserRestor_BattleAudio.upk', 'CorpserRestor_InterwaveFX.upk', 'CorpserRestor_PillarChunks.upk')) {
        $CpUpkPath = Join-Path (Join-Path $GameDir 'GearGame\Content') $CpUpk
        if (Test-Path -LiteralPath $CpUpkPath) {
            Set-ItemProperty -LiteralPath $CpUpkPath -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $CpUpkPath -Force
            Say "  removed: $CpUpk" 'DarkGray'
            $count++
        }
    }
    # Saves made with the restored fight (they contain the new level logic) crash with the original map: remove them and put the old checkpoint back.
    $CpSaveDirU = Join-Path $GameDir 'GearGame\SaveData'
    if (Test-Path -LiteralPath $CpSaveDirU) {
        foreach ($cps in @(Get-ChildItem -LiteralPath $CpSaveDirU -Filter 'GearsCheckpoint*.sav' -File -ErrorAction SilentlyContinue)) {
            if ($iso88591.GetString([System.IO.File]::ReadAllBytes($cps.FullName)).Contains('SP_Badlands_13_S')) { Remove-Item -LiteralPath $cps.FullName -Force; Say "  removed: $($cps.Name) (made with the restored Mother Corpser fight)" 'DarkGray'; $count++ }
        }
    }
    $CpStashU = Join-Path (Split-Path $BackupRoot) 'CheckpointStash'
    if (Test-Path -LiteralPath $CpStashU) {
        New-Item -ItemType Directory -Force -Path $CpSaveDirU | Out-Null
        foreach ($cps in @(Get-ChildItem -LiteralPath $CpStashU -File -ErrorAction SilentlyContinue)) {
            if (-not (Test-Path -LiteralPath (Join-Path $CpSaveDirU $cps.Name))) { Move-Item -LiteralPath $cps.FullName -Destination (Join-Path $CpSaveDirU $cps.Name) -Force; Say "  restored: $($cps.Name)" 'DarkGray'; $count++ }
        }
        if (-not (Get-ChildItem -LiteralPath $CpStashU -File -ErrorAction SilentlyContinue)) { Remove-Item -LiteralPath $CpStashU -Recurse -Force }
    }

    $Act3FixMap = Join-Path $GameDir 'GearGame\Content\Maps\SP\SP_09_Mercy\SP_Mercy_01_S_Showdown.gear'
    if (Test-Path $Act3FixMap) {
        $MapBytes = [System.IO.File]::ReadAllBytes($Act3FixMap)
        $Prefix = [byte[]]@(0x4F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x57, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00)
        $Suffix = [byte[]]@(0x4E, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x57, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00)
        $Hits = [GOW3SemanticByteLocator]::Find($MapBytes, $Prefix, $Suffix)
        if ($Hits.Count -eq 1 -and $MapBytes[$Hits[0]] -eq 0x00) {
            $MapBytes[$Hits[0]] = 0x01
            Set-ItemProperty -Path $Act3FixMap -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
            [System.IO.File]::WriteAllBytes($Act3FixMap, $MapBytes)
            Say "  ok  Act 3 transition fix reverted." 'Green'
            $count++
        }
    }

    $TempestMap = Join-Path $GameDir 'GearGame\Content\Maps\SP\SP_15_Tower\SP_Rooftop_01b_S.gear'
    if (Test-Path $TempestMap) {
        $TBytes = [System.IO.File]::ReadAllBytes($TempestMap)
        $THits = [GOW3SemanticByteLocator]::Find($TBytes, (HexToBytes 'b902000000000000b7020000000000000400000000000000'), (HexToBytes '0000008e03000000000000b702000000000000040000000000000001000000c4030000000000008d030000000000000400000000000000c21400009003000000000000b7020000000000000400000000000000c0ffffff9103000000000000b702000000000000040000000000000078f9ffff'))
        if ($THits.Count -eq 1 -and $TBytes[$THits[0]] -eq 0x02) {
            $TBytes[$THits[0]] = 0x03
            Set-ItemProperty -Path $TempestMap -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
            [System.IO.File]::WriteAllBytes($TempestMap, $TBytes)
            Say "  ok  Tempest boss fix reverted." 'Green'
            $count++
        }
    }

    if (Test-Path $TempestMap) {
        $TBytes = [System.IO.File]::ReadAllBytes($TempestMap)
        $TcpHits = [GOW3SemanticByteLocator]::FindSpan($TBytes, (HexToBytes 'ec020000000000008d030000000000000400000000000000'), (HexToBytes '9502000000000000b7020000000000000400000000000000000000008203000000000000ec020000000000008d030000000000000400000000000000970f0000'), 4)
        if ($TcpHits.Count -eq 1 -and [System.BitConverter]::ToString($TBytes, $TcpHits[0], 4) -eq '69-0E-00-00') {
            $TcpOld = HexToBytes 'f40d0000'
            for ($k = 0; $k -lt 4; $k++) { $TBytes[$TcpHits[0] + $k] = $TcpOld[$k] }
            Set-ItemProperty -Path $TempestMap -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
            [System.IO.File]::WriteAllBytes($TempestMap, $TBytes)
            Say "  ok  Tempest boss checkpoint-reload fix reverted." 'Green'
            $count++
        }
    }

    if (Test-Path $TempestMap) {
        $TBytes = [System.IO.File]::ReadAllBytes($TempestMap)
        $TdLinkPre = '210300000000000002000000ee020000000000003100000000000000840000000000000002000000ec020000000000008d030000000000000400000000000000'
        $TdLinkSuf = '9502000000000000b7020000000000000400000000000000000000008203000000000000ec020000000000008d030000000000000400000000000000'
        $TdEdits = @(
            @{ Pre = $TdLinkPre; Suf = $TdLinkSuf + 'b00e0000'; Old = '240f0000'; New = 'a40d0000' },
            @{ Pre = $TdLinkPre; Suf = $TdLinkSuf + 'a60d0000'; Old = 'a40d0000'; New = '240f0000' },
            @{ Pre = '000000003d0e0000ca0100000000000023020000000000000400000000000000'; Suf = '96020000000000003100000000000000e70300000000000003000000ea020000'; Old = 'cdcccc3d'; New = '0000003f' }
        )
        $TdPos = foreach ($ed in $TdEdits) {
            $h = [GOW3SemanticByteLocator]::FindSpan($TBytes, (HexToBytes $ed.Pre), (HexToBytes $ed.Suf), 4)
            if ($h.Count -eq 1 -and ([System.BitConverter]::ToString($TBytes, $h[0], 4) -replace '-', '').ToLower() -eq $ed.New) { $h[0] } else { -1 }
        }
        if (@($TdPos | Where-Object { $_ -lt 0 }).Count -eq 0) {
            for ($j = 0; $j -lt $TdEdits.Count; $j++) { $ob = HexToBytes $TdEdits[$j].Old; for ($k = 0; $k -lt 4; $k++) { $TBytes[$TdPos[$j] + $k] = $ob[$k] } }
            Set-ItemProperty -Path $TempestMap -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
            [System.IO.File]::WriteAllBytes($TempestMap, $TBytes)
            Say "  ok  Tempest boss death crash fix reverted." 'Green'
            $count++
        }
    }

    $RazorMap = Join-Path $GameDir 'GearGame\Content\Maps\SP\SP_02_Hanover\SP_Hanover_CoopDoor_Razor.gear'
    if (Test-Path $RazorMap) {
        $RBytes = [System.IO.File]::ReadAllBytes($RazorMap)
        $RHits = [GOW3SemanticByteLocator]::Find($RBytes, (HexToBytes '31000000000000003e000000000000000000000000000000'), (HexToBytes '42010000000000001e0100000000000004000000000000000401000054010000000000001400000000000000c8060000'))
        if ($RHits.Count -eq 2 -and (@($RHits | Where-Object { $RBytes[$_] -eq 1 }).Count -eq 2)) {
            foreach ($h in $RHits) { $RBytes[$h] = 0 }
            Set-ItemProperty -Path $RazorMap -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
            [System.IO.File]::WriteAllBytes($RazorMap, $RBytes)
            Say "  ok  Razor wire fix reverted." 'Green'
            $count++
        }
    }

    $LiftRevert = @(
        @{ Path = (Join-Path $GameDir 'GearGame\Content\Maps\SP\SP_10_Char\SP_Char_Tower_2_S.gear'); Orig = 1; Fixed = 0
           Prefix = '00000000020300000000000004000000000000005e040000590200000000000076020000000000000400000000000000'
           Suffix = '000000fa02000000000000960200000000000007040000000000000800000000000000040000004f7574008a00000000000000a5000000000000000000000000000000006c00000000000000a5000000000000000000000000000000006e00000000000000a5000000000000000000000000000000009802000000' },
        @{ Path = (Join-Path $GameDir 'GearGame\Content\Maps\SP\SP_10_Char\SP_Char_Tower_2_elevator_S.gear'); Orig = 0; Fixed = 1
           Prefix = '00000000390200000000000004000000000000002c010000b001000000000000cd010000000000000400000000000000'
           Suffix = '0000003302000000000000ed0100000000000010030000000000000800000000000000040000004f7574005b000000000000006b0000000000000000000000000000000045000000000000006b0000000000000000000000000000000047000000000000006b00000000000000000000000000000000ef01000000' },
        @{ Path = (Join-Path $GameDir 'GearGame\Content\Maps\SP\SP_10_Char\SP_Char_Tower_2_elevator_S.gear'); Orig = 0; Fixed = 1
           Prefix = '000000002900000065000000000000006b000000000000000000000000000000'
           Suffix = 'e80100000000000011030000000000003a00000000000000e701000000000000' }
    )
    $LiftHoleMap = Join-Path $GameDir 'GearGame\Content\Maps\SP\SP_10_Char\SP_Char_Tower_2_elevator_S.gear'
    if (Test-Path $LiftHoleMap) {
        if ((Set-IntAtContext $LiftHoleMap $LiftHoleLinkPre $LiftHoleLinkSuf 249 299) -eq 'applied') {
            Say "  ok  Elevator deck hole fix reverted." 'Green'
            $count++
        }
    }
    if (Test-Path $LiftHoleMap) {
        $PushRev = @(for ($i = $LiftPushLinks.Count - 1; $i -ge 0; $i--) { $L = $LiftPushLinks[$i]; Set-IntAtContext $LiftHoleMap $L.Pre $L.Suf $L.New $L.Old })
        if ($PushRev -contains 'applied') { Say "  ok  Elevator push teleport fix reverted." 'Green'; $count++ }
    }    $LiftPersistRevert = Join-Path $GameDir 'GearGame\Content\Maps\SP\SP_10_Char\SP_Char_P.gear'
    if ((Test-Path $LiftHoleMap) -and (Test-Path $LiftPersistRevert)) {
        $SkipRev = @(Set-BlockExact $LiftPersistRevert $LiftSkipPNew $LiftSkipPOld) + @(foreach ($S in $LiftSkipElev) { Set-BlockExact $LiftHoleMap $S.New $S.Old })
        if ($SkipRev -contains 'applied') { Say "  ok  Elevator ride skip reverted." 'Green'; $count++ }
    }
    if ((Test-Path $LiftPersistRevert) -and (Set-BlockExact $LiftPersistRevert $LiftBv15New $LiftBv15Old) -eq 'applied') {
        Say "  ok  Elevator floor volume fix reverted." 'Green'
        $count++
    }    if ((Test-Path $LiftHoleMap) -and (Set-BlockExact $LiftHoleMap $LiftDbv3New $LiftDbv3Old) -eq 'applied') {
        Say "  ok  Elevator slab fix reverted." 'Green'
        $count++
    }    if ((Test-Path $LiftHoleMap) -and (Set-BlockExact $LiftHoleMap $LiftFlatNew $LiftFlatOld) -eq 'applied') {
        Say "  ok  Elevator ride smoothing reverted." 'Green'
        $count++
    }    foreach ($LT in $LiftRevert) {
        if (-not (Test-Path $LT.Path)) { continue }
        $LBytes = [System.IO.File]::ReadAllBytes($LT.Path)
        $LHits = [GOW3SemanticByteLocator]::Find($LBytes, (HexToBytes $LT.Prefix), (HexToBytes $LT.Suffix))
        if ($LHits.Count -eq 1 -and $LBytes[$LHits[0]] -eq $LT.Fixed) {
            $LBytes[$LHits[0]] = $LT.Orig
            Set-ItemProperty -Path $LT.Path -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
            [System.IO.File]::WriteAllBytes($LT.Path, $LBytes)
            Say "  ok  Elevator fix reverted: $(Split-Path -Leaf $LT.Path)." 'Green'
            $count++
        }
    }

    $PlateTargets = @(
        @{ Orig = 114; Fixed = 29; Prefix = '000000000000130500000b02000000000000bb01000000000000040000000000000000000000830200000000000063010000000000000400000000000000cdcc4c3d5b0000000000000082000000000000000000000000000000008c0000000000000082000000000000000000000000000000000d02000000000000bb010000000000000400000000000000ec00000042020000000000001d000000000000002e0400000000000003000000dc010000000000001d00000000000000440000000000000001000000da0100000000000038020000000000000400000000000000'; Suffix = '030000a601000000000000bb010000000000000400000000000000000000003202000000000000d80100000000000036030000000000000c0000000000000008000000546f756368656400680000000000000082000000000000000000000000000000004f00000000000000820000000000000000000000000000' },
        @{ Orig = 116; Fixed = 113; Prefix = '000000000000140500000b02000000000000bb01000000000000040000000000000000000000830200000000000063010000000000000400000000000000cdcc4c3d5b0000000000000082000000000000000000000000000000008c0000000000000082000000000000000000000000000000000d02000000000000bb010000000000000400000000000000ec00000042020000000000001d000000000000002e0400000000000003000000dc010000000000001d00000000000000440000000000000001000000da0100000000000038020000000000000400000000000000'; Suffix = '030000a601000000000000bb010000000000000400000000000000000000003202000000000000d80100000000000036030000000000000c0000000000000008000000546f756368656400680000000000000082000000000000000000000000000000004f00000000000000820000000000000000000000000000' },
        @{ Orig = 117; Fixed = 34; Prefix = '000000000000160500000b02000000000000bb01000000000000040000000000000000000000830200000000000063010000000000000400000000000000cdcc4c3d5b0000000000000082000000000000000000000000000000008c0000000000000082000000000000000000000000000000000d02000000000000bb010000000000000400000000000000ec00000042020000000000001d000000000000002e0400000000000003000000dc010000000000001d00000000000000440000000000000001000000da0100000000000038020000000000000400000000000000'; Suffix = '030000a601000000000000bb010000000000000400000000000000000000003202000000000000d80100000000000036030000000000000c0000000000000008000000546f756368656400680000000000000082000000000000000000000000000000004f00000000000000820000000000000000000000000000' },
        @{ Orig = 115; Fixed = 112; Prefix = '000000000000150500000b02000000000000bb01000000000000040000000000000000000000830200000000000063010000000000000400000000000000cdcc4c3d5b0000000000000082000000000000000000000000000000008c0000000000000082000000000000000000000000000000000d02000000000000bb010000000000000400000000000000ec00000042020000000000001d000000000000006e0400000000000003000000dc010000000000001d00000000000000440000000000000001000000da0100000000000038020000000000000400000000000000'; Suffix = '030000a601000000000000bb010000000000000400000000000000000000003202000000000000d80100000000000036030000000000000c0000000000000008000000546f756368656400680000000000000082000000000000000000000000000000004f00000000000000820000000000000000000000000000' },
        @{ Orig = 115; Fixed = 112; Prefix = '0000000000050000000000000063010000000000000400000000000000000000003101000000000000bb010000000000000400000000000000e50700006900000000000000820000000000000000000000000000000045000000000000008200000000000000000000000000000000460000000000000082000000000000000000000000000000004602000000000000bb0100000000000004000000000000000e0000003202000000000000dc010000000000001d00000000000000440000000000000001000000da0100000000000038020000000000000400000000000000'; Suffix = '030000a601000000000000bb010000000000000400000000000000000000003202000000000000d80100000000000036030000000000000e000000000000000a000000556e546f756368656400680000000000000082000000000000000000000000000000004f0000000000000082000000000000000000000000' }
    )
    $PlateRevertMap = Join-Path $GameDir 'GearGame\Content\Maps\SP\SP_11_OldShipyard\SP_OldShipyard_05_S.gear'
    if (Test-Path $PlateRevertMap) {
        $PBytes = [System.IO.File]::ReadAllBytes($PlateRevertMap)
        $PHits = @(); $PBad = $false
        foreach ($PT in $PlateTargets) {
            $H = [GOW3SemanticByteLocator]::Find($PBytes, (HexToBytes $PT.Prefix), (HexToBytes $PT.Suffix))
            if ($H.Count -ne 1) { $PBad = $true; break }
            $PHits += ,@($H[0], $PT)
        }
        if (-not $PBad -and @($PHits | Where-Object { $PBytes[$_[0]] -eq $_[1].Fixed }).Count -eq $PHits.Count) {
            foreach ($PH in $PHits) { $PBytes[$PH[0]] = $PH[1].Orig }
            Set-ItemProperty -Path $PlateRevertMap -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
            [System.IO.File]::WriteAllBytes($PlateRevertMap, $PBytes)
            Say "  ok  Loader plate fix reverted." 'Green'
            $count++
        }
    }

    $KantusRevertMap = Join-Path $GameDir 'GearGame\Content\Maps\SP\SP_11_OldShipyard\SP_OldShipyard_P.gear'
    if (Test-Path $KantusRevertMap) {
        $KBytes = [System.IO.File]::ReadAllBytes($KantusRevertMap)
        $KHits = [GOW3SemanticByteLocator]::Find($KBytes, (HexToBytes '00000000a301000000000000040000000000000008030000430100000000000054010000000000000400000000000000'), (HexToBytes '0000009b0100000000000073010000000000005a020000000000000800000000000000040000004f75740057000000000000006400000000000000000000000000000000490000000000000064000000000000000000000000000000004b0000000000000064000000000000000000000000000000007501000000'))
        if ($KHits.Count -eq 1 -and $KBytes[$KHits[0]] -eq 0) {
            $KBytes[$KHits[0]] = 2
            Set-ItemProperty -Path $KantusRevertMap -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
            [System.IO.File]::WriteAllBytes($KantusRevertMap, $KBytes)
            Say "  ok  Final Kantus battle fix reverted." 'Green'
            $count++
        }
    }

    $CoastRevertMap = Join-Path $GameDir 'GearGame\Content\Maps\SP\SP_13_Coast\SP_Coast_01_S.gear'
    if (Test-Path $CoastRevertMap) {
        $CBytes = [System.IO.File]::ReadAllBytes($CoastRevertMap)
        $CHits = [GOW3SemanticByteLocator]::FindSpan($CBytes, (HexToBytes '04000000000000002d000000d40200000000000022050000a303000000000000c4010000000000000400000000000000'), (HexToBytes '250200000000000021000000000000004d01000000000000010000006c02000000000000e303000000000000070000000000000003000000496e0076000000000000008f0000000000000000000000000000000022030000000000003c020000'), 88)
        if ($CHits.Count -eq 1) {
            $CVals = @(@(0, 28, 56, 84) | ForEach-Object { [BitConverter]::ToSingle($CBytes, $CHits[0] + $_) })
            if (@($CVals | Where-Object { $_ -eq 900.0 }).Count -eq 4) {
                foreach ($Off in @(0, 28, 56, 84)) { [Array]::Copy([BitConverter]::GetBytes([single]1900.0), 0, $CBytes, $CHits[0] + $Off, 4) }
                Set-ItemProperty -Path $CoastRevertMap -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
                [System.IO.File]::WriteAllBytes($CoastRevertMap, $CBytes)
                Say "  ok  Coast stealth fix reverted." 'Green'
                $count++
            }
        }
    }

    $TurretRevertMap = Join-Path $GameDir 'GearGame\Content\Maps\SP\SP_13_Coast\SP_Coast_01_S.gear'
    $TurretTargetsRev = @(
        @{ Span = 1; Offsets = @(0); Prefix = '5b335d000c010000000000002100000000000000040000000000000000000000c503000000000000210000000000000004000000000000000000000040000000000000008f000000000000000000000000000000013f000000000000008f00000000000000000000000000000001f301000000000000da02000000000000040000000000000072fffffff800000000000000da02000000000000040000000000000094ffffff740200000000000021000000000000000800000000000000010000008dffffff34000000000000008f00000000000000000000000000000001d603000000000000c00200000000000008000000000000001400000000000000a6000000000000008f000000000000000000000000000000000a00000000000000c0020000000000000800000000000000d4020000000000000a03000000000000c0020000000000000800000000000000d40200000000000042000000000000008f000000000000000000000000000000'; Suffix = '45000000000000008f0000000000000000000000000000000073000000000000008f0000000000000000000000000000000074000000000000008f00000000000000000000000000000000a5000000000000008f000000000000000000000000000000009c030000000000003c020000000000000400000000000000000000002900000000000000e30300000000000004000000000000000000000063000000000000008f0000000000000000000000000000000033000000000000008f00000000000000000000000000000000d803000000000000c40100000000000004000000000000000000f0419e03000000000000b6000000000000000800000000000000930100000000000068020000000000000c03000000000000c40100000000000004000000000000000000003f61000000000000008f000000000000000000000000000000005f000000000000008f0000000000000000000000000000000039000000000000008f00000000000000' },
        @{ Span = 26; Offsets = @(0,25); Prefix = '000000000000000000000000000000d603000000000000c00200000000000008000000000000001400000000000000a6000000000000008f000000000000000000000000000000000a00000000000000c0020000000000000800000000000000d4020000000000000a03000000000000c0020000000000000800000000000000d40200000000000042000000000000008f000000000000000000000000000000'; Suffix = '73000000000000008f0000000000000000000000000000000074000000000000008f00000000000000000000000000000000a5000000000000008f000000000000000000000000000000009c030000000000003c020000000000000400000000000000000000002900000000000000e30300000000000004000000000000000000000063000000000000008f0000000000000000000000000000000133000000' },
        @{ Span = 1; Offsets = @(0); Prefix = '00000000000000000b01000000000000c401000000000000040000000000000000000000340400000000000021000000000000000c000000000000000100000032040000000000001b0400000000000021000000000000007800000000000000050000000a0000005365742031205b315d00150000005365742031205b537061776e20506f696e74735d001b0000005365742031205b537061776e2044657374696e6174696f6e735d00120000005365742031205b4d617820416c6976655d00140000005365742031205b537061776e20546f74616c5d00e50200000000000021000000000000001400000000000000010000000c000000537061776e2031205b315d000c010000000000002100000000000000040000000000000000000000c503000000000000210000000000000004000000000000000000000040000000000000008f000000000000000000000000000000013f000000000000008f00000000000000000000000000000001f301000000000000da02000000000000040000000000000072fffffff800000000000000da02000000000000040000000000000094ffffff740200000000000021000000000000000800000000000000010000008dffffff34000000000000008f00000000000000000000000000000001d603000000000000c00200000000000008000000000000000000000000000000a6000000000000008f000000000000000000000000000000000a00000000000000c0020000000000000800000000000000d4020000000000000a03000000000000c0020000000000000800000000000000d40200000000000042000000000000008f000000000000000000000000000000'; Suffix = '45000000000000008f0000000000000000000000000000000073000000000000008f0000000000000000000000000000000074000000000000008f00000000000000000000000000000000a5000000000000008f000000000000000000000000000000009c030000000000003c020000000000000400000000000000000000002900000000000000e30300000000000004000000000000000000000063000000000000008f0000000000000000000000000000000033000000000000008f00000000000000000000000000000000d803000000000000c40100000000000004000000000000000000f0419e03000000000000b6000000000000000800000000000000930100000000000068020000000000000c03000000000000c40100000000000004000000000000000000003f61000000000000008f000000000000000000000000000000005f000000000000008f0000000000000000000000000000000039000000000000008f0000000000000000000000000000000072000000000000008f000000000000000000000000000000000f02000000000000b60000000000000008000000000000008d010000000000002803000000000000c3030000000000002100000000000000040000000000000000000000fb00000000000000c40100000000000004000000000000000000000066000000000000008f000000000000000000000000000000007301000000000000c4010000000000000400000000000000000000007201000000000000c4010000000000000400000000000000000000007401000000000000c401000000000000040000000000000000000000d4020000000000000f00000000000000b6' },
        @{ Span = 26; Offsets = @(0,25); Prefix = '00000000000034000000000000008f00000000000000000000000000000001d603000000000000c00200000000000008000000000000000000000000000000a6000000000000008f000000000000000000000000000000000a00000000000000c0020000000000000800000000000000d4020000000000000a03000000000000c0020000000000000800000000000000d40200000000000042000000000000008f000000000000000000000000000000'; Suffix = '73000000000000008f0000000000000000000000000000000074000000000000008f00000000000000000000000000000000a5000000000000008f000000000000000000000000000000009c030000000000003c020000000000000400000000000000000000002900000000000000e30300000000000004000000000000000000000063000000000000008f0000000000000000000000000000000033000000000000008f0000000000000000000000' },
        @{ Span = 1; Offsets = @(0); Prefix = '00000000000000000b01000000000000c401000000000000040000000000000000000000340400000000000021000000000000000c000000000000000100000032040000000000001b0400000000000021000000000000007800000000000000050000000a0000005365742031205b315d00150000005365742031205b537061776e20506f696e74735d001b0000005365742031205b537061776e2044657374696e6174696f6e735d00120000005365742031205b4d617820416c6976655d00140000005365742031205b537061776e20546f74616c5d00e50200000000000021000000000000001400000000000000010000000c000000537061776e2031205b315d000c010000000000002100000000000000040000000000000000000000c503000000000000210000000000000004000000000000000000000040000000000000008f000000000000000000000000000000013f000000000000008f00000000000000000000000000000001f301000000000000da02000000000000040000000000000072fffffff800000000000000da02000000000000040000000000000094ffffff740200000000000021000000000000000800000000000000010000008dffffff34000000000000008f00000000000000000000000000000001d603000000000000c00200000000000008000000000000000000000000000000a6000000000000008f000000000000000000000000000000000a00000000000000c0020000000000000800000000000000d4020000000000000a03000000000000c0020000000000000800000000000000d40200000000000042000000000000008f000000000000000000000000000000'; Suffix = '45000000000000008f0000000000000000000000000000000073000000000000008f0000000000000000000000000000000074000000000000008f00000000000000000000000000000000a5000000000000008f000000000000000000000000000000009c030000000000003c020000000000000400000000000000000000002900000000000000e30300000000000004000000000000000000000063000000000000008f0000000000000000000000000000000033000000000000008f00000000000000000000000000000000d803000000000000c40100000000000004000000000000000000f0419e03000000000000b6000000000000000800000000000000930100000000000068020000000000000c03000000000000c40100000000000004000000000000000000003f61000000000000008f000000000000000000000000000000005f000000000000008f0000000000000000000000000000000039000000000000008f0000000000000000000000000000000072000000000000008f000000000000000000000000000000000f02000000000000b60000000000000008000000000000008d010000000000002803000000000000c3030000000000002100000000000000040000000000000000000000fb00000000000000c40100000000000004000000000000000000000066000000000000008f000000000000000000000000000000007301000000000000c4010000000000000400000000000000000000007201000000000000c4010000000000000400000000000000000000007401000000000000c401000000000000040000000000000000000000d402000000000000ff02000000000000b6' }
    )
    if (Test-Path $TurretRevertMap) {
        $TBytes = [System.IO.File]::ReadAllBytes($TurretRevertMap)
        $TList = @(); $TBad = $false
        foreach ($TT in $TurretTargetsRev) {
            $H = [GOW3SemanticByteLocator]::FindSpan($TBytes, (HexToBytes $TT.Prefix), (HexToBytes $TT.Suffix), $TT.Span)
            if ($H.Count -ne 1) { $TBad = $true; break }
            foreach ($Off in $TT.Offsets) { $TList += ($H[0] + $Off) }
        }
        if (-not $TBad -and @($TList | Where-Object { $TBytes[$_] -eq 0 }).Count -eq $TList.Count) {
            foreach ($TO in $TList) { $TBytes[$TO] = 1 }
            Set-ItemProperty -Path $TurretRevertMap -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
            [System.IO.File]::WriteAllBytes($TurretRevertMap, $TBytes)
            Say "  ok  Coast turret guard fix reverted." 'Green'
            $count++
        }
    }

    $InsaneRevertMap = Join-Path $GameDir 'GearGame\Content\Maps\SP\SP_15_Tower\SP_Tower_P.gear'
    if (Test-Path $InsaneRevertMap) {
        $IBytes = [System.IO.File]::ReadAllBytes($InsaneRevertMap)
        $IHits = [GOW3SemanticByteLocator]::FindSpan($IBytes, (HexToBytes '86000000000000002d010000000000000400000000000000'), (HexToBytes '1d010000000000001e000000000000004d01000000000000'), 4)
        if ($IHits.Count -eq 1 -and $IBytes[$IHits[0]] -eq 30) {
            $IBytes[$IHits[0]] = 29
            Set-ItemProperty -Path $InsaneRevertMap -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
            [System.IO.File]::WriteAllBytes($InsaneRevertMap, $IBytes)
            Say "  ok  Insane unlock fix reverted." 'Green'
            $count++
        }
    }

    $WreckRevDir = Join-Path $GameDir 'GearGame\Content\Maps\SP\SP_04_Wreckage'
    if ((Test-Path (Join-Path $WreckRevDir 'SP_Wreckage_P.gear')) -and (Test-Path (Join-Path $WreckRevDir 'SP_Wreckage_01_S_CombatD.gear'))) {
        $BrumakRev = @()
        # reverse order: CombatD first so the early prepare exists again before SP_Wreckage_P goes back
        foreach ($E in @($BrumakEndEdits)[2, 1, 0]) {
            $BrumakRev += (Set-IntAtContext (Join-Path $WreckRevDir $E.Map) $E.Pre $E.Suf $E.New $E.Old)
        }
        if ($BrumakRev -contains 'applied') {
            Say "  ok  Wreckage Brumak chapter end fix reverted." 'Green'
            $count++
        }
    }
    $StubRev = @(foreach ($E in $BrumakStubEdits) {
        $F = Join-Path $WreckRevDir $E.Map
        if (Test-Path $F) { Set-IntAtContext $F $E.Pre $E.Suf $E.New $E.Old }
    })
    if ($StubRev -contains 'applied') {
        Say "  ok  Brumak death placeholders restored." 'Green'
        $count++
    }

    $DevRev = @(foreach ($E in $DevLeftoverEdits) {
        $F = Join-Path (Join-Path $GameDir 'GearGame\Content\Maps\SP') (Join-Path $E.Dir $E.Map)
        if (Test-Path $F) { Set-IntAtContext $F $E.Pre $E.Suf $E.New $E.Old }
    })
    if ($DevRev -contains 'applied') {
        Say "  ok  Dev build leftover fixes (slow motion, gamma) reverted." 'Green'
        $count++
    }

    if ($count -gt 0) {
        Say "`n  ok  Revert complete ($count files restored/reverted). Press any key to return..." 'Green'
    }
    else {
        Say "`n  No backups/fixes found to revert. Press any key to return..." 'Yellow'
    }
    $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
}

# ==============================================================================
# MAIN CONFIGURATION (Keep these updated in your Gist)
# ==============================================================================
$CURRENT_VERSION = "0.3.6"  # Update this string whenever you release a new version

function Check-ForUpdate {
    $scriptSource = $env:GOW3_MULTIPATCH_SOURCE
    if ([string]::IsNullOrWhiteSpace($scriptSource)) {
        $scriptSource = if ($PSScriptRoot) { 'Local' } else { 'Remote' }
    }

    switch ($scriptSource) {
        'Remote' {
            Say "  Running the latest Gist version ($CURRENT_VERSION)." 'Green'
            Say "  Official Gist: https://gist.github.com/DryreL/de1c192d4b95842d90fa6c28800f1d6d" 'Yellow'
        }
        'Local fallback' {
            Say "  Running the local fallback version ($CURRENT_VERSION). It may be older." 'Yellow'
            Say "  (If you are already on the latest version, ignore this message.)" 'Yellow'
        }
        default {
            Say "  Running the local version ($CURRENT_VERSION). It may be older." 'Yellow'
            Say "  (If you are already on the latest version, ignore this message.)" 'Yellow'
        }
    }
}

function Show-Credits {
    Clear-Host
    Write-Host '====================================================================' -ForegroundColor Cyan
    Write-Host ' CREDITS - MULTI PATCH TOOL' -ForegroundColor Cyan
    Write-Host '====================================================================' -ForegroundColor Cyan
    Write-Host '' -ForegroundColor White
    Write-Host ' DryreL        - Multi Patch Tool Developer' -ForegroundColor Yellow
    Write-Host ' DryreL        - Online Campaign Co-op Through Steam, Split-screen Co-op ' -ForegroundColor White
    Write-Host ' DryreL        - Campaign Soft-lock/Boss Fixes: "Homecoming" Razor Wire, "Bon Voyage" Plates + Last Stage' -ForegroundColor White
    Write-Host ' DryreL        - "Crater" Tower 2 Elevator, "Coast" Stealth Mechanism, "Wreckage" Brumak Boss, "Final" Tempest Boss' -ForegroundColor White
    Write-Host ' DryreL        - Mercy Mausoleum Cinematic: Dom''s COG tags / Maria necklace, Marcus receives Dom''s knife' -ForegroundColor White
    Write-Host ' DryreL        - Ultrawide & resolution UI scaling, Objective Failed screen scaling, D3DERR_INVALIDCALL crash fix' -ForegroundColor White
    Write-Host ' DryreL        - HUD Scaling Fix (virtual 720p canvas, scales with resolution)' -ForegroundColor White
    Write-Host ' DryreL        - Keyboard ESC / Enter navigation in Video Settings, Credits, War Journal; ActionScript PC translation' -ForegroundColor White
    Write-Host ' DryreL        - Installer options: player name, keybind, language selection, HUD scale, FOV and more...' -ForegroundColor White
    Write-Host ' DryreL        - Texture streaming / VRAM profile / 16x AF / 300 FPS ceiling (HD Textures Pack compatible)' -ForegroundColor White
    Write-Host ' DryreL        - Inverted analog stick fix' -ForegroundColor White
    Write-Host ' DryreL        - Debug text and warning removal; DLC placeholder removal; Insane difficulty unlock fix; Quit button' -ForegroundColor White
    Write-Host ' DryreL        - Linux support: Campaign "Connection Lost" fix, Linux resolution / uninstall fixes' -ForegroundColor White
    Write-Host ' DryreL        - Clean installation: automatic backups and one-click revert' -ForegroundColor White
    Write-Host ' Dlposn        - Loading Profile bypass + F10 server travel shortcut, opening intro logos restored' -ForegroundColor White
    Write-Host ' HJ            - Keyboard & Mouse button icons (replaces the gamepad icons in the UI, Warfare_HUD.upk)' -ForegroundColor White
    Write-Host ' Grilex        - High-resolution subtitle font (WarfareFonts2x)' -ForegroundColor White
    Write-Host ' pasha12071991 - New fonts with Turkish and Ukrainian characters' -ForegroundColor White
    Write-Host ' refresh50     - Checkpoint & Load Game fix (Fixsave), Bink / XAudio2 crash fix, Multi-language Missing Objectives fix' -ForegroundColor White
    Write-Host ' LukeBoya      - Act 2 Chapter 4 (Mother Corpser): the whole boss encounter is now a restored, retail-style fight' -ForegroundColor White
    Write-Host ' LukeBoya      - Act 3 to Act 4 Transition (Objective Failed Bugfix)' -ForegroundColor White
    Write-Host '' -ForegroundColor White
    Write-Host ' Page 1/2. Press ENTER for the Jacinto build credits.' -ForegroundColor DarkGray
    Read-Host | Out-Null
    Clear-Host
    Write-Host '====================================================================' -ForegroundColor Cyan
    Write-Host ' CREDITS - JACINTO BUILD' -ForegroundColor Cyan
    Write-Host '====================================================================' -ForegroundColor Cyan
    Write-Host ' Gears of War 3 Jacinto v1.1.1 (UNOFFICIAL PC BUILD)' -ForegroundColor Yellow
    Write-Host '' -ForegroundColor White
    Write-Host ' Helios        - Made the Jacinto build' -ForegroundColor White
    Write-Host ' Helios        - Worked on the Gears 3 campaign' -ForegroundColor White
    Write-Host ' Helios        - Re-made the menu script (with the help of Shauny, creator of Azura)' -ForegroundColor White
    Write-Host ' Helios        - Ported ALL the Gears of War: Judgment characters' -ForegroundColor White
    Write-Host ' Helios        - Ported the voice lines for Kait / Judgment characters' -ForegroundColor White
    Write-Host ' Helios        - Ported all Spanish voice lines for the campaign' -ForegroundColor White
    Write-Host ' Helios        - Added custom weapon skins and new playable Locusts' -ForegroundColor White
    Write-Host ' Extinct       - Scripts to save weapon skins / character selection in game' -ForegroundColor White
    Write-Host ' Extinct       - Ported the Kait character model from Gears 4' -ForegroundColor White
    Write-Host ' Casper        - Ported all Spanish voice lines for multiplayer' -ForegroundColor White
    Write-Host '' -ForegroundColor White
    Write-Host ' This is an unofficial build and not a 1:1 copy of the original campaign.' -ForegroundColor DarkGray
    Write-Host ' It is something to play while waiting for an official Gears release.' -ForegroundColor DarkGray
    Write-Host '====================================================================' -ForegroundColor Cyan
    Read-Host "Press ENTER to return"
    Clear-Host
}

# ---------------------------------------------------------------------------------------------------------------------
# Keybind editor (keyboard & mouse). The PC bindings are lines like  Bindings=(Name="R",Command="GBA_Reload")  in
# [Engine.PlayerInput] of GearInput.ini / DefaultInput.ini (and the Documents GearInput.ini, which overrides them).
# Only the first (primary) key of an action is changed; extra keys of an action (e.g. the thumb mouse button for Reload) stay.
# ---------------------------------------------------------------------------------------------------------------------
$KbActions = @(
    @('GBA_MoveForward', 'Move forward'), @('GBA_MoveBackward', 'Move backward'), @('GBA_StrafeLeft', 'Strafe left'), @('GBA_StrafeRight', 'Strafe right'),
    @('GBA_MoveAction', 'Cover / Roll / Sprint / Vault (A button)'), @('GBA_Crouch', 'Crouch / Switch shoulder'), @('GBA_Walk', 'Walk'),
    @('GBA_Shoot', 'Fire'), @('GBA_Target', 'Aim'), @('GBA_Melee', 'Melee / Execute (B button)'), @('GBA_Reload', 'Reload / Active Reload'),
    @('GBA_Zoom', 'Zoom / Scope'), @('GBA_Pickup', 'Pick up / Swap weapon / Revive (X button)'), @('GBA_POI', 'Look at point of interest / Ping (Y button)'),
    @('GBA_Taccom', 'Squad commands (TacCom)'), @('GBA_Weap1', 'Weapon slot 1'), @('GBA_Weap2', 'Weapon slot 2'), @('GBA_Pistols', 'Pistol'),
    @('GBA_Grenades', 'Grenade'), @('GBA_PrevWeapon', 'Previous weapon'), @('GBA_NextWeapon', 'Next weapon'), @('GBA_Scoreboard', 'Scoreboard')
)
$KbDefaults = @{
    GBA_MoveForward = 'W'; GBA_MoveBackward = 'S'; GBA_StrafeLeft = 'A'; GBA_StrafeRight = 'D'; GBA_MoveAction = 'SpaceBar'; GBA_Crouch = 'C'
    GBA_Walk = 'LeftControl'; GBA_Shoot = 'LeftMouseButton'; GBA_Target = 'RightMouseButton'; GBA_Melee = 'F'; GBA_Reload = 'R'; GBA_Zoom = 'T'
    GBA_Pickup = 'E'; GBA_POI = 'Q'; GBA_Taccom = 'H'; GBA_Weap1 = 'One'; GBA_Weap2 = 'Two'; GBA_Pistols = 'Three'; GBA_Grenades = 'Four'
    GBA_PrevWeapon = 'MouseScrollUp'; GBA_NextWeapon = 'MouseScrollDown'; GBA_Scoreboard = 'Slash'
}
$KbKeyFile = Join-Path (Split-Path -Parent $BackupRoot) "keybinds.json"

# Valid Unreal Engine 3 key names, plus friendly aliases people type
$KbKeyNames = @([char[]](65..90) | ForEach-Object { [string]$_ }) + @('Zero', 'One', 'Two', 'Three', 'Four', 'Five', 'Six', 'Seven', 'Eight', 'Nine') + @(1..12 | ForEach-Object { "F$_" }) + @(
    'LeftShift', 'RightShift', 'LeftControl', 'RightControl', 'LeftAlt', 'RightAlt', 'SpaceBar', 'Tab', 'CapsLock', 'Enter', 'BackSpace', 'Escape',
    'LeftMouseButton', 'RightMouseButton', 'MiddleMouseButton', 'ThumbMouseButton', 'ThumbMouseButton2', 'MouseScrollUp', 'MouseScrollDown',
    'Up', 'Down', 'Left', 'Right', 'Home', 'End', 'PageUp', 'PageDown', 'Insert', 'Delete', 'Semicolon', 'Equals', 'Comma', 'Underscore', 'Period',
    'Slash', 'Tilde', 'LeftBracket', 'RightBracket', 'Backslash', 'Quote',
    'NumPadZero', 'NumPadOne', 'NumPadTwo', 'NumPadThree', 'NumPadFour', 'NumPadFive', 'NumPadSix', 'NumPadSeven', 'NumPadEight', 'NumPadNine')
$KbAliases = @{
    'space' = 'SpaceBar'; 'spacebar' = 'SpaceBar'; 'shift' = 'LeftShift'; 'lshift' = 'LeftShift'; 'rshift' = 'RightShift'; 'ctrl' = 'LeftControl'
    'control' = 'LeftControl'; 'lctrl' = 'LeftControl'; 'rctrl' = 'RightControl'; 'alt' = 'LeftAlt'; 'lalt' = 'LeftAlt'; 'ralt' = 'RightAlt'
    'lmb' = 'LeftMouseButton'; 'mouse1' = 'LeftMouseButton'; 'rmb' = 'RightMouseButton'; 'mouse2' = 'RightMouseButton'; 'mmb' = 'MiddleMouseButton'
    'mouse3' = 'MiddleMouseButton'; 'mouse4' = 'ThumbMouseButton'; 'mouse5' = 'ThumbMouseButton2'; 'wheelup' = 'MouseScrollUp'; 'wheeldown' = 'MouseScrollDown'
    'esc' = 'Escape'; 'return' = 'Enter'; 'caps' = 'CapsLock'; 'backspace' = 'BackSpace'; 'pgup' = 'PageUp'; 'pgdn' = 'PageDown'; 'del' = 'Delete'; 'ins' = 'Insert'
    'backslash' = 'Backslash'; ';' = 'Semicolon'; '=' = 'Equals'; ',' = 'Comma'; '.' = 'Period'; '/' = 'Slash'; '`' = 'Tilde'; '[' = 'LeftBracket'; ']' = 'RightBracket'
    "'" = 'Quote'; '\' = 'Backslash'
}
for ($d = 0; $d -le 9; $d++) { $KbAliases["$d"] = @('Zero', 'One', 'Two', 'Three', 'Four', 'Five', 'Six', 'Seven', 'Eight', 'Nine')[$d] }

function ConvertTo-KbKeyName($Text) {
    $s = ([string]$Text).Trim().Trim('"')
    if (-not $s) { return $null }
    $k = $s.ToLowerInvariant().Replace(' ', '')
    if ($KbAliases.ContainsKey($k)) { return $KbAliases[$k] }
    foreach ($n in $KbKeyNames) { if ($n -ieq $s.Replace(' ', '')) { return $n } }
    return $null
}

# One keyboard / mouse binding line of an action: prefix, key, command (group 4 = GBA_ name)
$KbLineRegex = '(?m)^([ \t]*[.+]?Bindings=\(Name=")([^"]+)(",Command="(GBA_\w+)[^"]*"\)[^\r\n]*)\r?$'

# action -> list of keys (primary first) found in this ini text (gamepad entries ignored)
function Get-KbBindings($Text) {
    $map = @{}
    foreach ($m in [regex]::Matches($Text, $KbLineRegex)) {
        $key = $m.Groups[2].Value; $cmd = $m.Groups[4].Value
        if ($key -match '^(XboxTypeS_|Gamepad)') { continue }
        if (-not $map.ContainsKey($cmd)) { $map[$cmd] = [System.Collections.Generic.List[string]]::new() }
        $map[$cmd].Add($key)
    }
    return $map
}

# $Want: action -> new primary key. Rewrites only the first keyboard / mouse line of each listed action.
function Set-KbBindings($Text, $Want) {
    $seen = @{}
    return [regex]::Replace($Text, $KbLineRegex, [System.Text.RegularExpressions.MatchEvaluator] {
            param($m)
            $key = $m.Groups[2].Value; $cmd = $m.Groups[4].Value
            if ($key -match '^(XboxTypeS_|Gamepad)') { return $m.Value }
            if ($seen.ContainsKey($cmd)) { return $m.Value }
            $seen[$cmd] = $true
            if ($Want.ContainsKey($cmd)) { return $m.Groups[1].Value + $Want[$cmd] + $m.Groups[3].Value }
            return $m.Value
        })
}

function Get-KbInputInis {
    $docIni = Join-Path ([Environment]::GetFolderPath("MyDocuments")) "My Games\Gears of War 3\GearGame\Config\GearInput.ini"
    return @($docIni, (Join-Path $ConfigDir "GearInput.ini"), (Join-Path $ConfigDir "DefaultInput.ini")) | Where-Object { Test-Path -LiteralPath $_ }
}

# Writes the wanted primary keys into every existing input ini (backups are taken first)
function Save-KbBindings($Want) {
    $n = 0
    foreach ($ini in (Get-KbInputInis)) {
        Backup $ini
        $txt = [System.IO.File]::ReadAllText($ini)
        $new = Set-KbBindings $txt $Want
        if ($new -ne $txt) {
            Set-ItemProperty -Path $ini -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
            [System.IO.File]::WriteAllText($ini, $new)
        }
        $n++
    }
    return $n
}

function Get-SavedKeybinds {
    $want = @{}
    if (Test-Path -LiteralPath $KbKeyFile) {
        try {
            $obj = Get-Content -LiteralPath $KbKeyFile -Raw | ConvertFrom-Json
            foreach ($p in $obj.PSObject.Properties) { if ($p.Value -and ($KbDefaults.ContainsKey($p.Name))) { $want[$p.Name] = [string]$p.Value } }
        }
        catch { }
    }
    return $want
}

function Show-KeybindEditor {
    $inis = @(Get-KbInputInis)
    if ($inis.Count -eq 0) { Say "  No GearInput.ini found in the game folder." 'Red'; Read-Host "Press ENTER to return" | Out-Null; return }
    # Current state = what the first (effective) ini says, then our saved choices on top
    $cur = Get-KbBindings ([System.IO.File]::ReadAllText($inis[0]))
    $want = @{}
    foreach ($a in $KbActions) {
        $c = $a[0]
        $want[$c] = if ($cur.ContainsKey($c)) { $cur[$c][0] } else { $KbDefaults[$c] }
    }
    $extra = @{}   # secondary keys that stay bound (e.g. ThumbMouseButton -> Reload)
    foreach ($a in $KbActions) { if ($cur.ContainsKey($a[0]) -and $cur[$a[0]].Count -gt 1) { $extra[$a[0]] = @($cur[$a[0]] | Select-Object -Skip 1) } }

    while ($true) {
        Clear-Host
        Write-Host "====================================================================" -ForegroundColor Cyan
        Write-Host " KEYBIND EDITOR (Keyboard & Mouse)" -ForegroundColor Cyan
        Write-Host "====================================================================" -ForegroundColor Cyan
        $i = 1
        foreach ($a in $KbActions) {
            $c = $a[0]
            $ex = if ($extra.ContainsKey($c)) { "  (+ $($extra[$c] -join ', '))" } else { '' }
            $mark = if ($want[$c] -ne $KbDefaults[$c]) { '*' } else { ' ' }
            Write-Host ("  {0,2}. {1,-46} {2}{3}{4}" -f $i, $a[1], $want[$c], $ex, $mark) -ForegroundColor $(if ($mark -eq '*') { 'Yellow' } else { 'White' })
            $i++
        }
        Write-Host ""
        Write-Host "  * = changed from the game default.  Pause / Escape (menu) are not editable." -ForegroundColor DarkGray
        Write-Host "  Number = rebind that action   R = reset all to defaults   S = save and return   Q = return without saving" -ForegroundColor DarkGray
        Write-Host ""
        $choice = (Read-Host "Choose").Trim()
        if ($choice -ieq 'q') { return }
        if ($choice -ieq 's') {
            $n = Save-KbBindings $want
            $diff = @{}
            foreach ($c in $want.Keys) { if ($want[$c] -ne $KbDefaults[$c]) { $diff[$c] = $want[$c] } }
            $kbDir = Split-Path -Parent $KbKeyFile
            if (-not (Test-Path -LiteralPath $kbDir)) { New-Item -ItemType Directory -Force -Path $kbDir | Out-Null }
            ($diff | ConvertTo-Json) | Set-Content -LiteralPath $KbKeyFile -Encoding UTF8
            Say "  ok  Keybinds written to $n input file(s). They are re-applied automatically when you run the installer again." 'Green'
            Read-Host "Press ENTER to return" | Out-Null
            return
        }
        if ($choice -ieq 'r') {
            foreach ($a in $KbActions) { $want[$a[0]] = $KbDefaults[$a[0]] }
            continue
        }
        $num = 0
        if (-not ([int]::TryParse($choice, [ref]$num) -and $num -ge 1 -and $num -le $KbActions.Count)) { continue }
        $act = $KbActions[$num - 1]
        Write-Host ""
        Write-Host "  Rebinding: $($act[1])  (now: $($want[$act[0]]))" -ForegroundColor Cyan
        Write-Host "  Type the new key: a letter or digit, F1-F12, Space, Shift, Ctrl, Alt, Tab, LeftMouseButton, MiddleMouseButton," -ForegroundColor DarkGray
        Write-Host "  Mouse4 / Mouse5 (side buttons), MouseScrollUp, MouseScrollDown, arrows ... (empty = cancel)" -ForegroundColor DarkGray
        $raw = Read-Host "New key"
        if ([string]::IsNullOrWhiteSpace($raw)) { continue }
        $new = ConvertTo-KbKeyName $raw
        if (-not $new) { Say "  [!] '$raw' is not a known key name." 'Red'; Read-Host "Press ENTER" | Out-Null; continue }
        if ($new -eq 'Escape') { Say "  [!] Escape is reserved for the pause menu." 'Red'; Read-Host "Press ENTER" | Out-Null; continue }
        $owner = $null; $secondaryOf = $null
        foreach ($a in $KbActions) {
            if ($a[0] -ne $act[0] -and $want[$a[0]] -eq $new) { $owner = $a }
            if ($a[0] -ne $act[0] -and $extra.ContainsKey($a[0]) -and ($extra[$a[0]] -contains $new)) { $secondaryOf = $a }
        }
        if ($secondaryOf) { Say "  [!] $new is also bound (second key) to '$($secondaryOf[1])'. Rebind that one first." 'Red'; Read-Host "Press ENTER" | Out-Null; continue }
        if ($owner) {
            $want[$owner[0]] = $want[$act[0]]
            Say "  $new was used by '$($owner[1])': the two actions swapped keys ($($owner[1]) is now $($want[$owner[0]]))." 'Yellow'
            Read-Host "Press ENTER" | Out-Null
        }
        $want[$act[0]] = $new
    }
}

# True when $dst is our JacintoNet file: same as the _files copy, or (wsock32.dll) any JacintoNet build, found by its log text.
function Test-JacintoNetFile($src, $dst) {
    if (-not ((Test-Path -LiteralPath $src) -and (Test-Path -LiteralPath $dst))) { return $false }
    if ((Get-FileHash -LiteralPath $src).Hash -eq (Get-FileHash -LiteralPath $dst).Hash) { return $true }
    return (Split-Path -Leaf $dst) -ieq 'wsock32.dll' -and [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($dst)).Contains('JacintoNet %s')
}

# ---- Game Settings (main menu option 5): player name, language, resolution, display mode, FOV, mouse sensitivity, subtitles, VSync,
# run camera shake, post-process effects. Saved in graphics.json next to the backups (_files) and applied again by every install.
$GfxFile = Join-Path (Split-Path -Parent $BackupRoot) "graphics.json"
$GfxEffects = @(
    @{ Id = 'MotionBlur';           Name = 'Motion Blur';                                   Keys = @('MotionBlur', 'MotionBlurPause') },
    @{ Id = 'DepthOfField';         Name = 'Depth of Field';                                Keys = @('DepthOfField') },
    @{ Id = 'Bloom';                Name = 'Bloom';                                         Keys = @('Bloom') },
    @{ Id = 'LensFlares';           Name = 'Lens Flares';                                   Keys = @('LensFlares') },
    @{ Id = 'LightShafts';          Name = 'Light Shafts';                                  Keys = @('bAllowLightShafts') },
    @{ Id = 'Distortion';           Name = 'Distortion (heat haze, shock waves)';           Keys = @('Distortion', 'FilteredDistortion') },
    @{ Id = 'RadialBlur';           Name = 'Radial Blur';                                   Keys = @('AllowRadialBlur') },
    @{ Id = 'Subsurface';           Name = 'Subsurface Scattering (skin / flesh glow)';     Keys = @('AllowSubsurfaceScattering') },
    @{ Id = 'SeparateTranslucency'; Name = 'Separate Translucency (smoke / glass sorting)'; Keys = @('bAllowSeparateTranslucency') }
)
$GsLanguages = @(
    @{ Code = 'INT'; Name = 'English (INT)' },
    @{ Code = 'ESM'; Name = 'Spanish (ESM)' },
    @{ Code = 'ITA'; Name = 'Italian (ITA)' },
    @{ Code = 'TUR'; Name = 'Turkish (TUR) - Community Mod'; Url = 'https://www.nexusmods.com/gearsofwar3/mods/26' },
    @{ Code = 'INT'; Name = 'Portuguese - Brazil (INT override) - Community Mod'; Url = 'https://www.nexusmods.com/gearsofwar3/mods/24' },
    @{ Code = 'CHT'; Name = 'Traditional Chinese (CHT) - Community Mod'; Url = 'https://www.nexusmods.com/gearsofwar3/mods/31' },
    @{ Code = 'RUS'; Name = 'Russian (RUS) - Community Mod'; Url = 'https://www.nexusmods.com/gearsofwar3/mods/33' }
)
$GsResolutions = @(
    @{ Value = 'native';    Name = 'Native Display (auto-detect)' },
    @{ Value = '1280x720';  Name = '1280 x 720   (HD - 16:9)' },
    @{ Value = '1920x1080'; Name = '1920 x 1080  (Full HD - 16:9)' },
    @{ Value = '2560x1440'; Name = '2560 x 1440  (2K / QHD - 16:9)' },
    @{ Value = '3840x2160'; Name = '3840 x 2160  (4K / UHD - 16:9)' },
    @{ Value = '2560x1080'; Name = '2560 x 1080  (Ultrawide - 21:9)' },
    @{ Value = '3440x1440'; Name = '3440 x 1440  (Ultrawide 2K - 21:9)' },
    @{ Value = '5120x1440'; Name = '5120 x 1440  (Super Ultrawide - 32:9)' },
    # Triple monitor: NVIDIA Surround / AMD Eyefinity show the three screens as one wide display. 'span' = the whole desktop (all monitors).
    @{ Value = 'span';      Name = 'Triple monitor - span all displays (NVIDIA Surround / AMD Eyefinity, auto-detect)' },
    @{ Value = '5760x1080'; Name = '5760 x 1080  (Triple monitor 3 x 1080p - 48:9)' },
    @{ Value = '7680x1440'; Name = '7680 x 1440  (Triple monitor 3 x 1440p - 48:9)' }
)
$GsSubtitleSizes = [ordered]@{ big = 'Big (WarfareFonts2x, recommended for PC)'; normal = 'Normal (WarfareFonts, handhelds)'; default = 'Default (the game''s original font)' }

# Languages with non-Latin / special character sets (Chinese, Japanese, Korean, Russian, Arabic, etc.).
# WarfareFonts is designed for Latin and Turkish character sets. For special character languages,
# WarfareFonts is NOT installed by default to prevent broken characters/tofu glyphs, unless explicitly forced by the user.
# All other languages (including custom Latin packs like FRA, DEU, POL, POR, etc.) install WarfareFonts normally.
$GsSpecialGlyphLanguages = @('RUS', 'CHN', 'CHT', 'ZHO', 'CHI', 'JPN', 'JAP', 'KOR', 'ARA', 'PER', 'FAR', 'HEB', 'THA')
function Test-GsSpecialGlyphLanguage($code) {
    if (-not $code) { return $false }
    return ($GsSpecialGlyphLanguages -contains ([string]$code).ToUpper())
}
function Test-GsCustomFontLanguage($code) {
    return (Test-GsSpecialGlyphLanguage $code)
}
function Test-GsShouldSkipWarfareFonts($g) {
    if (-not $g) { return $false }
    $code = if ($g.Language) { $g.Language } else { 'INT' }
    if (Test-GsSpecialGlyphLanguage $code) {
        return (-not [bool]$g.ForceWarfareFonts)
    }
    return $false
}

# Refresh rates (Hz) the display offers at a resolution (Windows only; empty elsewhere)
function Get-DisplayRefreshRates([int]$W, [int]$H) {
    if (-not $IsWin) { return @() }
    try {
        if (-not ('GOW3DisplayModes' -as [type])) {
            Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class GOW3DisplayModes {
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
  public struct DEVMODE { [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName; public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public int dmFields; public int dmPositionX, dmPositionY; public int dmDisplayOrientation, dmDisplayFixedOutput; public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName; public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency, dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight; }
  [DllImport("user32.dll", CharSet = CharSet.Ansi)] public static extern bool EnumDisplaySettings(string dev, int mode, ref DEVMODE dm);
  public static int[] Rates(int w, int h) {
    var l = new System.Collections.Generic.SortedSet<int>(); var d = new DEVMODE(); d.dmSize = (short)Marshal.SizeOf(d); int i = 0;
    while (EnumDisplaySettings(null, i++, ref d)) { if (d.dmBitsPerPel == 32 && d.dmPelsWidth == w && d.dmPelsHeight == h && d.dmDisplayFrequency > 1) l.Add(d.dmDisplayFrequency); }
    var a = new int[l.Count]; l.CopyTo(a); return a;
  }
}
"@
        }
        return @([GOW3DisplayModes]::Rates($W, $H))
    }
    catch { return @() }
}

function Get-GameSettings([switch]$Defaults) {
    # Defaults: what the installer used to suggest (native resolution, fullscreen, English, automatic FOV, big subtitles, VSync off ...)
    $g = @{ PlayerName = ''; Language = 'INT'; Resolution = 'native'; Fullscreen = $true; VSync = $false; FpsLimit = 0; RefreshRate = 0
            DriveType = 'ssd'; Fov = 0; FovAllModes = $false; MouseSens = ''; Subtitles = $true; SubtitleSize = 'big'; ForceWarfareFonts = $false
            GraphicsQuality = 'high'; ShadowQuality = 'high'; TextureQuality = 'high'; FoliageQuality = 'high'
            Anisotropy = 16; AntiAliasing = 'off'; AmbientOcclusion = $true; DisableRunShake = $true; AudioFilter = $true; AudioChannels = 32; HordeBeast = $false; Disabled = @() }
    if (-not $Defaults -and (Test-Path -LiteralPath $GfxFile)) {
        try {
            $o = Get-Content -LiteralPath $GfxFile -Raw | ConvertFrom-Json
            foreach ($k in @($g.Keys)) {
                if ($null -eq $o.$k) { continue }
                if ($k -eq 'Disabled') { $g.Disabled = @($o.Disabled | Where-Object { $id = $_; $GfxEffects | Where-Object { $_.Id -eq $id } }) }
                elseif ($g[$k] -is [bool]) { $g[$k] = [bool]$o.$k }
                elseif ($g[$k] -is [int]) { $g[$k] = [int]$o.$k }
                else { $g[$k] = ([string]$o.$k).Trim() }
            }
        }
        catch { }
    }
    if ($g.Language -notmatch '^[A-Za-z]{3}$') { $g.Language = 'INT' } else { $g.Language = $g.Language.ToUpper() }
    if ($g.Resolution -notmatch '^(native|span|\d{3,5}x\d{3,5})$') { $g.Resolution = 'native' }
    if (-not $GsSubtitleSizes.Contains($g.SubtitleSize)) { $g.SubtitleSize = 'big' }
    if ($g.Fov -ne 0 -and ($g.Fov -lt 50 -or $g.Fov -gt 130)) { $g.Fov = 0 }
    if ($g.RefreshRate -lt 0 -or $g.RefreshRate -gt 1000) { $g.RefreshRate = 0 }
    if ($g.FpsLimit -lt 0 -or $g.FpsLimit -gt 500) { $g.FpsLimit = 0 }
    if ($g.DriveType -notin @('ssd', 'hdd')) { $g.DriveType = 'ssd' }
    if ($g.GraphicsQuality -notin @('low', 'medium', 'high', 'ultra')) { $g.GraphicsQuality = 'high' }
    if ($g.ShadowQuality -notin @('low', 'medium', 'high', 'ultra')) { $g.ShadowQuality = 'high' }
    if ($g.TextureQuality -notin @('low', 'medium', 'high', 'ultra')) { $g.TextureQuality = 'high' }
    if ($g.FoliageQuality -notin @('low', 'medium', 'high', 'ultra')) { $g.FoliageQuality = 'high' }
    if ($g.Anisotropy -notin @(1, 2, 4, 8, 16)) { $g.Anisotropy = 16 }
    if ($g.AntiAliasing -notin @('off', 'fxaa', 'msaa2x', 'msaa4x')) { $g.AntiAliasing = 'off' }
    if ($g.AudioChannels -notin @(32, 64, 128)) { $g.AudioChannels = 32 }
    return $g
}

function Save-GameSettings($g) {
    $dir = Split-Path -Parent $GfxFile
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $o = [ordered]@{}
    foreach ($k in @('PlayerName', 'Language', 'Resolution', 'Fullscreen', 'VSync', 'FpsLimit', 'RefreshRate', 'DriveType', 'Fov', 'FovAllModes', 'MouseSens', 'Subtitles', 'SubtitleSize', 'ForceWarfareFonts', 'GraphicsQuality', 'ShadowQuality', 'TextureQuality', 'FoliageQuality', 'Anisotropy', 'AntiAliasing', 'AmbientOcclusion', 'DisableRunShake', 'AudioFilter', 'AudioChannels', 'HordeBeast')) { $o[$k] = $g[$k] }
    $o['Disabled'] = @($g.Disabled)
    ($o | ConvertTo-Json) | Set-Content -LiteralPath $GfxFile -Encoding UTF8
}

function Get-GsResolution($g) {
    if ($g.Resolution -match '^(\d+)x(\d+)$') { return @([int]$matches[1], [int]$matches[2]) }
    if ($g.Resolution -eq 'span') { return Get-SpanResolution }
    return Get-NativeResolution
}

# The game ships with FOV 80 (tuned for 16:9, AspectRatio_MaintainXFOV = horizontal FOV). On ultrawide the tool uses MaintainYFOV
# (vertical FOV fixed), so those screens see a much wider horizontal view and 80 feels too wide; automatic FOV picks 65 there.
function Get-GsFov($g, $resX, $resY) {
    if ($g.Fov -ge 50 -and $g.Fov -le 130) { return $g.Fov }
    if ($resX / $resY -ge 2.0) { return 65 } else { return 80 }
}

function Write-IniIfChanged($Path, $Old, $New) {
    if ($New -eq $Old) { return }
    Set-ItemProperty -LiteralPath $Path -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
    [System.IO.File]::WriteAllText($Path, $New)
}

# The startup parameters of a settings object: window mode, VSync, resolution, language (the exe switches override the ini values)
function Get-GsStartArgs($g) {
    $res = Get-GsResolution $g
    $fsFlag = $(if ($g.Fullscreen) { "-FULLSCREEN" } else { "-WINDOWED" }) + $(if ($g.VSync) { " -VSYNC" } else { " -NOVSYNC" })
    # Audio filter off: the engine runs the console commands of Binaries\Win64\Gears3_Audio.txt once at startup (-exec=, path relative to Binaries\Win64).
    $audioExec = if (-not $g.AudioFilter) { ' -exec=Gears3_Audio.txt' } else { '' }
    return "$fsFlag -ResX=$($res[0]) -ResY=$($res[1]) -language=$($g.Language)$audioExec"
}

# Puts the current startup parameters into every launcher file that exists: Gears3_Start_Game.bat / .sh, Gears3_Start_Game_Debug.bat / .sh and
# Binaries\Win64\StartCampaign.bat. The debug launchers also read the parameters from the start launcher when they run, but their own fallback
# line is kept equal too, so every file shows the same parameters. Returns the number of files written.
function Update-LauncherFiles($g) {
    $startArgs = Get-GsStartArgs $g
    $n = 0
    $specs = @(
        @{ Path = (Join-Path $GameDir 'Gears3_Start_Game.bat');       Rx = '(?m)(?<=GearGame\.exe )-(?:FULLSCREEN|WINDOWED)[^\r\n]*'; New = $startArgs },
        @{ Path = (Join-Path $BinariesDir 'StartCampaign.bat');       Rx = '(?m)(?<=^GearGame\.exe )-(?:FULLSCREEN|WINDOWED)[^\r\n]*'; New = $startArgs },
        @{ Path = (Join-Path $GameDir 'Gears3_Start_Game_Debug.bat'); Rx = '(?m)(?<=^set "ARGS=)[^"\r\n]*(?=")'; New = $startArgs },
        @{ Path = (Join-Path $GameDir 'Gears3_Start_Game.sh');        Rx = '(?m)(?<=^ARGS=")[^"\r\n]*(?=")'; New = $startArgs },
        @{ Path = (Join-Path $GameDir 'Gears3_Start_Game_Debug.sh');  Rx = '(?m)(?<=ARGS=")-(?:FULLSCREEN|WINDOWED)[^"\r\n]*(?=")|(?<=\[ -z "\$ARGS" \] && ARGS=")[^"\r\n]*(?=")'; New = $startArgs }
    )
    foreach ($s in $specs) {
        if (-not (Test-Path -LiteralPath $s.Path)) { continue }
        $bytes = [System.IO.File]::ReadAllBytes($s.Path)
        $bomUtf8 = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
        $txt = [System.Text.Encoding]::UTF8.GetString($bytes).TrimStart([char]0xFEFF)
        $newArgs = $s.New
        $new = [regex]::Replace($txt, $s.Rx, [System.Text.RegularExpressions.MatchEvaluator] { param($m) $newArgs })
        if ($new -ne $txt) {
            Set-ItemProperty -LiteralPath $s.Path -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
            [System.IO.File]::WriteAllText($s.Path, $new, (New-Object System.Text.UTF8Encoding($bomUtf8)))
            Say "  ok  Startup parameters updated in $(Split-Path -Leaf $s.Path): $startArgs" 'DarkGray'
            $n++
        }
    }
    return $n
}

# Writes every Game Setting into the game files (backups are taken first). Used by the menu (S) and by the installer. Returns the number of files checked.
function Apply-GameSettings($g) {
    $n = 0
    $res = Get-GsResolution $g; $resX = $res[0]; $resY = $res[1]
    $fov = Get-GsFov $g $resX $resY
    $lang = $g.Language
    $vs = if ($g.VSync) { 'True' } else { 'False' }
    $fs = if ($g.Fullscreen) { 'True' } else { 'False' }
    $docDir = Join-Path ([Environment]::GetFolderPath("MyDocuments")) "My Games\Gears of War 3\GearGame\Config"

    # Subtitle font package: big / normal copy the tool's fonts; default restores original.
    # For languages with special character sets (Chinese, Japanese, Korean, Russian, Arabic ...), WarfareFonts
    # is skipped by default to preserve the language's native/mod font, unless ForceWarfareFonts is explicitly enabled.
    # For all other languages (both built-in and custom Latin packs like FRA, DEU, POL), WarfareFonts is installed.
    $fontName = switch ($g.SubtitleSize) { 'big' { 'WarfareFonts2x' } 'normal' { 'WarfareFonts' } default { $null } }
    $skipFont = Test-GsShouldSkipWarfareFonts $g
    foreach ($f in $(if ($skipFont) { @() } else { @('WarfareFonts2x', 'WarfareFonts') })) {
        $dst = Join-Path $GameDir "GearGame\Content\Interface\$f.upk"
        if ($f -eq $fontName) {
            $src = Join-Path $FilesDir "$f.upk"
            if (Test-Path -LiteralPath $src) {
                Backup $dst
                if (Test-Path -LiteralPath $dst) { Set-ItemProperty -LiteralPath $dst -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue }
                Copy-Item -LiteralPath $src -Destination $dst -Force
                $n++
            }
            else { Say "  skip  _files\$f.upk not found; the subtitle font package is left as it is." 'Yellow' }
        }
        elseif ($null -eq $fontName) {
            $bak = Get-LongPath (Get-BackupPath $dst)
            if (Test-Path -LiteralPath $bak) { Copy-Item -LiteralPath $bak -Destination $dst -Force; $n++ }
        }
    }

    foreach ($ini in @((Join-Path $ConfigDir "GearEngine.ini"), (Join-Path $ConfigDir "DefaultEngine.ini"), (Join-Path $GameDir "Engine\Config\BaseEngine.ini"), (Join-Path $docDir "GearEngine.ini"))) {
        if (-not (Test-Path -LiteralPath $ini)) { continue }
        Backup $ini
        $txt = [System.IO.File]::ReadAllText($ini)
        $new = [regex]::Replace($txt, '(?m)^ResX=[^\r\n]*', "ResX=$resX")
        $new = [regex]::Replace($new, '(?m)^ResY=[^\r\n]*', "ResY=$resY")
        # MaintainXFOV (game default) makes FOV a true horizontal angle, so 80 means 80 on 16:9 / 16:10. MaintainYFOV fixes the vertical angle, which makes the same value look much wider; only ultrawide uses it.
        $axis = if ($resX / $resY -ge 2.0) { 'AspectRatio_MaintainYFOV' } else { 'AspectRatio_MaintainXFOV' }
        $new = [regex]::Replace($new, '(?m)^AspectRatioAxisConstraint=[^\r\n]*', "AspectRatioAxisConstraint=$axis")
        $new = [regex]::Replace($new, '(?m)^Fullscreen=[^\r\n]*', "Fullscreen=$fs")
        $new = [regex]::Replace($new, '(?m)^UseVsync=[^\r\n]*', "UseVsync=$vs")
        $new = Set-IniSectionValue $new 'SystemSettings' 'OneFrameThreadLag' 'True'
        # Fullscreen refresh rate: the game builds its list of display modes from [WinDrv.WindowsClient] Min / MaxAllowableRefreshRate (0 = every mode)
        $new = Set-IniSectionValue $new 'WinDrv.WindowsClient' 'MinAllowableRefreshRate' $g.RefreshRate
        $new = Set-IniSectionValue $new 'WinDrv.WindowsClient' 'MaxAllowableRefreshRate' $g.RefreshRate
        $new = [regex]::Replace($new, '(?m)^Language=[a-zA-Z]{3}(?=\r?$)', "Language=$lang")
        $new = [regex]::Replace($new, '(?m)^LanguageExt=[^\r\n]*', "LanguageExt=$lang")
        foreach ($fx in $GfxEffects) {
            $val = if ($g.Disabled -contains $fx.Id) { 'False' } else { 'True' }
            foreach ($k in $fx.Keys) { $new = Set-IniSectionValue $new 'SystemSettings' $k $val }
        }
        # Off forces subtitles off; On leaves it to the in-game option (Options > Subtitles).
        if ($new -match '(?m)^bSubtitlesForcedOff=') { $new = Set-IniSectionValue $new 'Engine.Engine' 'bSubtitlesForcedOff' $(if ($g.Subtitles) { 'FALSE' } else { 'TRUE' }) }

        # FPS limit (smooth frame rate)
        if ($g.FpsLimit -gt 0) {
            $new = Set-IniSectionValue $new 'Engine.Engine' 'bSmoothFrameRate' 'True'
            $new = Set-IniSectionValue $new 'Engine.Engine' 'MinSmoothedFrameRate' '0'
            $new = Set-IniSectionValue $new 'Engine.Engine' 'MaxSmoothedFrameRate' "$($g.FpsLimit)"
            $new = Set-IniSectionValue $new 'Engine.GameEngine' 'bSmoothFrameRate' 'True'
            $new = Set-IniSectionValue $new 'Engine.GameEngine' 'MaxSmoothedFrameRate' "$($g.FpsLimit)"
        } else {
            $new = Set-IniSectionValue $new 'Engine.Engine' 'bSmoothFrameRate' 'False'
            $new = Set-IniSectionValue $new 'Engine.Engine' 'MinSmoothedFrameRate' '0'
            $new = Set-IniSectionValue $new 'Engine.Engine' 'MaxSmoothedFrameRate' '300'
            $new = Set-IniSectionValue $new 'Engine.GameEngine' 'bSmoothFrameRate' 'False'
            $new = Set-IniSectionValue $new 'Engine.GameEngine' 'MaxSmoothedFrameRate' '300'
        }

        # Overall graphics quality / detail mode
        switch ($g.GraphicsQuality) {
            'low' {
                $new = Set-IniSectionValue $new 'SystemSettings' 'DetailMode' '0'
                $new = Set-IniSectionValue $new 'SystemSettings' 'MaxDrawDistanceScale' '0.800000'
                $new = Set-IniSectionValue $new 'SystemSettings' 'SkeletalMeshLODBias' '1'
                $new = Set-IniSectionValue $new 'SystemSettings' 'StaticMeshLODBias' '1'
                $new = Set-IniSectionValue $new 'SystemSettings' 'bUseMaxQualityMode' 'False'
            }
            'medium' {
                $new = Set-IniSectionValue $new 'SystemSettings' 'DetailMode' '1'
                $new = Set-IniSectionValue $new 'SystemSettings' 'MaxDrawDistanceScale' '1.000000'
                $new = Set-IniSectionValue $new 'SystemSettings' 'SkeletalMeshLODBias' '0'
                $new = Set-IniSectionValue $new 'SystemSettings' 'StaticMeshLODBias' '0'
                $new = Set-IniSectionValue $new 'SystemSettings' 'bUseMaxQualityMode' 'False'
            }
            'ultra' {
                $new = Set-IniSectionValue $new 'SystemSettings' 'DetailMode' '2'
                $new = Set-IniSectionValue $new 'SystemSettings' 'MaxDrawDistanceScale' '1.250000'
                $new = Set-IniSectionValue $new 'SystemSettings' 'SkeletalMeshLODBias' '-1'
                $new = Set-IniSectionValue $new 'SystemSettings' 'StaticMeshLODBias' '-1'
                $new = Set-IniSectionValue $new 'SystemSettings' 'bUseMaxQualityMode' 'False'  # True makes the streamer request top mips missing from cooked packages -> Offset != INDEX_NONE crash
            }
            default { # high
                $new = Set-IniSectionValue $new 'SystemSettings' 'DetailMode' '2'
                $new = Set-IniSectionValue $new 'SystemSettings' 'MaxDrawDistanceScale' '1.000000'
                $new = Set-IniSectionValue $new 'SystemSettings' 'SkeletalMeshLODBias' '0'
                $new = Set-IniSectionValue $new 'SystemSettings' 'StaticMeshLODBias' '0'
                $new = Set-IniSectionValue $new 'SystemSettings' 'bUseMaxQualityMode' 'False'
            }
        }

        # Shadow Quality
        switch ($g.ShadowQuality) {
            'low' {
                $new = Set-IniSectionValue $new 'SystemSettings' 'DynamicShadows' 'True'
                $new = Set-IniSectionValue $new 'SystemSettings' 'LightEnvironmentShadows' 'True'
                $new = Set-IniSectionValue $new 'SystemSettings' 'MinShadowResolution' '32'
                $new = Set-IniSectionValue $new 'SystemSettings' 'MaxShadowResolution' '512'
                $new = Set-IniSectionValue $new 'SystemSettings' 'MaxWholeSceneDominantShadowResolution' '512'
                $new = Set-IniSectionValue $new 'SystemSettings' 'ShadowFilterQualityBias' '-1'
                $new = Set-IniSectionValue $new 'SystemSettings' 'bAllowWholeSceneDominantShadows' 'False'
            }
            'medium' {
                $new = Set-IniSectionValue $new 'SystemSettings' 'DynamicShadows' 'True'
                $new = Set-IniSectionValue $new 'SystemSettings' 'LightEnvironmentShadows' 'True'
                $new = Set-IniSectionValue $new 'SystemSettings' 'MinShadowResolution' '64'
                $new = Set-IniSectionValue $new 'SystemSettings' 'MaxShadowResolution' '1024'
                $new = Set-IniSectionValue $new 'SystemSettings' 'MaxWholeSceneDominantShadowResolution' '1024'
                $new = Set-IniSectionValue $new 'SystemSettings' 'ShadowFilterQualityBias' '0'
                $new = Set-IniSectionValue $new 'SystemSettings' 'bAllowWholeSceneDominantShadows' 'True'
            }
            'ultra' {
                $new = Set-IniSectionValue $new 'SystemSettings' 'DynamicShadows' 'True'
                $new = Set-IniSectionValue $new 'SystemSettings' 'LightEnvironmentShadows' 'True'
                $new = Set-IniSectionValue $new 'SystemSettings' 'MinShadowResolution' '128'
                $new = Set-IniSectionValue $new 'SystemSettings' 'MaxShadowResolution' '2048'
                $new = Set-IniSectionValue $new 'SystemSettings' 'MaxWholeSceneDominantShadowResolution' '2048'
                $new = Set-IniSectionValue $new 'SystemSettings' 'ShadowFilterQualityBias' '1'
                $new = Set-IniSectionValue $new 'SystemSettings' 'bAllowWholeSceneDominantShadows' 'True'
            }
            default { # high
                $new = Set-IniSectionValue $new 'SystemSettings' 'DynamicShadows' 'True'
                $new = Set-IniSectionValue $new 'SystemSettings' 'LightEnvironmentShadows' 'True'
                $new = Set-IniSectionValue $new 'SystemSettings' 'MinShadowResolution' '64'
                $new = Set-IniSectionValue $new 'SystemSettings' 'MaxShadowResolution' '1120'
                $new = Set-IniSectionValue $new 'SystemSettings' 'MaxWholeSceneDominantShadowResolution' '1344'
                $new = Set-IniSectionValue $new 'SystemSettings' 'ShadowFilterQualityBias' '0'
                $new = Set-IniSectionValue $new 'SystemSettings' 'bAllowWholeSceneDominantShadows' 'True'
            }
        }

        # Texture Quality (LODBias on world/character/weapon texture groups)
        $lodBias = switch ($g.TextureQuality) { 'low' { '2' } 'medium' { '1' } default { '0' } }
        $texGroups = @('TEXTUREGROUP_World', 'TEXTUREGROUP_WorldNormalMap', 'TEXTUREGROUP_Character', 'TEXTUREGROUP_CharacterNormalMap', 'TEXTUREGROUP_Weapon', 'TEXTUREGROUP_WeaponNormalMap', 'TEXTUREGROUP_Vehicle', 'TEXTUREGROUP_VehicleNormalMap')
        foreach ($tg in $texGroups) {
            $new = [regex]::Replace($new, "(?m)^(" + [regex]::Escape($tg) + '=\([^\r\n]*LODBias=)-?\d+([^\r\n]*\))', [System.Text.RegularExpressions.MatchEvaluator] { param($m) $m.Groups[1].Value + $lodBias + $m.Groups[2].Value })
        }

        # Foliage & Grass Quality (SpeedTree)
        switch ($g.FoliageQuality) {
            'low' {
                $new = Set-IniSectionValue $new 'SystemSettings' 'SpeedTreeLeaves' 'False'
                $new = Set-IniSectionValue $new 'SystemSettings' 'SpeedTreeFronds' 'False'
            }
            'medium' {
                $new = Set-IniSectionValue $new 'SystemSettings' 'SpeedTreeLeaves' 'True'
                $new = Set-IniSectionValue $new 'SystemSettings' 'SpeedTreeFronds' 'False'
            }
            default { # high / ultra
                $new = Set-IniSectionValue $new 'SystemSettings' 'SpeedTreeLeaves' 'True'
                $new = Set-IniSectionValue $new 'SystemSettings' 'SpeedTreeFronds' 'True'
            }
        }

        # Anisotropic Filtering
        $anisoVal = if ($g.Anisotropy -in @(1, 2, 4, 8, 16)) { $g.Anisotropy } else { 16 }
        $new = Set-IniSectionValue $new 'SystemSettings' 'MaxAnisotropy' "$anisoVal"

        # Anti-Aliasing
        switch ($g.AntiAliasing) {
            'fxaa' {
                $new = Set-IniSectionValue $new 'SystemSettings' 'bAllowTemporalAA' 'True'
                $new = Set-IniSectionValue $new 'SystemSettings' 'bAllowD3D9MSAA' 'False'
                $new = Set-IniSectionValue $new 'SystemSettings' 'MaxMultiSamples' '1'
            }
            'msaa2x' {
                $new = Set-IniSectionValue $new 'SystemSettings' 'bAllowTemporalAA' 'False'
                $new = Set-IniSectionValue $new 'SystemSettings' 'bAllowD3D9MSAA' 'True'
                $new = Set-IniSectionValue $new 'SystemSettings' 'MaxMultiSamples' '2'
            }
            'msaa4x' {
                $new = Set-IniSectionValue $new 'SystemSettings' 'bAllowTemporalAA' 'False'
                $new = Set-IniSectionValue $new 'SystemSettings' 'bAllowD3D9MSAA' 'True'
                $new = Set-IniSectionValue $new 'SystemSettings' 'MaxMultiSamples' '4'
            }
            default { # off
                $new = Set-IniSectionValue $new 'SystemSettings' 'bAllowTemporalAA' 'False'
                $new = Set-IniSectionValue $new 'SystemSettings' 'bAllowD3D9MSAA' 'False'
                $new = Set-IniSectionValue $new 'SystemSettings' 'MaxMultiSamples' '1'
            }
        }

        # Ambient Occlusion
        $new = Set-IniSectionValue $new 'SystemSettings' 'AmbientOcclusion' $(if ($g.AmbientOcclusion) { 'True' } else { 'False' })

        # Audio channels (simultaneous sounds; the game ships 32, so busy fights cut sounds off)
        foreach ($sec in @('XAudio2.XAudio2Device', 'ALAudio.ALAudioDevice')) { $new = Set-IniSectionValue $new $sec 'MaxChannels' "$($g.AudioChannels)" }

        # Storage Drive Optimization (SSD vs Mechanical HDD)
        $isHdd = ($g.DriveType -eq 'hdd')
        $new = Set-IniSectionValue $new 'SystemSettings' 'OnlyStreamInTextures' $(if ($isHdd) { 'True' } else { 'False' })
        $new = Set-IniSectionValue $new 'Engine.Engine' 'bEnableMipLevelFading' $(if ($isHdd) { 'True' } else { 'False' })
        $new = Set-IniSectionValue $new 'TextureStreaming' 'MinRequestedMipsToConsider' $(if ($isHdd) { '1' } else { '11' })
        $new = Set-IniSectionValue $new 'TextureStreaming' 'MinTimeToGuaranteeMinMipCount' $(if ($isHdd) { '12' } else { '2' })
        $new = Set-IniSectionValue $new 'TextureStreaming' 'MaxTimeToGuaranteeMinMipCount' $(if ($isHdd) { '30' } else { '12' })
        $new = Set-IniSectionValue $new 'TextureStreaming' 'LoadMapTimeLimit' $(if ($isHdd) { '60.0' } else { '20.0' })
        $new = Set-IniSectionValue $new 'TextureStreaming' 'DropMipLevelsLimit' $(if ($isHdd) { '24' } else { '16' })
        $new = Set-IniSectionValue $new 'TextureStreaming' 'StopIncreasingLimit' $(if ($isHdd) { '16' } else { '12' })
        $new = Set-IniSectionValue $new 'TextureStreaming' 'StopStreamingLimit' $(if ($isHdd) { '12' } else { '8' })

        if ((Split-Path -Leaf $ini) -ne 'BaseEngine.ini' -and -not $skipFont) {
            $fontLine = $null
            if ($fontName) { $fontLine = "SubtitleFontName=$fontName.Fonts.WarfareFonts_Euro20" }
            else {
                $bak = Get-LongPath (Get-BackupPath $ini)
                if (Test-Path -LiteralPath $bak) { $m = [regex]::Match([System.IO.File]::ReadAllText($bak), '(?m)^SubtitleFontName=[^\r\n]*'); if ($m.Success) { $fontLine = $m.Value } }
            }
            if ($fontLine) {
                if ($new -match '(?m)^SubtitleFontName=') { $new = [regex]::Replace($new, '(?m)^SubtitleFontName=[^\r\n]*', $fontLine) }
                else { $new = [regex]::Replace($new, '(?m)^\[Engine\.Engine\]\r?$', "[Engine.Engine]`r`n$fontLine") }
            }
        }
        Write-IniIfChanged $ini $txt $new
        $n++
    }

    # Camera: FOV (default gameplay camera, or every camera mode) and the weapon icon scale for wide screens
    $iconScale = if ($resX / $resY -gt 2.0) { "0.68f" } elseif ($resX / $resY -gt 1.8) { "0.85f" } else { "1.00f" }
    foreach ($ini in @((Join-Path $ConfigDir "GearCamera.ini"), (Join-Path $ConfigDir "DefaultCamera.ini"), (Join-Path $docDir "GearCamera.ini"))) {
        if (-not (Test-Path -LiteralPath $ini)) { continue }
        Backup $ini
        $txt = [System.IO.File]::ReadAllText($ini)
        $new = [regex]::Replace($txt, '(?m)^BaseFOV=[^\r\n]*', "BaseFOV=$fov")
        if ($g.FovAllModes) { $new = [regex]::Replace($new, '(?m)^FOVAngle=[^\r\n]*', "FOVAngle=$fov") }
        else {
            # The other camera modes keep (or get back) their original FOV (aiming 50, cover 95, roadie run 105 ...)
            $bak = Get-LongPath (Get-BackupPath $ini)
            if (Test-Path -LiteralPath $bak) {
                $orig = [regex]::Matches([System.IO.File]::ReadAllText($bak), '(?m)^FOVAngle=[^\r\n]*')
                $cur = [regex]::Matches($new, '(?m)^FOVAngle=[^\r\n]*')
                if ($orig.Count -eq $cur.Count) {
                    $i = [ref]0
                    $new = [regex]::Replace($new, '(?m)^FOVAngle=[^\r\n]*', [System.Text.RegularExpressions.MatchEvaluator] { param($m) $v = $orig[$i.Value].Value; $i.Value++; $v })
                }
            }
            $new = Set-IniSectionValue $new 'GearGame.GameplayCam_Default' 'FOVAngle' $fov
        }
        $new = [regex]::Replace($new, '(?m)^WeaponIconScale=[^\r\n]*', "WeaponIconScale=$iconScale")
        Write-IniIfChanged $ini $txt $new
        $n++
    }

    # Mouse sensitivity lives in [Engine.PlayerInput] MouseSensitivity (game default 60); empty = leave the files alone
    if ($g.MouseSens -ne '') {
        foreach ($ini in @((Join-Path $ConfigDir "DefaultInput.ini"), (Join-Path $ConfigDir "GearInput.ini"), (Join-Path $docDir "GearInput.ini"), (Join-Path $GameDir "Engine\Config\BaseInput.ini"))) {
            if (-not (Test-Path -LiteralPath $ini)) { continue }
            Backup $ini
            $txt = [System.IO.File]::ReadAllText($ini)
            Write-IniIfChanged $ini $txt (Set-IniSectionValue $txt 'Engine.PlayerInput' 'MouseSensitivity' $g.MouseSens)
            $n++
        }
    }

    # Player name (sera.ini of the build's loader); empty = leave it
    $seraIni = Join-Path $BinariesDir "sera.ini"
    if ($g.PlayerName -ne '') {
        if (Test-Path -LiteralPath $seraIni) {
            Backup $seraIni
            $txt = [System.IO.File]::ReadAllText($seraIni)
            Write-IniIfChanged $seraIni $txt ([regex]::Replace($txt, '(?m)^player_name=[^\r\n]*', "player_name=$($g.PlayerName)"))
            $n++
        }
        else { Say "  sera.ini not found: player name not set." 'Yellow' }
        # Multiplayer / Versus / co-op use the URL name (?Name=) from [DefaultPlayer] Name (default 'Helios'), the same value the
        # SetName console command saves. URL option separators are dropped and the game keeps 20 characters.
        $urlName = ($g.PlayerName -replace '[?=#"]', '')
        if ($urlName.Length -gt 20) { $urlName = $urlName.Substring(0, 20) }
        foreach ($ini in @((Join-Path $ConfigDir 'DefaultGame.ini'), (Join-Path $ConfigDir 'GearGame.ini'), (Join-Path $docDir 'GearGame.ini'))) {
            if (-not (Test-Path -LiteralPath $ini)) { continue }
            Backup $ini
            $txt = [System.IO.File]::ReadAllText($ini)
            Write-IniIfChanged $ini $txt (Set-IniSectionValue $txt 'DefaultPlayer' 'Name' $urlName)
        }
    }

    # Run camera shake: 0 removes it, the game values are 0.6 (everyone) and 0.45 (armored Kantus)
    $stock = @{ 'GearGame.GearPawn' = '0.6f'; 'GearGameContent.GearPawn_LocustKantusArmor' = '0.45f' }
    foreach ($dir in @($ConfigDir, $docDir)) {
        foreach ($name in @('GearPawn.ini', 'DefaultPawn.ini', 'GearPawnMP.ini', 'DefaultPawnMP.ini')) {
            $ini = Join-Path $dir $name
            if (-not (Test-Path -LiteralPath $ini)) { continue }
            Backup $ini
            $txt = [System.IO.File]::ReadAllText($ini)
            $new = $txt
            foreach ($sec in $stock.Keys) {
                if ($new -match ('(?m)^\[' + [regex]::Escape($sec) + '\]\r?$')) {
                    $new = Set-IniSectionValue $new $sec 'RoadieRunShakyCamDampeningFactor' $(if ($g.DisableRunShake) { '0.0f' } else { $stock[$sec] })
                }
            }
            Write-IniIfChanged $ini $txt $new
            $n++
        }
    }

    # Horde / Beast "Standard" asks to join the Jacinto build's dedicated server (UIScene_FrontEnd.HandleDediSelect -> open 86.8.211.63),
    # but its message box strings are missing in every language, so the box showed ?INT?GearGameUI.MessageBoxStrings.ConnectToDedi_...?.
    # Add them to every installed language that lacks them (UTF-16 files stay UTF-16).
    $dediText = @{
        'INT' = @('Join Dedicated Server', 'Connect to the Jacinto build''s dedicated server (86.8.211.63)? It may be offline. For a local match, choose Private instead.')
        'ITA' = @('Server dedicato', 'Connettersi al server dedicato della build Jacinto (86.8.211.63)? Potrebbe essere offline. Per una partita locale scegli Privata.')
        'ESM' = @('Servidor dedicado', '¿Conectarse al servidor dedicado de la build Jacinto (86.8.211.63)? Puede estar desconectado. Para una partida local elige Privada.')
    }
    foreach ($locDir in @(Get-ChildItem -LiteralPath (Join-Path $GameDir 'GearGame\Localization') -Directory -ErrorAction SilentlyContinue)) {
        $locFile = Get-ChildItem -LiteralPath $locDir.FullName -File -ErrorAction SilentlyContinue | Where-Object { $_.BaseName -ieq 'GearGameUI' } | Select-Object -First 1
        if (-not $locFile) { continue }
        $raw = [System.IO.File]::ReadAllBytes($locFile.FullName)
        $enc = if ($raw.Length -ge 2 -and $raw[0] -eq 0xFF -and $raw[1] -eq 0xFE) { [System.Text.Encoding]::Unicode } else { New-Object System.Text.UTF8Encoding($false) }
        $txt = $enc.GetString($raw).TrimStart([char]0xFEFF)
        if ($txt -match '(?m)^ConnectToDedi_Title=' -or $txt -notmatch '(?m)^\[MessageBoxStrings\]\r?$') { continue }
        $s = if ($dediText.ContainsKey($locDir.Name.ToUpper())) { $dediText[$locDir.Name.ToUpper()] } else { $dediText['INT'] }
        $new = [regex]::Replace($txt, '(?m)^\[MessageBoxStrings\]\r?\n', { param($m) $m.Value + "ConnectToDedi_Title=$($s[0])`r`nConnectToDedi_Message=$($s[1])`r`n" }, 1)
        Backup $locFile.FullName
        Set-ItemProperty -LiteralPath $locFile.FullName -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
        [System.IO.File]::WriteAllBytes($locFile.FullName, $(if ($enc -is [System.Text.UnicodeEncoding]) { [byte[]](0xFF, 0xFE) + $enc.GetBytes($new) } else { $enc.GetBytes($new) }))
    }

    # Horde & Beast (Siege) main menu entries: the Jacinto build ships them with bIsDisabled=true (MAINMENUOP_MainMenu list in DefaultUI / GearUI.ini).
    # Experimental opt-in; Off writes the original value back.
    $hbVal = if ($g.HordeBeast) { 'false' } else { 'true' }
    foreach ($ini in @((Join-Path $ConfigDir 'DefaultUI.ini'), (Join-Path $ConfigDir 'GearUI.ini'), (Join-Path $docDir 'GearUI.ini'))) {
        if (-not (Test-Path -LiteralPath $ini)) { continue }
        Backup $ini
        $txt = [System.IO.File]::ReadAllText($ini)
        $new = [regex]::Replace($txt, '(\(Label="(?:Horde|Siege)",ImagePath="[^"]*",Next=MAINMENUOP_(?:Horde|Siege),bIsDisabled=)(?:true|false)\)', "`${1}$hbVal)")
        Write-IniIfChanged $ini $txt $new
    }

    # Audio filter off: console commands run once at startup through -exec= (see Get-GsStartArgs). DisableLPF is the engine's
    # "LPF disabled for all sources" command, so no sound gets the muffled / underwater low-pass filter.
    $audioExecFile = Join-Path $BinariesDir 'Gears3_Audio.txt'
    if (-not $g.AudioFilter) { [System.IO.File]::WriteAllText($audioExecFile, "DisableLPF`r`n") }
    elseif (Test-Path -LiteralPath $audioExecFile) { Remove-Item -LiteralPath $audioExecFile -Force -ErrorAction SilentlyContinue }

    # Launchers. The exe switches -FULLSCREEN / -WINDOWED, -VSYNC / -NOVSYNC, -ResX / -ResY and -language override the ini.
    $fsFlag = $(if ($g.Fullscreen) { "-FULLSCREEN" } else { "-WINDOWED" }) + $(if ($g.VSync) { " -VSYNC" } else { " -NOVSYNC" })
    $startArgs = Get-GsStartArgs $g
    if ($IsWin) {
        $startBat1 = Join-Path $GameDir "Gears3_Start_Game.bat"
        $startBat2 = Join-Path $BinariesDir "StartCampaign.bat"
        $batContent1 = "@echo off`r`ntitle Gears of War 3 Jacinto`r`necho Starting Gears of War 3...`r`necho Please wait until the game launches.`r`ncd /d `"%~dp0Binaries\Win64`"`r`nstart `"`" GearGame.exe $startArgs`r`n:wait`r`ntimeout /t 1 /nobreak >nul`r`ntasklist /FI `"IMAGENAME eq GearGame.exe`" | find /I `"GearGame.exe`" >nul`r`nif errorlevel 1 goto wait`r`n"
        if (Test-Path -LiteralPath $startBat1) { Set-ItemProperty -LiteralPath $startBat1 -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue }
        [System.IO.File]::WriteAllText($startBat1, $batContent1)
        if (Test-Path -LiteralPath (Split-Path -Parent $startBat2)) {
            if (Test-Path -LiteralPath $startBat2) { Set-ItemProperty -LiteralPath $startBat2 -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue }
            [System.IO.File]::WriteAllText($startBat2, "GearGame.exe $startArgs`r`n")
        }
        Say "  ok  Launch batch files updated ($startArgs)" 'Green'
    }
    else {
        $startSh = Join-Path $GameDir "Gears3_Start_Game.sh"
        $shText = @"
#!/bin/sh
# Gears of War 3 Jacinto launcher (Linux / SteamOS). ARGS is reused by Gears3_Start_Game_Debug.sh.
ARGS="$startArgs"
cd "`$(dirname "`$0")/Binaries/Win64" || exit 1
if command -v wine >/dev/null 2>&1; then
    wine GearGame.exe `$ARGS "`$@"
    rc=`$?
    if [ `$rc -ne 0 ]; then
        echo "If the game stopped with 'DwTriovizCreatePixelShader' (Fatal error), the prefix is missing the DirectX DLLs the game needs."
        echo "Install them:  protontricks <AppID> d3dcompiler_43 d3dx9_43   (or: WINEPREFIX=<prefix> winetricks d3dcompiler_43 d3dx9_43)"
    fi
    exit `$rc
fi
echo "wine not found. On Steam Deck / Steam: add Binaries/Win64/GearGame.exe as a non-Steam game, force a Proton version"
echo "and set the launch options to:  `$ARGS"
echo "Game stops with 'DwTriovizCreatePixelShader'? Install the DirectX DLLs into that prefix: protontricks <AppID> d3dcompiler_43 d3dx9_43"
exit 1
"@
        [System.IO.File]::WriteAllText($startSh, (($shText -replace "`r`n", "`n").TrimEnd() + "`n"), (New-Object System.Text.UTF8Encoding($false)))
        & chmod +x $startSh
        Say "  ok  Launch script updated: Gears3_Start_Game.sh ($startArgs)" 'Green'
    }
    $n += Update-LauncherFiles $g
    return $n
}

function Get-GsLanguageNote($code) {
    $l = $GsLanguages | Where-Object { $_.Code -eq $code -and $_.Code -ne 'INT' -and $_.Url } | Select-Object -First 1   # PT-BR overrides INT: noted only when picked
    $notes = @()
    if ($l) { $notes += "Community translation: install the language pack from NexusMods first: $($l.Url)" }
    if (Test-GsSpecialGlyphLanguage $code) {
        $notes += "WarfareFonts is skipped for $code to preserve the special character glyphs of this language. (Can be forced in Game Settings if desired)."
    }
    if ($code -ne 'INT' -and -not (Test-Path -LiteralPath (Join-Path $GameDir "GearGame\Localization\$code"))) { $notes += "GearGame\Localization\$code not found: install the language files there." }
    return $notes
}

# One line per setting, for the menu and the installer summary
function Get-GameSettingsLines($g) {
    $res = if ($g.Resolution -eq 'native') { $nr = Get-NativeResolution; "Native ($($nr[0])x$($nr[1]))" } elseif ($g.Resolution -eq 'span') { $nr = Get-SpanResolution; "All displays ($($nr[0])x$($nr[1]))" } else { $g.Resolution }
    $r = Get-GsResolution $g
    $fov = if ($g.Fov -eq 0) { "Automatic ($(Get-GsFov $g $r[0] $r[1]))" } else { "$($g.Fov)" }
    $subSize = if (Test-GsSpecialGlyphLanguage $g.Language) {
        if ($g.ForceWarfareFonts -and $g.SubtitleSize -ne 'default') {
            "$($GsSubtitleSizes[$g.SubtitleSize]) (forced for $($g.Language))"
        } else {
            "Original font (WarfareFonts skipped for $($g.Language))"
        }
    } else {
        $GsSubtitleSizes[$g.SubtitleSize]
    }
    $fpsText = if ($g.FpsLimit -gt 0) { "$($g.FpsLimit) FPS" } else { 'Uncapped (300 FPS)' }
    $aaText = switch ($g.AntiAliasing) { 'fxaa' { 'FXAA / Temporal AA' } 'msaa2x' { 'MSAA 2x' } 'msaa4x' { 'MSAA 4x' } default { 'Off' } }
    $anisoText = if ($g.Anisotropy -gt 1) { "$($g.Anisotropy)x" } else { 'Off (1x)' }
    $cap = { param($s) if ($s) { ([cultureinfo]::InvariantCulture.TextInfo.ToTitleCase([string]$s)) } else { '' } }

    $driveText = if ($g.DriveType -eq 'hdd') { 'HDD (Mechanical - Anti-popin pre-cache)' } else { 'SSD / NVMe (High-speed streaming)' }

    # name, value, key (the menu numbers follow this order; RunShake is listed with the post-process effects)
    return @(
        @('Player name', $(if ($g.PlayerName) { $g.PlayerName } else { '(unchanged)' }), 'PlayerName'),
        @('Language', $g.Language, 'Language'),
        @('Resolution', $res, 'Resolution'),
        @('VSync', $(if ($g.VSync) { 'On' } else { 'Off' }), 'VSync'),
        @('FPS limit', $fpsText, 'FpsLimit'),
        @('Fullscreen refresh rate', $(if ($g.RefreshRate -gt 0) { "$($g.RefreshRate) Hz" } else { 'Game default' }), 'RefreshRate'),
        @('Display mode', $(if ($g.Fullscreen) { 'Fullscreen' } else { 'Windowed' }), 'Fullscreen'),
        @('Field of view (FOV)', $fov, 'Fov'),
        @('FOV applies to', $(if ($g.FovAllModes) { 'Every camera mode' } else { 'Default gameplay camera only' }), 'FovAllModes'),
        @('Mouse sensitivity', $(if ($g.MouseSens) { $g.MouseSens } else { '(unchanged)' }), 'MouseSens'),
        @('Subtitles', $(if ($g.Subtitles) { 'On (in-game option decides)' } else { 'Off' }), 'Subtitles'),
        @('Subtitle size', $subSize, 'SubtitleSize'),
        @('Horde & Beast in the main menu (experimental)', $(if ($g.HordeBeast) { 'Unlocked' } else { 'Locked (original)' }), 'HordeBeast'),
        @('Audio filter (muffled / underwater sound)', $(if ($g.AudioFilter) { 'On (original)' } else { 'Off' }), 'AudioFilter'),
        @('Audio channels', "$($g.AudioChannels)$(if ($g.AudioChannels -eq 32) { ' (original)' })", 'AudioChannels'),
        @('Graphics detail mode', (& $cap $g.GraphicsQuality), 'GraphicsQuality'),
        @('Shadow quality', (& $cap $g.ShadowQuality), 'ShadowQuality'),
        @('Texture quality', (& $cap $g.TextureQuality), 'TextureQuality'),
        @('Storage drive optimization', $driveText, 'DriveType'),
        @('Foliage & grass quality', (& $cap $g.FoliageQuality), 'FoliageQuality'),
        @('Anisotropic filtering', $anisoText, 'Anisotropy'),
        @('Anti-aliasing (AA)', $aaText, 'AntiAliasing'),
        @('Ambient occlusion (AO)', $(if ($g.AmbientOcclusion) { 'On' } else { 'Off' }), 'AmbientOcclusion'),
        @('Camera shake while running (roadie run)', $(if ($g.DisableRunShake) { 'Off' } else { 'On' }), 'RunShake')
    )
}

function Show-GameSettings {
    if (-not (Test-Path -LiteralPath (Join-Path $ConfigDir "GearEngine.ini"))) { Say "  No GearEngine.ini found in the game folder." 'Red'; Read-Host "Press ENTER to return" | Out-Null; return }
    $g = Get-GameSettings
    while ($true) {
        Clear-Host
        Write-Host "====================================================================" -ForegroundColor Cyan
        Write-Host " GAME SETTINGS" -ForegroundColor Cyan
        Write-Host "====================================================================" -ForegroundColor Cyan
        $lines = Get-GameSettingsLines $g
        Write-Host "`n  Game:" -ForegroundColor White
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i][2] -eq 'GraphicsQuality') {
                Write-Host "`n  Graphics Quality:" -ForegroundColor White
            }
            elseif ($lines[$i][2] -eq 'AudioFilter') {
                Write-Host "`n  Audio:" -ForegroundColor White
            }
            elseif ($lines[$i][2] -eq 'RunShake') {
                Write-Host "`n  Post-process effects (On = as in the original game):" -ForegroundColor White
            }

            if ($lines[$i][2] -eq 'RunShake') {
                Write-Host ("  {0,2}. {1,-46} {2}" -f ($i + 1), $lines[$i][0], $lines[$i][1]) -ForegroundColor $(if ($g.DisableRunShake) { 'Yellow' } else { 'Gray' })
            }
            else { Write-Host ("  {0,2}. {1,-46} {2}" -f ($i + 1), $lines[$i][0], $lines[$i][1]) }
        }
        for ($i = 0; $i -lt $GfxEffects.Count; $i++) {
            $isOff = $g.Disabled -contains $GfxEffects[$i].Id
            Write-Host ("  {0,2}. {1,-46} {2}" -f ($i + $lines.Count + 1), $GfxEffects[$i].Name, $(if ($isOff) { 'Off' } else { 'On' })) -ForegroundColor $(if ($isOff) { 'Yellow' } else { 'Gray' })
        }
        Write-Host ""
        Write-Host "  Number = change that setting   A = disable all effects   E = enable all effects   R = reset all to defaults`n  S = save and apply   Q = return without saving" -ForegroundColor DarkGray
        Write-Host ""
        $choice = (Read-Host "Choose").Trim()
        if ($choice -ieq 'q') { return }
        if ($choice -ieq 'a') { $g.Disabled = @($GfxEffects | ForEach-Object { $_.Id }); continue }
        if ($choice -ieq 'e') { $g.Disabled = @(); continue }
        if ($choice -ieq 'r') { $g = Get-GameSettings -Defaults; continue }   # written with S, like every other change
        if ($choice -ieq 's') {
            $n = Apply-GameSettings $g
            Save-GameSettings $g
            foreach ($note in (Get-GsLanguageNote $g.Language)) { Say "  [i] $note" 'Cyan' }
            Say "  ok  Game settings written ($n file(s) checked). They are re-applied automatically when you run the installer again." 'Green'
            Say "      Saved in: $GfxFile" 'DarkGray'
            Read-Host "Press ENTER to return" | Out-Null
            return
        }
        $num = 0
        if (-not ([int]::TryParse($choice, [ref]$num) -and $num -ge 1 -and $num -le ($GfxEffects.Count + $lines.Count))) { continue }
        $key = if ($num -le $lines.Count) { $lines[$num - 1][2] } else { 'Effect' }
        switch ($key) {
            'PlayerName' { $g.PlayerName = (Read-Host "Player name (empty = leave the current name)").Trim() }
            'Language' {
                for ($i = 0; $i -lt $GsLanguages.Count; $i++) { Write-Host ("   [{0}] {1}" -f ($i + 1), $GsLanguages[$i].Name) }
                Write-Host ("   [{0}] Custom language code (another pack, e.g. FRA, DEU, KOR, ARA)" -f ($GsLanguages.Count + 1))
                $c = (Read-Host "Your choice (ENTER = keep $($g.Language))").Trim(); $k = 0
                $prevLang = $g.Language
                if ([int]::TryParse($c, [ref]$k) -and $k -ge 1 -and $k -le $GsLanguages.Count) {
                    $g.Language = $GsLanguages[$k - 1].Code
                    if ($GsLanguages[$k - 1].Url) { Say "  Install the translation from NexusMods first: $($GsLanguages[$k - 1].Url)" 'Cyan'; Start-Sleep -Seconds 2 }
                }
                elseif ($k -eq $GsLanguages.Count + 1) { $c = (Read-Host "3-letter language code").Trim(); if ($c -match '^[A-Za-z]{3}$') { $g.Language = $c.ToUpper() } }
                elseif ($c -match '^[A-Za-z]{3}$') { $g.Language = $c.ToUpper() }

                if ($g.Language -ne $prevLang) {
                    if (Test-GsSpecialGlyphLanguage $g.Language) {
                        if (-not $g.ForceWarfareFonts) {
                            $g.SubtitleSize = 'default'
                            Say "  Note: $($g.Language) uses special characters. WarfareFonts is skipped by default to preserve fonts." 'Cyan'
                            Start-Sleep -Seconds 2
                        }
                    } else {
                        if ($g.SubtitleSize -eq 'default') {
                            $g.SubtitleSize = 'big'
                        }
                        $g.ForceWarfareFonts = $false
                    }
                }
            }
            'Resolution' {
                for ($i = 0; $i -lt $GsResolutions.Count; $i++) { Write-Host ("   [{0}] {1}" -f ($i + 1), $GsResolutions[$i].Name) }
                $c = (Read-Host "Your choice (ENTER = keep, or type WIDTHxHEIGHT)").Trim(); $k = 0
                if ([int]::TryParse($c, [ref]$k) -and $k -ge 1 -and $k -le $GsResolutions.Count) { $g.Resolution = $GsResolutions[$k - 1].Value }
                elseif ($c -match '^(\d{3,5})\s*x\s*(\d{3,5})$') { $g.Resolution = "$($matches[1])x$($matches[2])" }
            }
            'VSync' { $g.VSync = -not $g.VSync }
            'FpsLimit' {
                Write-Host "`n  FPS Limit (Frame rate limiter):" -ForegroundColor DarkGray
                Write-Host "   [0] Uncapped (game default: 300 FPS)"
                Write-Host "   [1] 30 FPS"
                Write-Host "   [2] 60 FPS"
                Write-Host "   [3] 90 FPS"
                Write-Host "   [4] 120 FPS"
                Write-Host "   [5] 144 FPS"
                Write-Host "   [6] 165 FPS"
                Write-Host "   [7] 240 FPS"
                $c = (Read-Host "Your choice [0-7] or type custom FPS (ENTER = keep $(if ($g.FpsLimit -gt 0) { "$($g.FpsLimit) FPS" } else { 'Uncapped' }))").Trim()
                $k = 0
                if ($c -eq '0') { $g.FpsLimit = 0 }
                elseif ($c -eq '1') { $g.FpsLimit = 30 }
                elseif ($c -eq '2') { $g.FpsLimit = 60 }
                elseif ($c -eq '3') { $g.FpsLimit = 90 }
                elseif ($c -eq '4') { $g.FpsLimit = 120 }
                elseif ($c -eq '5') { $g.FpsLimit = 144 }
                elseif ($c -eq '6') { $g.FpsLimit = 165 }
                elseif ($c -eq '7') { $g.FpsLimit = 240 }
                elseif ([int]::TryParse($c, [ref]$k) -and $k -ge 15 -and $k -le 500) { $g.FpsLimit = $k }
            }
            'Fullscreen' { $g.Fullscreen = -not $g.Fullscreen }
            'RefreshRate' {
                $r = Get-GsResolution $g
                $rates = @(Get-DisplayRefreshRates $r[0] $r[1])
                Write-Host "`n  Fullscreen only: the Hz the game uses at $($r[0])x$($r[1]). With VSync the frame rate follows it (60 Hz = 60 fps, and 30 fps whenever the game cannot keep up with 60)." -ForegroundColor DarkGray
                Write-Host "  Windowed mode always follows the Windows desktop refresh rate (Settings > Display > Advanced display), the game cannot change it." -ForegroundColor DarkGray
                Write-Host "   [0] Game default (the game picks the mode)"
                for ($i = 0; $i -lt $rates.Count; $i++) { Write-Host ("   [{0}] {1} Hz" -f ($i + 1), $rates[$i]) }
                if ($rates.Count -eq 0) { Write-Host "   (the display modes could not be listed here: type the Hz yourself)" -ForegroundColor DarkGray }
                $c = (Read-Host "Your choice (ENTER = keep $(if ($g.RefreshRate -gt 0) { "$($g.RefreshRate) Hz" } else { 'game default' }), or type the Hz)").Trim(); $k = 0
                if ($c -eq '0') { $g.RefreshRate = 0 }
                elseif ([int]::TryParse($c, [ref]$k)) {
                    if ($k -ge 1 -and $k -le $rates.Count) { $g.RefreshRate = $rates[$k - 1] }
                    elseif ($k -ge 24 -and $k -le 1000) {
                        if ($rates.Count -gt 0 -and $rates -notcontains $k) { Say "  [!] $k Hz is not offered by your display at this resolution ($($rates -join ', ') Hz); the game would find no display mode. Not changed." 'Yellow'; Read-Host "Press ENTER" | Out-Null }
                        else { $g.RefreshRate = $k }
                    }
                }
            }
            'Fov' {
                $c = (Read-Host "FOV [game default 80, range 50 - 130] (empty = automatic: 80, ultrawide 65)").Trim(); $k = 0
                if ($c -eq '') { $g.Fov = 0 } elseif ([int]::TryParse($c, [ref]$k) -and $k -ge 50 -and $k -le 130) { $g.Fov = $k }
            }
            'FovAllModes' { $g.FovAllModes = -not $g.FovAllModes }
            'MouseSens' {
                $c = (Read-Host "Mouse sensitivity [game default 60, range 1 - 200] (empty = leave unchanged)").Trim(); $d = 0.0
                if ($c -eq '') { $g.MouseSens = '' }
                elseif ([double]::TryParse($c, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$d) -and $d -ge 1 -and $d -le 200) {
                    $g.MouseSens = $d.ToString("0.0", [System.Globalization.CultureInfo]::InvariantCulture)
                }
            }
            'Subtitles' { $g.Subtitles = -not $g.Subtitles }
            'SubtitleSize' {
                if (Test-GsSpecialGlyphLanguage $g.Language) {
                    if (-not $g.ForceWarfareFonts) {
                        Write-Host ""
                        Write-Host "  ====================================================================" -ForegroundColor Yellow
                        Write-Host "  [!] WARNING: SPECIAL CHARACTER / NON-LATIN LANGUAGE DETECTED" -ForegroundColor Yellow
                        Write-Host "  ====================================================================" -ForegroundColor Yellow
                        Write-Host "  The selected language ($($g.Language)) uses special characters/glyphs" -ForegroundColor Yellow
                        Write-Host "  (Chinese, Japanese, Korean, Russian, Arabic, etc.)." -ForegroundColor Yellow
                        Write-Host ""
                        Write-Host "  WarfareFonts only contains Latin and Turkish character sets." -ForegroundColor Yellow
                        Write-Host "  Installing WarfareFonts on this language may cause missing letters," -ForegroundColor Yellow
                        Write-Host "  broken glyphs, or empty square boxes (tofu) in subtitles!" -ForegroundColor Yellow
                        Write-Host "  The translation pack normally provides its own custom font." -ForegroundColor Yellow
                        Write-Host ""
                        $ans = (Read-Host "  Do you still want to install WarfareFonts anyway? (y/N)").Trim()
                        if ($ans -match '^(y|yes|e|evet)$') {
                            $g.ForceWarfareFonts = $true
                            Write-Host ""
                            Write-Host "   [1] Big (WarfareFonts2x, recommended for PC)"
                            Write-Host "   [2] Normal (WarfareFonts, handhelds)"
                            Write-Host "   [3] Default (the game's original font)"
                            $sc = (Read-Host "Choose subtitle size [1-3] (ENTER = 1)").Trim()
                            if ($sc -eq '2') { $g.SubtitleSize = 'normal' }
                            elseif ($sc -eq '3') { $g.SubtitleSize = 'default'; $g.ForceWarfareFonts = $false }
                            else { $g.SubtitleSize = 'big' }
                        }
                        else {
                            $g.ForceWarfareFonts = $false
                            $g.SubtitleSize = 'default'
                            Say "  WarfareFonts will not be installed for $($g.Language). Original font preserved." 'Cyan'
                            Start-Sleep -Seconds 2
                        }
                    }
                    else {
                        Write-Host ""
                        Write-Host "  WarfareFonts is currently force-enabled for $($g.Language)." -ForegroundColor Yellow
                        Write-Host "   [1] Big (WarfareFonts2x)"
                        Write-Host "   [2] Normal (WarfareFonts)"
                        Write-Host "   [3] Revert to Default original font (Recommended for $($g.Language))"
                        $sc = (Read-Host "Choose subtitle size [1-3]").Trim()
                        if ($sc -eq '1') { $g.SubtitleSize = 'big' }
                        elseif ($sc -eq '2') { $g.SubtitleSize = 'normal' }
                        elseif ($sc -eq '3') { $g.SubtitleSize = 'default'; $g.ForceWarfareFonts = $false }
                    }
                }
                else {
                    $keys = @($GsSubtitleSizes.Keys)
                    $g.SubtitleSize = $keys[([array]::IndexOf($keys, $g.SubtitleSize) + 1) % $keys.Count]
                    $g.ForceWarfareFonts = $false
                }
            }
            'GraphicsQuality' {
                $levels = @('low', 'medium', 'high', 'ultra')
                Write-Host "`n  Graphics Detail Mode (Draw distance, mesh LOD):" -ForegroundColor DarkGray
                Write-Host "   [1] Low     (DetailMode 0, reduced draw distance and LOD)"
                Write-Host "   [2] Medium  (DetailMode 1, balanced performance)"
                Write-Host "   [3] High    (DetailMode 2, original game default)"
                Write-Host "   [4] Ultra   (DetailMode 2, extended draw distance, mesh LOD -1)"
                $c = (Read-Host "Your choice [1-4] (ENTER = cycle)").Trim()
                if ($c -eq '1') { $g.GraphicsQuality = 'low' }
                elseif ($c -eq '2') { $g.GraphicsQuality = 'medium' }
                elseif ($c -eq '3') { $g.GraphicsQuality = 'high' }
                elseif ($c -eq '4') { $g.GraphicsQuality = 'ultra' }
                else {
                    $idx = [array]::IndexOf($levels, $g.GraphicsQuality)
                    $g.GraphicsQuality = $levels[($idx + 1) % $levels.Count]
                }
            }
            'ShadowQuality' {
                $levels = @('low', 'medium', 'high', 'ultra')
                Write-Host "`n  Shadow Quality:" -ForegroundColor DarkGray
                Write-Host "   [1] Low     (512px resolution, dominant shadows off)"
                Write-Host "   [2] Medium  (1024px resolution)"
                Write-Host "   [3] High    (1120/1344px, game default)"
                Write-Host "   [4] Ultra   (2048px high-resolution shadows)"
                $c = (Read-Host "Your choice [1-4] (ENTER = cycle)").Trim()
                if ($c -eq '1') { $g.ShadowQuality = 'low' }
                elseif ($c -eq '2') { $g.ShadowQuality = 'medium' }
                elseif ($c -eq '3') { $g.ShadowQuality = 'high' }
                elseif ($c -eq '4') { $g.ShadowQuality = 'ultra' }
                else {
                    $idx = [array]::IndexOf($levels, $g.ShadowQuality)
                    $g.ShadowQuality = $levels[($idx + 1) % $levels.Count]
                }
            }
            'TextureQuality' {
                $levels = @('low', 'medium', 'high', 'ultra')
                Write-Host "`n  Texture Quality:" -ForegroundColor DarkGray
                Write-Host "   [1] Low     (LODBias 2, lower VRAM usage for weak GPUs / Deck)"
                Write-Host "   [2] Medium  (LODBias 1)"
                Write-Host "   [3] High    (LODBias 0, full original game resolution)"
                Write-Host "   [4] Ultra   (LODBias 0, maximum texture streaming pool)"
                $c = (Read-Host "Your choice [1-4] (ENTER = cycle)").Trim()
                if ($c -eq '1') { $g.TextureQuality = 'low' }
                elseif ($c -eq '2') { $g.TextureQuality = 'medium' }
                elseif ($c -eq '3') { $g.TextureQuality = 'high' }
                elseif ($c -eq '4') { $g.TextureQuality = 'ultra' }
                else {
                    $idx = [array]::IndexOf($levels, $g.TextureQuality)
                    $g.TextureQuality = $levels[($idx + 1) % $levels.Count]
                }
            }
            'DriveType' {
                Write-Host "`n  Storage Drive Optimization:" -ForegroundColor DarkGray
                Write-Host "   [1] SSD / NVMe (High-speed streaming, evict distant textures - default)"
                Write-Host "   [2] HDD (Mechanical - Anti-popin VRAM pre-caching, keep textures in memory)"
                $c = (Read-Host "Your choice [1-2] (ENTER = toggle)").Trim()
                if ($c -eq '1') { $g.DriveType = 'ssd' }
                elseif ($c -eq '2') { $g.DriveType = 'hdd' }
                else {
                    $g.DriveType = if ($g.DriveType -eq 'hdd') { 'ssd' } else { 'hdd' }
                }
            }
            'FoliageQuality' {
                $levels = @('low', 'medium', 'high', 'ultra')
                Write-Host "`n  Foliage & Grass Quality (SpeedTree vegetation):" -ForegroundColor DarkGray
                Write-Host "   [1] Low     (Leaves and fronds reduced)"
                Write-Host "   [2] Medium  (Leaves enabled, fronds reduced)"
                Write-Host "   [3] High    (Leaves and fronds fully rendered - default)"
                Write-Host "   [4] Ultra   (Maximum foliage rendering and density)"
                $c = (Read-Host "Your choice [1-4] (ENTER = cycle)").Trim()
                if ($c -eq '1') { $g.FoliageQuality = 'low' }
                elseif ($c -eq '2') { $g.FoliageQuality = 'medium' }
                elseif ($c -eq '3') { $g.FoliageQuality = 'high' }
                elseif ($c -eq '4') { $g.FoliageQuality = 'ultra' }
                else {
                    $idx = [array]::IndexOf($levels, $g.FoliageQuality)
                    $g.FoliageQuality = $levels[($idx + 1) % $levels.Count]
                }
            }
            'Anisotropy' {
                $rates = @(1, 2, 4, 8, 16)
                Write-Host "`n  Anisotropic Filtering (Texture filtering):" -ForegroundColor DarkGray
                Write-Host "   [1] Off (1x / Trilinear)"
                Write-Host "   [2] 2x"
                Write-Host "   [3] 4x"
                Write-Host "   [4] 8x"
                Write-Host "   [5] 16x (Game default)"
                $c = (Read-Host "Your choice [1-5] (ENTER = cycle)").Trim()
                if ($c -eq '1') { $g.Anisotropy = 1 }
                elseif ($c -eq '2') { $g.Anisotropy = 2 }
                elseif ($c -eq '3') { $g.Anisotropy = 4 }
                elseif ($c -eq '4') { $g.Anisotropy = 8 }
                elseif ($c -eq '5') { $g.Anisotropy = 16 }
                else {
                    $idx = [array]::IndexOf($rates, [int]$g.Anisotropy)
                    $g.Anisotropy = $rates[($idx + 1) % $rates.Count]
                }
            }
            'AntiAliasing' {
                $modes = @('off', 'fxaa', 'msaa2x', 'msaa4x')
                Write-Host "`n  Anti-Aliasing (Edge smoothing):" -ForegroundColor DarkGray
                Write-Host "   [1] Off"
                Write-Host "   [2] FXAA / Temporal AA (post-process AA, fast)"
                Write-Host "   [3] MSAA 2x (hardware multi-sampling)"
                Write-Host "   [4] MSAA 4x (high quality multi-sampling)"
                $c = (Read-Host "Your choice [1-4] (ENTER = cycle)").Trim()
                if ($c -eq '1') { $g.AntiAliasing = 'off' }
                elseif ($c -eq '2') { $g.AntiAliasing = 'fxaa' }
                elseif ($c -eq '3') { $g.AntiAliasing = 'msaa2x' }
                elseif ($c -eq '4') { $g.AntiAliasing = 'msaa4x' }
                else {
                    $idx = [array]::IndexOf($modes, $g.AntiAliasing)
                    $g.AntiAliasing = $modes[($idx + 1) % $modes.Count]
                }
            }
            'AmbientOcclusion' { $g.AmbientOcclusion = -not $g.AmbientOcclusion }
            'HordeBeast' {
                $g.HordeBeast = -not $g.HordeBeast
                if ($g.HordeBeast) {
                    Write-Host "`n  Horde & Beast unlocked in the main menu (EXPERIMENTAL). The Jacinto build ships them locked and they are not fully tested." -ForegroundColor Yellow
                    Write-Host "  Use the Private option (Standard needs Xbox LIVE matchmaking). If a match crashes, send GearGame\Logs\Launch.log." -ForegroundColor Yellow
                    Start-Sleep -Seconds 3
                }
            }
            'AudioFilter' {
                $g.AudioFilter = -not $g.AudioFilter
                if (-not $g.AudioFilter) {
                    Write-Host "`n  Audio filter off: sounds are no longer muffled (behind walls, under water, after explosions)." -ForegroundColor DarkGray
                    Write-Host "  Fixes audio that randomly sounds underwater / far away. Works when the game is started with Gears3_Start_Game." -ForegroundColor DarkGray
                    Start-Sleep -Seconds 2
                }
            }
            'AudioChannels' {
                $levels = @(32, 64, 128)
                Write-Host "`n  Audio channels (sounds that can play at the same time):" -ForegroundColor DarkGray
                Write-Host "   [1] 32   (original)"
                Write-Host "   [2] 64   (fewer sounds cut off in big fights)"
                Write-Host "   [3] 128"
                $c = (Read-Host "Your choice [1-3] (ENTER = cycle)").Trim()
                if ($c -match '^[1-3]$') { $g.AudioChannels = $levels[[int]$c - 1] }
                else { $g.AudioChannels = $levels[([array]::IndexOf($levels, [int]$g.AudioChannels) + 1) % $levels.Count] }
            }
            'RunShake' { $g.DisableRunShake = -not $g.DisableRunShake }
            default {
                $id = $GfxEffects[$num - $lines.Count - 1].Id
                if ($g.Disabled -contains $id) { $g.Disabled = @($g.Disabled | Where-Object { $_ -ne $id }) } else { $g.Disabled = @($g.Disabled) + $id }
            }
        }
    }
}

# ---- Open the NexusMods page (main menu N) or another link (main menu D: Discord) in the default browser.
function Open-NexusPage($url = 'https://www.nexusmods.com/gearsofwar3/mods/27') {
    try {
        if ($IsWin) { Start-Process $url }
        elseif ($IsMacOS) { Start-Process 'open' -ArgumentList $url }
        else { Start-Process 'xdg-open' -ArgumentList $url }
    }
    catch {
        Say "  Could not open the browser. Open this page yourself: $url" 'Yellow'
        Read-Host "Press ENTER to return" | Out-Null
    }
}

# ---- Start the Game / Start the Game (Debug) (main menu S / SD): runs the launcher the installer creates in the game folder.
# The launcher (Gears3_Start_Game.bat / .sh, Gears3_Start_Game_Debug.bat / .sh) does not exist before the first installation.
function Test-InstallDone {
    $ext = if ($IsWin) { 'bat' } else { 'sh' }
    return (Test-Path -LiteralPath (Join-Path $GameDir "Gears3_Start_Game.$ext"))
}

function Start-GameFromMenu([bool]$Debug) {
    $ext = if ($IsWin) { 'bat' } else { 'sh' }
    $name = if ($Debug) { "Gears3_Start_Game_Debug.$ext" } else { "Gears3_Start_Game.$ext" }
    $path = Join-Path $GameDir $name
    if (-not (Test-Path -LiteralPath $path)) {
        Say "  $name does not exist yet: the installer creates it. Please run '1. Install Multi-Patch' first." 'Yellow'
        Read-Host "Press ENTER to return" | Out-Null
        return
    }
    if ($IsWin) { Start-Process -FilePath $path -WorkingDirectory $GameDir } else { Start-Process -FilePath 'sh' -ArgumentList "`"$path`"" -WorkingDirectory $GameDir }
    exit 0
}

# ---- Installation Verification (main menu option 2): runs verify\Gears3_Multipatch_Verify.ps1, a read-only audit of what the patch did to the game folder
function Invoke-VerifyInstallation {
    $v = @((Join-Path $ScriptDir '_files\verify\Gears3_Multipatch_Verify.ps1'), (Join-Path $ScriptDir 'verify\Gears3_Multipatch_Verify.ps1'), (Join-Path $ScriptDir 'Gears3_Multipatch_Verify.ps1')) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $v) {
        Say "  Gears3_Multipatch_Verify.ps1 not found (expected in the '_files\verify' folder next to this script). Download the complete package from NexusMods." 'Yellow'
        Read-Host "Press ENTER to return" | Out-Null
        return
    }
    if (-not (Test-InstallDone)) {
        Say "  The installation has not been run yet. Please run '1. Install Multi-Patch' first, then verify it." 'Yellow'
        Read-Host "Press ENTER to return" | Out-Null
        return
    }
    $exe = if ($PSVersionTable.PSEdition -eq 'Core') { [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName } else { 'powershell' }
    # The audit prints its own report (and saves VerifyReport_<date>.txt next to it) and waits for ENTER at the end.
    & $exe -NoProfile -File $v -GameDir $GameDir
}

# ---- Changelog (main menu option 6): read from changelog.txt next to the script every time, shown page by page.
function Show-Changelog {
    $file = Join-Path $ScriptDir "changelog.txt"
    Clear-Host
    if (-not (Test-Path -LiteralPath $file)) {
        Say "  changelog.txt not found next to the tool ($ScriptDir)." 'Yellow'
        Say "  The full changelog is on the NexusMods page: https://www.nexusmods.com/gearsofwar3/mods/27?tab=logs" 'Cyan'
        Read-Host "Press ENTER to return" | Out-Null
        return
    }
    $width = 120; $height = 30
    try { $width = [Math]::Max(40, [Console]::WindowWidth - 1); $height = [Math]::Max(10, [Console]::WindowHeight - 3) } catch { }
    # Wrap long entries at word boundaries; continuation lines are indented under the "- " of the entry.
    $rows = New-Object System.Collections.Generic.List[object]
    foreach ($line in (Get-Content -LiteralPath $file -Encoding UTF8)) {
        $color = if ($line -match '^v\d+\.\d+') { 'Cyan' } elseif ($line -match '^=+$') { 'DarkGray' } elseif ($line -match '^\s*Known:') { 'Yellow' } else { 'Gray' }
        $indent = if ($line -match '^(\s*-\s+)') { ' ' * $matches[1].Length } else { '' }
        $rest = $line
        do {
            if ($rest.Length -le $width) { $rows.Add(@($rest, $color)); break }
            $cut = $rest.LastIndexOf(' ', $width)
            if ($cut -le $indent.Length) { $cut = $width }
            $rows.Add(@($rest.Substring(0, $cut), $color))
            $rest = $indent + $rest.Substring($cut).TrimStart()
        } while ($true)
    }
    $i = 0
    while ($i -lt $rows.Count) {
        $end = [Math]::Min($i + $height, $rows.Count)
        for (; $i -lt $end; $i++) { Write-Host $rows[$i][0] -ForegroundColor $rows[$i][1] }
        if ($i -ge $rows.Count) { break }
        $k = Read-Host ("-- {0}% -- ENTER = more, Q = back to the menu" -f [int](100 * $i / $rows.Count))
        if ($k -ieq 'q') { return }
    }
    Read-Host "`n-- End of changelog -- Press ENTER to return" | Out-Null
}

# ---- Cheats (main menu option 5): the developer hotkeys of GearInput.ini, console commands, level "ce" events (checkpoint jumps) ----
function Show-CheatTable($Title, $Note, $Rows, $KeyWidth) {
    Clear-Host
    Write-Host '====================================================================' -ForegroundColor Cyan
    Write-Host " CHEATS - $Title" -ForegroundColor Cyan
    Write-Host '====================================================================' -ForegroundColor Cyan
    foreach ($n in $Note) { Write-Host " $n" -ForegroundColor DarkGray }
    if ($Note) { Write-Host '' }
    foreach ($r in $Rows) {
        if ($r.Count -eq 1) { Write-Host " $($r[0])" -ForegroundColor Yellow; continue }
        Write-Host (" {0}" -f $r[0].PadRight($KeyWidth)) -ForegroundColor Green -NoNewline
        Write-Host $r[1] -ForegroundColor White
    }
}

function Show-Cheats {
    $keys = @(
        @('Cheats and debug', 'GOD / TOOLS'),
        @('G', 'God mode on / off (a message shows on screen)'),
        @('Ctrl+G', 'Badass: super damage + god mode'),
        @('L', 'Loaded: all weapons and full ammo'),
        @('K', 'Kill enemies (kill enemy)'),
        @('Ctrl+K', 'Kill all AI (kill ai)'),
        @('I', 'Invisible to the AI on / off'),
        @('J', 'Fly mode'),
        @('Alt+J', 'Ghost mode (fly through walls)'),
        @('Ctrl+J', 'Walk: back to normal movement after Fly / Ghost'),
        @('O', 'Slow motion x5 (slomo 5)'),
        @('Shift+O', 'Slow motion x0.5 (slomo .5)'),
        @('Alt+O', 'Fast motion x10 (slomo 10)'),
        @('Ctrl+O', 'Normal speed (slomo 1)'),
        @('P', 'PlayersOnly: freezes the world, only players move'),
        @('Shift+P / Alt+P / Ctrl+P', 'Show FPS / engine stats / D3D stats'),
        @('F10', 'Server travel shortcut (servertravel)'),
        @('Z', 'Toggle hand IK'),
        @('Debug views', 'VIEW / AI / CAMERA'),
        @('U / Ctrl+U / Shift+U', 'AI debug display / cycle AI forward / cycle AI back'),
        @('Ctrl+I / Alt+I', 'Show AI paths / show cover points'),
        @('Ctrl+C', 'Show collision'),
        @('N', 'Show dynamic shadows on / off'),
        @('F1 F2 F3 F4 F5', 'Wireframe / unlit / lit / detail lighting / shader complexity'),
        @('F9', 'Screenshot'),
        @('Insert', 'Free camera'),
        @('Delete / End', 'Default camera / first person camera'),
        @('Page Down / Page Up', 'Third person camera / fixed camera'),
        @('Home', 'Fixed tracking camera'),
        @('Num + / Num -', 'Free camera distance'),
        @('Weapon keys', 'WEAPONS (switch to / give the weapon)'),
        @('5 6 7 8', 'Frag / ink / smoke / incendiary grenade'),
        @('Num 0 .. Num 3', 'Flamethrower / heavy minigun / heavy mortar / Locust assault rifle'),
        @('Num 4 .. Num 6', 'Locust burst pistol / Locust pistol / sniper rifle'),
        @('Num 7 .. Num 9', 'Boomshot / bow / Hammer of Dawn'),
        @('Ctrl+Num 0 .. Num 4', 'Digger launcher / Retro Lancer / sawed-off shotgun / cleaver / heavy flail')
    )
    Show-CheatTable 'PAGE 1/3: HOTKEYS IN THE GAME' @('Keyboard shortcuts from GearGame\Config\GearInput.ini (work during a mission).', 'Press the key while playing; most cheats show a message on the screen.') $keys 26
    Write-Host ''
    Write-Host ' Page 1/3. Press ENTER for the console commands, Q to go back.' -ForegroundColor DarkGray
    if ((Read-Host).Trim().ToUpper() -eq 'Q') { Clear-Host; return }

    $cmds = @(
        @('Player', 'CHEAT COMMANDS'),
        @('God', 'God mode on / off'),
        @('Badass', 'Super damage + god mode'),
        @('SuperDamage', 'Damage multiplier x8'),
        @('UnlimitedHealth', 'No health loss on / off'),
        @('UnlimitedAmmo / UnlimitedClips', 'Infinite ammo / infinite clips'),
        @('Loaded', 'All weapons and ammo'),
        @('Invis', 'Invisible to the AI on / off'),
        @('Fly / Ghost / Walk', 'Fly / ghost (no clip) / back to walking'),
        @('slomo <speed>', 'Game speed, e.g. slomo 0.2 or slomo 3 (slomo 1 = normal)'),
        @('SpawnWeapon <class>', 'Gives a weapon by class name, e.g. SpawnWeapon GearGame.GearWeap_Shotgun_SawedOff'),
        @('GiveWeapon <class>', 'Same, other path (also gives the weapon to the player)'),
        @('Enemies and AI', 'ENEMIES'),
        @('kill enemy / kill ai', 'Kill enemies / all AI'),
        @('KillMyTarget', 'Kills the pawn you aim at'),
        @('DBNOMyTarget', 'Knocks the pawn you aim at down (DBNO)'),
        @('DebugAI [category]', 'AI debug display (a category name is optional)'),
        @('ToggleFlocking', 'Flocking on / off'),
        @('DebugHeadshots', 'Headshot debug on / off'),
        @('Game and levels', 'GAME'),
        @('open <map>', 'Loads a map, e.g. open SP_Char_P'),
        @('servertravel <map>', 'Same as F10 with a map name (map change for the host and clients)'),
        @('ce <event>', 'Fires a Kismet console event in the loaded map (see page 3)'),
        @('HasChapterUnlock <n>', 'Tells if chapter n is unlocked'),
        @('UnlockAllChapters', 'Unlocks every chapter in Chapter Select and the Insane difficulty'),
        @('ShowDamage', 'Damage numbers on / off'),
        @('FF', 'Friendly fire state'),
        @('LaughTrack 1 / 0', 'Laugh track on / off'),
        @('ShowProfileSettings', 'Gore and mature language profile settings'),
        @('ScreenshotMode', 'Hides the HUD for screenshots, call again to leave'),
        @('Co-op', 'ONLINE / LAN'),
        @('open <ip>:7777', 'Join a host on the LAN (the host needs the network option, step 30)'),
        @('Display', 'STATS'),
        @('stat fps / stat engine / stat d3d', 'Frame rate and performance overlays (type again to hide)'),
        @('viewmode wireframe/unlit/lit', 'Rendering mode'),
        @('show collision / show paths', 'Collision and AI path debug views')
    )
    Show-CheatTable 'PAGE 2/3: CONSOLE COMMANDS' @('Open the console with the tilde key (~), type the command and press ENTER.', 'Cheat commands are meant for the campaign on your own PC (or as the co-op host).') $cmds 36
    Write-Host ''
    Write-Host ' Page 2/3. Press ENTER for the level events (checkpoint jumps), Q to go back.' -ForegroundColor DarkGray
    if ((Read-Host).Trim().ToUpper() -eq 'Q') { Clear-Host; return }

    $ce = @(
        @('Checkpoint jumps (ce <name>)', 'MAP: EVENTS'),
        @('SP_Char_P', 'jump_01_checkpoint .. jump_11_tower1_return (tower 1, subway, tower 2, gondola)'),
        @('SP_Badlands_P', 'jump_01_1stpatrol, jump_02_siegebeast, jump_03_gateapproach, jump_04_throughmaingate, jump_05_throughsidegate'),
        @('SP_Badlands2_P', 'jump_01_tickercages .. jump_06_blimptower (jump_04_corpsermama = Mother Corpser)'),
        @('SP_Mercy_P', 'jump1 .. jump11 (the Mercy chapter, in order)'),
        @('SP_Wreckage_P', 'jump1 .. jump4, jumpCollect, jumpPrescott'),
        @('SP_Maelstrom2_01_S', 'JumpToMaelstrom, JumpToArches, JumpToMines, JumpToTunnel, JumpToTurrets, JumpToChase'),
        @('SP_Maelstrom2_04_S', 'JumpToTunnel, JumpToTurrets'),
        @('SP_Maelstrom_01_S', 'JumpToFish, JumpToKelp'),
        @('SP_OldShipyard_P', 'CHAPTER1, CHAPTER2 (Intro_P: CHAPTER1)'),
        @('Other useful events', 'MAP: EVENTS'),
        @('SP_Char_P', 'load_01_s .. load_tower_2_s load the sub levels of the chapter; stream_in_audio_level / stream_out_audio_level'),
        @('SP_Badlands2_P', 'cine_tower, cine_transition2 (play the cinematics)'),
        @('SP_Ravens_P, SP_Ravens2_P', 'firstfight, hangar, shakez, gogo (battle / flow triggers)'),
        @('SP_Azura_P, SP_Azura2_P', 'gogo (start the next section)'),
        @('SP_Char_Tower_2_elevator_S', 'enable_lift_ride, golift (starts the Tower 2 lift)'),
        @('SP_Char_05_S', 'gotrain, holego, test_trainjumper, test_truck'),
        @('SP_Hanover_P / SP_Mercy_P', 'cine_stranded / cine_townhall, cometopapa, prepshowdown'),
        @('SP_Badlands_xx_S, SP_Char_xx_S', 'test_xx_S events spawn the section enemies (spawntest, test_bomb, test_14_S ...)')
    )
    Show-CheatTable 'PAGE 3/3: LEVEL EVENTS (ce) AND CHECKPOINT JUMPS' @('ce = Kismet console event. Type  ce <name>  in the console while that map is loaded.', 'Names come from the map files; they are developer test events, results can differ from the normal game flow.', 'A jump moves the story flow forward: save before you use it.') $ce 28
    Write-Host ''
    Read-Host ' -- End of the cheat list -- Press ENTER to return' | Out-Null
    Clear-Host
}

Clear-Host
Check-ForUpdate

while ($true) {


    Write-Host "====================================================================" -ForegroundColor Cyan

    Write-Host " GEARS OF WAR 3 PC (JACINTO v1.1.1) MULTI PATCH TOOL " -ForegroundColor Cyan -NoNewline
    Write-Host " v$CURRENT_VERSION " -ForegroundColor Black -BackgroundColor Cyan

    Write-Host " NexusMods: https://www.nexusmods.com/gearsofwar3/mods/27" -ForegroundColor Cyan

    Write-Host " | Multi Patch Tool Developer: DryreL" -ForegroundColor Yellow

    Write-Host " | Please check the 'Credits' screen to see all contributors." -ForegroundColor DarkYellow

    Write-Host " | Discord (DryreL Hub): https://discord.gg/euqRzjQKvc" -ForegroundColor Magenta

    Write-Host " | Contact me there, or get your own unique Steam artwork!" -ForegroundColor DarkMagenta

    Write-Host "====================================================================" -ForegroundColor Cyan

    Write-Host "  1. Install Multi-Patch (All-In-One Fix)" -ForegroundColor Green
    Write-Host "  2. Installation Verification"
    Write-Host "  3. Keybind Editor (Keyboard & Mouse)"
    Write-Host "  4. Cheats and Console Commands"
    Write-Host "  5. Game Settings"
    Write-Host "  6. Credits"
    Write-Host "  7. Changelog"
    Write-Host "  8. Uninstall"
    Write-Host "  9. Exit"
    Write-Host ""
    Write-Host "  N.  Open the NexusMods Page"
    Write-Host "  D.  Join the Discord Server (DryreL Hub)"
    Write-Host "  S.  Start the Game"
    Write-Host "  SD. Start the Game (Debug)"
    Write-Host ""

    $opt = Read-Host "Choose an option"

    switch ($opt) {
        '1' { Install-Multipatch }
        '2' { Invoke-VerifyInstallation; Clear-Host }
        '3' { Show-KeybindEditor; Clear-Host }
        '4' { Show-Cheats }
        '5' { Show-GameSettings; Clear-Host }
        '6' { Show-Credits }
        '7' { Show-Changelog; Clear-Host }
        '8' { Uninstall-Multipatch }
        '9' { exit 0 }
        'N' { Open-NexusPage; Clear-Host }
        'D' { Open-NexusPage 'https://discord.gg/euqRzjQKvc'; Clear-Host }
        'S' { Start-GameFromMenu $false; Clear-Host }
        'SD' { Start-GameFromMenu $true; Clear-Host }

    }

}

