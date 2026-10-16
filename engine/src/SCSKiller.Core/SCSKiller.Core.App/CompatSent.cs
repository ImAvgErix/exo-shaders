using System;
using System.Collections.Generic;
using System.IO;

namespace SCSKiller.Core.App;

public sealed record CompatSent(CompatChoice Choice, DateTimeOffset At)
{
	public const string FileName = "compat-sent.json";

	public static CompatSent? For(string gameDir, string buildKey)
	{
		return Community.Read<Dictionary<string, CompatSent>>(Path.Combine(gameDir, "compat-sent.json"))?.GetValueOrDefault(buildKey);
	}

	public static void Save(string gameDir, string buildKey, CompatSent sent)
	{
		string path = Path.Combine(gameDir, "compat-sent.json");
		Dictionary<string, CompatSent> dictionary = Community.Read<Dictionary<string, CompatSent>>(path) ?? new Dictionary<string, CompatSent>();
		dictionary[buildKey] = sent;
		Directory.CreateDirectory(gameDir);
		Community.Write(path, dictionary);
	}
}
