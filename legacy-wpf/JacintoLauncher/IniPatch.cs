using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace JacintoLauncher;

public static class IniPatch
{
	public static string FindGearEngineIni(string gameDir)
	{
		string text = Path.Combine(gameDir, "GearGame", "Config", "GearEngine.ini");
		if (!File.Exists(text))
		{
			return null;
		}
		return text;
	}

	public static string Read(string iniPath, string section, string key)
	{
		if (!File.Exists(iniPath))
		{
			return null;
		}
		bool flag = false;
		string[] array = File.ReadAllLines(iniPath);
		for (int i = 0; i < array.Length; i++)
		{
			string text = array[i].Trim();
			if (text.StartsWith("["))
			{
				flag = string.Equals(text, "[" + section + "]", StringComparison.OrdinalIgnoreCase);
			}
			else if (flag && text.Length != 0 && !text.StartsWith(";"))
			{
				int num = text.IndexOf('=');
				if (num > 0 && string.Equals(text.Substring(0, num).Trim(), key, StringComparison.OrdinalIgnoreCase))
				{
					return text.Substring(num + 1).Trim();
				}
			}
		}
		return null;
	}

	public static void Write(string iniPath, string section, string key, string value)
	{
		List<string> list = new List<string>(File.ReadAllLines(iniPath));
		int num = -1;
		int num2 = list.Count;
		for (int i = 0; i < list.Count; i++)
		{
			string text = list[i].Trim();
			if (text.StartsWith("["))
			{
				if (num < 0 && string.Equals(text, "[" + section + "]", StringComparison.OrdinalIgnoreCase))
				{
					num = i;
				}
				else if (num >= 0)
				{
					num2 = i;
					break;
				}
			}
		}
		if (num < 0)
		{
			list.Add("");
			list.Add("[" + section + "]");
			list.Add(key + "=" + value);
		}
		else
		{
			bool flag = false;
			for (int j = num + 1; j < num2; j++)
			{
				string text2 = list[j].Trim();
				int num3 = text2.IndexOf('=');
				if (num3 > 0 && !text2.StartsWith(";") && string.Equals(text2.Substring(0, num3).Trim(), key, StringComparison.OrdinalIgnoreCase))
				{
					list[j] = key + "=" + value;
					flag = true;
					break;
				}
			}
			if (!flag)
			{
				list.Insert(num2, key + "=" + value);
			}
		}
		File.WriteAllLines(iniPath, list, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
	}
}
