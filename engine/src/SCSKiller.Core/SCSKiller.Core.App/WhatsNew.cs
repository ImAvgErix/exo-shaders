using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.RegularExpressions.Generated;

namespace SCSKiller.Core.App;

public static class WhatsNew
{
	public static string Changelog => field ?? (field = ContentFile.Embedded("SCSKiller.Core.CHANGELOG.md"));

	[GeneratedCode("System.Text.RegularExpressions.Generator", "10.0.14.42308")]
	private static Regex Released()
	{
		return _003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Released_2.Instance;
	}

	[GeneratedCode("System.Text.RegularExpressions.Generator", "10.0.14.42308")]
	private static Regex Inline()
	{
		return _003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Inline_3.Instance;
	}

	public static IReadOnlyList<Span> Spans(string text)
	{
		List<Span> list = new List<Span>();
		int num = 0;
		foreach (Match item2 in Inline().Matches(text))
		{
			if (!item2.Groups["u"].Success || (Uri.TryCreate(item2.Groups["u"].Value, UriKind.Absolute, out Uri result) && result.Scheme == Uri.UriSchemeHttps))
			{
				if (item2.Index > num)
				{
					int num2 = num;
					list.Add(new Span(text.Substring(num2, item2.Index - num2)));
				}
				List<Span> list2 = list;
				Span item;
				if (item2.Groups["b"].Success)
				{
					item = new Span(item2.Groups["b"].Value, SpanKind.Bold);
				}
				else
				{
					item = (item2.Groups["c"].Success ? new Span(item2.Groups["c"].Value, SpanKind.Code) : new Span(item2.Groups["t"].Value, SpanKind.Link, item2.Groups["u"].Value));
				}
				list2.Add(item);
				num = item2.Index + item2.Length;
			}
		}
		if (num < text.Length)
		{
			list.Add(new Span(text.Substring(num)));
		}
		return list;
	}

	private static string Key(AppVersion v)
	{
		return $"{v.Major}.{v.Minor}.{v.Patch}";
	}

	public static WhatsNewSection? Section(string changelog, string version)
	{
		List<string> list = new List<string>();
		bool flag = false;
		string[] array = changelog.Split('\n');
		for (int i = 0; i < array.Length; i++)
		{
			string text = array[i].TrimEnd('\r');
			if (text.StartsWith("## "))
			{
				flag = text.Contains("[" + version + "]");
			}
			else if (flag)
			{
				if (text.StartsWith("- "))
				{
					list.Add(text.Substring(2).Trim());
				}
				else if (text.StartsWith("  ") && text.Trim().Length > 0 && list.Count > 0)
				{
					List<string> list2 = list;
					int index = list2.Count - 1;
					list2[index] = list2[index] + " " + text.Trim();
				}
			}
		}
		if (list.Count <= 0)
		{
			return null;
		}
		return new WhatsNewSection(version, list.Select((string b) => FirstSentence(b)).ToList());
	}

	private static string FirstSentence(string s)
	{
		int num = s.IndexOf(". ", StringComparison.Ordinal);
		if (num <= 0)
		{
			return s;
		}
		return s.Substring(0, num + 1);
	}

	public static WhatsNewSection? ForAbout(AppVersion v)
	{
		WhatsNewSection? whatsNewSection = Section(Changelog, Key(v));
		if ((object)whatsNewSection == null)
		{
			Match match = (from l in Changelog.Split('\n')
				select Released().Match(l)).FirstOrDefault((Match m) => m.Success);
			if (match == null)
			{
				return null;
			}
			whatsNewSection = Section(Changelog, match.Groups[1].Value);
		}
		return whatsNewSection;
	}

	public static WhatsNewSection? AtStart(AppStore store, bool firstRun, AppVersion v, string? changelog = null)
	{
		NewsSeen newsSeen = store.LoadNewsSeen();
		if (newsSeen.WhatsNew == Key(v))
		{
			return null;
		}
		WhatsNewSection whatsNewSection = (firstRun ? null : Section(changelog ?? Changelog, Key(v)));
		if (firstRun || whatsNewSection != null)
		{
			store.SaveNewsSeen(newsSeen with
			{
				WhatsNew = Key(v)
			});
		}
		return whatsNewSection;
	}
}
