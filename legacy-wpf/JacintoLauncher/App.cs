using System;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;

namespace JacintoLauncher;

public class App : Application
{
	[Serializable]
	[CompilerGenerated]
	private sealed class _003C_003Ec
	{
		public static readonly _003C_003Ec _003C_003E9 = new _003C_003Ec();

		public static DispatcherUnhandledExceptionEventHandler _003C_003E9__0_0;

		internal void _003COnStartup_003Eb__0_0(object s, DispatcherUnhandledExceptionEventArgs args)
		{
			MessageBox.Show(args.Exception.ToString(), "Launcher error", MessageBoxButton.OK, MessageBoxImage.Hand);
			args.Handled = true;
		}
	}

	private bool _contentLoaded;

	protected override void OnStartup(StartupEventArgs e)
	{
		//IL_001c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0027: Expected O, but got Unknown
		base.OnStartup(e);
		object obj = _003C_003Ec._003C_003E9__0_0;
		if (obj == null)
		{
			DispatcherUnhandledExceptionEventHandler val = delegate(object s, DispatcherUnhandledExceptionEventArgs args)
			{
				MessageBox.Show(args.Exception.ToString(), "Launcher error", MessageBoxButton.OK, MessageBoxImage.Hand);
				args.Handled = true;
			};
			_003C_003Ec._003C_003E9__0_0 = val;
			obj = (object)val;
		}
		base.DispatcherUnhandledException += (DispatcherUnhandledExceptionEventHandler)obj;
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.29.0")]
	public void InitializeComponent()
	{
		if (!_contentLoaded)
		{
			_contentLoaded = true;
			base.StartupUri = new Uri("MainWindow.xaml", UriKind.Relative);
			Uri resourceLocator = new Uri("/GoW3Jacinto_Launcher;component/app.xaml", UriKind.Relative);
			Application.LoadComponent(this, resourceLocator);
		}
	}

	[STAThread]
	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.29.0")]
	public static void Main()
	{
		App app = new App();
		app.InitializeComponent();
		app.Run();
	}
}
