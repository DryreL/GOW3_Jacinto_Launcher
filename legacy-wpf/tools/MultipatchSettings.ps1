[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('GetGraphics', 'ApplyGraphics', 'GetControls', 'ApplyControls', 'Rollback', 'RollbackGraphics', 'RollbackControls')][string]$Action,
    [Parameter(Mandatory = $true)][string]$GameDir,
    [Parameter(Mandatory = $true)][string]$TransactionRoot,
    [string]$RequestPath,
    [string]$DocumentsRoot,
    [string]$TransactionId
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($GameDir).TrimEnd('\')
$txRoot = [IO.Path]::GetFullPath($TransactionRoot)
$configDir = Join-Path $root 'GearGame\Config'
# Full Jacinto install: tools\multipatch\_files. Existing DryreL install: Gears3_Multipatch_Tool\_files.
$filesDir = @((Join-Path $root 'tools\multipatch\_files'), (Join-Path $root 'Gears3_Multipatch_Tool\_files')) | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'graphics.json') } | Select-Object -First 1
if (-not $filesDir) { $filesDir = Join-Path $root 'tools\multipatch\_files' }
$graphicsPath = Join-Path $filesDir 'graphics.json'
$keybindsPath = Join-Path $filesDir 'keybinds.json'
$binariesDir = Join-Path $root 'Binaries\Win64'
$docsConfig = if ($DocumentsRoot) { Join-Path ([IO.Path]::GetFullPath($DocumentsRoot)) 'My Games\Gears of War 3\GearGame\Config' } else { Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'My Games\Gears of War 3\GearGame\Config' }
$null = New-Item -ItemType Directory -Force -Path $txRoot

function Get-Hash([string]$Path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes($Path)))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}
function Read-JsonObject([string]$Path, [switch]$Optional) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { if ($Optional) { return [pscustomobject]@{} }; throw "Required file is missing: $Path" }
    try { $obj = [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8) | ConvertFrom-Json -ErrorAction Stop } catch { throw "Invalid JSON at '$Path': $($_.Exception.Message)" }
    if ($null -eq $obj -or $obj -is [array] -or $obj -is [string]) { throw "Expected a JSON object at '$Path'." }
    return $obj
}
function Set-JsonProperty($Object, [string]$Name, $Value) {
    $p = $Object.PSObject.Properties[$Name]
    if ($null -eq $p) { $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value }
    else { $p.Value = $Value }
}
function Write-Atomic([string]$Path, [byte[]]$Bytes) {
    $temp = $Path + '.jacinto-' + [guid]::NewGuid().ToString('N') + '.tmp'
    $swapBackup = $Path + '.jacinto-' + [guid]::NewGuid().ToString('N') + '.bak'
    $existed = Test-Path -LiteralPath $Path -PathType Leaf
    $attributes = if ($existed) { [IO.File]::GetAttributes($Path) } else { [IO.FileAttributes]::Normal }
    try {
        if ($existed -and (($attributes -band [IO.FileAttributes]::ReadOnly) -ne 0)) { [IO.File]::SetAttributes($Path, ($attributes -bxor [IO.FileAttributes]::ReadOnly)) }
        [IO.File]::WriteAllBytes($temp, $Bytes)
        if ($existed) { [IO.File]::Replace($temp, $Path, $swapBackup); [IO.File]::SetAttributes($Path, $attributes); Remove-Item -LiteralPath $swapBackup -Force }
        else { [IO.File]::Move($temp, $Path) }
    }
    finally {
        if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue }
        if (Test-Path -LiteralPath $swapBackup) { Remove-Item -LiteralPath $swapBackup -Force -ErrorAction SilentlyContinue }
    }
}
function Set-IniSectionValue([string]$Text, [string]$Section, [string]$Key, [string]$Value) {
    # Adapted from the Multipatch section writer: replace every duplicate key in
    # the first matching section; preserve same-named keys in other sections.
    $line = "$Key=$Value"
    $sectionEscaped = [regex]::Escape($Section)
    $keyEscaped = [regex]::Escape($Key)
    $sectionPattern = '(?m)^\[' + $sectionEscaped + '\]\r?$'
    if ($Text -match $sectionPattern) {
        $pattern = '(?ms)(^\[' + $sectionEscaped + '\]\r?\n)(.*?)(?=(?m)^\[[^\]\r\n]+\]\r?$|\z)'
        $regex = New-Object System.Text.RegularExpressions.Regex($pattern)
        $match = $regex.Match($Text)
        if ($match.Success) {
            $body = $match.Groups[2].Value
            $keyPattern = '(?m)^' + $keyEscaped + '=[^\r\n]*'
            if ($body -match $keyPattern) { $newBody = [regex]::Replace($body, $keyPattern, [Text.RegularExpressions.MatchEvaluator]{ param($m) $line }) }
            else { $newBody = $body.TrimEnd() + "`r`n$line`r`n" }
            $full = $match.Groups[1].Value + $newBody
            return $Text.Substring(0, $match.Index) + $full + $Text.Substring($match.Index + $match.Length)
        }
    }
    return $Text.TrimEnd() + "`r`n`r`n[$Section]`r`n$line`r`n"
}
function Get-SectionValue([string]$Text, [string]$Section, [string]$Key) {
    $pattern = '(?ms)^\[' + [regex]::Escape($Section) + '\]\r?\n(.*?)(?=^\[[^\]\r\n]+\]\r?$|\z)'
    $match = [regex]::Match($Text, $pattern)
    if (-not $match.Success) { return $null }
    $m = [regex]::Match($match.Groups[1].Value, '(?m)^' + [regex]::Escape($Key) + '=([^\r\n]*)')
    if ($m.Success) { return $m.Groups[1].Value }
    return $null
}
function Get-Resolution($Settings) {
    $resolution = [string]$Settings.Resolution
    if ($resolution -match '^(\d{3,5})x(\d{3,5})$') { return @([int]$Matches[1], [int]$Matches[2]) }
    Add-Type -AssemblyName System.Windows.Forms
    if ($resolution -eq 'span') {
        $screens = [Windows.Forms.Screen]::AllScreens
        $left = ($screens | ForEach-Object { $_.Bounds.Left } | Measure-Object -Minimum).Minimum
        $top = ($screens | ForEach-Object { $_.Bounds.Top } | Measure-Object -Minimum).Minimum
        $right = ($screens | ForEach-Object { $_.Bounds.Right } | Measure-Object -Maximum).Maximum
        $bottom = ($screens | ForEach-Object { $_.Bounds.Bottom } | Measure-Object -Maximum).Maximum
        return @(($right - $left), ($bottom - $top))
    }
    $bounds = [Windows.Forms.Screen]::PrimaryScreen.Bounds
    return @($bounds.Width, $bounds.Height)
}
function Validate-Graphics($Settings) {
    if ([string]$Settings.Resolution -notmatch '^(native|span|\d{3,5}x\d{3,5})$') { throw 'Unsupported Resolution value.' }
    if ($Settings.Resolution -match '^(\d+)x(\d+)$' -and ([int]$Matches[1] -lt 640 -or [int]$Matches[2] -lt 480)) { throw 'Resolution must be at least 640x480.' }
    $fpsValue=0; $refreshValue=0
    if ($Settings.FpsLimit -is [bool] -or -not [int]::TryParse([string]$Settings.FpsLimit,[ref]$fpsValue) -or $fpsValue -lt 0 -or $fpsValue -gt 500) { throw 'FpsLimit must be an integer from 0 to 500 (0 means the Multipatch default).' }
    if ($Settings.RefreshRate -is [bool] -or -not [int]::TryParse([string]$Settings.RefreshRate,[ref]$refreshValue) -or $refreshValue -lt 0 -or $refreshValue -gt 1000) { throw 'RefreshRate must be an integer from 0 to 1000.' }
    if ([int]$Settings.Fov -ne 0 -and ([int]$Settings.Fov -lt 50 -or [int]$Settings.Fov -gt 130)) { throw 'Fov must be 0 (automatic) or 50 to 130.' }
    if ([string]$Settings.AntiAliasing -notin @('off','fxaa','msaa2x','msaa4x')) { throw 'AntiAliasing must be off, fxaa, msaa2x or msaa4x.' }
    if ([int]$Settings.Anisotropy -notin @(1,2,4,8,16)) { throw 'Anisotropy must be 1, 2, 4, 8 or 16.' }
    foreach ($name in 'Fullscreen','VSync','FovAllModes','AmbientOcclusion') { if ($Settings.$name -isnot [bool]) { throw "$name must be a JSON boolean." } }
    foreach ($name in 'GraphicsQuality','ShadowQuality','TextureQuality','FoliageQuality') { if ([string]$Settings.$name -notin @('low','medium','high','ultra')) { throw "$name must be low, medium, high or ultra." } }
    if ([string]$Settings.DriveType -notin @('ssd','hdd')) { throw 'DriveType must be ssd or hdd.' }
    $validEffects = @('MotionBlur','DepthOfField','Bloom','LensFlares','LightShafts','Distortion','RadialBlur','Subsurface','SeparateTranslucency')
    if ($Settings.Disabled -isnot [array]) { throw 'Disabled must be a JSON array.' }
    foreach ($effect in $Settings.Disabled) { if ([string]$effect -notin $validEffects) { throw "Unsupported disabled effect: $effect" } }
    foreach ($name in 'AudioFilter','DisableRunShake') { if ($Settings.$name -isnot [bool]) { throw "$name must be a JSON boolean." } }
    if ([int]$Settings.AudioChannels -notin @(32,64,128) -or $Settings.AudioChannels -is [bool]) { throw 'AudioChannels must be 32, 64 or 128.' }
}
# Audio / camera options of the Multi Patch Tool Game Settings; defaults are the tool's (Get-GameSettings -Defaults).
function Add-AudioDefaults($Settings) {
    if ($null -eq $Settings.AudioFilter) { Set-JsonProperty $Settings 'AudioFilter' $true }
    if ($null -eq $Settings.AudioChannels) { Set-JsonProperty $Settings 'AudioChannels' 32 }
    if ($null -eq $Settings.DisableRunShake) { Set-JsonProperty $Settings 'DisableRunShake' $true }
    return $Settings
}
$RunShakeStock = [ordered]@{ 'GearGame.GearPawn' = '0.6f'; 'GearGameContent.GearPawn_LocustKantusArmor' = '0.45f' }
function Get-PawnFiles {
    $list = foreach ($dir in @($configDir, $docsConfig)) { foreach ($name in 'GearPawn.ini','DefaultPawn.ini','GearPawnMP.ini','DefaultPawnMP.ini') { Join-Path $dir $name } }
    return @($list | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
}
$audioExecPath = Join-Path $binariesDir 'Gears3_Audio.txt'
function Get-EngineFiles {
    $list = @((Join-Path $configDir 'GearEngine.ini'),(Join-Path $configDir 'DefaultEngine.ini'),(Join-Path $root 'Engine\Config\BaseEngine.ini'),(Join-Path $docsConfig 'GearEngine.ini'))
    return @($list | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
}
function Get-CameraFiles {
    $list = @((Join-Path $configDir 'GearCamera.ini'),(Join-Path $configDir 'DefaultCamera.ini'),(Join-Path $docsConfig 'GearCamera.ini'))
    return @($list | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
}
function Get-LaunchFiles {
    $list = @((Join-Path $root 'Gears3_Start_Game.bat'),(Join-Path $binariesDir 'StartCampaign.bat'),(Join-Path $root 'Gears3_Start_Game_Debug.bat'))
    return @($list | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
}
function Get-ControlFiles {
    $list = @((Join-Path $docsConfig 'GearInput.ini'),(Join-Path $configDir 'GearInput.ini'),(Join-Path $configDir 'DefaultInput.ini'))
    return @($list | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
}
function Get-EffectiveGraphicsState($Settings) {
    $res=Get-Resolution $Settings;$rx=[int]$res[0];$ry=[int]$res[1]
    $fpsExpected=if([int]$Settings.FpsLimit -gt 0){[string]$Settings.FpsLimit}else{'300'}
    $smoothExpected=if([int]$Settings.FpsLimit -gt 0){'True'}else{'False'}
    $samplesExpected=switch([string]$Settings.AntiAliasing){'msaa2x'{'2'}'msaa4x'{'4'}default{'1'}}
    $msaaExpected=if([string]$Settings.AntiAliasing -like 'msaa*'){'True'}else{'False'}
    $taaExpected=if([string]$Settings.AntiAliasing -eq 'fxaa'){'True'}else{'False'}
    $fovExpected=if([int]$Settings.Fov -ge 50){[int]$Settings.Fov}elseif($rx/$ry -ge 2){65}else{80}
    $quality=@{low=@('0','0.800000','1','1');medium=@('1','1.000000','0','0');high=@('2','1.000000','0','0');ultra=@('2','1.250000','-1','-1')}[[string]$Settings.GraphicsQuality]
    $shadow=@{low=@('32','512','512','-1','False');medium=@('64','1024','1024','0','True');high=@('64','1120','1344','0','True');ultra=@('128','2048','2048','1','True')}[[string]$Settings.ShadowQuality]
    $lod=switch([string]$Settings.TextureQuality){'low'{'2'}'medium'{'1'}default{'0'}}
    $hdd=([string]$Settings.DriveType -eq 'hdd')
    $stream=@{MinRequestedMipsToConsider=$(if($hdd){'1'}else{'11'});MinTimeToGuaranteeMinMipCount=$(if($hdd){'12'}else{'2'});MaxTimeToGuaranteeMinMipCount=$(if($hdd){'30'}else{'12'});LoadMapTimeLimit=$(if($hdd){'60.0'}else{'20.0'});DropMipLevelsLimit=$(if($hdd){'24'}else{'16'});StopIncreasingLimit=$(if($hdd){'16'}else{'12'});StopStreamingLimit=$(if($hdd){'12'}else{'8'})}
    $effects=[ordered]@{MotionBlur=@('MotionBlur','MotionBlurPause');DepthOfField=@('DepthOfField');Bloom=@('Bloom');LensFlares=@('LensFlares');LightShafts=@('bAllowLightShafts');Distortion=@('Distortion','FilteredDistortion');RadialBlur=@('AllowRadialBlur');Subsurface=@('AllowSubsurfaceScattering');SeparateTranslucency=@('bAllowSeparateTranslucency')}
    $states=@();$allMatch=$true;$primaryEngine=Join-Path $docsConfig 'GearEngine.ini';if(-not(Test-Path -LiteralPath $primaryEngine)){$primaryEngine=Join-Path $configDir 'GearEngine.ini'}
    foreach($path in (Get-EngineFiles)){
        $text=[IO.File]::ReadAllText($path);$rxm=[regex]::Match($text,'(?m)^ResX=(\d+)');$rym=[regex]::Match($text,'(?m)^ResY=(\d+)')
        $actual=[ordered]@{Resolution=if($rxm.Success -and $rym.Success){"$($rxm.Groups[1].Value)x$($rym.Groups[1].Value)"}else{''};Fullscreen=Get-SectionValue $text 'SystemSettings' 'Fullscreen';VSync=Get-SectionValue $text 'SystemSettings' 'UseVsync';Fps=Get-SectionValue $text 'Engine.Engine' 'MaxSmoothedFrameRate';Smooth=Get-SectionValue $text 'Engine.Engine' 'bSmoothFrameRate';Samples=Get-SectionValue $text 'SystemSettings' 'MaxMultiSamples';MSAA=Get-SectionValue $text 'SystemSettings' 'bAllowD3D9MSAA';TAA=Get-SectionValue $text 'SystemSettings' 'bAllowTemporalAA';Anisotropy=Get-SectionValue $text 'SystemSettings' 'MaxAnisotropy';AmbientOcclusion=Get-SectionValue $text 'SystemSettings' 'AmbientOcclusion';RefreshMin=Get-SectionValue $text 'WinDrv.WindowsClient' 'MinAllowableRefreshRate';RefreshMax=Get-SectionValue $text 'WinDrv.WindowsClient' 'MaxAllowableRefreshRate';DetailMode=Get-SectionValue $text 'SystemSettings' 'DetailMode';MaxDrawDistanceScale=Get-SectionValue $text 'SystemSettings' 'MaxDrawDistanceScale';SkeletalMeshLODBias=Get-SectionValue $text 'SystemSettings' 'SkeletalMeshLODBias';StaticMeshLODBias=Get-SectionValue $text 'SystemSettings' 'StaticMeshLODBias';bUseMaxQualityMode=Get-SectionValue $text 'SystemSettings' 'bUseMaxQualityMode';MinShadowResolution=Get-SectionValue $text 'SystemSettings' 'MinShadowResolution';MaxShadowResolution=Get-SectionValue $text 'SystemSettings' 'MaxShadowResolution';MaxWholeSceneDominantShadowResolution=Get-SectionValue $text 'SystemSettings' 'MaxWholeSceneDominantShadowResolution';ShadowFilterQualityBias=Get-SectionValue $text 'SystemSettings' 'ShadowFilterQualityBias';bAllowWholeSceneDominantShadows=Get-SectionValue $text 'SystemSettings' 'bAllowWholeSceneDominantShadows';SpeedTreeLeaves=Get-SectionValue $text 'SystemSettings' 'SpeedTreeLeaves';SpeedTreeFronds=Get-SectionValue $text 'SystemSettings' 'SpeedTreeFronds';OnlyStreamInTextures=Get-SectionValue $text 'SystemSettings' 'OnlyStreamInTextures';bEnableMipLevelFading=Get-SectionValue $text 'Engine.Engine' 'bEnableMipLevelFading'}
        $expected=[ordered]@{Resolution="$rx`x$ry";Fullscreen=[string]$Settings.Fullscreen;VSync=[string]$Settings.VSync;Fps=$fpsExpected;Smooth=$smoothExpected;Samples=$samplesExpected;MSAA=$msaaExpected;TAA=$taaExpected;Anisotropy=[string]$Settings.Anisotropy;AmbientOcclusion=[string]$Settings.AmbientOcclusion;RefreshMin=[string]$Settings.RefreshRate;RefreshMax=[string]$Settings.RefreshRate;DetailMode=$quality[0];MaxDrawDistanceScale=$quality[1];SkeletalMeshLODBias=$quality[2];StaticMeshLODBias=$quality[3];bUseMaxQualityMode='False';MinShadowResolution=$shadow[0];MaxShadowResolution=$shadow[1];MaxWholeSceneDominantShadowResolution=$shadow[2];ShadowFilterQualityBias=$shadow[3];bAllowWholeSceneDominantShadows=$shadow[4];SpeedTreeLeaves=$(if($Settings.FoliageQuality -eq 'low'){'False'}else{'True'});SpeedTreeFronds=$(if($Settings.FoliageQuality -in @('high','ultra')){'True'}else{'False'});OnlyStreamInTextures=[string]$hdd;bEnableMipLevelFading=[string]$hdd}
        $fileMatch=$true
        foreach($key in $expected.Keys){if($null -eq $actual[$key]){if([IO.Path]::GetFullPath($path) -eq [IO.Path]::GetFullPath($primaryEngine)){$allMatch=$false;$fileMatch=$false}}elseif([string]$actual[$key] -ne [string]$expected[$key]){$allMatch=$false;$fileMatch=$false}}
        foreach($key in $stream.Keys){if((Get-SectionValue $text 'TextureStreaming' $key) -ne [string]$stream[$key]){$allMatch=$false}}
        foreach($effect in $effects.Keys){$value=if(@($Settings.Disabled) -contains $effect){'False'}else{'True'};foreach($key in $effects[$effect]){if((Get-SectionValue $text 'SystemSettings' $key) -ne $value){$allMatch=$false}}}
        $textureRows=@([regex]::Matches($text,'(?m)^TEXTUREGROUP_(?:World|WorldNormalMap|Character|CharacterNormalMap|Weapon|WeaponNormalMap|Vehicle|VehicleNormalMap)=\([^\r\n]*LODBias=-?\d+[^\r\n]*\)'))
        foreach($row in $textureRows){$bias=[regex]::Match($row.Value,'LODBias=(-?\d+)').Groups[1].Value;if($bias -ne $lod){$allMatch=$false;$fileMatch=$false}}
        if([IO.Path]::GetFullPath($path) -eq [IO.Path]::GetFullPath($primaryEngine) -and $textureRows.Count -eq 0){$allMatch=$false;$fileMatch=$false}
        $states += [pscustomobject]@{File=$path;Matches=$fileMatch;Actual=$actual;Expected=$expected;TextureGroupsChecked=$textureRows.Count}
    }
    foreach($path in (Get-CameraFiles)){$text=[IO.File]::ReadAllText($path);$m=[regex]::Match($text,'(?m)^BaseFOV=([^\r\n]*)');$v=if($m.Success){$m.Groups[1].Value}else{$null};$fovValues=if($Settings.FovAllModes){@([regex]::Matches($text,'(?m)^FOVAngle=([^\r\n]*)')|ForEach-Object{$_.Groups[1].Value})}else{@((Get-SectionValue $text 'GearGame.GameplayCam_Default' 'FOVAngle'))};$camOk=($fovValues.Count -gt 0) -and @($fovValues|Where-Object{$_ -ne [string]$fovExpected}).Count -eq 0;$baseOk=($null -eq $v) -or ($v -eq [string]$fovExpected);$ok=$baseOk -and $camOk;$primaryCamera=Join-Path $docsConfig 'GearCamera.ini';if(-not(Test-Path -LiteralPath $primaryCamera)){$primaryCamera=Join-Path $configDir 'GearCamera.ini'};if(-not $ok -and [IO.Path]::GetFullPath($path) -eq [IO.Path]::GetFullPath($primaryCamera)){$allMatch=$false};$states += [pscustomobject]@{File=$path;ExpectedFov=[string]$fovExpected;ActualFov=$v;GameplayFov=$fovValues;Matches=$ok}}
    $filterOff = ($Settings.AudioFilter -eq $false)
    foreach($path in (Get-LaunchFiles)){$text=[IO.File]::ReadAllText($path);$ok=$text.Contains("-ResX=$rx -ResY=$ry") -and $text.Contains($(if($Settings.Fullscreen){'-FULLSCREEN'}else{'-WINDOWED'})) -and $text.Contains($(if($Settings.VSync){'-VSYNC'}else{'-NOVSYNC'})) -and ($text.Contains('-exec=Gears3_Audio.txt') -eq $filterOff);if(-not $ok){$allMatch=$false};$states += [pscustomobject]@{File=$path;LaunchArgumentsMatch=$ok}}
    # Audio: channels in every engine INI that has the audio sections, the filter command file, run shake in the pawn INIs.
    $channels=[string]$Settings.AudioChannels
    foreach($path in (Get-EngineFiles)){$text=[IO.File]::ReadAllText($path);foreach($sec in 'XAudio2.XAudio2Device','ALAudio.ALAudioDevice'){$v=Get-SectionValue $text $sec 'MaxChannels';if($null -ne $v -and $v -ne $channels){$allMatch=$false}}}
    $execOk=if($filterOff){(Test-Path -LiteralPath $audioExecPath) -and ([IO.File]::ReadAllText($audioExecPath).Trim() -eq 'DisableLPF')}else{-not(Test-Path -LiteralPath $audioExecPath)}
    if(-not $execOk){$allMatch=$false}
    foreach($path in (Get-PawnFiles)){$text=[IO.File]::ReadAllText($path);foreach($sec in $RunShakeStock.Keys){$v=Get-SectionValue $text $sec 'RoadieRunShakyCamDampeningFactor';$want=if($Settings.DisableRunShake){'0.0f'}else{$RunShakeStock[$sec]};if($null -ne $v -and $v -ne $want){$allMatch=$false}}}
    $states += [pscustomobject]@{Audio=[pscustomobject]@{AudioFilter=$Settings.AudioFilter;AudioChannels=$Settings.AudioChannels;DisableRunShake=$Settings.DisableRunShake;FilterCommandFileOk=$execOk}}
    return [pscustomobject]@{IsApplied=$allMatch;Files=$states}
}
function Get-AllowedTargets([string]$Kind) {
    $common = @($graphicsPath)
    if ($Kind -eq 'Graphics') { return @($common + (Get-EngineFiles) + (Get-CameraFiles) + (Get-LaunchFiles) + (Get-PawnFiles) + @($audioExecPath)) }
    return @($common + @($keybindsPath) + (Get-ControlFiles))
}
function Assert-Preflight([string]$Kind) {
    if (-not (Test-Path -LiteralPath (Join-Path $binariesDir 'GearGame.exe') -PathType Leaf)) { throw 'Game executable is missing from the selected directory.' }
    if (-not (Test-Path -LiteralPath (Join-Path $root 'Gears3_Start_Game.bat') -PathType Leaf)) { throw 'Multipatch start script is missing.' }
    if (-not (Test-Path -LiteralPath $graphicsPath -PathType Leaf)) { throw 'Multipatch graphics.json is missing.' }
    if ((Get-Process -Name GearGame -ErrorAction SilentlyContinue).Count -gt 0) { throw 'Close GearGame before applying settings.' }
    $journalPath = Join-Path $filesDir 'installation-state.json'
    if (Test-Path -LiteralPath $journalPath) {
        $journal = Read-JsonObject $journalPath
        if ([string]$journal.Status -ne 'complete') { throw "Multipatch journal is '$($journal.Status)'; settings changes are blocked until installation is complete." }
    }
    if ($Kind -eq 'Graphics' -and (Get-EngineFiles).Count -eq 0) { throw 'No effective engine INI files were found.' }
    if ($Kind -eq 'Controls' -and (Get-ControlFiles).Count -eq 0) { throw 'No supported GearInput.ini or DefaultInput.ini was found.' }
}
function New-Transaction([string]$Kind, [string[]]$Targets) {
    $id = [guid]::NewGuid().ToString('N')
    $dir = Join-Path $txRoot $id
    $snapDir = Join-Path $dir 'before'
    $null = New-Item -ItemType Directory -Force -Path $snapDir
    $entries = @()
    $i = 0
    foreach ($path in ($Targets | Select-Object -Unique)) {
        $full = [IO.Path]::GetFullPath($path)
        $exists = Test-Path -LiteralPath $full -PathType Leaf
        $entry = [ordered]@{ Path=$full; Exists=$exists; Snapshot=$null; BeforeSHA256=$null; AfterSHA256=$null; Attributes=$null }
        if ($exists) {
            $snapshot = Join-Path $snapDir (('{0:D3}.bin' -f $i)); $i++
            Copy-Item -LiteralPath $full -Destination $snapshot
            $entry.Snapshot = $snapshot; $entry.BeforeSHA256 = Get-Hash $full; $entry.Attributes = [int][IO.File]::GetAttributes($full)
        }
        $entries += [pscustomobject]$entry
    }
    $manifest = [pscustomobject]@{ Id=$id; Kind=$Kind; GameRoot=$root; CreatedUtc=[DateTime]::UtcNow.ToString('o'); Status='applying'; Entries=$entries }
    $manifestPath = Join-Path $dir 'transaction.json'
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 12), (New-Object Text.UTF8Encoding($false)))
    return $manifest
}
function Save-Transaction($Transaction) {
    $path = Join-Path (Join-Path $txRoot $Transaction.Id) 'transaction.json'
    [IO.File]::WriteAllText($path, ($Transaction | ConvertTo-Json -Depth 12), (New-Object Text.UTF8Encoding($false)))
}
function Restore-Snapshot($Transaction) {
    foreach ($entry in $Transaction.Entries) {
        if ($entry.Exists) {
            if (-not (Test-Path -LiteralPath $entry.Snapshot -PathType Leaf)) { throw "Rollback snapshot is missing: $($entry.Snapshot)" }
            $bytes = [IO.File]::ReadAllBytes([string]$entry.Snapshot)
            if ((Get-Hash ([string]$entry.Snapshot)) -ne [string]$entry.BeforeSHA256) { throw "Rollback snapshot hash mismatch: $($entry.Snapshot)" }
            $parent = Split-Path -Parent ([string]$entry.Path); $null = New-Item -ItemType Directory -Force -Path $parent
            if ((Test-Path -LiteralPath $entry.Path -PathType Leaf) -and ((Get-Hash ([string]$entry.Path)) -eq [string]$entry.BeforeSHA256)) { [IO.File]::SetAttributes([string]$entry.Path, [IO.FileAttributes][int]$entry.Attributes); continue }
            if ((Test-Path -LiteralPath $entry.Path -PathType Leaf) -and (([IO.File]::GetAttributes([string]$entry.Path) -band [IO.FileAttributes]::ReadOnly) -ne 0)) { [IO.File]::SetAttributes([string]$entry.Path, ([IO.File]::GetAttributes([string]$entry.Path) -bxor [IO.FileAttributes]::ReadOnly)) }
            Write-Atomic ([string]$entry.Path) $bytes
            [IO.File]::SetAttributes([string]$entry.Path, [IO.FileAttributes][int]$entry.Attributes)
            if ((Get-Hash ([string]$entry.Path)) -ne [string]$entry.BeforeSHA256) { throw "Rollback verification failed for '$($entry.Path)'." }
        } elseif (Test-Path -LiteralPath $entry.Path -PathType Leaf) { Remove-Item -LiteralPath $entry.Path -Force }
    }
}
function Invoke-Transactional([string]$Kind, [string[]]$Targets, [scriptblock]$Body) {
    $lockPath = Join-Path $txRoot 'apply.lock'
    $lock = $null
    try {
        $lock = New-Object IO.FileStream($lockPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        $transaction = New-Transaction $Kind $Targets
        try {
            & $Body $transaction
            foreach ($entry in $transaction.Entries) { if (Test-Path -LiteralPath $entry.Path -PathType Leaf) { $entry.AfterSHA256 = Get-Hash $entry.Path } }
            $transaction.Status = 'committed'; $transaction | Add-Member -NotePropertyName CompletedUtc -NotePropertyValue ([DateTime]::UtcNow.ToString('o')) -Force
            Save-Transaction $transaction
            return $transaction
        } catch {
            $failure = $_
            try { Restore-Snapshot $transaction; $transaction.Status = 'rolledback'; $transaction | Add-Member -NotePropertyName Failure -NotePropertyValue ([string]$failure) -Force; Save-Transaction $transaction }
            catch { throw "Apply failed ('$failure') and automatic rollback also failed ('$($_.Exception.Message)'). Transaction: $($transaction.Id)" }
            throw "Apply failed and was rolled back: $failure"
        }
    } catch { throw }
    finally { if ($null -ne $lock) { $lock.Dispose() } }
}
function Apply-GraphicsSettings($Settings) {
    $Settings = Add-AudioDefaults $Settings
    Validate-Graphics $Settings
    Assert-Preflight 'Graphics'
    $engineFiles = Get-EngineFiles; $cameraFiles = Get-CameraFiles; $launchFiles = Get-LaunchFiles; $pawnFiles = Get-PawnFiles
    $targets = @($graphicsPath) + $engineFiles + $cameraFiles + $launchFiles + $pawnFiles + @($audioExecPath)
    $transaction = Invoke-Transactional 'Graphics' $targets {
        param($tx)
        $res = Get-Resolution $Settings; $rx=[int]$res[0]; $ry=[int]$res[1]
        $fov = if ([int]$Settings.Fov -ge 50) { [int]$Settings.Fov } elseif ($rx / $ry -ge 2.0) { 65 } else { 80 }
        $fs = if ($Settings.Fullscreen) { 'True' } else { 'False' }; $vs=if ($Settings.VSync) { 'True' } else { 'False' }
        $effectKeys = [ordered]@{ MotionBlur=@('MotionBlur','MotionBlurPause'); DepthOfField=@('DepthOfField'); Bloom=@('Bloom'); LensFlares=@('LensFlares'); LightShafts=@('bAllowLightShafts'); Distortion=@('Distortion','FilteredDistortion'); RadialBlur=@('AllowRadialBlur'); Subsurface=@('AllowSubsurfaceScattering'); SeparateTranslucency=@('bAllowSeparateTranslucency') }
        foreach ($path in $engineFiles) {
            $text=[IO.File]::ReadAllText($path); $new=$text
            $new=[regex]::Replace($new,'(?m)^ResX=[^\r\n]*',"ResX=$rx"); $new=[regex]::Replace($new,'(?m)^ResY=[^\r\n]*',"ResY=$ry")
            $axis=if ($rx/$ry -ge 2.0) {'AspectRatio_MaintainYFOV'} else {'AspectRatio_MaintainXFOV'}
            $new=[regex]::Replace($new,'(?m)^AspectRatioAxisConstraint=[^\r\n]*',"AspectRatioAxisConstraint=$axis")
            $new=[regex]::Replace($new,'(?m)^Fullscreen=[^\r\n]*',"Fullscreen=$fs"); $new=[regex]::Replace($new,'(?m)^UseVsync=[^\r\n]*',"UseVsync=$vs")
            $new=Set-IniSectionValue $new 'SystemSettings' 'OneFrameThreadLag' 'True'
            $new=Set-IniSectionValue $new 'WinDrv.WindowsClient' 'MinAllowableRefreshRate' ([string]$Settings.RefreshRate)
            $new=Set-IniSectionValue $new 'WinDrv.WindowsClient' 'MaxAllowableRefreshRate' ([string]$Settings.RefreshRate)
            if ([int]$Settings.FpsLimit -gt 0) {
                $new=Set-IniSectionValue $new 'Engine.Engine' 'bSmoothFrameRate' 'True'; $new=Set-IniSectionValue $new 'Engine.Engine' 'MinSmoothedFrameRate' '0'; $new=Set-IniSectionValue $new 'Engine.Engine' 'MaxSmoothedFrameRate' ([string]$Settings.FpsLimit)
                $new=Set-IniSectionValue $new 'Engine.GameEngine' 'bSmoothFrameRate' 'True'; $new=Set-IniSectionValue $new 'Engine.GameEngine' 'MaxSmoothedFrameRate' ([string]$Settings.FpsLimit)
            } else {
                $new=Set-IniSectionValue $new 'Engine.Engine' 'bSmoothFrameRate' 'False'; $new=Set-IniSectionValue $new 'Engine.Engine' 'MinSmoothedFrameRate' '0'; $new=Set-IniSectionValue $new 'Engine.Engine' 'MaxSmoothedFrameRate' '300'
                $new=Set-IniSectionValue $new 'Engine.GameEngine' 'bSmoothFrameRate' 'False'; $new=Set-IniSectionValue $new 'Engine.GameEngine' 'MaxSmoothedFrameRate' '300'
            }
            $gq=[string]$Settings.GraphicsQuality
            $quality=@{low=@('0','0.800000','1','1');medium=@('1','1.000000','0','0');high=@('2','1.000000','0','0');ultra=@('2','1.250000','-1','-1')}[$gq]
            foreach ($pair in @(@('DetailMode',$quality[0]),@('MaxDrawDistanceScale',$quality[1]),@('SkeletalMeshLODBias',$quality[2]),@('StaticMeshLODBias',$quality[3]),@('bUseMaxQualityMode','False'))) { $new=Set-IniSectionValue $new 'SystemSettings' $pair[0] $pair[1] }
            $sq=[string]$Settings.ShadowQuality
            $shadow=@{low=@('32','512','512','-1','False');medium=@('64','1024','1024','0','True');high=@('64','1120','1344','0','True');ultra=@('128','2048','2048','1','True')}[$sq]
            foreach ($pair in @(@('DynamicShadows','True'),@('LightEnvironmentShadows','True'),@('MinShadowResolution',$shadow[0]),@('MaxShadowResolution',$shadow[1]),@('MaxWholeSceneDominantShadowResolution',$shadow[2]),@('ShadowFilterQualityBias',$shadow[3]),@('bAllowWholeSceneDominantShadows',$shadow[4]))) { $new=Set-IniSectionValue $new 'SystemSettings' $pair[0] $pair[1] }
            $lod=switch ([string]$Settings.TextureQuality) {'low'{'2'} 'medium'{'1'} default{'0'}}
            foreach ($group in @('TEXTUREGROUP_World','TEXTUREGROUP_WorldNormalMap','TEXTUREGROUP_Character','TEXTUREGROUP_CharacterNormalMap','TEXTUREGROUP_Weapon','TEXTUREGROUP_WeaponNormalMap','TEXTUREGROUP_Vehicle','TEXTUREGROUP_VehicleNormalMap')) { $new=[regex]::Replace($new,'(?m)^('+[regex]::Escape($group)+'=\([^\r\n]*LODBias=)-?\d+([^\r\n]*\))',[Text.RegularExpressions.MatchEvaluator]{param($m)$m.Groups[1].Value+$lod+$m.Groups[2].Value}) }
            $new=Set-IniSectionValue $new 'SystemSettings' 'SpeedTreeLeaves' $(if ($Settings.FoliageQuality -eq 'low') {'False'} else {'True'})
            $new=Set-IniSectionValue $new 'SystemSettings' 'SpeedTreeFronds' $(if ($Settings.FoliageQuality -in @('high','ultra')) {'True'} else {'False'})
            $new=Set-IniSectionValue $new 'SystemSettings' 'MaxAnisotropy' ([string]$Settings.Anisotropy)
            switch ([string]$Settings.AntiAliasing) {
                'fxaa' { $taa='True';$msaa='False';$samples='1' }
                'msaa2x' { $taa='False';$msaa='True';$samples='2' }
                'msaa4x' { $taa='False';$msaa='True';$samples='4' }
                default { $taa='False';$msaa='False';$samples='1' }
            }
            $new=Set-IniSectionValue $new 'SystemSettings' 'bAllowTemporalAA' $taa; $new=Set-IniSectionValue $new 'SystemSettings' 'bAllowD3D9MSAA' $msaa; $new=Set-IniSectionValue $new 'SystemSettings' 'MaxMultiSamples' $samples
            $new=Set-IniSectionValue $new 'SystemSettings' 'AmbientOcclusion' ([string]$Settings.AmbientOcclusion)
            foreach ($effect in $effectKeys.Keys) { $enabled=if (@($Settings.Disabled) -contains $effect) {'False'} else {'True'}; foreach ($key in $effectKeys[$effect]) { $new=Set-IniSectionValue $new 'SystemSettings' $key $enabled } }
            $hdd=([string]$Settings.DriveType -eq 'hdd')
            $new=Set-IniSectionValue $new 'SystemSettings' 'OnlyStreamInTextures' $(if($hdd){'True'}else{'False'}); $new=Set-IniSectionValue $new 'Engine.Engine' 'bEnableMipLevelFading' $(if($hdd){'True'}else{'False'})
            $streamingValues = @(
                @('MinRequestedMipsToConsider', $(if ($hdd) { '1' } else { '11' })),
                @('MinTimeToGuaranteeMinMipCount', $(if ($hdd) { '12' } else { '2' })),
                @('MaxTimeToGuaranteeMinMipCount', $(if ($hdd) { '30' } else { '12' })),
                @('LoadMapTimeLimit', $(if ($hdd) { '60.0' } else { '20.0' })),
                @('DropMipLevelsLimit', $(if ($hdd) { '24' } else { '16' })),
                @('StopIncreasingLimit', $(if ($hdd) { '16' } else { '12' })),
                @('StopStreamingLimit', $(if ($hdd) { '12' } else { '8' }))
            )
            foreach ($pair in $streamingValues) { $new=Set-IniSectionValue $new 'TextureStreaming' $pair[0] $pair[1] }
            # Audio channels (simultaneous sounds; the game ships 32), as the Multi Patch Tool writes them.
            foreach ($sec in 'XAudio2.XAudio2Device','ALAudio.ALAudioDevice') { $new=Set-IniSectionValue $new $sec 'MaxChannels' ([string]$Settings.AudioChannels) }
            if ($new -ne $text) { Write-Atomic $path ([Text.Encoding]::UTF8.GetBytes($new)) }
        }
        # Run camera shake: 0 removes it; the game values are 0.6 (everyone) and 0.45 (armored Kantus). Only sections that exist.
        foreach ($path in $pawnFiles) {
            $text=[IO.File]::ReadAllText($path); $new=$text
            foreach ($sec in $RunShakeStock.Keys) {
                if ($new -match ('(?m)^\[' + [regex]::Escape($sec) + '\]\r?$')) { $new=Set-IniSectionValue $new $sec 'RoadieRunShakyCamDampeningFactor' $(if ($Settings.DisableRunShake) { '0.0f' } else { $RunShakeStock[$sec] }) }
            }
            if ($new -ne $text) { Write-Atomic $path ([Text.Encoding]::UTF8.GetBytes($new)) }
        }
        # Audio filter off: the engine runs Binaries\Win64\Gears3_Audio.txt once at startup (-exec=); DisableLPF removes the muffled low-pass filter.
        if (-not $Settings.AudioFilter) { Write-Atomic $audioExecPath ([Text.Encoding]::ASCII.GetBytes("DisableLPF`r`n")) }
        elseif (Test-Path -LiteralPath $audioExecPath) { Remove-Item -LiteralPath $audioExecPath -Force }
        $iconScale=if ($rx/$ry -gt 2.0) {'0.68f'} elseif ($rx/$ry -gt 1.8) {'0.85f'} else {'1.00f'}
        foreach ($path in $cameraFiles) {
            $text=[IO.File]::ReadAllText($path); $new=[regex]::Replace($text,'(?m)^BaseFOV=[^\r\n]*',"BaseFOV=$fov")
            if ($Settings.FovAllModes) { $new=[regex]::Replace($new,'(?m)^FOVAngle=[^\r\n]*',"FOVAngle=$fov") }
            else { $new=Set-IniSectionValue $new 'GearGame.GameplayCam_Default' 'FOVAngle' ([string]$fov) }
            $new=[regex]::Replace($new,'(?m)^WeaponIconScale=[^\r\n]*',"WeaponIconScale=$iconScale")
            if ($new -ne $text) { Write-Atomic $path ([Text.Encoding]::UTF8.GetBytes($new)) }
        }
        $startupArgs = "$(if($Settings.Fullscreen){'-FULLSCREEN'}else{'-WINDOWED'}) $(if($Settings.VSync){'-VSYNC'}else{'-NOVSYNC'}) -ResX=$rx -ResY=$ry -language=$($Settings.Language)$(if (-not $Settings.AudioFilter) { ' -exec=Gears3_Audio.txt' })"
        foreach ($path in $launchFiles) {
            $text=[IO.File]::ReadAllText($path); $new=$text
            if ($text -match '(?m)^rem GearGame\.exe ') { $argPattern='(?m)(?<=rem GearGame\.exe )-(?:FULLSCREEN|WINDOWED)[^\r\n]*' }
            elseif ((Split-Path -Leaf $path) -eq 'Gears3_Start_Game.bat') { $argPattern='(?m)(?<=GearGame\.exe )-(?:FULLSCREEN|WINDOWED)[^\r\n]*' }
            elseif ((Split-Path -Leaf $path) -eq 'StartCampaign.bat') { $argPattern='(?m)(?<=^GearGame\.exe )-(?:FULLSCREEN|WINDOWED)[^\r\n]*' }
            else { $argPattern='(?m)(?<=^set "ARGS=)[^"\r\n]*(?=")' }
            if (-not [regex]::IsMatch($text,$argPattern)) { throw "Could not locate the Multipatch startup arguments in '$path'." }
            $new=[regex]::Replace($text,$argPattern,[string]$startupArgs)
            if ($new -ne $text) { Write-Atomic $path ([Text.Encoding]::UTF8.GetBytes($new)) }
        }
        $json = Read-JsonObject $graphicsPath
        foreach ($p in $Settings.PSObject.Properties) { Set-JsonProperty $json $p.Name $p.Value }
        Write-Atomic $graphicsPath ([Text.Encoding]::UTF8.GetBytes(($json | ConvertTo-Json -Depth 20)))
        $effective=Get-EffectiveGraphicsState $json
        if(-not $effective.IsApplied){throw 'Post-apply verification found configuration files that do not match the saved settings.'}
    }
    $checks = @()
    foreach ($path in $engineFiles) {
        $text=[IO.File]::ReadAllText($path)
        $checks += [pscustomobject]@{ File=$path; Resolution="$(Get-SectionValue $text 'SystemSettings' 'ResX')x$(Get-SectionValue $text 'SystemSettings' 'ResY')"; Fps=[string](Get-SectionValue $text 'Engine.Engine' 'MaxSmoothedFrameRate'); VSync=Get-SectionValue $text 'SystemSettings' 'UseVsync'; Samples=Get-SectionValue $text 'SystemSettings' 'MaxMultiSamples'; Anisotropy=Get-SectionValue $text 'SystemSettings' 'MaxAnisotropy'; AmbientOcclusion=Get-SectionValue $text 'SystemSettings' 'AmbientOcclusion' }
    }
    $effective=Get-EffectiveGraphicsState (Read-JsonObject $graphicsPath)
    return [pscustomobject]@{ Success=$true; TransactionId=$transaction.Id; Status='Applied and file values verified; restart game if it was already open.'; Applied=$effective }
}

$KbActions = @(
    @('GBA_MoveForward','Move forward'),@('GBA_MoveBackward','Move backward'),@('GBA_StrafeLeft','Strafe left'),@('GBA_StrafeRight','Strafe right'),
    @('GBA_MoveAction','Cover / Roll / Sprint / Vault'),@('GBA_Crouch','Crouch / Switch shoulder'),@('GBA_Walk','Walk'),@('GBA_Shoot','Fire'),@('GBA_Target','Aim'),@('GBA_Melee','Melee / Execute'),@('GBA_Reload','Reload / Active Reload'),@('GBA_Zoom','Zoom / Scope'),@('GBA_Pickup','Pick up / Swap / Revive'),@('GBA_POI','Point of interest / Ping'),@('GBA_Taccom','Squad commands'),@('GBA_Weap1','Weapon slot 1'),@('GBA_Weap2','Weapon slot 2'),@('GBA_Pistols','Pistol'),@('GBA_Grenades','Grenade'),@('GBA_PrevWeapon','Previous weapon'),@('GBA_NextWeapon','Next weapon'),@('GBA_Scoreboard','Scoreboard')
)
$KbDefaults = @{ GBA_MoveForward='W';GBA_MoveBackward='S';GBA_StrafeLeft='A';GBA_StrafeRight='D';GBA_MoveAction='SpaceBar';GBA_Crouch='C';GBA_Walk='LeftControl';GBA_Shoot='LeftMouseButton';GBA_Target='RightMouseButton';GBA_Melee='F';GBA_Reload='R';GBA_Zoom='T';GBA_Pickup='E';GBA_POI='Q';GBA_Taccom='H';GBA_Weap1='One';GBA_Weap2='Two';GBA_Pistols='Three';GBA_Grenades='Four';GBA_PrevWeapon='MouseScrollUp';GBA_NextWeapon='MouseScrollDown';GBA_Scoreboard='Slash' }
$KbKeys = @([char[]](65..90) | ForEach-Object {[string]$_}) + @('Zero','One','Two','Three','Four','Five','Six','Seven','Eight','Nine') + @(1..12 | ForEach-Object {"F$_"}) + @('LeftShift','RightShift','LeftControl','RightControl','LeftAlt','RightAlt','SpaceBar','Tab','CapsLock','Enter','BackSpace','Escape','LeftMouseButton','RightMouseButton','MiddleMouseButton','ThumbMouseButton','ThumbMouseButton2','MouseScrollUp','MouseScrollDown','Up','Down','Left','Right','Home','End','PageUp','PageDown','Insert','Delete','Semicolon','Equals','Comma','Underscore','Period','Slash','Tilde','LeftBracket','RightBracket','Backslash','Quote') + @(0..9 | ForEach-Object {"NumPad$_"})
$KbLineRegex = '(?m)^([ \t]*[.+]?Bindings=\(Name=")([^"]+)(",Command="(GBA_\w+)[^"]*"\)[^\r\n]*)\r?$'
function Get-BindingMap([string]$Text) {
    $map=@{}
    foreach ($m in [regex]::Matches($Text,$KbLineRegex)) { $key=$m.Groups[2].Value; $cmd=$m.Groups[4].Value; if($key -match '^(XboxTypeS_|Gamepad)'){continue}; if(-not $map.ContainsKey($cmd)){$map[$cmd]=@()}; $map[$cmd]+= $key }
    return $map
}
function Set-Bindings([string]$Text,$Wanted) {
    $seen=@{}
    return [regex]::Replace($Text,$KbLineRegex,[Text.RegularExpressions.MatchEvaluator]{param($m)$key=$m.Groups[2].Value;$cmd=$m.Groups[4].Value;if($key -match '^(XboxTypeS_|Gamepad)'){return $m.Value};if($seen.ContainsKey($cmd)){return $m.Value};$seen[$cmd]=$true;if($Wanted.Contains($cmd)){$cr=if($m.Value.EndsWith("`r")){"`r"}else{''};return $m.Groups[1].Value+$Wanted[$cmd]+$m.Groups[3].Value+$cr};return $m.Value})
}
function Get-Controls {
    Assert-Preflight 'Controls'; $files=Get-ControlFiles; $active=Get-BindingMap ([IO.File]::ReadAllText($files[0]))
    $saved=Read-JsonObject $keybindsPath -Optional; $rows=@()
    foreach($a in $KbActions){$cmd=$a[0];$key=if($active.ContainsKey($cmd) -and $active[$cmd].Count){[string]$active[$cmd][0]}elseif($saved.PSObject.Properties[$cmd]){[string]$saved.$cmd}else{$KbDefaults[$cmd]};$rows += [pscustomobject]@{Command=$cmd;Action=$a[1];Key=$key;Default=$KbDefaults[$cmd]}}
    return [pscustomobject]@{Success=$true;Source=$files[0];MouseSensitivity=(Get-SectionValue ([IO.File]::ReadAllText($files[0])) 'Engine.PlayerInput' 'MouseSensitivity');Actions=$rows;SupportedKeys=$KbKeys;ReservedKeys=@('Escape','F8','F10');PauseAndEscapeEditable=$false}
}
function Apply-Controls($Request) {
    Assert-Preflight 'Controls'; $files=Get-ControlFiles
    if($Request.Bindings -isnot [System.Collections.IDictionary] -and $Request.Bindings -isnot [pscustomobject]){throw 'Bindings must be a JSON object.'}
    $sensitivity=$null
    if($Request.PSObject.Properties['MouseSensitivity'] -and $null -ne $Request.MouseSensitivity){
        $value=0.0
        if(-not [double]::TryParse([string]$Request.MouseSensitivity,[Globalization.NumberStyles]::Float,[Globalization.CultureInfo]::InvariantCulture,[ref]$value) -or [double]::IsNaN($value) -or $value -lt 1 -or $value -gt 200){throw 'Mouse sensitivity must be between 1 and 200 (Multipatch range).'}
        $sensitivity=$value.ToString('0.################',[Globalization.CultureInfo]::InvariantCulture)
    }
    $wanted=@{}; $seen=@{}
    foreach($p in $Request.Bindings.PSObject.Properties){$cmd=$p.Name;$key=[string]$p.Value;if(-not $KbDefaults.ContainsKey($cmd)){throw "Unsupported control command: $cmd"};if($key -notin $KbKeys){throw "Unsupported UE3 key name: $key"};if($key -in @('Escape','F8','F10')){throw "$key is reserved by the game or Multipatch and cannot be assigned."};if($seen.ContainsKey($key)){throw "Key conflict: $key is assigned to both $($seen[$key]) and $cmd."};$seen[$key]=$cmd;$wanted[$cmd]=$key}
    if($wanted.Count -eq 0){throw 'No control bindings were provided.'}
    $effective=Get-BindingMap ([IO.File]::ReadAllText($files[0]))
    foreach($cmd in $wanted.Keys){if(-not $effective.ContainsKey($cmd) -or $effective[$cmd].Count -eq 0){throw "Effective input file '$($files[0])' does not contain $cmd."}}
    $targets=@($keybindsPath)+$files
    $transaction=Invoke-Transactional 'Controls' $targets {
        param($tx)
        foreach($path in $files){$text=[IO.File]::ReadAllText($path);$new=Set-Bindings $text $wanted;if($null -ne $sensitivity){$new=Set-IniSectionValue $new 'Engine.PlayerInput' 'MouseSensitivity' $sensitivity};if($new -ne $text){Write-Atomic $path ([Text.Encoding]::UTF8.GetBytes($new))}}
        if($null -ne $sensitivity){foreach($path in $files){if((Get-SectionValue ([IO.File]::ReadAllText($path)) 'Engine.PlayerInput' 'MouseSensitivity') -ne $sensitivity){throw 'Mouse sensitivity verification failed.'}}}
        $old=Read-JsonObject $keybindsPath -Optional
        foreach($p in $Request.Bindings.PSObject.Properties){Set-JsonProperty $old $p.Name ([string]$p.Value)}
        $defaults=@{};foreach($cmd in $KbDefaults.Keys){$defaults[$cmd]=$KbDefaults[$cmd]}
        foreach($cmd in $KbDefaults.Keys){if($old.PSObject.Properties[$cmd] -and [string]$old.$cmd -eq $KbDefaults[$cmd]){$old.PSObject.Properties.Remove($cmd)}}
        Write-Atomic $keybindsPath ([Text.Encoding]::UTF8.GetBytes(($old | ConvertTo-Json -Depth 10)))
        foreach($path in $files){$map=Get-BindingMap ([IO.File]::ReadAllText($path));foreach($cmd in $wanted.Keys){if($map.ContainsKey($cmd) -and $map[$cmd][0] -ne $wanted[$cmd]){throw "Post-apply verification failed for $cmd in '$path'."}}}
    }
    $verify=@();foreach($path in $files){$map=Get-BindingMap ([IO.File]::ReadAllText($path));foreach($cmd in $wanted.Keys){if($map.ContainsKey($cmd) -and $map[$cmd][0] -ne $wanted[$cmd]){throw "Post-apply verification failed for $cmd in '$path'."}};$verify += [pscustomobject]@{File=$path;Verified=$true}}
    return [pscustomobject]@{Success=$true;TransactionId=$transaction.Id;Status='Bindings applied and verified in effective input files.';Files=$verify}
}
function Invoke-Rollback([string]$Id, [string]$Kind) {
    if($Id){$dirs=@((Join-Path $txRoot $Id))}
    else{$dirs=@(Get-ChildItem -LiteralPath $txRoot -Directory | ForEach-Object {$manifestPath=Join-Path $_.FullName 'transaction.json';if(Test-Path -LiteralPath $manifestPath){$m=Read-JsonObject $manifestPath;[pscustomobject]@{Created=[string]$m.CreatedUtc;Path=$_.FullName}}} | Sort-Object Created -Descending | ForEach-Object Path)}
    foreach($dir in $dirs){$manifestPath=Join-Path $dir 'transaction.json';if(-not(Test-Path -LiteralPath $manifestPath)){continue};$tx=Read-JsonObject $manifestPath;if([string]$tx.Status -ne 'committed'){continue};if([string]$tx.GameRoot -ne $root){continue};if($Kind -and [string]$tx.Kind -ne $Kind){continue}
        foreach($entry in $tx.Entries){$path=[IO.Path]::GetFullPath([string]$entry.Path);$allowed=Get-AllowedTargets ([string]$tx.Kind);if($path -notin $allowed){throw "Rollback manifest contains an out-of-scope path: $path"};if($entry.Exists){if(-not(Test-Path -LiteralPath $entry.Snapshot -PathType Leaf) -or (Get-Hash ([string]$entry.Snapshot)) -ne [string]$entry.BeforeSHA256){throw "Rollback refused because the snapshot for '$path' is missing or corrupt."};$after=[string]$entry.AfterSHA256;if($after){if(-not(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Hash $path) -ne $after){throw "Rollback refused because '$path' changed after the transaction."}}elseif(Test-Path -LiteralPath $path -PathType Leaf){throw "Rollback refused because '$path' was recreated after the transaction removed it."}}elseif((Test-Path -LiteralPath $path -PathType Leaf) -and (Get-Hash $path) -ne [string]$entry.AfterSHA256){throw "Rollback refused because '$path' changed after the transaction."}}
        Restore-Snapshot $tx;$tx.Status='rolledback';$tx | Add-Member -NotePropertyName RolledBackUtc -NotePropertyValue ([DateTime]::UtcNow.ToString('o')) -Force;Save-Transaction $tx
        return [pscustomobject]@{Success=$true;TransactionId=$tx.Id;Status='Previous state restored and hashes verified.'}
    }
    throw 'No committed settings transaction is available to roll back.'
}
function Recover-IncompleteTransactions {
    $incomplete=@(Get-ChildItem -LiteralPath $txRoot -Directory | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'transaction.json') } | ForEach-Object { $manifest=Read-JsonObject (Join-Path $_.FullName 'transaction.json'); if([string]$manifest.Status -eq 'applying' -and [string]$manifest.GameRoot -eq $root){[pscustomobject]@{Directory=$_.FullName;Manifest=$manifest}} })
    if($incomplete.Count -eq 0){return}
    $lockPath=Join-Path $txRoot 'apply.lock';$lock=New-Object IO.FileStream($lockPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try {
        foreach($item in $incomplete){$tx=$item.Manifest;$allowed=Get-AllowedTargets ([string]$tx.Kind);foreach($entry in $tx.Entries){$path=[IO.Path]::GetFullPath([string]$entry.Path);if($path -notin $allowed){throw "Incomplete transaction contains an out-of-scope path: $path"};if($entry.Exists -and (-not(Test-Path -LiteralPath $entry.Snapshot -PathType Leaf) -or (Get-Hash ([string]$entry.Snapshot)) -ne [string]$entry.BeforeSHA256)){throw "Cannot recover incomplete settings transaction $($tx.Id): a snapshot is missing or corrupt."}};Restore-Snapshot $tx;$tx.Status='rolledback';$tx | Add-Member -NotePropertyName RecoveredUtc -NotePropertyValue ([DateTime]::UtcNow.ToString('o')) -Force;Save-Transaction $tx }
    } finally {$lock.Dispose()}
}

try {
    Recover-IncompleteTransactions
    if ($Action -in @('GetGraphics','GetControls')) { Assert-Preflight $(if($Action -eq 'GetGraphics'){'Graphics'}else{'Controls'}) }
    switch($Action){
        'GetGraphics' { $g=Add-AudioDefaults (Read-JsonObject $graphicsPath); Validate-Graphics $g; $out=[pscustomobject]@{Success=$true;Graphics=$g;Applied=(Get-EffectiveGraphicsState $g)} }
        'ApplyGraphics' { if(-not $RequestPath){throw 'RequestPath is required.'};$req=Read-JsonObject $RequestPath;$g=Read-JsonObject $graphicsPath;foreach($p in $req.PSObject.Properties){Set-JsonProperty $g $p.Name $p.Value};$out=Apply-GraphicsSettings $g }
        'GetControls' { $out=Get-Controls }
        'ApplyControls' { if(-not $RequestPath){throw 'RequestPath is required.'};$out=Apply-Controls (Read-JsonObject $RequestPath) }
        'Rollback' { $out=Invoke-Rollback $TransactionId $null }
        'RollbackGraphics' { $out=Invoke-Rollback $TransactionId 'Graphics' }
        'RollbackControls' { $out=Invoke-Rollback $TransactionId 'Controls' }
    }
    $out | ConvertTo-Json -Depth 20 -Compress
    exit 0
} catch {
    [pscustomobject]@{Success=$false;Error=[string]$_.Exception.Message;Details=[string]$_} | ConvertTo-Json -Depth 5 -Compress
    exit 2
}

