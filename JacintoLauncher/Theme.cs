#nullable enable
using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace JacintoLauncher;

/// <summary>The theme is one accent colour, on a light or dark base; every surface takes a faint tint of its hue, so the whole window follows it.</summary>
internal static class LauncherTheme
{
    internal sealed record Option(string Id, string Name, Color Accent);
    internal static readonly Option[] Options =
    {
        new("blue", "Blue", Color.Parse("#16809F")), new("red", "Red", Color.Parse("#C22A30")), new("green", "Green", Color.Parse("#3B8238")),
        new("amber", "Amber", Color.Parse("#B5650F")), new("purple", "Purple", Color.Parse("#7B4FB5")),
        new("pink", "Pink", Color.Parse("#C2357E")), new("yellow", "Yellow", Color.Parse("#A88600"))
    };
    internal static string Current { get; private set; } = "blue";
    internal static string Mode { get; private set; } = "auto";
    private static bool watching;
    internal static event Action? Changed;

    private static LinearGradientBrush Gradient(params (Color Color, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative) };
        foreach (var (color, offset) in stops) brush.GradientStops.Add(new GradientStop(color, offset));
        return brush;
    }

    internal static void Apply(string? id, string? mode = null)
    {
        var option = Array.Find(Options, o => o.Id == id) ?? Options[0];
        Current = option.Id;
        Mode = mode ?? Mode;
        var resources = Application.Current!.Resources;
        var platform = Application.Current.PlatformSettings;
        if (!watching && platform is not null) { watching = true; platform.ColorValuesChanged += (_, _) => { if (Mode == "auto") Apply(Current); }; }
        var light = Mode == "light" || (Mode != "dark" && platform?.GetColorValues().ThemeVariant == Avalonia.Platform.PlatformThemeVariant.Light);
        Application.Current.RequestedThemeVariant = light ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark;
        var accent = option.Accent.ToHsl();
        var (hue, saturation, lightness) = (accent.H, accent.S, accent.L);
        Color Tint(double s, double l) => new HslColor(1, hue, s, l).ToRgb();
        Color Accent(double shift) => new HslColor(1, hue, saturation, Math.Clamp(lightness + shift, 0, 1)).ToRgb();

        foreach (var (key, color) in new (string, Color)[]
        {
            ("SystemAccentColor", option.Accent), ("SystemAccentColorLight1", Accent(.07)), ("SystemAccentColorLight2", Accent(.14)), ("SystemAccentColorLight3", Accent(.22)),
            ("SystemAccentColorDark1", Accent(-.07)), ("SystemAccentColorDark2", Accent(-.14)), ("SystemAccentColorDark3", Accent(-.2))
        }) resources[key] = color;

        var field = light ? Tint(.14, .925) : Tint(.11, .125); var fieldHover = light ? Tint(.18, .84) : Tint(.11, .16); var line = light ? Tint(.14, .8) : Tint(.11, .205); var lineHover = light ? Tint(.18, .6) : Tint(.18, .35);
        // Light keeps these strong: selected navigation shows white text on them.
        var dim = light ? option.Accent : Tint(.38, .165); var dimmer = light ? Accent(.14) : Tint(.5, .24);
        foreach (var (key, color) in new (string, Color)[]
        {
            ("InkBrush", light ? Tint(.12, .965) : Tint(.1, .04)), ("SidebarBrush", light ? Tint(.14, .925) : Tint(.12, .063)), ("PanelBrush", light ? Colors.White : Tint(.11, .088)), ("FieldBrush", field), ("FieldHoverBrush", fieldHover),
            ("LineBrush", line), ("LineHoverBrush", lineHover), ("TextBrush", light ? Tint(.2, .1) : Tint(.12, .93)), ("MutedBrush", light ? Tint(.1, .36) : Tint(.07, .67)), ("StatusBrush", light ? Tint(.14, .22) : Tint(.1, .83)),
            ("AccentBrush", option.Accent), ("AccentHoverBrush", Accent(.07)), ("AccentTextBrush", new HslColor(1, hue, Math.Min(saturation + .1, 1), light ? .32 : .68).ToRgb()),
            ("AccentDimBrush", dim), ("AccentDisabledBorderBrush", dimmer), ("AccentDisabledTextBrush", light ? Colors.White : Tint(.15, .66)), ("DisabledBrush", light ? Tint(.1, .88) : Tint(.1, .09)), ("DisabledTextBrush", light ? Tint(.05, .58) : Tint(.05, .52)),
            // Fluent's own keys: every dropdown, text box and popup shares one surface.
            ("ComboBoxBackground", field), ("ComboBoxBackgroundPointerOver", fieldHover), ("ComboBoxBackgroundPressed", fieldHover), ("ComboBoxBackgroundFocused", fieldHover),
            ("ComboBoxBorderBrush", line), ("ComboBoxBorderBrushPointerOver", lineHover), ("ComboBoxDropDownBackground", field), ("ComboBoxDropDownBorderBrush", line),
            ("ComboBoxItemBackgroundPointerOver", light ? fieldHover : dim), ("ComboBoxItemBackgroundSelected", light ? line : dimmer), ("ComboBoxItemBackgroundSelectedPointerOver", light ? lineHover : Accent(-.14)),
            ("TextControlBackground", field), ("TextControlBackgroundPointerOver", fieldHover), ("TextControlBackgroundFocused", fieldHover),
            ("TextControlBorderBrush", line), ("TextControlBorderBrushPointerOver", lineHover), ("TextControlBorderBrushFocused", option.Accent), ("ExpanderHeaderBackground", light ? Colors.White : Tint(.11, .088)), ("ExpanderHeaderBackgroundPointerOver", field), ("ExpanderHeaderBackgroundPressed", fieldHover), ("ExpanderHeaderBorderBrush", line), ("ExpanderHeaderBorderBrushPointerOver", lineHover), ("ExpanderHeaderBorderBrushPressed", lineHover),
            ("ExpanderContentBackground", light ? Colors.White : Tint(.11, .088)), ("ExpanderContentBorderBrush", line)
        }) resources[key] = new SolidColorBrush(color);
        // Fades start from the hover colour with no alpha: Transparent is white with no alpha, and a fade from it flashes white.
        resources["HoverClearBrush"] = new SolidColorBrush(Color.FromArgb(0, fieldHover.R, fieldHover.G, fieldHover.B));
        // The banner dissolves into the page, whatever colour the page has.
        var ink = light ? Tint(.12, .965) : Tint(.1, .04);
        resources["HomeFadeBrush"] = Gradient((Color.FromArgb(0, ink.R, ink.G, ink.B), 0), (ink, 1));
        resources["HomeScrimBrush"] = Gradient((Color.FromArgb(0, ink.R, ink.G, ink.B), 0), (Color.FromArgb(235, ink.R, ink.G, ink.B), .4), (ink, 1));
        Changed?.Invoke();
    }
}

/// <summary>A row of colour swatches, used in Settings. Picking one saves it at once.</summary>
internal sealed class ThemePicker : StackPanel
{
    private readonly Dictionary<string, Button> swatches = new();

    internal ThemePicker(LauncherConfig config, string exeDir)
    {
        Orientation = Avalonia.Layout.Orientation.Horizontal;
        foreach (var option in LauncherTheme.Options)
        {
            var button = new Button { Background = new SolidColorBrush(option.Accent) };
            button.Classes.Add("swatch");
            ToolTip.SetTip(button, L.T(option.Name));
            AutomationProperties.SetName(button, "Theme " + option.Name);
            button.Click += (_, _) =>
            {
                LauncherTheme.Apply(option.Id);
                config.Theme = option.Id;
                try { config.Save(exeDir); } catch (Exception exception) { LauncherDiagnostics.Error("Settings save failed", exception); }
            };
            swatches[option.Id] = button;
            Children.Add(button);
        }
        LauncherTheme.Changed += Mark;
        Mark();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { LauncherTheme.Changed -= Mark; base.OnDetachedFromVisualTree(e); }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { LauncherTheme.Changed -= Mark; LauncherTheme.Changed += Mark; Mark(); base.OnAttachedToVisualTree(e); }
    private void Mark() { foreach (var (id, button) in swatches) button.Classes.Set("selected", id == LauncherTheme.Current); }
}
