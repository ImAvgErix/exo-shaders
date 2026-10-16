using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SCSKiller.Core.App;

public sealed record StagedUpdate(string Channel, string Version, string FileName, long Size, string Sha256, IReadOnlyDictionary<string, SignedFeed> Feeds)
{
	private static string FileIn(string dataDir)
	{
		return Path.Combine(dataDir, "update-staged.json");
	}

	public static StagedUpdate? Load(string dataDir)
	{
		try
		{
			StagedUpdate stagedUpdate = JsonSerializer.Deserialize<StagedUpdate>(File.ReadAllText(FileIn(dataDir)));
			return ((object)stagedUpdate != null && stagedUpdate.Channel != null && stagedUpdate.Version != null && stagedUpdate.FileName != null && stagedUpdate.Sha256 != null && stagedUpdate.Feeds != null && AppVersion.Parse(stagedUpdate.Version) != null) ? stagedUpdate : null;
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is JsonException) ? true : false)
		{
			return null;
		}
	}

	public void Save(string dataDir)
	{
		Directory.CreateDirectory(dataDir);
		File.WriteAllText(FileIn(dataDir), JsonSerializer.Serialize(this));
	}

	public static void Forget(string dataDir)
	{
		try
		{
			File.Delete(FileIn(dataDir));
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
		}
	}

	public string? Refused(IReadOnlyDictionary<string, string> keys)
	{
		int num = Array.IndexOf(UpdateChannels.All, Channel);
		if (num < 0)
		{
			return "unknown channel '" + Channel + "'";
		}
		if (Feeds.Count == 0)
		{
			return "no signed feed";
		}
		bool flag = false;
		foreach (var (text2, signedFeed2) in Feeds)
		{
			int num2 = Array.IndexOf(UpdateChannels.All, text2);
			if (num2 < 0 || num2 > num)
			{
				return $"the {text2} feed isn't read on the {Channel} channel";
			}
			try
			{
				FeedTrust.Verify(keys, text2, signedFeed2.Feed, signedFeed2.Sig);
			}
			catch (FeedRejectedException ex)
			{
				return "the " + text2 + " feed: " + ex.Message;
			}
			flag |= Lists(signedFeed2.Feed);
		}
		if (!flag)
		{
			return "no signed feed lists " + FileName + " as recorded";
		}
		return null;
	}

	private bool Lists(byte[] feed)
	{
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(feed);
			return jsonDocument.RootElement.GetProperty("Assets").EnumerateArray().Any((JsonElement a) =>
			{
				if (a.GetProperty("FileName").GetString() == FileName)
				{
					AppVersion appVersion = AppVersion.Parse(a.GetProperty("Version").GetString());
					if ((object)appVersion != null && appVersion == AppVersion.Parse(Version) && string.Equals(a.GetProperty("SHA256").GetString(), Sha256, StringComparison.OrdinalIgnoreCase) && a.GetProperty("Size").GetInt64() == Size)
					{
						JsonElement property = a.GetProperty("Type");
						if (property.ValueKind != JsonValueKind.String)
						{
							return property.GetInt32() == 1;
						}
						return property.GetString() == "Full";
					}
				}
				return false;
			});
		}
		catch (Exception ex) when ((ex is JsonException || ex is KeyNotFoundException || ex is InvalidOperationException || ex is FormatException) ? true : false)
		{
			return false;
		}
	}

	public StartStep AtStart(AppVersion current, bool handedOver)
	{
		int num = AppVersion.Parse(Version).CompareTo(current);
		if (num != 0)
		{
			if (!handedOver)
			{
				if (num <= 0)
				{
					return StartStep.Older;
				}
				return StartStep.Apply;
			}
			return StartStep.Failed;
		}
		return StartStep.Installed;
	}
}
