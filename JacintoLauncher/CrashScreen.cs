#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace JacintoLauncher;

/// <summary>
/// What the player sees when the launcher itself dies at start or later: the error, a copy button, and whether the report
/// went out by itself or has to be made by hand. The window runs in a second process, because after a crash Avalonia in
/// this one cannot be trusted; if that fails too, a native message box says the same.
/// </summary>
internal static class CrashScreen
{
    internal const string Arg = "--crash-report";
    internal const string BugsUrl = "https://www.nexusmods.com/gearsofwar3/mods/27?tab=bugs";

    // A translation table that failed to load must not hide the crash message.
    internal static string T(string text) { try { return L.T(text); } catch { return text; } }

    private static string Summary(bool sent) => sent
        ? T("The launcher closed because of an error. An anonymous error report was sent automatically, so there is nothing else you need to do.")
        : T("The launcher closed because of an error. Nothing was sent: error reports are turned off in Settings, or there is no internet connection. Please copy the details below and report them on the NexusMods bug page:") + " " + BugsUrl;

    /// <summary>Called in the crashed process: sends the report when allowed, then shows the window.</summary>
    internal static void Report(string operation, Exception exception)
    {
        var details = $"v{LauncherUpdate.Current.ToString(3)}  {System.Runtime.InteropServices.RuntimeInformation.OSDescription}{Environment.NewLine}{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {operation}{Environment.NewLine}{exception}";
        var sent = false;
        try { sent = Telemetry.SendCrash(LauncherConfig.Load(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)), exception); }
        catch (Exception failure) { LauncherDiagnostics.Info("Crash report not sent: " + failure.Message); }
        try
        {
            var file = Path.Combine(Path.GetTempPath(), "jacinto-crash-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(file, (sent ? "sent" : "manual") + "\n" + details);
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            start.ArgumentList.Add(Arg); start.ArgumentList.Add(file);
            using var window = Process.Start(start);
            if (window is not null) { window.WaitForExit(); if (window.ExitCode == 0) return; }
        }
        catch (Exception failure) { LauncherDiagnostics.Info("Crash window did not start: " + failure.Message); }
        Native(Summary(sent) + "\n\n" + details, sent);
    }

    /// <summary>The second process: <c>--crash-report file</c>.</summary>
    internal static int Show(string file)
    {
        try
        {
            var parts = File.ReadAllText(file).Split('\n', 2);
            CrashApp.Sent = parts[0].Trim() == "sent"; CrashApp.Details = parts.Length > 1 ? parts[1] : "";
            return AppBuilder.Configure<CrashApp>().UsePlatformDetect().WithInterFont().StartWithClassicDesktopLifetime(Array.Empty<string>());
        }
        catch { return 1; }
        finally { try { File.Delete(file); } catch { } }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(nint window, string text, string caption, uint type);

    // Last resort: whatever dialog the system has. Always also goes to stderr.
    private static void Native(string message, bool sent)
    {
        Console.Error.WriteLine(message);
        const string title = "Gears of War 3 Jacinto Launcher";
        var shown = message.Length > 1800 ? message[..1800] + "..." : message;
        try
        {
            if (OperatingSystem.IsWindows()) { MessageBoxW(0, shown, title, 0x10); return; }
            if (OperatingSystem.IsMacOS()) { Run("osascript", "-e", "display dialog \"" + shown.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\" with title \"" + title + "\" buttons {\"OK\"} with icon stop"); return; }
            if (Run("zenity", "--error", "--no-markup", "--title", title, "--text", shown) || Run("kdialog", "--error", shown, "--title", title)) return;
            Run("xmessage", "-center", shown);
        }
        catch (Exception failure) { LauncherDiagnostics.Info("Crash message box failed: " + failure.Message); }
    }

    private static bool Run(string program, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(program) { UseShellExecute = false };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            process?.WaitForExit();
            return process is not null;
        }
        catch (System.ComponentModel.Win32Exception) { return false; } // The tool is not installed.
    }

    internal sealed class CrashApp : Application
    {
        internal static bool Sent;
        internal static string Details = "";

        public override void Initialize() { RequestedThemeVariant = ThemeVariant.Dark; Styles.Add(new FluentTheme()); }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.MainWindow = Build();
            base.OnFrameworkInitializationCompleted();
        }

        private static Window Build()
        {
            var window = new Window { Title = "Gears of War 3 Jacinto Launcher", Width = 680, Height = 560, MinWidth = 420, MinHeight = 380, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            var box = new TextBox { Text = Details, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Consolas,Menlo,monospace"), FontSize = 12, Margin = new Thickness(0, 12, 0, 12) };
            ScrollViewer.SetHorizontalScrollBarVisibility(box, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollBarVisibility(box, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
            var copy = new Button { Content = T("Copy details"), Margin = new Thickness(0, 0, 8, 0) };
            copy.Click += async (_, _) =>
            {
                if (window.Clipboard is { } clipboard) await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, Details);
                copy.Content = T("Copied");
            };
            var bugs = new Button { Content = T("Open bug reports"), Margin = new Thickness(0, 0, 8, 0) };
            bugs.Click += (_, _) => { try { LauncherDiagnostics.Open(BugsUrl); } catch (Exception) { } };
            var logs = new Button { Content = T("Open log folder"), Margin = new Thickness(0, 0, 8, 0) };
            logs.Click += (_, _) => { try { LauncherDiagnostics.OpenFolder(); } catch (Exception) { } };
            var close = new Button { Content = T("Close") };
            close.Click += (_, _) => window.Close();
            var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var button in new[] { copy, bugs, logs, close }) { button.Margin = new Thickness(0, 0, 8, 8); buttons.Children.Add(button); }
            var message = new TextBlock { Text = Summary(Sent), TextWrapping = TextWrapping.Wrap, FontSize = 14 };
            var title = new TextBlock { Text = T("The launcher stopped"), FontSize = 22, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 10), Foreground = Brushes.IndianRed };
            var layout = new DockPanel { Margin = new Thickness(24) };
            DockPanel.SetDock(title, Dock.Top); DockPanel.SetDock(message, Dock.Top); DockPanel.SetDock(buttons, Dock.Bottom);
            layout.Children.Add(title); layout.Children.Add(message); layout.Children.Add(buttons); layout.Children.Add(box);
            window.Content = layout;
            return window;
        }
    }
}
