using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SCSKiller.Core.App;

namespace SCSKiller.Core.FromSoft;

public sealed class SoulsKeys(string dataDir, Func<string, byte[]?>? download = null, string uxmSha256 = "e016cb66fed930d70a60d982b25fab9ec3729bd7fb1f48bb35c6bf059ca23441")
{
	public const string Uxm = "https://raw.githubusercontent.com/Nordgaren/UXM-Selective-Unpack/9501be87e272b6dae55e60a13c3f4753ca6fb3bb/UXM/ArchiveKeys.cs";

	public const string UxmSha256 = "e016cb66fed930d70a60d982b25fab9ec3729bd7fb1f48bb35c6bf059ca23441";

	private static readonly byte[] Begin = "-----BEGIN RSA PUBLIC KEY-----"u8.ToArray();

	private static readonly byte[] End = "-----END RSA PUBLIC KEY-----"u8.ToArray();

	public string KeyFile(Game g)
	{
		return Path.Combine(new AppStore(dataDir).GameDir(g.Id), "archive.keys");
	}

	private static string Stamp(string exe)
	{
		FileInfo fileInfo = new FileInfo(exe);
		if (fileInfo == null || !fileInfo.Exists)
		{
			return "";
		}
		return $"{fileInfo.Length}:{fileInfo.LastWriteTimeUtc.Ticks}";
	}

	public Dictionary<string, string> Get(Game game, IReadOnlyDictionary<string, string> archives, string? uxm, out List<string> missing, IProgress<string>? log = null)
	{
		string path = KeyFile(game);
		string[] source = (File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>());
		string[] array = source.FirstOrDefault((string l) => l.StartsWith("exe "))?.Split(' ');
		Dictionary<string, string> stored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (string item in source.Where((string l) => !l.StartsWith("exe ")))
		{
			string[] array2 = item.Trim().Split(' ', 2, StringSplitOptions.TrimEntries);
			if (array2 != null && array2.Length == 2)
			{
				string key = array2[0];
				string value = array2[1];
				stored[key] = value;
			}
		}
		Dictionary<string, string> keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var (key2, bhd) in archives)
		{
			if (stored.TryGetValue(key2, out string value2) && Opens(bhd, value2))
			{
				keys[key2] = value2;
			}
		}
		missing = Missing();
		if (missing.Count == 0)
		{
			return keys;
		}
		bool write = false;
		string element = ((array == null) ? null : string.Join(' ', array));
		string text3 = Stamp(game.ExePath);
		if (text3 != "")
		{
			if (array != null && array.Length == 3)
			{
				string text4 = array[2];
				if (text4 == text3)
				{
					goto IL_026d;
				}
			}
			try
			{
				byte[] array3 = File.ReadAllBytes(game.ExePath);
				string text5 = Convert.ToHexStringLower(SHA256.HashData(array3));
				if (array != null && array.Length >= 2)
				{
					string text6 = array[1];
					if (!(text6 != text5))
					{
						goto IL_021e;
					}
				}
				Match(Pem(array3));
				goto IL_021e;
				IL_021e:
				element = "exe " + text5 + " " + text3;
				write = true;
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
			}
		}
		goto IL_026d;
		IL_026d:
		if (keys.Count < archives.Count && uxm != null)
		{
			byte[] array4 = (download ?? new Func<string, byte[]>(Download.Bytes))("https://raw.githubusercontent.com/Nordgaren/UXM-Selective-Unpack/9501be87e272b6dae55e60a13c3f4753ca6fb3bb/UXM/ArchiveKeys.cs");
			if (array4 != null)
			{
				string text7 = Convert.ToHexStringLower(SHA256.HashData(array4));
				if (text7 == uxmSha256)
				{
					Match(UxmKeys(Encoding.UTF8.GetString(array4), uxm));
				}
				else
				{
					log?.Report($"{"https://raw.githubusercontent.com/Nordgaren/UXM-Selective-Unpack/9501be87e272b6dae55e60a13c3f4753ca6fb3bb/UXM/ArchiveKeys.cs"}: SHA-256 {text7}, not the pinned {uxmSha256}: not used");
				}
			}
		}
		missing = Missing();
		if (!write)
		{
			return keys;
		}
		Directory.CreateDirectory(Path.GetDirectoryName(path));
		File.WriteAllLines(path, (from p in stored.OrderBy((KeyValuePair<string, string> p) => p.Key, StringComparer.OrdinalIgnoreCase)
			select p.Key + " " + p.Value).Prepend(element).OfType<string>());
		return keys;
		void Match(List<string> found)
		{
			foreach (string name in Missing())
			{
				string text8 = found.FirstOrDefault((string k) => Opens(archives[name], k));
				if (text8 != null)
				{
					Dictionary<string, string> dictionary = stored;
					string key3 = name;
					string value3 = (keys[name] = text8);
					dictionary[key3] = value3;
					write = true;
				}
			}
		}
		List<string> Missing()
		{
			return archives.Keys.Where((string a) => !keys.ContainsKey(a)).ToList();
		}
	}

	public IEnumerable<string> Unstored(Game game, IEnumerable<string> archives)
	{
		string path = KeyFile(game);
		HashSet<string> stored = (from l in File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>()
			where !l.StartsWith("exe ")
			select l.Trim().Split(' ', 2, StringSplitOptions.TrimEntries) into p
			where p.Length == 2
			select p[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
		return archives.Where((string a) => !stored.Contains(a));
	}

	public static List<string> UxmKeys(string source, string section)
	{
		Match match = Regex.Match(source, "\\b" + Regex.Escape(section) + "\\s*=");
		if (match == null || !match.Success)
		{
			return new List<string>();
		}
		int num = source.IndexOf("};", match.Index, StringComparison.Ordinal);
		Encoding aSCII = Encoding.ASCII;
		int index = match.Index;
		return Pem(aSCII.GetBytes(source.Substring(index, ((num < 0) ? source.Length : num) - index)));
	}

	public static List<string> Pem(ReadOnlySpan<byte> b)
	{
		List<string> list = new List<string>();
		int num = 0;
		int num2;
		while ((num2 = b.Slice(num).IndexOf(Begin)) >= 0)
		{
			ReadOnlySpan<byte> span = b.Slice(num + num2 + Begin.Length);
			int num3 = span.IndexOf(End);
			if (num3 > 0 && num3 < 4096)
			{
				string text = string.Concat(from c in Encoding.ASCII.GetString(span.Slice(0, num3))
					where !char.IsWhiteSpace(c)
					select c);
				if (!list.Contains(text) && Parses(text))
				{
					list.Add(text);
				}
			}
			num += num2 + Begin.Length;
		}
		return list;
	}

	private static bool Parses(string key)
	{
		try
		{
			using RSA rSA = RSA.Create();
			rSA.ImportRSAPublicKey(Convert.FromBase64String(key), out var _);
			return true;
		}
		catch (Exception ex) when ((ex is FormatException || ex is CryptographicException) ? true : false)
		{
			return false;
		}
	}

	public static bool Opens(string bhd, string key)
	{
		try
		{
			byte[] array = new byte[256];
			using (FileStream fileStream = File.OpenRead(bhd))
			{
				if (fileStream.ReadAtLeast(array, array.Length, throwOnEndOfStream: false) != array.Length)
				{
					return false;
				}
			}
			return ((ReadOnlySpan<byte>)Souls.DecryptBhd(array, key).AsSpan()).StartsWith("BHD5"u8);
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is FormatException || ex is CryptographicException || ex is InvalidDataException || ex is AggregateException) ? true : false)
		{
			return false;
		}
	}
}
