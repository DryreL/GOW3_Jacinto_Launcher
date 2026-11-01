[CmdletBinding()]
param(
    # Clean Jacinto 1.1.1. It is only read.
    [Parameter(Mandatory)][string]$CleanGame,
    # Package holding Install-Jacinto.ps1 and the PowerShell Multi Patch Tool: the reference engine.
    [string]$ToolPackage,
    # Disposable folder on the game's drive (big unchanged files are hard links, the game is never written).
    [string]$Work = 'D:\JacintoWork',
    [string]$Launcher,
    # The current JacintoNet build; it replaces the older DLLs of the reference package.
    [string]$JacintoNetBin
)
# Runs the reference engine on a disposable copy, builds multipatch.jcpatch from the result, installs the
# patch with the C# engine on a second copy and fails unless every game file is byte-identical. Windows only.
$ErrorActionPreference = 'Stop'
if (-not $ToolPackage) { $ToolPackage = Join-Path $PSScriptRoot '..\multipatch\reference-package' }
if (-not $JacintoNetBin) { $JacintoNetBin = Join-Path $PSScriptRoot '..\multipatch\tool\_files\JacintoNet\bin' }
if (-not $Launcher) { $Launcher = Join-Path $PSScriptRoot '..\bin\Release\net10.0\win-x64\GoW3Jacinto_Launcher.exe' }
$CleanGame = [IO.Path]::GetFullPath($CleanGame); $Launcher = [IO.Path]::GetFullPath($Launcher)
$output = Join-Path $PSScriptRoot '..\patch-data\multipatch.jcpatch'; [void][IO.Directory]::CreateDirectory((Split-Path -Parent $output))
$engineFiles = 'ini','txt','bat','cfg','json','xml','log','u','config','int','esm','ita'
# A file the tool adds that is already in the "clean" copy looks unchanged, so it would silently stay out of the patch data.
foreach ($added in 'GearGame\Content\CorpserRestor_Audio.upk','GearGame\Content\Interface\WarfareFonts2x.upk','Binaries\Win64\wsock32.dll') {
    if (Test-Path -LiteralPath (Join-Path $CleanGame $added)) { throw "Not a clean game: it already has $added. Extract the original archive again." }
}

function New-Fixture([string]$Destination) {
    if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination -Recurse -Force }
    foreach ($tree in 'Binaries','Engine','GearGame') {
        Get-ChildItem -LiteralPath (Join-Path $CleanGame $tree) -Recurse -File -Force | ForEach-Object {
            $target = Join-Path $Destination $_.FullName.Substring($CleanGame.Length + 1)
            [void][IO.Directory]::CreateDirectory((Split-Path -Parent $target))
            $mayChange = $_.Length -lt 40MB -or $_.Extension.TrimStart('.') -in $engineFiles -or $_.Name -in 'GearGame.exe','Engine.u','GearGame.u','GearGameContent.u','Open_Logos.bik'
            if ($mayChange) { [IO.File]::Copy($_.FullName, $target, $true) } else { New-Item -ItemType HardLink -Path $target -Target $_.FullName | Out-Null }
        }
    }
    Set-Content -LiteralPath (Join-Path $Destination '.jacinto-test-fixture') -Value ''
}

# The reference engine has culture bugs (on Turkish Windows its @{} lookups miss every path with a capital I),
# so the disposable copy of it runs with en-US and an ordinal lookup. The shipped C# engine has neither problem.
function Set-EngineCulture([string]$Package) {
    $culture = "[Threading.Thread]::CurrentThread.CurrentCulture=[Globalization.CultureInfo]::GetCultureInfo('en-US');[Threading.Thread]::CurrentThread.CurrentUICulture=[Globalization.CultureInfo]::GetCultureInfo('en-US')`r`n"
    $installer = Join-Path $Package 'Install-Jacinto.ps1'; $tool = Join-Path $Package 'Payload\Gears3_Multipatch_Tool\Gears3_Multipatch_Tool.ps1'
    $text = [IO.File]::ReadAllText($installer).Replace('$realCase = @{}', '$realCase = New-Object Collections.Hashtable ([StringComparer]::OrdinalIgnoreCase)')
    $text = [regex]::Replace($text, '(?m)^\$dotnet = .*\r?\nif \(-not \(Test-Path -LiteralPath \$dotnet\).*\r?\n', '')
    foreach ($name in 'wsock32.dll','steam_api64.dll') { foreach ($place in '_files','_files\JacintoNet\bin') { Copy-Item -LiteralPath (Join-Path $JacintoNetBin $name) -Destination (Join-Path $Package "Payload\Gears3_Multipatch_Tool\$place\$name") -Force } }
    [IO.File]::WriteAllText($installer, $text.Insert($text.IndexOf('$ErrorActionPreference'), $culture), (New-Object Text.UTF8Encoding($true)))
    $text = [IO.File]::ReadAllText($tool)
    [IO.File]::WriteAllText($tool, $text.Insert($text.IndexOf('$P0Action = [string]$env:JACINTO_P0_ACTION'), $culture), (New-Object Text.UTF8Encoding($true)))
    $sums = Join-Path $Package 'SHA256SUMS.json'; $manifest = Get-Content -LiteralPath $sums -Raw | ConvertFrom-Json
    foreach ($entry in $manifest) { if ($entry.Path -in 'Install-Jacinto.ps1','Payload\Gears3_Multipatch_Tool\Gears3_Multipatch_Tool.ps1' -or $entry.Path -match '(wsock32|steam_api64)\.dll$') { $path = Join-Path $Package $entry.Path; $entry.SHA256 = (Get-FileHash -LiteralPath $path).Hash; $entry.Length = (Get-Item -LiteralPath $path).Length } }
    [IO.File]::WriteAllText($sums, (ConvertTo-Json @($manifest) -Depth 3))
}

function Invoke-Hidden([string]$File, [string[]]$Arguments) {
    $process = Start-Process $File -ArgumentList $Arguments -Wait -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $Work 'last.out') -RedirectStandardError (Join-Path $Work 'last.err')
    if ($process.ExitCode -ne 0) { throw "$File $($Arguments[0]) exited with $($process.ExitCode): $(Get-Content -LiteralPath (Join-Path $Work 'last.err') -Raw)" }
}

New-Item -ItemType Directory -Force -Path $Work | Out-Null
$reference = Join-Path $Work 'reference'; $package = Join-Path $reference (Split-Path -Leaf $ToolPackage)
New-Fixture $reference
& robocopy $ToolPackage $package /E /NFL /NDL /NJH /NJS /NP | Out-Null
Set-EngineCulture $package
$env:JACINTO_TEST_DESKTOP = Join-Path $Work 'desktop'
Invoke-Hidden 'powershell.exe' @('-NoLogo','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',"`"$(Join-Path $package 'Install-Jacinto.ps1')`"")
Remove-Item -LiteralPath $package -Recurse -Force
# The engine leaves a JacintoNet DLL it recognises in place; the result must carry the current build.
foreach ($name in 'wsock32.dll','steam_api64.dll') { Copy-Item -LiteralPath (Join-Path $JacintoNetBin $name) -Destination (Join-Path $reference "Binaries\Win64\$name") -Force }

# Fixes made after the reference engine was frozen. Each checks what it replaces, so a changed input stops the build.
function Set-Byte([string]$Path, [long]$Offset, [byte]$From, [byte]$To) {
    $stream = [IO.File]::Open($Path, 'Open', 'ReadWrite', 'None')
    try {
        $stream.Position = $Offset
        $current = $stream.ReadByte()
        if ($current -ne $From) { throw "$Path at $Offset holds $current, expected $From." }
        $stream.Position = $Offset; $stream.WriteByte($To)
    } finally { $stream.Dispose() }
}
function Edit-Ini([string]$Path, [string]$Pattern, [string]$Value) {
    $text = [IO.File]::ReadAllText($Path)
    if ($text -notmatch $Pattern) { throw "$Path has no $Pattern." }
    [IO.File]::WriteAllText($Path, [regex]::Replace($text, $Pattern, $Value))
}
# Act 2 Chapter 5 from Chapter Select: SP_Badlands_14a was only switched on by the Mother Corpser outro, which that start skips, so the ground was missing.
Set-Byte (Join-Path $reference 'GearGame\Content\Maps\SP\SP_05_Badlands\SP_Badlands2_P.gear') 0x4999c 1 0
foreach ($file in 'GearGame\Config\GearEngine.ini','GearGame\Config\DefaultEngine.ini') {
    # Game time stopped at 50 ms per frame while sound kept real time, so every hitch pushed cutscene audio ahead of the picture.
    Edit-Ini (Join-Path $reference $file) '(?m)^MaxDeltaTime=0\.05(?=\r?$)' 'MaxDeltaTime=0.25'
}
foreach ($file in 'GearGame\Config\GearInput.ini','GearGame\Config\DefaultInput.ini') {
    # Turn acceleration for a stick; the gamepad profile turns it back on.
    Edit-Ini (Join-Path $reference $file) '(?m)^bViewAccelEnabled=TRUE(?=\r?$)' 'bViewAccelEnabled=FALSE'
}
Remove-Item -LiteralPath $output -Force -ErrorAction SilentlyContinue
Invoke-Hidden $Launcher @('--build-patchset', "`"$CleanGame`"", "`"$reference`"", "`"$output`"", "`"$(Join-Path $PSScriptRoot '..\multipatch\tool\_files')`"")

$check = Join-Path $Work 'check'; New-Fixture $check
$staging = Join-Path $Work 'staging'; if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
& robocopy (Split-Path -Parent $Launcher) (Join-Path $staging 'Launcher') /E /NFL /NDL /NJH /NJS /NP | Out-Null
Copy-Item -LiteralPath $output -Destination (Join-Path $staging 'Launcher')
Invoke-Hidden (Join-Path $staging 'Launcher\GoW3Jacinto_Launcher.exe') @('--install', "`"$check`"")
$different = @()
foreach ($tree in 'Binaries','Engine','GearGame') {
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $reference $tree) -Recurse -File -Force) {
        $other = Join-Path $check $file.FullName.Substring($reference.Length + 1)
        if (-not (Test-Path -LiteralPath $other) -or (Get-Item -LiteralPath $other).Length -ne $file.Length -or (Get-FileHash -LiteralPath $other).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) { $different += $file.FullName }
    }
}
foreach ($name in 'Gears3_Start_Game.bat','Gears3_Start_Game_Debug.bat') { if ((Get-FileHash -LiteralPath (Join-Path $check $name)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $reference $name)).Hash) { $different += $name } }
if ($different.Count) { throw "$($different.Count) file(s) differ from the reference engine. First: $($different[0])" }
Write-Output "Patch data verified against the reference engine: $output ($([int]((Get-Item -LiteralPath $output).Length / 1MB)) MB)"
