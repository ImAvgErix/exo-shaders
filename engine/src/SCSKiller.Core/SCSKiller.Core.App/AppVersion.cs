using System;
using System.CodeDom.Compiler;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Text.RegularExpressions.Generated;

namespace SCSKiller.Core.App;

public sealed record AppVersion(int Major, int Minor, int Patch, string Pre) : IComparable<AppVersion>
{
	public static AppVersion Current { get; } = Parse((Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly).GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion) ?? new AppVersion(0, 0, 0, "internal.0");

	public string Channel
	{
		get
		{
			if (Pre.Length != 0)
			{
				string text = Pre.Split('.')[0];
				if (!UpdateChannels.All.Contains(text))
				{
					return "internal";
				}
				return text;
			}
			return "stable";
		}
	}

	[GeneratedCode("System.Text.RegularExpressions.Generator", "10.0.14.42308")]
	private static Regex Form()
	{
		return _003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Form_1.Instance;
	}

	public static AppVersion? Parse(string? s)
	{
		if (s != null)
		{
			Match match = Form().Match(s.Trim());
			if (match != null && match.Success && int.TryParse(match.Groups[1].Value, out var result) && int.TryParse(match.Groups[2].Value, out var result2) && int.TryParse(match.Groups[3].Value, out var result3))
			{
				return new AppVersion(result, result2, result3, match.Groups[4].Value);
			}
		}
		return null;
	}

	public override string ToString()
	{
		if (Pre.Length == 0)
		{
			return $"{Major}.{Minor}.{Patch}";
		}
		return $"{Major}.{Minor}.{Patch}-{Pre}";
	}

	public int CompareTo(AppVersion? o)
	{
		if ((object)o == null)
		{
			return 1;
		}
		int num = (Major, Minor, Patch).CompareTo((o.Major, o.Minor, o.Patch));
		if (num != 0)
		{
			return num;
		}
		if (Pre.Length == 0 || o.Pre.Length == 0)
		{
			return (Pre.Length == 0).CompareTo(o.Pre.Length == 0);
		}
		string[] array = Pre.Split('.');
		string[] array2 = o.Pre.Split('.');
		for (int i = 0; i < Math.Min(array.Length, array2.Length); i++)
		{
			bool flag = array[i].All(char.IsAsciiDigit);
			bool flag2 = array2[i].All(char.IsAsciiDigit);
			if (flag & flag2)
			{
				num = Numeric(array[i], array2[i]);
			}
			else
			{
				num = (flag ? (-1) : (flag2 ? 1 : string.CompareOrdinal(array[i], array2[i])));
			}
			if (num != 0)
			{
				return Math.Sign(num);
			}
		}
		return array.Length.CompareTo(array2.Length);
		static int Numeric(string a, string b)
		{
			string text = a.TrimStart('0');
			b = b.TrimStart('0');
			a = text;
			if (a.Length == b.Length)
			{
				return string.CompareOrdinal(a, b);
			}
			return a.Length.CompareTo(b.Length);
		}
	}
}
