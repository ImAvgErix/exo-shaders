using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NSec.Cryptography;

namespace SCSKiller.Core.App;

public sealed class FeedTrust(AppStore store, IReadOnlyDictionary<string, string> keys)
{
	public static readonly IReadOnlyDictionary<string, string> ReleaseKeys = new Dictionary<string, string>
	{
		["rel-a"] = "sitj5K2fcZku26c/EvUo793SeSuVIb0YcN+6/mi0CwM=",
		["rel-b"] = "OBpxPkqkWZJIx6p89z8R4cEp52aMRcmeZQVHZQEZ0ns="
	};

	private static readonly SignatureAlgorithm Ed = SignatureAlgorithm.Ed25519;

	public static byte[] Message(string channel, string signedAt, string feedSha256)
	{
		return Encoding.UTF8.GetBytes($"scskiller-feed-v1\n{channel}\n{signedAt}\n{feedSha256}");
	}

	public static string Sign(ReadOnlySpan<byte> seed, string kid, string channel, byte[] feed, DateTimeOffset signedAt)
	{
		using Key key = Key.Import(Ed, seed, KeyBlobFormat.RawPrivateKey, default(KeyCreationParameters));
		string text = signedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
		string text2 = Convert.ToHexStringLower(SHA256.HashData(feed));
		return JsonSerializer.Serialize(new
		{
			v = 1,
			channel = channel,
			signed_at = text,
			feed_sha256 = text2,
			kid = kid,
			sig = Convert.ToBase64String(Ed.Sign(key, Message(channel, text, text2)))
		});
	}

	public static (string Seed, string Public) NewKey()
	{
		SignatureAlgorithm ed = Ed;
		KeyCreationParameters creationParameters = new KeyCreationParameters
		{
			ExportPolicy = KeyExportPolicies.AllowPlaintextExport
		};
		using Key key = Key.Create(ed, in creationParameters);
		return (Seed: Convert.ToBase64String(key.Export(KeyBlobFormat.RawPrivateKey)), Public: Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey)));
	}

	public void Accept(string channel, byte[] feed, byte[] sig)
	{
		DateTimeOffset dateTimeOffset = Verify(keys, channel, feed, sig);
		lock (store)
		{
			Dictionary<string, DateTimeOffset> dictionary = store.LoadFeedTimes();
			if (dictionary.TryGetValue(channel, out var value) && dateTimeOffset < value)
			{
				throw new FeedRejectedException($"an older feed than one already seen ({value:u})");
			}
			dictionary[channel] = dateTimeOffset;
			store.SaveFeedTimes(dictionary);
		}
	}

	public static DateTimeOffset Verify(IReadOnlyDictionary<string, string> keys, string channel, byte[] feed, byte[] sig)
	{
		string text3;
		string text4;
		string text5;
		byte[] array;
		try
		{
			JsonElement rootElement = JsonDocument.Parse(sig).RootElement;
			if (rootElement.GetProperty("v").GetInt32() != 1)
			{
				throw new FeedRejectedException("unknown signature version");
			}
			if (rootElement.GetProperty("channel").GetString() != channel)
			{
				throw new FeedRejectedException("the signature is for another channel");
			}
			string? text = rootElement.GetProperty("signed_at").GetString();
			string? text2 = rootElement.GetProperty("feed_sha256").GetString();
			text3 = rootElement.GetProperty("kid").GetString();
			text4 = text2;
			text5 = text;
			array = Convert.FromBase64String(rootElement.GetProperty("sig").GetString());
		}
		catch (Exception ex) when ((ex is JsonException || ex is KeyNotFoundException || ex is InvalidOperationException || ex is FormatException || ex is NullReferenceException) ? true : false)
		{
			throw new FeedRejectedException("malformed signature file");
		}
		if (!keys.TryGetValue(text3, out string value))
		{
			throw new FeedRejectedException("unknown key '" + text3 + "'");
		}
		if (!PublicKey.TryImport(Ed, Convert.FromBase64String(value), KeyBlobFormat.RawPublicKey, out PublicKey result) || !Ed.Verify(result, Message(channel, text5, text4), array))
		{
			throw new FeedRejectedException("bad signature");
		}
		if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(feed)), text4, StringComparison.OrdinalIgnoreCase))
		{
			throw new FeedRejectedException("the feed doesn't match its signature");
		}
		if (!DateTimeOffset.TryParse(text5, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var result2))
		{
			throw new FeedRejectedException("malformed signed_at");
		}
		return result2;
	}
}
