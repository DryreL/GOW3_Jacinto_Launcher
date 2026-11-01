#nullable enable
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace JacintoLauncher;

// Rebuilt adapter for a decompiler-corrupted source file. It keeps the original
// options-window contract: return UE3 key names, never write a binding itself.
internal static class KeyCapture
{
    public static string? Ask(Window owner, string action)
    {
        string? selected = null;
        var text = new TextBlock { Text = L.F("Press a key or mouse button for\n\"{0}\"\n\nEsc cancels", action), Foreground = Brushes.White, FontSize = 15, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var dialog = new Window { Title = L.T("Press a key"), Owner = owner, Width = 420, Height = 200, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.FromRgb(27, 27, 27)), Content = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(192, 39, 45)), BorderThickness = new Thickness(1), Child = text } };
        dialog.PreviewKeyDown += (_, args) => { args.Handled = true; if (args.Key == Key.Escape) { dialog.Close(); return; } selected = ToUe3Key(args.Key == Key.System ? args.SystemKey : args.Key); if (selected is null) text.Text = L.T("That key is not supported here.\nTry another one, or Esc to cancel."); else dialog.Close(); };
        dialog.PreviewMouseDown += (_, args) => { args.Handled = true; selected = args.ChangedButton switch { MouseButton.Left => "LeftMouseButton", MouseButton.Right => "RightMouseButton", MouseButton.Middle => "MiddleMouseButton", MouseButton.XButton1 => "ThumbMouseButton", MouseButton.XButton2 => "ThumbMouseButton2", _ => null }; if (selected is not null) dialog.Close(); };
        dialog.PreviewMouseWheel += (_, args) => { args.Handled = true; selected = args.Delta > 0 ? "MouseScrollUp" : "MouseScrollDown"; dialog.Close(); };
        dialog.ShowDialog();
        return selected;
    }

    private static string? ToUe3Key(Key key)
    {
        if (key is >= Key.A and <= Key.Z || key is >= Key.F1 and <= Key.F12) return key.ToString();
        var digits = new[] { "Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine" };
        if (key is >= Key.D0 and <= Key.D9) return digits[(int)key - (int)Key.D0];
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return "NumPad" + digits[(int)key - (int)Key.NumPad0];
        return key switch
        {
            Key.Space => "SpaceBar", Key.Tab => "Tab", Key.Enter => "Enter", Key.Back => "BackSpace", Key.CapsLock => "CapsLock",
            Key.Up => "Up", Key.Down => "Down", Key.Left => "Left", Key.Right => "Right", Key.Home => "Home", Key.End => "End", Key.PageUp => "PageUp", Key.PageDown => "PageDown", Key.Insert => "Insert", Key.Delete => "Delete",
            Key.LeftShift => "LeftShift", Key.RightShift => "RightShift", Key.LeftCtrl => "LeftControl", Key.RightCtrl => "RightControl", Key.LeftAlt => "LeftAlt", Key.RightAlt => "RightAlt",
            Key.Add => "Add", Key.Subtract => "Subtract", Key.Multiply => "Multiply", Key.Divide => "Divide", Key.Decimal => "Decimal",
            Key.OemTilde => "Tilde", Key.OemMinus => "Hyphen", Key.OemPlus => "Equals", Key.OemOpenBrackets => "LeftBracket", Key.OemCloseBrackets => "RightBracket", Key.OemPipe => "Backslash", Key.OemSemicolon => "Semicolon", Key.OemQuotes => "Quote", Key.OemComma => "Comma", Key.OemPeriod => "Period", Key.OemQuestion => "Slash", _ => null
        };
    }
}
