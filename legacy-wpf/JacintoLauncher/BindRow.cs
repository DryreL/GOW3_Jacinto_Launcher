using System.ComponentModel;

namespace JacintoLauncher;

public class BindRow : INotifyPropertyChanged
{
	private string key;

	public string Action { get; set; }

	public string Command { get; set; }

	public string Key
	{
		get
		{
			return key;
		}
		set
		{
			key = value;
			this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Key"));
		}
	}

	public string KeyOriginal { get; set; }

	public event PropertyChangedEventHandler PropertyChanged;
}
