#nullable enable
using System;
using System.IO;
using System.Linq;

namespace JacintoLauncher;

/// <summary>What each file of the patch data is for, so the install log names the fix rather than only the file.</summary>
internal static class FixCatalog
{
    private static readonly (string Name, string Fix)[] Files =
    {
        ("GearGame.exe", "Game executable: ultrawide and HUD scaling, crash fixes, debug text removed"),
        ("StartCampaign.bat", "Campaign start script"),
        ("wsock32.dll", "JacintoNet: online co-op through Steam"),
        ("steam_api64.dll", "JacintoNet: Steamworks library"),
        ("GOW3Helper.dll", "Multipatch helper library"),
        ("BaseEngine.ini", "Engine settings: texture streaming, Alt-Tab freeze fix"),
        ("DefaultEngine.ini", "Engine settings: texture streaming, Alt-Tab freeze fix"),
        ("GearEngine.ini", "Engine settings: texture streaming, Alt-Tab freeze fix"),
        ("DefaultCamera.ini", "Camera: field of view"), ("GearCamera.ini", "Camera: field of view"),
        ("DefaultGame.ini", "Game settings: DLC placeholder removed, Insane difficulty fix"), ("GearGame.ini", "Game settings: DLC placeholder removed, Insane difficulty fix"),
        ("DefaultInput.ini", "Keyboard and mouse bindings, cutscene skip on Enter"), ("GearInput.ini", "Keyboard and mouse bindings, cutscene skip on Enter"),
        ("DefaultPawn.ini", "Characters: run camera shake"), ("GearPawn.ini", "Characters: run camera shake"), ("DefaultPawnMP.ini", "Multiplayer characters: run camera shake"), ("GearPawnMP.ini", "Multiplayer characters: run camera shake"),
        ("DefaultUI.ini", "Menus: Campaign DLC button removed"), ("GearUI.ini", "Menus: Campaign DLC button removed"),
        ("Warfare_HUD.upk", "Keyboard and mouse button icons"),
        ("CorpserRestor_Audio.upk", "Act 2 Chapter 4: Mother Corpser fight restored"), ("CorpserRestor_BattleAudio.upk", "Act 2 Chapter 4: Mother Corpser fight restored"), ("CorpserRestor_InterwaveFX.upk", "Act 2 Chapter 4: Mother Corpser fight restored"), ("CorpserRestor_PillarChunks.upk", "Act 2 Chapter 4: Mother Corpser fight restored"),
        ("WarfareFonts2x.upk", "High-resolution subtitle font"),
        ("SP_Ravens2_05_S.gear", "Act 1, Abandon Ship: endless slow motion fixed"),
        ("SP_Hanover_CoopDoor_Razor.gear", "Act 1, Homecoming: razor wire soft-lock fixed"),
        ("SP_Wreckage_01_S_CombatD.gear", "Act 2, Wreckage: Brumak boss fix"), ("SP_Wreckage_P.gear", "Act 2, Wreckage: Brumak boss fix"),
        ("SP_Wreckage_Cine_BrumakDeathA.gear", "Act 2, Wreckage: placeholder Brumak death scene removed"), ("SP_Wreckage_Cine_BrumakDeathB.gear", "Act 2, Wreckage: placeholder Brumak death scene removed"),
        ("SP_Badlands2_P.gear", "Act 2: Mother Corpser fight restored, Hijack's missing ground fixed"), ("SP_Badlands_13_S.gear", "Act 2 Chapter 4: Mother Corpser fight restored"), ("SP_Badlands_Cine_Corpser_Outro.gear", "Act 2 Chapter 4: Mother Corpser fight restored"), ("SP_Badlands_Cine_EggRoom.gear", "Act 2 Chapter 4: Mother Corpser fight restored"),
        ("SP_Mercy_01_S_Showdown.gear", "Act 3, Mercy: Act 3 to Act 4 transition fix"),
        ("SP_Mercy_Cine_FoundPump.gear", "Act 3, Mercy: Dom's COG tags and Maria's necklace"),
        ("SP_Char_GondolaRide.gear", "Act 4 Chapter 2, Crater: cable car teleport fixed"),
        ("SP_Char_P.gear", "Act 4 Chapter 2, Crater: Tower 2 elevator fix"), ("SP_Char_Tower_2_S.gear", "Act 4 Chapter 2, Crater: Tower 2 elevator fix"), ("SP_Char_Tower_2_elevator_S.gear", "Act 4 Chapter 2, Crater: Tower 2 elevator fix"),
        ("SP_OldShipyard_05_S.gear", "Bon Voyage: plates and last stage fix"), ("SP_OldShipyard_P.gear", "Bon Voyage: plates and last stage fix"),
        ("SP_Coast_01_S.gear", "Act 5, Coast: stealth mechanism fix"), ("SP_Coast_P.gear", "Act 5, Coast: washed-out brightness removed"),
        ("SP_Rooftop_01b_S.gear", "Act 5 Chapter 6: final boss fixes"), ("SP_Tower_P.gear", "Act 5 Chapter 6: final boss fixes"),
        ("Azura_Intro.txt", "Act 5 Chapter 3: intro subtitle timing"), ("Wreckage_Outro.txt", "Act 2, Wreckage: outro subtitle timing"),
        ("Open_Logos.bik", "Opening logos restored"),
        ("Engine.u", "Engine script: developer key messages"), ("GearGame.u", "Game script: co-op, split-screen, cheats and menu fixes"), ("GearGameContent.u", "Game content script fixes"),
        ("Gears3_Start_Game.bat", "Game start script"), ("Gears3_Start_Game_Debug.bat", "Debug start script"),
        ("graphics.json", "Default game settings"), ("keybinds.json", "Default key bindings"),
        ("changelog.txt", "Multipatch changelog"), ("readme.txt", "Multipatch readme")
    };

    internal static string Describe(string path)
    {
        var name = Path.GetFileName(path.Replace('\\', '/'));
        var known = Files.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Fix;
        if (known is not null) return known;
        if (name.StartsWith("UI_", StringComparison.OrdinalIgnoreCase)) return "Menus: keyboard navigation and screen scaling";
        if (name.StartsWith("level_objectives", StringComparison.OrdinalIgnoreCase)) return "Text: missing objectives";
        if (name.StartsWith("level_poi", StringComparison.OrdinalIgnoreCase)) return "Text: missing point of interest names";
        if (name.StartsWith("geargame", StringComparison.OrdinalIgnoreCase)) return "Text: missing game and menu lines";
        return "Game file";
    }
}
