#nullable enable
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace JacintoLauncher;

/// <summary>What the two showcase pages share.</summary>
internal static class InfoUi
{
    internal static TextBlock Text(string text, double size = 13, FontWeight weight = FontWeight.Normal) => new() { Text = L.T(text), FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap };
    internal static TextBlock Muted(string text) { var block = Text(text); block.Classes.Add("muted"); return block; }
    internal static TextBlock Heading(string text) { var block = Text(text); block.Classes.Add("h2"); return block; }
    internal static Bitmap Asset(string path) { using var stream = AssetLoader.Open(new Uri("avares://GoW3Jacinto_Launcher/assets/" + path)); return new Bitmap(stream); }

    /// <summary>A page whose title stays in place while its content scrolls under it.</summary>
    internal static Control Titled(string title, Control body)
    {
        var heading = new TextBlock { Text = title, Margin = new Thickness(32, 28, 32, 16) };
        heading.Classes.Add("h1");
        body.Margin = new Thickness(32, 0, 32, 28);
        var viewer = new ScrollViewer { Content = body };
        viewer.Loaded += (_, _) => viewer.Offset = default;
        Grid.SetRow(viewer, 1);
        return new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Children = { heading, viewer } };
    }

    internal static Border Accented(Control child, string resource)
    {
        var border = new Border { Child = child };
        border.Bind(Border.BackgroundProperty, border.GetResourceObservable(resource));
        return border;
    }

    internal static Button Open(string text, string url, bool primary = false)
    {
        var button = new Button { Content = L.T(text), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        if (primary) button.Classes.Add("primary");
        button.Click += async (_, _) => { if (TopLevel.GetTopLevel(button) is { } top) await top.Launcher.LaunchUriAsync(new Uri(url)); };
        return button;
    }
}

/// <summary>The Multipatch at a glance: the changelog's highlights, grouped by what they mean to a player.</summary>
internal sealed class FeaturesPage : UserControl
{
    private const string Star = "M12 17.27 18.18 21l-1.64-7.03L22 9.24l-7.19-.61L12 2 9.19 8.63 2 9.24l5.46 4.73L5.82 21z";

    private static readonly (string Title, string Icon, string[] Points)[] Groups =
    {
        ("CAMPAIGN CO-OP ONLINE", "M16 11a3 3 0 1 0 0-6 3 3 0 0 0 0 6zm-8 0a3 3 0 1 0 0-6 3 3 0 0 0 0 6zm0 2c-2.33 0-7 1.17-7 3.5V19h14v-2.5C15 14.17 10.33 13 8 13zm8 0c-.29 0-.62.02-.97.05A4.2 4.2 0 0 1 17 16.5V19h6v-2.5c0-2.33-4.67-3.5-7-3.5z", new[]
        {
            "Play the whole campaign with Steam friends, with no port forwarding and no VPN.",
            "Press F8 in the game to invite. Your friend accepts and joins automatically.",
            "Split-screen on one PC keeps working."
        }),
        ("THE CAMPAIGN CAN BE FINISHED", "M14.4 6 14 4H5v17h2v-7h5.6l.4 2h7V6z", new[]
        {
            "Soft-locks removed: the Homecoming razor wire, the Crater elevator and cable car, the Bon Voyage plates and last stage, the Tempest final boss.",
            "The Mother Corpser fight of Act 2 is restored, built almost from scratch.",
            "The Brumak fight now ends properly, even after a checkpoint reload.",
            "Coast stealth works, and its washed-out picture is gone.",
            "Dom's COG tags and Maria's necklace are back in the Act 3 cinematic."
        }),
        ("ANY SCREEN, ANY RESOLUTION", "M21 3H3a2 2 0 0 0-2 2v11a2 2 0 0 0 2 2h7v2H8v2h8v-2h-2v-2h7a2 2 0 0 0 2-2V5a2 2 0 0 0-2-2zm0 13H3V5h18v11z", new[]
        {
            "Menus and the HUD fit ultrawide 21:9 and 32:9 and scale up to 4K.",
            "Field of view from 50 to 130.",
            "Fixed the crash on high and non-16:9 resolutions.",
            "Optional borderless window through the Vulkan renderer."
        }),
        ("FASTER AND MORE STABLE", "M13 2 3 14h9l-1 8 10-12h-9z", new[]
        {
            "No more 18-second wait at every start for a server that no longer exists.",
            "Crash fixes for Ultra detail and for the bulk data of cooked textures.",
            "Texture streaming tuned, and the Alt-Tab freeze fixed.",
            "Audio channels up to 128, so fewer sounds cut off in big fights."
        }),
        ("YOUR CONTROLS", "M21 6H3a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h18a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2zm-10 7H8v3H6v-3H3v-2h3V8h2v3h3v2zm4.5 2a1.5 1.5 0 1 1 0-3 1.5 1.5 0 0 1 0 3zm4-3a1.5 1.5 0 1 1 0-3 1.5 1.5 0 0 1 0 3z", new[]
        {
            "ESC and Enter work in every menu, with keyboard key icons on screen.",
            "Prefer a gamepad? One switch brings its buttons and icons back.",
            "Rebind every keyboard and mouse action, including the Numpad.",
            "The inverted analog stick is fixed."
        }),
        ("ONE-CLICK LAUNCHER", "M10 20v-6h4v6h5v-8h3L12 3 2 12h3v8z", new[]
        {
            "Install in seconds: every file is checked before and after it is written.",
            "Verify and repair game files, and return to the original game at any time.",
            "Automatic save backups, and updates that keep your settings.",
            "Windows, Linux, Steam Deck and macOS, with keyboard or gamepad navigation."
        })
    };

    internal FeaturesPage()
    {
        var title = new TextBlock { Text = L.T("EVERY FIX IN ONE CLICK"), FontSize = 30, FontWeight = FontWeight.Bold, LetterSpacing = 1, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
        var subtitle = new TextBlock { Text = L.T("Dozens of fixes for Gears of War 3 Jacinto 1.1.1: the campaign from start to end, online co-op, and any screen."), FontSize = 15, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, MaxWidth = 620, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        var shade = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative) };
        shade.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), .25));
        shade.GradientStops.Add(new GradientStop(Color.FromArgb(225, 0, 0, 0), 1));
        // The banner stays while the list below it scrolls.
        var banner = new Panel
        {
            Height = 210,
            ClipToBounds = true,
            Children =
            {
                new Image { Source = InfoUi.Asset("bg.jpg"), Stretch = Stretch.UniformToFill },
                new Border { Background = shade, Padding = new Thickness(32, 20), Child = new StackPanel { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom, Children = { title, subtitle } } }
            }
        };
        var panel = new StackPanel();
        var grid = new Avalonia.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(26, 22, 26, 10) };
        foreach (var (name, icon, points) in Groups) grid.Children.Add(Group(name, icon, points));
        panel.Children.Add(grid);
        panel.Children.Add(new StackPanel { Margin = new Thickness(32, 0, 32, 28), Children = { InfoUi.Open("Full list on Nexus Mods", "https://www.nexusmods.com/gearsofwar3/mods/27?tab=description") } });
        var viewer = new ScrollViewer { Content = panel };
        Grid.SetRow(viewer, 1);
        Content = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Children = { banner, viewer } };
    }

    private static Control Group(string title, string icon, string[] points)
    {
        var badge = InfoUi.Accented(new PathIcon { Width = 20, Height = 20, Foreground = Brushes.White, Data = StreamGeometry.Parse(icon) }, "AccentBrush");
        badge.Width = badge.Height = 42; badge.CornerRadius = new CornerRadius(21); badge.Margin = new Thickness(0, 0, 14, 0);
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(badge, Dock.Left);
        var heading = InfoUi.Heading(title); heading.Margin = default; heading.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        head.Children.Add(badge); head.Children.Add(heading);
        var stack = new StackPanel { Children = { head } };
        foreach (var point in points)
        {
            var mark = new PathIcon { Width = 9, Height = 9, Margin = new Thickness(0, 5, 10, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, Data = StreamGeometry.Parse(Star) };
            mark.Bind(ForegroundProperty, mark.GetResourceObservable("AccentTextBrush"));
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
            DockPanel.SetDock(mark, Dock.Left);
            row.Children.Add(mark); row.Children.Add(InfoUi.Text(point));
            stack.Children.Add(row);
        }
        var card = new Border { Child = stack, Margin = new Thickness(6) };
        card.Classes.Add("card");
        return card;
    }
}

/// <summary>Other mods worth having, and the community translations.</summary>
internal sealed class RecommendedPage : UserControl
{
    private static readonly (string Title, string Text, string Image, string Url)[] Mods =
    {
        ("HD Textures Pack", "Sharper textures for the whole game. Install it before or after the Multipatch, in any order.", "mods/hd_textures.jpg", "https://www.nexusmods.com/gearsofwar3/mods/28"),
        ("HD Movies Pack", "Cinematics in high definition. Replace Tower_Outro.bik with the original file, or the game crashes at the final movie.", "mods/hd_movies.jpg", "https://www.nexusmods.com/gearsofwar3/mods/29?tab=files")
    };
    private static readonly (string Title, string Text, string Image, string Url)[] Languages =
    {
        ("Türkçe", "Turkish translation.", "mods/turkish.jpg", "https://www.nexusmods.com/gearsofwar3/mods/26"),
        ("Português - Brasil", "Brazilian Portuguese translation. Install the Multipatch again after it.", "mods/ptbr.jpg", "https://www.nexusmods.com/gearsofwar3/mods/24"),
        ("繁體中文", "Traditional Chinese translation. Its larger subtitle font is already in the launcher, so mod 35 is not needed.", "mods/chinese.jpg", "https://www.nexusmods.com/gearsofwar3/mods/31")
    };

    internal RecommendedPage()
    {
        var panel = new StackPanel();
        panel.Children.Add(Section("RECOMMENDED MODS", Mods));
        panel.Children.Add(Section("COMMUNITY LANGUAGES", Languages, "Download these from Nexus Mods and install them yourself. Then choose the language in Settings > General > Game language: the launcher sets it up to work with the Multipatch.", "Made a translation? Upload it to Nexus Mods (nexusmods.com/games/gearsofwar3/mods) and contact DryreL. It will gladly be added here."));
        Content = InfoUi.Titled(L.T("RECOMMENDED"), panel);
    }

    private static Control Section(string title, (string Title, string Text, string Image, string Url)[] items, string? note = null, string? footer = null)
    {
        var stack = new StackPanel { Children = { InfoUi.Heading(title) } };
        if (note is not null) { var intro = InfoUi.Muted(note); intro.Margin = new Thickness(0, 0, 0, 14); stack.Children.Add(intro); }
        foreach (var (name, text, image, url) in items)
        {
            var picture = new Border { Width = 214, Height = 120, CornerRadius = new CornerRadius(4), ClipToBounds = true, Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, Child = new Image { Source = InfoUi.Asset(image), Stretch = Stretch.UniformToFill } };
            var open = InfoUi.Open("Open on Nexus Mods", url);
            open.Margin = new Thickness(0, 10, 0, 0);
            var description = InfoUi.Muted(text); description.Margin = new Thickness(0, 4, 0, 0);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
            row.Classes.Add("enter");
            DockPanel.SetDock(picture, Dock.Left);
            row.Children.Add(picture);
            row.Children.Add(new StackPanel { Children = { InfoUi.Text(name, 15, FontWeight.SemiBold), description, open } });
            stack.Children.Add(row);
        }
        if (footer is not null) stack.Children.Add(InfoUi.Muted(footer));
        var card = new Border { Child = stack };
        card.Classes.Add("card");
        return card;
    }
}

/// <summary>The old tool's cheat list: hotkeys, console commands and the map events that jump between checkpoints.</summary>
internal sealed class CheatsPage : UserControl
{
    private static readonly (string Key, string Text)[] Hotkeys =
    {
        ("#Cheats and debug", ""),
        ("G", "God mode on / off (a message shows on screen)"), ("Ctrl+G", "Badass: super damage + god mode"), ("L", "Loaded: all weapons and full ammo"), ("K", "Kill enemies"), ("Ctrl+K", "Kill all AI"), ("I", "Invisible to the AI on / off"),
        ("J", "Fly mode"), ("Alt+J", "Ghost mode (fly through walls)"), ("Ctrl+J", "Walk: back to normal movement after Fly / Ghost"), ("O", "Slow motion x5"), ("Shift+O", "Slow motion x0.5"), ("Alt+O", "Fast motion x10"), ("Ctrl+O", "Normal speed"),
        ("P", "PlayersOnly: freezes the world, only players move"), ("Shift+P / Alt+P / Ctrl+P", "Show FPS / engine stats / D3D stats"), ("F10", "Server travel shortcut"), ("Z", "Toggle hand IK"),
        ("#Debug views", ""),
        ("U / Ctrl+U / Shift+U", "AI debug display / cycle AI forward / cycle AI back"), ("Ctrl+I / Alt+I", "Show AI paths / show cover points"), ("Ctrl+C", "Show collision"), ("N", "Show dynamic shadows on / off"),
        ("F1 F2 F3 F4 F5", "Wireframe / unlit / lit / detail lighting / shader complexity"), ("F9", "Screenshot"), ("Insert", "Free camera"), ("Delete / End", "Default camera / first person camera"), ("Page Down / Page Up", "Third person camera / fixed camera"), ("Home", "Fixed tracking camera"), ("Num + / Num -", "Free camera distance"),
        ("#Weapon keys", ""),
        ("5 6 7 8", "Frag / ink / smoke / incendiary grenade"), ("Num 0 .. Num 3", "Flamethrower / heavy minigun / heavy mortar / Locust assault rifle"), ("Num 4 .. Num 6", "Locust burst pistol / Locust pistol / sniper rifle"), ("Num 7 .. Num 9", "Boomshot / bow / Hammer of Dawn"), ("Ctrl+Num 0 .. Num 4", "Digger launcher / Retro Lancer / sawed-off shotgun / cleaver / heavy flail")
    };
    private static readonly (string Key, string Text)[] Commands =
    {
        ("#Player", ""),
        ("God", "God mode on / off"), ("Badass", "Super damage + god mode"), ("SuperDamage", "Damage multiplier x8"), ("UnlimitedHealth", "No health loss on / off"), ("UnlimitedAmmo / UnlimitedClips", "Infinite ammo / infinite clips"), ("Loaded", "All weapons and ammo"), ("Invis", "Invisible to the AI on / off"), ("Fly / Ghost / Walk", "Fly / ghost (no clip) / back to walking"),
        ("slomo <speed>", "Game speed, e.g. slomo 0.2 or slomo 3 (slomo 1 = normal)"), ("SpawnWeapon <class>", "Gives a weapon by class name, e.g. SpawnWeapon GearGame.GearWeap_Shotgun_SawedOff"), ("GiveWeapon <class>", "Same, another way to give the weapon to the player"),
        ("#Enemies and AI", ""),
        ("kill enemy / kill ai", "Kill enemies / all AI"), ("KillMyTarget", "Kills the pawn you aim at"), ("DBNOMyTarget", "Knocks the pawn you aim at down (DBNO)"), ("DebugAI [category]", "AI debug display (a category name is optional)"), ("ToggleFlocking", "Flocking on / off"), ("DebugHeadshots", "Headshot debug on / off"),
        ("#Game and levels", ""),
        ("open <map>", "Loads a map, e.g. open SP_Char_P"), ("servertravel <map>", "Same as F10 with a map name (map change for the host and clients)"), ("ce <event>", "Fires a level event in the loaded map (see below)"), ("HasChapterUnlock <n>", "Tells if chapter n is unlocked"), ("UnlockAllChapters", "Unlocks every chapter in Chapter Select and the Insane difficulty"), ("ShowDamage", "Damage numbers on / off"), ("FF", "Friendly fire state"), ("LaughTrack 1 / 0", "Laugh track on / off"), ("ShowProfileSettings", "Gore and mature language profile settings"), ("ScreenshotMode", "Hides the HUD for screenshots, call again to leave"),
        ("#Co-op", ""),
        ("open <ip>:7777", "Join a host on the LAN"),
        ("#Display", ""),
        ("stat fps / stat engine / stat d3d", "Frame rate and performance overlays (type again to hide)"), ("viewmode wireframe/unlit/lit", "Rendering mode"), ("show collision / show paths", "Collision and AI path debug views")
    };
    private static readonly (string Key, string Text)[] Events =
    {
        ("#Checkpoint jumps", ""),
        ("SP_Char_P", "jump_01_checkpoint .. jump_11_tower1_return (tower 1, subway, tower 2, gondola)"), ("SP_Badlands_P", "jump_01_1stpatrol, jump_02_siegebeast, jump_03_gateapproach, jump_04_throughmaingate, jump_05_throughsidegate"), ("SP_Badlands2_P", "jump_01_tickercages .. jump_06_blimptower (jump_04_corpsermama = Mother Corpser)"), ("SP_Mercy_P", "jump1 .. jump11 (the Mercy chapter, in order)"), ("SP_Wreckage_P", "jump1 .. jump4, jumpCollect, jumpPrescott"), ("SP_Maelstrom2_01_S", "JumpToMaelstrom, JumpToArches, JumpToMines, JumpToTunnel, JumpToTurrets, JumpToChase"), ("SP_Maelstrom2_04_S", "JumpToTunnel, JumpToTurrets"), ("SP_Maelstrom_01_S", "JumpToFish, JumpToKelp"), ("SP_OldShipyard_P", "CHAPTER1, CHAPTER2 (Intro_P: CHAPTER1)"),
        ("#Other useful events", ""),
        ("SP_Char_P", "load_01_s .. load_tower_2_s load the sub levels of the chapter; stream_in_audio_level / stream_out_audio_level"), ("SP_Badlands2_P", "cine_tower, cine_transition2 (play the cinematics)"), ("SP_Ravens_P, SP_Ravens2_P", "firstfight, hangar, shakez, gogo (battle / flow triggers)"), ("SP_Azura_P, SP_Azura2_P", "gogo (start the next section)"), ("SP_Char_Tower_2_elevator_S", "enable_lift_ride, golift (starts the Tower 2 lift)"), ("SP_Char_05_S", "gotrain, holego, test_trainjumper, test_truck"), ("SP_Hanover_P / SP_Mercy_P", "cine_stranded / cine_townhall, cometopapa, prepshowdown"), ("SP_Badlands_xx_S, SP_Char_xx_S", "test_xx_S events spawn the section enemies (spawntest, test_bomb, test_14_S ...)")
    };

    internal CheatsPage()
    {
        var panel = new StackPanel();
        panel.Children.Add(Section("HOTKEYS IN THE GAME", "Press the key during a mission. Most cheats show a message on the screen.", Hotkeys));
        panel.Children.Add(Section("CONSOLE COMMANDS", "Open the console with the tilde key (~), type the command and press Enter. They are meant for the campaign on your own PC, or as the co-op host.", Commands));
        panel.Children.Add(Section("LEVEL EVENTS AND CHECKPOINT JUMPS", "Type ce <name> in the console while that map is loaded. They are developer test events, so the result can differ from the normal game. A jump moves the story forward: save before you use it.", Events));
        Content = InfoUi.Titled(L.T("CHEATS"), panel);
    }

    private static Control KeyBox(string key)
    {
        var name = new SelectableTextBlock { Text = key, FontFamily = new FontFamily("Consolas, Menlo, monospace"), FontSize = 13, TextWrapping = TextWrapping.Wrap };
        name.Bind(TextBlock.ForegroundProperty, name.GetResourceObservable("AccentTextBrush"));
        var box = new Border { Child = name, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(7, 2), Margin = new Thickness(0, 0, 6, 3) };
        box.Bind(Border.BackgroundProperty, box.GetResourceObservable("FieldBrush"));
        box.Bind(Border.BorderBrushProperty, box.GetResourceObservable("LineHoverBrush"));
        return box;
    }

    private static Control Section(string title, string note, (string Key, string Text)[] rows)
    {
        var intro = InfoUi.Muted(note); intro.Margin = new Thickness(0, 0, 0, 12);
        var stack = new StackPanel { Children = { InfoUi.Heading(title), intro } };
        foreach (var (key, text) in rows)
        {
            if (key.StartsWith('#')) { stack.Children.Add(new TextBlock { Text = L.T(key[1..]), FontWeight = FontWeight.SemiBold, FontSize = 14, Margin = new Thickness(0, 10, 0, 6) }); continue; }
            var keys = new WrapPanel();
            foreach (var part in key.Split(" / ")) keys.Children.Add(KeyBox(part));
            var value = InfoUi.Text(text);
            value.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
            Grid.SetColumn(value, 1);
            stack.Children.Add(new Grid { ColumnDefinitions = new ColumnDefinitions("270,*"), Margin = new Thickness(0, 0, 0, 5), Children = { keys, value } });
        }
        var card = new Border { Child = stack };
        card.Classes.Add("card");
        return card;
    }
}

/// <summary>The default gamepad layout, read from the game's own bindings. Drawn, not an image, so it follows the theme and the language.</summary>
internal sealed class GamepadDiagram : Viewbox
{
    private const double Wide = 900, High = 470;

    private static readonly (string Text, double FromX, double FromY, bool Left, double RowY)[] Labels =
    {
        ("LT  Aim", 335, 88, true, 40), ("LB  TacCom", 340, 112, true, 88),
        ("Left stick  Move", 345, 178, true, 150), ("Click left stick  Crouch", 345, 178, true, 186),
        ("D-pad up  Grenades", 395, 256, true, 244), ("D-pad right  Weapon 1", 412, 272, true, 280), ("D-pad left  Weapon 2", 378, 272, true, 316), ("D-pad down  Pistol", 395, 288, true, 352),
        ("Back  Scoreboard", 412, 178, true, 412),
        ("RT  Fire", 565, 88, false, 40), ("RB  Reload", 560, 112, false, 88),
        ("Y  Point of interest", 557, 148, false, 136), ("B  Melee", 582, 174, false, 172), ("A  Run, take cover", 557, 200, false, 208), ("X  Use, pick up", 532, 174, false, 244),
        ("Right stick  Look", 505, 272, false, 300), ("Click right stick  Zoom", 505, 272, false, 336),
        ("Start  Pause", 488, 178, false, 412)
    };

    internal GamepadDiagram()
    {
        Stretch = Stretch.Uniform;
        var canvas = new Canvas { Width = Wide, Height = High };
        var body = new Avalonia.Controls.Shapes.Path
        {
            Data = StreamGeometry.Parse("M330 110 C352 94 400 96 420 112 L480 112 C500 96 548 94 570 110 C602 134 640 246 646 310 C652 366 606 392 570 348 C548 322 530 298 500 298 L400 298 C370 298 352 322 330 348 C294 392 248 366 254 310 C260 246 298 134 330 110 Z"),
            StrokeThickness = 2
        };
        Bind(body, Avalonia.Controls.Shapes.Shape.FillProperty, "FieldBrush");
        Bind(body, Avalonia.Controls.Shapes.Shape.StrokeProperty, "LineHoverBrush");
        canvas.Children.Add(body);
        void Round(double x, double y, double size, string? text = null, Color? color = null)
        {
            var dot = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), BorderThickness = new Thickness(2) };
            Bind(dot, Border.BorderBrushProperty, "MutedBrush");
            if (color is { } c) { dot.Background = new SolidColorBrush(c); dot.BorderBrush = null; }
            if (text is not null) dot.Child = new TextBlock { Text = text, Foreground = Brushes.White, FontWeight = FontWeight.Bold, FontSize = 12, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            Canvas.SetLeft(dot, x - size / 2); Canvas.SetTop(dot, y - size / 2);
            canvas.Children.Add(dot);
        }
        Round(345, 178, 48); Round(505, 272, 48);
        Round(557, 148, 25, "Y", Color.Parse("#B8960B")); Round(582, 174, 25, "B", Color.Parse("#B8322C")); Round(557, 200, 25, "A", Color.Parse("#3E8E2F")); Round(532, 174, 25, "X", Color.Parse("#2B6CB0"));
        Round(412, 178, 16); Round(488, 178, 16);
        var pad = new Avalonia.Controls.Shapes.Path { Data = StreamGeometry.Parse("M385 246 h20 v16 h16 v20 h-16 v16 h-20 v-16 h-16 v-20 h16 z") };
        Bind(pad, Avalonia.Controls.Shapes.Shape.FillProperty, "MutedBrush");
        canvas.Children.Add(pad);
        foreach (var (text, fromX, fromY, left, rowY) in Labels)
        {
            var endX = left ? 210.0 : 690.0;
            var leader = new Avalonia.Controls.Shapes.Polyline { Points = { new Point(fromX, fromY), new Point(fromX + (endX - fromX) * .45, rowY), new Point(endX, rowY) }, StrokeThickness = 1, Opacity = .55 };
            Bind(leader, Avalonia.Controls.Shapes.Shape.StrokeProperty, "MutedBrush");
            canvas.Children.Add(leader);
            var label = new TextBlock { Text = L.T(text), FontSize = 14, Width = 200, TextAlignment = left ? TextAlignment.Right : TextAlignment.Left };
            Canvas.SetLeft(label, left ? 4 : 696); Canvas.SetTop(label, rowY - 10);
            canvas.Children.Add(label);
        }
        Child = canvas;
    }

    private static void Bind(Control control, AvaloniaProperty property, string resource) => control.Bind(property, control.GetResourceObservable(resource));
}
