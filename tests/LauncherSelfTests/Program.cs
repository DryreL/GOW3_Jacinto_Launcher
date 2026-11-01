using System.Text.Json.Nodes;
using JacintoLauncher;

var count = 0;
void Check(bool ok, string message) { count++; if (!ok) throw new Exception("FAIL: " + message); }
void Throws(Action action, string message) { count++; try { action(); } catch { return; } throw new Exception("FAIL: expected failure: " + message); }
var temp = Path.Combine(Path.GetTempPath(), "Jacinto Settings Tests " + Guid.NewGuid().ToString("N"));
var game = Path.Combine(temp, "Game With Spaces"); var docs = Path.Combine(temp, "Documents With Spaces"); var tx = Path.Combine(temp, "Transactions");
Directory.CreateDirectory(game); Directory.CreateDirectory(docs);
string Put(string relative, string text) { var p = Path.Combine(game, relative); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); return p; }
string Hash(string p) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)));
try
{
    var cfg = "GearGame\\Config";
    var graphics = Put("tools\\multipatch\\_files\\graphics.json", """
    {"Resolution":"1920x1080","Fullscreen":true,"VSync":false,"FpsLimit":60,"RefreshRate":0,"Fov":80,"FovAllModes":false,"AntiAliasing":"msaa2x","Anisotropy":8,"AmbientOcclusion":true,"GraphicsQuality":"high","ShadowQuality":"high","TextureQuality":"high","FoliageQuality":"high","DriveType":"ssd","Disabled":[],"Language":"INT","FutureField":{"Keep":true}}
    """);
    Put("tools\\multipatch\\_files\\installation-state.json", "{\"Status\":\"complete\"}");
    var journal=Path.Combine(game,"tools","multipatch","_files","installation-state.json");var journalHash=Hash(journal);
    Put("Binaries\\Win64\\GearGame.exe", "exe"); Put("GearGame\\Script\\GearGame.u", "package");
    foreach(var name in new[]{"wsock32.dll","steam_api64.dll"}) { var payload=Put("tools\\multipatch\\_files\\"+name,"payload:"+name); Put("Binaries\\Win64\\"+name,File.ReadAllText(payload)); }
    var offline = Put("Gears3_Start_Game.bat", "@echo off\r\nstart Binaries\\Win64\\GearGame.exe -FULLSCREEN -VSYNC -ResX=1920 -ResY=1080 -language=INT\r\n");
    var start = Put("Binaries\\Win64\\StartCampaign.bat", "@echo off\r\nGearGame.exe -FULLSCREEN -VSYNC -ResX=1920 -ResY=1080 -language=INT\r\n");
    Put("Binaries\\Win64\\StartCampaign_Steam.bat", "@echo off\r\nGearGame.exe -FULLSCREEN -VSYNC -ResX=1920 -ResY=1080 -language=INT\r\n");
    var engine = "[SystemSettings]\r\nResX=1920\r\nResY=1080\r\nFullscreen=True\r\nUseVsync=False\r\nMaxMultiSamples=2\r\nMaxAnisotropy=8\r\nAmbientOcclusion=True\r\n" + string.Join("\r\n",new[]{"World","WorldNormalMap","Character","CharacterNormalMap","Weapon","WeaponNormalMap","Vehicle","VehicleNormalMap"}.Select(x=>$"TEXTUREGROUP_{x}=(MinLODSize=256,MaxLODSize=1024,LODBias=0,MinMagFilter=aniso,MipFilter=Linear)"))+"\r\n[Engine.Engine]\r\nbSmoothFrameRate=True\r\nMaxSmoothedFrameRate=60\r\n[Engine.GameEngine]\r\nMaxSmoothedFrameRate=60\r\n[TextureStreaming]\r\n";
    var ini1 = Put(cfg + "\\GearEngine.ini", engine); Put(cfg + "\\DefaultEngine.ini", engine);
    var camera = Put(cfg + "\\GearCamera.ini", "BaseFOV=80\r\n[GearGame.GameplayCam_Default]\r\nFOVAngle=80\r\nWeaponIconScale=1.00f\r\n"); Put(cfg + "\\DefaultCamera.ini", "BaseFOV=80\r\n[GearGame.GameplayCam_Default]\r\nFOVAngle=80\r\nWeaponIconScale=1.00f\r\n");
    var inputRoot = Path.Combine(docs, "My Games", "Gears of War 3", "GearGame", "Config"); Directory.CreateDirectory(inputRoot);
    var inputs = new List<string>();
    var actions = new[] { "GBA_MoveForward","GBA_MoveBackward","GBA_StrafeLeft","GBA_StrafeRight","GBA_MoveAction","GBA_Crouch","GBA_Walk","GBA_Shoot","GBA_Target","GBA_Melee","GBA_Reload","GBA_Zoom","GBA_Pickup","GBA_POI","GBA_Taccom","GBA_Weap1","GBA_Weap2","GBA_Pistols","GBA_Grenades","GBA_PrevWeapon","GBA_NextWeapon","GBA_Scoreboard" };
    var keys = new[] { "W","S","A","D","SpaceBar","C","LeftControl","LeftMouseButton","RightMouseButton","F","R","T","E","Q","H","One","Two","Three","Four","MouseScrollUp","MouseScrollDown","Slash" };
    string inputText = string.Join("\r\n", actions.Select((a,i) => $"+Bindings=(Name=\"{keys[i]}\",Command=\"{a}\")")) + "\r\n+Bindings=(Name=\"XboxTypeS_A\",Command=\"GBA_Shoot\")\r\n";
    inputs.Add(Path.Combine(inputRoot,"GearInput.ini")); File.WriteAllText(inputs[0],inputText);
    inputs.Add(Put(cfg + "\\GearInput.ini",inputText)); inputs.Add(Put(cfg + "\\DefaultInput.ini",inputText));
    var savesDir = Path.Combine(game,"GearGame","SaveData"); Directory.CreateDirectory(savesDir); var save = Path.Combine(savesDir,"keep.sav"); File.WriteAllText(save,"save sentinel"); var saveHash=Hash(save);
    var package=Path.Combine(game,"GearGame","Script","GearGame.u");var packageHash=Hash(package);var steamDllHash=Hash(Path.Combine(game,"Binaries","Win64","steam_api64.dll"));
    Environment.SetEnvironmentVariable("JACINTO_SETTINGS_TRANSACTION_ROOT",tx); Environment.SetEnvironmentVariable("JACINTO_SETTINGS_DOCUMENTS",docs); Environment.SetEnvironmentVariable("JACINTO_SETTINGS_SCRIPT",Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..","..","tools","MultipatchSettings.ps1")));

    // ---- single installation: layout + network mode ----
    var listenUrl = Convert.FromHexString("1f3f6c6f6164636865636b706f696e7400160ed0e9ffff614200d0a500001f3f6c697374656e0016");
    byte[] Package(int urls) { var body = new List<byte>(Enumerable.Repeat((byte)0x41, 64)); for (var i = 0; i < urls; i++) { body.AddRange(listenUrl); body.AddRange(Enumerable.Repeat((byte)0x42, 32)); } body.AddRange(System.Text.Encoding.ASCII.GetBytes("?listen elsewhere\0")); return body.ToArray(); }
    File.WriteAllBytes(package, Package(1)); packageHash = Hash(package);
    var bin = Path.Combine(game, "Binaries", "Win64"); var parked = NetworkMode.ParkingDir(game);
    var launcherDir = Path.Combine(game, "JacintoLauncher", "Launcher"); Directory.CreateDirectory(launcherDir);
    Check(GameLayout.ResolveGameDir(launcherDir) == game, "game detected from JacintoLauncher\\Launcher");
    Check(GameLayout.ToolFilesDir(game) == Path.Combine(game, "tools", "multipatch", "_files"), "full-install Multipatch data folder");
    Check(NetworkMode.Read(game).Describe() == "Steam", "initial state is Steam (listen + JacintoNet)");
    Check(MultipatchBridge.ValidateLaunch(game, NetworkMode.Steam, false).EndsWith("Gears3_Start_Game.bat"), "Steam preflight without running client");
    Check(MultipatchBridge.ValidateLaunch(game, NetworkMode.Offline).EndsWith("Gears3_Start_Game.bat"), "Offline preflight on the same installation");
    NetworkMode.Apply(game, NetworkMode.Offline);
    Check(NetworkMode.Read(game).Describe() == "Offline", "Offline: nolist and no JacintoNet DLLs");
    Check(!File.Exists(Path.Combine(bin, "wsock32.dll")) && !File.Exists(Path.Combine(bin, "steam_api64.dll")), "JacintoNet DLLs removed from Binaries for Offline");
    Check(Hash(Path.Combine(parked, "steam_api64.dll")) == steamDllHash, "JacintoNet DLLs parked, not deleted");
    var offlineBytes = File.ReadAllBytes(package); var steamBytes = Package(1);
    Check(offlineBytes.Length == steamBytes.Length && offlineBytes.Zip(steamBytes).Count(x => x.First != x.Second) == 6, "Offline changes exactly the 6 Campaign URL bytes");
    Check(System.Text.Encoding.ASCII.GetString(offlineBytes).Contains("?listen elsewhere"), "other ?listen strings are untouched");
    NetworkMode.Apply(game, NetworkMode.Offline); Check(NetworkMode.Read(game).Describe() == "Offline", "Offline is idempotent");
    NetworkMode.Apply(game, NetworkMode.Steam);
    Check(Hash(package) == packageHash, "Steam restores GearGame.u byte-for-byte");
    Check(Hash(Path.Combine(bin, "steam_api64.dll")) == steamDllHash, "Steam restores the JacintoNet DLLs");
    NetworkMode.Apply(game, NetworkMode.Steam); Check(NetworkMode.Read(game).Describe() == "Steam", "Steam is idempotent");
    NetworkMode.Apply(game, NetworkMode.Offline); File.WriteAllText(Path.Combine(bin, "wsock32.dll"), "foreign proxy");
    Throws(() => NetworkMode.Apply(game, NetworkMode.Offline), "Offline refuses a wsock32.dll that differs from the parked copy");
    Check(File.ReadAllText(Path.Combine(bin, "wsock32.dll")) == "foreign proxy", "foreign wsock32.dll left untouched");
    File.Delete(Path.Combine(bin, "wsock32.dll")); NetworkMode.Apply(game, NetworkMode.Steam);
    Check(NetworkMode.Read(game).Describe() == "Steam" && Hash(package) == packageHash, "recovery to Steam after a refused switch");
    var noSource = Path.Combine(temp, "No JacintoNet"); CopyDirectory(game, noSource);
    foreach (var name in NetworkMode.JacintoNetFiles) { File.Delete(Path.Combine(noSource, "Binaries", "Win64", name)); File.Delete(Path.Combine(noSource, "tools", "multipatch", "_files", name)); }
    Directory.Delete(NetworkMode.ParkingDir(noSource), true);
    Throws(() => MultipatchBridge.ValidateLaunch(noSource, NetworkMode.Steam, false), "Steam unavailable without any JacintoNet source");
    Check(MultipatchBridge.ValidateLaunch(noSource, NetworkMode.Offline).EndsWith("Gears3_Start_Game.bat"), "Offline still available without JacintoNet files");
    var duplicate = Path.Combine(temp, "Duplicate URL"); CopyDirectory(game, duplicate); File.WriteAllBytes(Path.Combine(duplicate, "GearGame", "Script", "GearGame.u"), Package(2));
    Throws(() => NetworkMode.Read(duplicate), "Campaign URL must be found exactly once");
    File.WriteAllBytes(Path.Combine(duplicate, "GearGame", "Script", "GearGame.u"), Package(0));
    Throws(() => NetworkMode.Apply(duplicate, NetworkMode.Offline), "unsupported GearGame.u is refused");
    var dryrel = Path.Combine(temp, "DryreL Game"); CopyDirectory(game, dryrel); Directory.Delete(NetworkMode.ParkingDir(dryrel), true);
    Directory.Move(Path.Combine(dryrel, "tools", "multipatch"), Path.Combine(dryrel, "Gears3_Multipatch_Tool"));
    Check(GameLayout.ToolFilesDir(dryrel) == Path.Combine(dryrel, "Gears3_Multipatch_Tool", "_files"), "existing DryreL layout detected");
    NetworkMode.Apply(dryrel, NetworkMode.Offline); NetworkMode.Apply(dryrel, NetworkMode.Steam);
    Check(Hash(Path.Combine(dryrel, "GearGame", "Script", "GearGame.u")) == packageHash, "mode switch round-trip on DryreL layout");
    var batDir = Path.Combine(temp, "Bat"); Directory.CreateDirectory(batDir);
    File.WriteAllText(Path.Combine(batDir, "Gears3_Start_Game.bat"), "@echo off\r\ncd /d \"%~dp0Binaries\\Win64\"\r\nstart \"\" GearGame.exe -WINDOWED -NOVSYNC -ResX=2560 -ResY=1440 -language=INT -exec=Gears3_Audio.txt\r\n:wait\r\n");
    Check(MultipatchBridge.LaunchArguments(batDir) == "-WINDOWED -NOVSYNC -ResX=2560 -ResY=1440 -language=INT -exec=Gears3_Audio.txt", "Multipatch start arguments parsed (audio filter off)");
    File.WriteAllText(Path.Combine(batDir, "Gears3_Start_Game.bat"), "start \"\" GearGame.exe -FULLSCREEN -NOVSYNC -ResX=1 -ResY=1 -language=INT & calc.exe\r\n");
    Throws(() => MultipatchBridge.LaunchArguments(batDir), "unexpected start arguments are refused");
    var modeConfig = new LauncherConfig { PlayerName = "Marcus" }; modeConfig.Save(temp);
    Check(LauncherConfig.Load(temp)?.PlayerName == "Marcus", "launcher config persists");
    var saveResult=MultipatchBridge.BackupAllSaves(game,Path.Combine(temp,"Backups")); Check(saveResult.Files==1,"save management backup");
    var profileDir=Path.Combine(game,"Binaries","GearGame","SaveData"); Directory.CreateDirectory(profileDir); File.WriteAllText(Path.Combine(profileDir,"CHANGE ME.sav"),"profile sentinel");
    var withProfile=MultipatchBridge.BackupAllSaves(game,Path.Combine(temp,"Backups"));
    Check(withProfile.Files==2 && File.ReadAllText(Path.Combine(withProfile.Directory,"Profile","CHANGE ME.sav"))=="profile sentinel","profile (Binaries\\GearGame\\SaveData) is backed up too");
    Directory.Delete(Path.Combine(game,"Binaries","GearGame"),true);
    var current=MultipatchBridge.LoadSettingsFromMultipatch(game); Check(current["Graphics"]?["FutureField"]?["Keep"]?.GetValue<bool>()==true,"unknown graphics JSON field retained");
    var beforeIni=Hash(ini1); var beforeCam=Hash(camera); var beforeStart=Hash(start);
    var request=JsonNode.Parse("""{"Resolution":"2560x1440","Fullscreen":true,"VSync":true,"FpsLimit":240,"RefreshRate":0,"Fov":100,"FovAllModes":false,"AntiAliasing":"msaa4x","Anisotropy":16,"AmbientOcclusion":true,"GraphicsQuality":"ultra","ShadowQuality":"high","TextureQuality":"high","FoliageQuality":"high","DriveType":"ssd","Disabled":[],"Language":"INT"}""")!.AsObject();
    var applied=MultipatchBridge.ApplyGraphicsThroughMultipatch(game,request); Check(applied["Applied"]?["IsApplied"]?.GetValue<bool>()==true,"effective graphics verified"); Check(applied["Applied"]?["Files"]?.AsArray().Any(x=>x?["TextureGroupsChecked"]?.GetValue<int>()>=8)==true,"texture quality verified in engine groups");
    Check(File.ReadAllText(ini1).Contains("ResX=2560")&&File.ReadAllText(ini1).Contains("MaxSmoothedFrameRate=240")&&File.ReadAllText(ini1).Contains("MaxMultiSamples=4")&&File.ReadAllText(ini1).Contains("MaxAnisotropy=16"),"effective INI values");
    Check(File.ReadAllText(camera).Contains("BaseFOV=100"),"effective FOV"); Check(File.ReadAllText(start).Contains("-ResX=2560 -ResY=1440"),"effective launch resolution args");
    Check(JsonNode.Parse(File.ReadAllText(graphics))!["FutureField"]!["Keep"]!.GetValue<bool>(),"unknown field preserved after save");
    var after1=Hash(ini1); var repeat=MultipatchBridge.ApplyGraphicsThroughMultipatch(game,request); Check(Hash(ini1)==after1,"idempotent repeat apply");
    Check(File.ReadAllText(ini1).Split("MaxAnisotropy=").Length-1==1,"no duplicate INI key");
    var bad=JsonNode.Parse(request.ToJsonString())!.AsObject(); bad["AntiAliasing"]="msaa8x"; Throws(()=>MultipatchBridge.ApplyGraphicsThroughMultipatch(game,bad),"reject unsupported MSAA 8x"); Check(Hash(ini1)==after1,"invalid setting leaves files unchanged");
    var lockFile=new FileStream(start,FileMode.Open,FileAccess.ReadWrite,FileShare.None); Throws(()=>MultipatchBridge.ApplyGraphicsThroughMultipatch(game,request),"locked target triggers failure"); lockFile.Dispose(); Check(Hash(ini1)==after1,"failed transaction rolls back prior writes");
    var rolled=MultipatchBridge.RollbackSettingsThroughMultipatch(game,repeat["TransactionId"]!.GetValue<string>()); Check(rolled["Success"]!.GetValue<bool>(),"latest graphics rollback");
    rolled=MultipatchBridge.RollbackSettingsThroughMultipatch(game,applied["TransactionId"]!.GetValue<string>()); Check(rolled["Success"]!.GetValue<bool>(),"original graphics rollback");
    Check(Hash(ini1)==beforeIni,"graphics rollback restored engine INI ("+Hash(ini1)+" vs "+beforeIni+")"); Check(Hash(camera)==beforeCam,"graphics rollback restored camera INI"); Check(Hash(start)==beforeStart,"graphics rollback restored launch script");
    // Audio / camera options (Multi Patch Tool Game Settings): filter command file + -exec, MaxChannels, run shake.
    var pawn=Put(cfg+"\\DefaultPawn.ini","[GearGame.GearPawn]\r\nRoadieRunShakyCamDampeningFactor=0.6f\r\n[Other]\r\nX=1\r\n"); var pawnBefore=Hash(pawn); var audioExec=Path.Combine(game,"Binaries","Win64","Gears3_Audio.txt");
    var audioReq=JsonNode.Parse(request.ToJsonString())!.AsObject(); audioReq["AudioFilter"]=false; audioReq["AudioChannels"]=64; audioReq["DisableRunShake"]=false;
    var audioTx=MultipatchBridge.ApplyGraphicsThroughMultipatch(game,audioReq); Check(audioTx["Applied"]?["IsApplied"]?.GetValue<bool>()==true,"audio settings verified");
    Check(File.ReadAllText(audioExec).Trim()=="DisableLPF","audio filter off writes DisableLPF");
    Check(File.ReadAllText(start).Contains("-language=INT -exec=Gears3_Audio.txt"),"audio filter off adds -exec to the start arguments");
    Check(File.ReadAllText(ini1).Contains("[XAudio2.XAudio2Device]")&&File.ReadAllText(ini1).Contains("MaxChannels=64"),"audio channels written");
    Check(File.ReadAllText(pawn).Contains("RoadieRunShakyCamDampeningFactor=0.6f"),"run shake on keeps the game value");
    var badAudio=JsonNode.Parse(audioReq.ToJsonString())!.AsObject(); badAudio["AudioChannels"]=48; Throws(()=>MultipatchBridge.ApplyGraphicsThroughMultipatch(game,badAudio),"reject unsupported audio channel count");
    var audioOn=JsonNode.Parse(audioReq.ToJsonString())!.AsObject(); audioOn["AudioFilter"]=true; audioOn["DisableRunShake"]=true;
    var audioTx2=MultipatchBridge.ApplyGraphicsThroughMultipatch(game,audioOn);
    Check(!File.Exists(audioExec)&&!File.ReadAllText(start).Contains("-exec="),"audio filter on removes the command file and -exec");
    Check(File.ReadAllText(pawn).Contains("RoadieRunShakyCamDampeningFactor=0.0f")&&File.ReadAllText(pawn).Contains("[Other]\r\nX=1"),"run shake off writes 0.0f only in its section");
    MultipatchBridge.RollbackSettingsThroughMultipatch(game,audioTx2["TransactionId"]!.GetValue<string>()); Check(File.ReadAllText(audioExec).Trim()=="DisableLPF","rollback restores the audio command file");
    MultipatchBridge.RollbackSettingsThroughMultipatch(game,audioTx["TransactionId"]!.GetValue<string>()); Check(!File.Exists(audioExec)&&Hash(pawn)==pawnBefore,"rollback removes the created command file and restores the pawn INI");
    var missingBackupTx=MultipatchBridge.ApplyGraphicsThroughMultipatch(game,request);var missingId=missingBackupTx["TransactionId"]!.GetValue<string>();var missingManifestPath=Path.Combine(tx,missingId,"transaction.json");var missingManifest=JsonNode.Parse(File.ReadAllText(missingManifestPath))!.AsObject();var lastEntry=missingManifest["Entries"]!.AsArray().Last(e=>e?["Snapshot"] is JsonValue)!.AsObject();var currentBeforeRejectedRollback=Hash(ini1);File.Delete(lastEntry["Snapshot"]!.GetValue<string>());Throws(()=>MultipatchBridge.RollbackSettingsThroughMultipatch(game,missingId),"missing rollback snapshot");Check(Hash(ini1)==currentBeforeRejectedRollback,"missing snapshot refuses rollback before partial restore");
    if(OperatingSystem.IsWindows())
    {
        var aclRoot=Path.Combine(game,"Binaries","Win64");var aclSid=new System.Security.Principal.NTAccount(Environment.UserDomainName,Environment.UserName).Translate(typeof(System.Security.Principal.SecurityIdentifier)).Value;
        int RunIcacls(params string[] args){var psi=new System.Diagnostics.ProcessStartInfo("icacls.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var arg in args)psi.ArgumentList.Add(arg);using var p=System.Diagnostics.Process.Start(psi)!;p.WaitForExit();return p.ExitCode;}
        var aclAdded=RunIcacls(aclRoot,"/deny","*"+aclSid+":(OI)(CI)(M)");try{Check(aclAdded==0,"test ACL deny write/modify installed on disposable clone only");var aclState=Hash(ini1);Throws(()=>MultipatchBridge.ApplyGraphicsThroughMultipatch(game,request),"permission denied during apply");RunIcacls(aclRoot,"/remove:d","*"+aclSid);_=MultipatchBridge.LoadSettingsFromMultipatch(game);Check(Hash(ini1)==aclState,"permission failure rolled back or recovered after ACL removal");}finally{RunIcacls(aclRoot,"/remove:d","*"+aclSid);}
    }
    Check(Hash(save)==saveHash,"saves preserved");Check(Hash(journal)==journalHash,"Multipatch journal preserved");Check(Hash(package)==packageHash&&Hash(Path.Combine(game,"Binaries","Win64","steam_api64.dll"))==steamDllHash,"game package and Steam dependency preserved");
    var controls=MultipatchBridge.LoadControlsFromMultipatch(game); Check(controls["Actions"]!.AsArray().Count==22,"22 supported actions listed");
    var bindings=new JsonObject { ["GBA_Taccom"]="J" }; var controlApplied=MultipatchBridge.ApplyControlsThroughMultipatch(game,bindings); Check(controlApplied["Success"]!.GetValue<bool>(),"control apply");
    Check(inputs.All(p=>File.ReadAllText(p).Contains("Name=\"J\",Command=\"GBA_Taccom\"")),"binding applied to all effective input files"); Check(inputs.All(p=>File.ReadAllText(p).Contains("Name=\"XboxTypeS_A\"")),"gamepad binding preserved");
    Check(inputs.All(p=>File.ReadAllText(p).Contains("Name=\"W\",Command=\"GBA_MoveForward\"")),"unmodified keyboard bindings preserved");
    var controlsRollback=MultipatchBridge.RollbackControlsThroughMultipatch(game,controlApplied["TransactionId"]!.GetValue<string>()); Check(controlsRollback["Success"]!.GetValue<bool>(),"controls rollback"); Check(inputs.All(p=>Hash(p)==HashFromText(inputText)),"controls rollback restores inputs");
    var defaultBindings=new JsonObject();foreach(var row in controls["Actions"]!.AsArray()){var item=row!.AsObject();defaultBindings[item["Command"]!.GetValue<string>()]=item["Default"]!.GetValue<string>();}var defaultsApplied=MultipatchBridge.ApplyControlsThroughMultipatch(game,defaultBindings);Check(inputs.All(p=>Hash(p)==HashFromText(inputText)),"restore defaults writes the existing default layout; diff: "+string.Join(";",inputs.Where(p=>Hash(p)!=HashFromText(inputText)).Select(p=>Path.GetFileName(Path.GetDirectoryName(p))+"/"+Path.GetFileName(p))));var defaultsRollback=MultipatchBridge.RollbackControlsThroughMultipatch(game,defaultsApplied["TransactionId"]!.GetValue<string>());Check(defaultsRollback["Success"]!.GetValue<bool>(),"restore-defaults transaction can be rolled back");
    Throws(()=>MultipatchBridge.ApplyControlsThroughMultipatch(game,new JsonObject { ["GBA_Taccom"]="F8" }),"reserved F8");
    Throws(()=>MultipatchBridge.ApplyControlsThroughMultipatch(game,new JsonObject { ["GBA_Taccom"]="F",["GBA_Melee"]="F" }),"conflicting keys");
    var badJson=File.ReadAllText(graphics); File.WriteAllText(graphics,"{"); Throws(()=>MultipatchBridge.LoadSettingsFromMultipatch(game),"invalid JSON"); File.WriteAllText(graphics,badJson);
    var missing=Path.Combine(temp,"Wrong Directory"); Directory.CreateDirectory(missing); Throws(()=>MultipatchBridge.LoadSettingsFromMultipatch(missing),"incorrect directory preflight");
    var noIni=Path.Combine(temp,"Missing INI Game");Directory.CreateDirectory(noIni);Directory.CreateDirectory(Path.Combine(noIni,"Binaries","Win64"));Directory.CreateDirectory(Path.Combine(noIni,"tools","multipatch","_files"));File.WriteAllText(Path.Combine(noIni,"Binaries","Win64","GearGame.exe"),"x");File.WriteAllText(Path.Combine(noIni,"Gears3_Start_Game.bat"),"x");File.WriteAllText(Path.Combine(noIni,"tools","multipatch","_files","graphics.json"),File.ReadAllText(graphics));File.WriteAllText(Path.Combine(noIni,"tools","multipatch","_files","installation-state.json"),"{\"Status\":\"complete\"}");Environment.SetEnvironmentVariable("JACINTO_SETTINGS_DOCUMENTS",Path.Combine(temp,"No User Config"));Throws(()=>MultipatchBridge.LoadSettingsFromMultipatch(noIni),"missing engine INI preflight");Environment.SetEnvironmentVariable("JACINTO_SETTINGS_DOCUMENTS",docs);
    var incomplete=File.ReadAllText(journal);File.WriteAllText(journal,"{\"Status\":\"installing\"}");Throws(()=>MultipatchBridge.LoadSettingsFromMultipatch(game),"incomplete journal preflight");File.WriteAllText(journal,incomplete);
    var interruptedId=Guid.NewGuid().ToString("N");var interruptedDir=Path.Combine(tx,interruptedId);Directory.CreateDirectory(Path.Combine(interruptedDir,"before"));var interruptedSnapshot=Path.Combine(interruptedDir,"before","001.bin");File.Copy(ini1,interruptedSnapshot);var baselineHash=Hash(ini1);File.AppendAllText(ini1,"\r\nInterruptedWrite=true\r\n");
    var manifest=new {Id=interruptedId,Kind="Graphics",GameRoot=game,CreatedUtc=DateTime.UtcNow.ToString("o"),Status="applying",Entries=new[]{new{Path=ini1,Exists=true,Snapshot=interruptedSnapshot,BeforeSHA256=baselineHash,AfterSHA256=(string?)null,Attributes=(int)File.GetAttributes(interruptedSnapshot)}}};File.WriteAllText(Path.Combine(interruptedDir,"transaction.json"),System.Text.Json.JsonSerializer.Serialize(manifest));
    _=MultipatchBridge.LoadSettingsFromMultipatch(game);Check(Hash(ini1)==baselineHash,"interrupted transaction auto-recovered");
    var movies = "[FullScreenMovie]\r\n+StartupMovies=Open_Logos\r\nStartupMovies=LoadingMovie\r\nSkippableMovies=Azura_Intro\r\n[Other]\r\nStartupMovies=Open_Logos\r\n";
    var skipped = IntroSettings.Transform(movies, true);
    Check(skipped.Contains(";JacintoSkipIntro:+StartupMovies=Open_Logos"), "intro disabled in movie section");
    Check(skipped.Contains("\r\nStartupMovies=LoadingMovie\r\n"), "loading movie preserved");
    Check(skipped.Contains("SkippableMovies=Azura_Intro"), "cutscene preserved");
    Check(skipped.EndsWith("[Other]\r\nStartupMovies=Open_Logos\r\n"), "other section preserved");
    Check(IntroSettings.Transform(skipped, false) == movies, "intro exact restoration");
    Check(IntroSettings.Transform(skipped, true) == skipped, "intro repeated enable idempotent");
    var introDirs = new[] { Path.Combine(temp, "Intro Game") };
    foreach(var d in introDirs) { Directory.CreateDirectory(Path.Combine(d,"GearGame","Config")); foreach(var n in new[]{"GearEngine.ini","DefaultEngine.ini"}) File.WriteAllText(Path.Combine(d,"GearGame","Config",n),movies); }
    Throws(()=>IntroSettings.Apply(introDirs,true,()=>throw new IOException("fixture persistence failure")),"intro persistence failure");
    Check(introDirs.All(d=>File.ReadAllText(Path.Combine(d,"GearGame","Config","GearEngine.ini"))==movies),"intro failure restores both runtimes");
    IntroSettings.Apply(introDirs,true,()=>{});
    Check(introDirs.All(d=>File.ReadAllText(Path.Combine(d,"GearGame","Config","GearEngine.ini"))==skipped),"intro applies both runtimes");
    IntroSettings.Apply(introDirs,false,()=>{});
    Check(introDirs.All(d=>File.ReadAllText(Path.Combine(d,"GearGame","Config","GearEngine.ini"))==movies),"intro disables both runtimes exactly");
    var cycle = new LaunchLifecycle(); var now = DateTime.UtcNow;
    cycle.Begin(now); Check(cycle.Observe(false,now),"launch grace blocks second launch");
    Check(cycle.Observe(true,now.AddSeconds(1)),"actual game blocks launch");
    Check(!cycle.Observe(false,now.AddSeconds(2)),"normal or unexpected exit restores launch");
    cycle.Begin(now.AddSeconds(3)); Check(cycle.Observe(true,now.AddSeconds(4)),"second Steam session starts");
    Check(!cycle.Observe(false,now.AddSeconds(5)),"second session exits");
    cycle.Begin(now); Check(!cycle.Observe(false,now.AddSeconds(31)),"failed BAT launch expires without waiting for helper");
    cycle.Begin(now); cycle.Failed(); Check(!cycle.Busy,"exception resets pending state");
    Check(cycle.Observe(true,now.AddMinutes(2)),"external running game blocks even after timeout");
    var sensitivityBefore = inputs.ToDictionary(path=>path, path=>Hash(path));
    var sensitivityTx = MultipatchBridge.ApplyControlsThroughMultipatch(game, new JsonObject { ["GBA_Taccom"]="J" }, "75.5");
    Check(inputs.All(path=>File.ReadAllText(path).Contains("MouseSensitivity=75.5")),"sensitivity written to all input files");
    Check(MultipatchBridge.LoadControlsFromMultipatch(game)["MouseSensitivity"]!.GetValue<string>()=="75.5","sensitivity persists across fresh adapter process");
    Throws(()=>MultipatchBridge.ApplyControlsThroughMultipatch(game,new JsonObject { ["GBA_Taccom"]="J" },"201"),"sensitivity upper bound");
    Throws(()=>MultipatchBridge.ApplyControlsThroughMultipatch(game,new JsonObject { ["GBA_Taccom"]="J" },"0"),"sensitivity lower bound");
    Throws(()=>MultipatchBridge.ApplyControlsThroughMultipatch(game,new JsonObject { ["GBA_Taccom"]="J" },"NaN"),"sensitivity finite");
    MultipatchBridge.RollbackControlsThroughMultipatch(game,sensitivityTx["TransactionId"]!.GetValue<string>());
    Check(inputs.All(path=>Hash(path)==sensitivityBefore[path]),"sensitivity rollback restores exact bytes");
    var secondRuntime = Path.Combine(temp,"Second Runtime");
    Directory.CreateDirectory(secondRuntime); CopyDirectory(game, secondRuntime);
    var sharedRequest = new JsonObject { ["GBA_Taccom"]="J" };
    var firstApply = MultipatchBridge.ApplyControlsThroughMultipatch(game, sharedRequest, "10");
    var secondApply = MultipatchBridge.ApplyControlsThroughMultipatch(secondRuntime, sharedRequest, "10");
    Check(sharedRequest.Parent is null,"two-runtime request remains reusable");
    Check(MultipatchBridge.LoadControlsFromMultipatch(game)["MouseSensitivity"]!.GetValue<string>()=="10","first runtime persists 10");
    Check(MultipatchBridge.LoadControlsFromMultipatch(secondRuntime)["MouseSensitivity"]!.GetValue<string>()=="10","second runtime persists 10");
    Check(File.ReadAllText(Path.Combine(secondRuntime,"GearGame","Config","GearInput.ini")).Contains("MouseSensitivity=10"),"second local INI updated");
    MultipatchBridge.RollbackControlsThroughMultipatch(secondRuntime,secondApply["TransactionId"]!.GetValue<string>());
    MultipatchBridge.RollbackControlsThroughMultipatch(game,firstApply["TransactionId"]!.GetValue<string>());
    Check(inputs.All(path=>Hash(path)==sensitivityBefore[path]),"two-runtime rollback restores exact bytes");
    var guardedScripts = new[] { offline, start, Put("Gears3_Start_Game_Debug.bat", "") };
    foreach (var path in guardedScripts)
        File.WriteAllText(path, "@echo off\r\nrem GearGame.exe -FULLSCREEN -NOVSYNC -ResX=1920 -ResY=1080 -language=INT\r\n\"%~dp0LeaseHost.exe\" Offline\r\nexit /b %errorlevel%\r\n");
    var guardedHashes = guardedScripts.ToDictionary(path => path, Hash);
    var guardedApply = MultipatchBridge.ApplyGraphicsThroughMultipatch(game, request);
    foreach (var path in guardedScripts) {
        var text = File.ReadAllText(path);
        Check(text.Contains("rem GearGame.exe -FULLSCREEN -VSYNC -ResX=2560 -ResY=1440"), "protected BAT receives UI graphics settings");
        Check(text.Contains("\"%~dp0LeaseHost.exe\" Offline") && !text.Split('\n').Any(line => line.StartsWith("GearGame.exe ")), "graphics update preserves protected BAT entry");
    }
    MultipatchBridge.RollbackSettingsThroughMultipatch(game, guardedApply["TransactionId"]!.GetValue<string>());
    Check(guardedScripts.All(path => Hash(path) == guardedHashes[path]), "protected BAT settings rollback restores exact bytes");
    Console.WriteLine($"PASS: {count} launcher/settings integration assertions.");
}
finally
{
    Environment.SetEnvironmentVariable("JACINTO_SETTINGS_TRANSACTION_ROOT",null); Environment.SetEnvironmentVariable("JACINTO_SETTINGS_DOCUMENTS",null); Environment.SetEnvironmentVariable("JACINTO_SETTINGS_SCRIPT",null);
    try { Directory.Delete(temp,true); } catch { }
}

static string HashFromText(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

static void CopyDirectory(string source, string destination)
{
    foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        var output = Path.Combine(destination, Path.GetRelativePath(source, file));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.Copy(file, output, false);
    }
}
