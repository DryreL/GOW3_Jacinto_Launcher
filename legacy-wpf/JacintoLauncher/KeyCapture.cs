using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace JacintoLauncher;

internal static class KeyCapture
{
	public static string Ask(Window dono, string acao)
	{
		string escolhida = null;
		TextBlock texto = new TextBlock
		{
			Text = "Press a key or mouse button for\n\"" + acao + "\"\n\nEsc cancels",
			Foreground = Brushes.White,
			FontSize = 15.0,
			TextAlignment = TextAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			HorizontalAlignment = HorizontalAlignment.Center
		};
		Window w = new Window
		{
			Title = "Press a key",
			Owner = dono,
			Width = 420.0,
			Height = 200.0,
			WindowStyle = WindowStyle.None,
			ResizeMode = ResizeMode.NoResize,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			Background = new SolidColorBrush(Color.FromRgb(27, 27, 27)),
			Content = new Border
			{
				BorderBrush = new SolidColorBrush(Color.FromRgb(192, 39, 45)),
				BorderThickness = new Thickness(1.0),
				Child = texto
			}
		};
		w.PreviewKeyDown += delegate(object s, KeyEventArgs e)
		{
			//IL_0008: Unknown result type (might be due to invalid IL or missing references)
			//IL_000f: Invalid comparison between Unknown and I4
			//IL_001e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0028: Invalid comparison between Unknown and I4
			//IL_0033: Unknown result type (might be due to invalid IL or missing references)
			//IL_002b: Unknown result type (might be due to invalid IL or missing references)
			e.Handled = true;
			if ((int)e.Key == 13)
			{
				w.Close();
			}
			else
			{
				string text = NomeDaTecla(((int)e.Key == 156) ? e.SystemKey : e.Key);
				if (text == null)
				{
					texto.Text = "That key is not supported here.\nTry another one, or Esc to cancel.";
				}
				else
				{
					escolhida = text;
					w.Close();
				}
			}
		};
		w.PreviewMouseDown += delegate(object s, MouseButtonEventArgs e)
		{
			e.Handled = true;
			switch (e.ChangedButton)
			{
			case MouseButton.Left:
				escolhida = "LeftMouseButton";
				break;
			case MouseButton.Right:
				escolhida = "RightMouseButton";
				break;
			case MouseButton.Middle:
				escolhida = "MiddleMouseButton";
				break;
			case MouseButton.XButton1:
				escolhida = "ThumbMouseButton";
				break;
			case MouseButton.XButton2:
				escolhida = "ThumbMouseButton2";
				break;
			}
			if (escolhida != null)
			{
				w.Close();
			}
		};
		w.PreviewMouseWheel += delegate(object s, MouseWheelEventArgs e)
		{
			e.Handled = true;
			escolhida = ((e.Delta > 0) ? "MouseScrollUp" : "MouseScrollDown");
			w.Close();
		};
		w.ShowDialog();
		return escolhida;
	}

	private static string NomeDaTecla(Key k)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0003: Invalid comparison between Unknown and I4
		//IL_0018: Unknown result type (might be due to invalid IL or missing references)
		//IL_001b: Invalid comparison between Unknown and I4
		//IL_0005: Unknown result type (might be due to invalid IL or missing references)
		//IL_0008: Invalid comparison between Unknown and I4
		//IL_0030: Unknown result type (might be due to invalid IL or missing references)
		//IL_0033: Invalid comparison between Unknown and I4
		//IL_001d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0020: Invalid comparison between Unknown and I4
		//IL_0136: Unknown result type (might be due to invalid IL or missing references)
		//IL_0139: Unknown result type (might be due to invalid IL or missing references)
		//IL_0157: Expected I4, but got Unknown
		//IL_0038: Unknown result type (might be due to invalid IL or missing references)
		//IL_003a: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e8: Expected I4, but got Unknown
		//IL_0157: Unknown result type (might be due to invalid IL or missing references)
		//IL_015d: Unknown result type (might be due to invalid IL or missing references)
		//IL_019f: Expected I4, but got Unknown
		//IL_00e8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00eb: Unknown result type (might be due to invalid IL or missing references)
		//IL_0131: Expected I4, but got Unknown
		if ((int)k >= 44 && (int)k <= 69)
		{
			return k.ToString();
		}
		if ((int)k >= 90 && (int)k <= 101)
		{
			return k.ToString();
		}
		if ((int)k <= 89)
		{
			switch (k - 2)
			{
			default:
				switch (k - 74)
				{
				case 0:
					return "NumPadZero";
				case 1:
					return "NumPadOne";
				case 2:
					return "NumPadTwo";
				case 3:
					return "NumPadThree";
				case 4:
					return "NumPadFour";
				case 5:
					return "NumPadFive";
				case 6:
					return "NumPadSix";
				case 7:
					return "NumPadSeven";
				case 8:
					return "NumPadEight";
				case 9:
					return "NumPadNine";
				case 11:
					return "Add";
				case 13:
					return "Subtract";
				case 10:
					return "Multiply";
				case 15:
					return "Divide";
				case 14:
					return "Decimal";
				}
				break;
			case 32:
				return "Zero";
			case 33:
				return "One";
			case 34:
				return "Two";
			case 35:
				return "Three";
			case 36:
				return "Four";
			case 37:
				return "Five";
			case 38:
				return "Six";
			case 39:
				return "Seven";
			case 40:
				return "Eight";
			case 41:
				return "Nine";
			case 16:
				return "SpaceBar";
			case 1:
				return "Tab";
			case 4:
				return "Enter";
			case 0:
				return "BackSpace";
			case 6:
				return "CapsLock";
			case 22:
				return "Up";
			case 24:
				return "Down";
			case 21:
				return "Left";
			case 23:
				return "Right";
			case 20:
				return "Home";
			case 19:
				return "End";
			case 17:
				return "PageUp";
			case 18:
				return "PageDown";
			case 29:
				return "Insert";
			case 30:
				return "Delete";
			case 2:
			case 3:
			case 5:
			case 7:
			case 8:
			case 9:
			case 10:
			case 11:
			case 12:
			case 13:
			case 14:
			case 15:
			case 25:
			case 26:
			case 27:
			case 28:
			case 31:
				break;
			}
		}
		else
		{
			switch (k - 116)
			{
			case 2:
				return "LeftControl";
			case 3:
				return "RightControl";
			case 0:
				return "LeftShift";
			case 1:
				return "RightShift";
			case 4:
				return "LeftAlt";
			case 5:
				return "RightAlt";
			}
			switch (k - 140)
			{
			case 6:
				return "Tilde";
			case 3:
				return "Hyphen";
			case 1:
				return "Equals";
			case 9:
				return "LeftBracket";
			case 11:
				return "RightBracket";
			case 10:
			case 14:
				return "Backslash";
			case 0:
				return "Semicolon";
			case 12:
				return "Quote";
			case 2:
				return "Comma";
			case 4:
				return "Period";
			case 5:
				return "Slash";
			}
		}
		return null;
	}
}
