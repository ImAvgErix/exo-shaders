using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using CUE4Parse.Encryption.Aes;
using SCSKiller.Core.App;

namespace SCSKiller.Core.Unreal;

public sealed class UnrealKeys(string dataDir)
{
	private const int ScanVersion = 4;

	private const string OutsideText = "code outside .text";

	private const int MaxCandidates = 65536;

	private string KeyFile(Game g)
	{
		return Path.Combine(new AppStore(dataDir).GameDir(g.Id), "aes.key");
	}

	private string ScanFile(Game g)
	{
		return Path.Combine(new AppStore(dataDir).GameDir(g.Id), "aes.scan");
	}

	private static string Stamp(string exe)
	{
		FileInfo fileInfo = new FileInfo(exe);
		if (fileInfo == null || !fileInfo.Exists)
		{
			return "";
		}
		return $"{4}:{fileInfo.Length}:{fileInfo.LastWriteTimeUtc.Ticks}";
	}

	private string AutoFile(Game g)
	{
		return Path.Combine(new AppStore(dataDir).GameDir(g.Id), "aes.auto");
	}

	public static string Short(FAesKey key)
	{
		string text = key.KeyString.Replace("0x", "", StringComparison.OrdinalIgnoreCase);
		return text.Substring(0, Math.Min(4, text.Length)) + "…";
	}

	/// <summary>Where the stored key came from when Exo Shaders found it by itself ("public list"), else null.</summary>
	public string? AutoSource(string gameId)
	{
		string path = Path.Combine(new AppStore(dataDir).GameDir(gameId), "aes.auto");
		string path2 = Path.Combine(new AppStore(dataDir).GameDir(gameId), "aes.key");
		try
		{
			return (File.Exists(path) && File.Exists(path2)) ? File.ReadLines(path).FirstOrDefault() : null;
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return null;
		}
	}

	/// <summary>The part of the detect stamp for the public list: its keys' hash, while no key is stored (and a due refetch in the background).</summary>
	public string PublicStamp(Game game, IEnumerable<string?>? hints = null)
	{
		PublicKeys.Source source = PublicKeys.For(game, hints);
		if (source == null || File.Exists(KeyFile(game)))
		{
			return "";
		}
		PublicKeys publicKeys = new PublicKeys(dataDir);
		publicKeys.RefreshInBackground(source, Stamp(game.ExePath));
		return "pk:" + publicKeys.Stamp(source);
	}

	private void Store(Game game, FAesKey key, string? auto)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(KeyFile(game)));
		File.WriteAllText(KeyFile(game), key.KeyString);
		if (auto != null)
		{
			File.WriteAllText(AutoFile(game), auto);
		}
		else
		{
			File.Delete(AutoFile(game));
		}
	}

	public FAesKey? Get(Game game, Func<FAesKey, bool> opens, out string why, IEnumerable<string?>? hints = null)
	{
		string stale = null;
		FAesKey fAesKey = null;
		if (File.Exists(KeyFile(game)))
		{
			fAesKey = Parse(File.ReadAllText(KeyFile(game)));
			if (fAesKey != null)
			{
				if (opens(fAesKey))
				{
					string text0 = AutoSource(game.Id);
					why = ((text0 != null) ? ("stored key (from the community's " + text0 + ")") : "stored key");
					return fAesKey;
				}
				// A patch changed the key: look for the new one as if none were stored.
				stale = "the stored key (" + Short(fAesKey) + ") no longer opens the game's files";
			}
		}
		string text = Stamp(game.ExePath);
		string scanWhy = null;
		if (File.Exists(ScanFile(game)))
		{
			string[] array = File.ReadAllLines(ScanFile(game));
			if (array != null && array.Length >= 2 && array[0] == text)
			{
				scanWhy = array[1];
			}
		}
		if (scanWhy == null)
		{
			Stopwatch stopwatch = Stopwatch.StartNew();
			FAesKey fAesKey2 = null;
			string how;
			bool unreadable = false;
			try
			{
				fAesKey2 = Scan(game.ExePath, opens, out how);
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
				how = "encrypted; the game's exe can't be read, so it isn't scanned for the key";
				unreadable = true;
			}
			catch (BadImageFormatException)
			{
				how = "not found: the exe's headers don't read as a PE image (a protector's?)";
			}
			scanWhy = (unreadable ? how : $"{how} ({stopwatch.Elapsed.TotalSeconds:F1} s)");
			Directory.CreateDirectory(Path.GetDirectoryName(KeyFile(game)));
			if (fAesKey2 != null)
			{
				Store(game, fAesKey2, null);
				File.Delete(ScanFile(game));
				why = scanWhy;
				return fAesKey2;
			}
			if (!unreadable)
			{
				File.WriteAllLines(ScanFile(game), new string[2] { text, scanWhy });
			}
		}
		why = ((stale != null) ? (stale + "; " + scanWhy) : scanWhy);
		PublicKeys.Source source = PublicKeys.For(game, hints);
		if (source == null)
		{
			if (!File.Exists(KeyFile(game)))
			{
				why += ": give the key by hand";
			}
			return null;
		}
		IReadOnlyList<string> readOnlyList = new PublicKeys(dataDir).Candidates(source, text, out string how2);
		int num = 0;
		foreach (string item in readOnlyList)
		{
			FAesKey fAesKey3 = Parse(item);
			if (fAesKey3 == null || (fAesKey != null && fAesKey3.KeyString == fAesKey.KeyString))
			{
				continue;
			}
			num++;
			if (opens(fAesKey3))
			{
				Store(game, fAesKey3, PublicKeys.SourceName + "\n" + source.Urls[0] + "\n" + DateTime.UtcNow.ToString("o"));
				why = $"{why}; key {Short(fAesKey3)} from the community's {PublicKeys.SourceName} ({how2}, {num} tried)";
				return fAesKey3;
			}
		}
		why = $"{why}; none of the {num} keys in the community's {PublicKeys.SourceName} opens the game's files ({how2})";
		return null;
	}

	public string KeyStamp(Game game)
	{
		if (!File.Exists(KeyFile(game)))
		{
			return "";
		}
		return File.GetLastWriteTimeUtc(KeyFile(game)).Ticks.ToString();
	}

	public FAesKey? Stored(Game game)
	{
		if (!File.Exists(KeyFile(game)))
		{
			return null;
		}
		return Parse(File.ReadAllText(KeyFile(game)));
	}

	public bool Set(Game game, string key, Func<FAesKey, bool> opens)
	{
		FAesKey fAesKey = Parse(key);
		if (fAesKey == null || !opens(fAesKey))
		{
			return false;
		}
		Directory.CreateDirectory(Path.GetDirectoryName(KeyFile(game)));
		Store(game, fAesKey, null);
		File.Delete(ScanFile(game));
		return true;
	}

	internal static FAesKey? Parse(string text)
	{
		string text2 = text.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);
		if (text2.Length != 64 || !text2.All(Uri.IsHexDigit))
		{
			return null;
		}
		return new FAesKey("0x" + text2);
	}

	public static FAesKey? Scan(string exePath, Func<FAesKey, bool> opens, out string how)
	{
		FileStream f = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536);
		SectionHeader[] sections;
		HashSet<string> tried;
		FAesKey found;
		long unread;
		string pattern;
		try
		{
			if (f.Length > int.MaxValue)
			{
				how = "not found: the exe is over 2 GB, more than the scan reads";
				return null;
			}
			using (PEReader pEReader = new PEReader(f, PEStreamOptions.LeaveOpen))
			{
				sections = pEReader.PEHeaders.SectionHeaders.AsSpan().ToArray();
			}
			List<SectionHeader> list = sections.Where((SectionHeader s) => (s.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0 && s.PointerToRawData >= 0).ToList();
			tried = new HashSet<string>();
			found = null;
			unread = f.Length;
			pattern = "";
			byte[] array = new byte[4194368];
			foreach (SectionHeader item2 in list)
			{
				List<(long Pos, int Size, int Base, int Disp, object Val)> stores = new List<(long, int, int, int, object)>();
				(long, byte[])[] array2 = new (long, byte[])[16];
				(long, long, int)[] array3 = new (long, long, int)[16];
				long num = item2.PointerToRawData;
				for (long num2 = item2.PointerToRawData + item2.SizeOfRawData; num < num2; num += 4194304)
				{
					if (Done())
					{
						break;
					}
					f.Position = num;
					int num3 = (int)Math.Min(4194304L, num2 - num);
					int num4 = f.ReadAtLeast(array.AsSpan(0, num3 + 64), num3 + 64, throwOnEndOfStream: false);
					if (num4 == 0)
					{
						break;
					}
					unread -= num3;
					int num5 = (int)Math.Min(4194304L, num2 - 1 - num);
					for (int num6 = 1; num6 <= num5 && num6 + 16 < num4 && !Done(); num6++)
					{
						long num7 = num + num6;
						byte b = array[num6];
						bool flag;
						switch (b)
						{
						case 199:
						{
							int num8 = (((array[num6 - 1] & 0xF0) == 64) ? array[num6 - 1] : 0);
							int[] array4 = ((num8 == 0) ? new int[1] : new int[2] { num8, 0 });
							foreach (int num10 in array4)
							{
								if ((num10 & 0xA) == 0 && ((array[num6 + 1] >> 3) & 7) == 0)
								{
									int num11 = Mem(array, num6 + 1, num10 & 1, out var bas, out var disp);
									if (num11 > 0)
									{
										Store(num7, 4, bas, disp, array[(num6 + 1 + num11)..(num6 + 5 + num11)]);
									}
								}
							}
							continue;
						}
						case 72:
						case 73:
							flag = true;
							break;
						default:
							flag = false;
							break;
						}
						if (flag)
						{
							byte b2 = array[num6 + 1];
							if (b2 >= 184 && b2 <= 191)
							{
								array2[(array[num6 + 1] - 184) | ((b & 1) << 3)] = (num7, array[(num6 + 2)..(num6 + 10)]);
								continue;
							}
						}
						flag = (((uint)(b - 72) <= 1u || (uint)(b - 76) <= 1u) ? true : false);
						if (flag && array[num6 + 1] == 137)
						{
							int num12 = ((array[num6 + 2] >> 3) & 7) | ((b & 4) << 1);
							byte[] item = array2[num12].Item2;
							if (item != null && num7 - array2[num12].Item1 < 48 && Mem(array, num6 + 2, b & 1, out var bas2, out var disp2) > 0)
							{
								Store(num7, 8, bas2, disp2, item);
							}
							continue;
						}
						flag = b == 15;
						if (!flag)
						{
							bool flag2 = ((b == 102 || b == 243) ? true : false);
							flag = flag2 && (array[num6 + 1] == 15 || ((array[num6 + 1] & 0xF0) == 64 && array[num6 + 2] == 15));
						}
						if (flag || ((b & 0xF0) == 64 && array[num6 + 1] == 15))
						{
							int num9 = num6;
							flag = ((b == 102 || b == 243) ? true : false);
							int num13 = num9 + (flag ? 1 : 0);
							int num14 = (((array[num13] & 0xF0) == 64) ? array[num13++] : 0);
							if (array[num13] != 15)
							{
								continue;
							}
							byte b3 = array[num13 + 1];
							byte b4 = array[num13 + 2];
							byte b5 = b3;
							int num15 = ((b4 >> 3) & 7) | ((num14 & 4) << 1);
							flag = ((b5 == 16 || b5 == 40 || b5 == 111) ? true : false);
							if (flag && (b4 & 0xC7) == 5)
							{
								array3[num15] = (num7, FileOf(item2.VirtualAddress + (num7 - item2.PointerToRawData) + (num13 + 7 - num6) + BitConverter.ToInt32(array, num13 + 3)), 16);
								continue;
							}
							flag = ((b5 == 17 || b5 == 41 || b5 == 127) ? true : false);
							if (flag && (b4 & 0xC0) != 192)
							{
								(long, long, int) tuple = array3[num15];
								if (tuple.Item2 > 0 && tuple.Item3 == 16 && num7 - array3[num15].Item1 < 64 && Mem(array, num13 + 2, num14 & 1, out var bas3, out var disp3) > 0)
								{
									Store(num7, 16, bas3, disp3, array3[num15].Item2);
								}
							}
						}
						else
						{
							if ((uint)(b - 196) > 1u)
							{
								continue;
							}
							bool flag3 = b == 196;
							byte b6 = array[num6 + ((!flag3) ? 1 : 2)];
							if ((flag3 && (array[num6 + 1] & 0x1F) != 1) || ((b6 >> 3) & 0xF) != 15)
							{
								continue;
							}
							int num16 = num6 + (flag3 ? 3 : 2);
							byte b7 = array[num16];
							byte b8 = array[num16 + 1];
							byte b9 = b7;
							int num17 = ((b8 >> 3) & 7) | ((~array[num6 + 1] >> 4) & 8);
							int num18 = (((b6 & 4) != 0) ? 32 : 16);
							flag = ((b9 == 16 || b9 == 40 || b9 == 111) ? true : false);
							if (flag && (b8 & 0xC7) == 5)
							{
								array3[num17] = (num7, FileOf(item2.VirtualAddress + (num7 - item2.PointerToRawData) + (num16 + 6 - num6) + BitConverter.ToInt32(array, num16 + 2)), num18);
								continue;
							}
							flag = ((b9 == 17 || b9 == 41 || b9 == 127) ? true : false);
							if (flag && (b8 & 0xC0) != 192 && array3[num17].Item2 > 0 && array3[num17].Item3 == num18 && num7 - array3[num17].Item1 < 64 && Mem(array, num16 + 1, flag3 ? ((~array[num6 + 1] >> 5) & 1) : 0, out var bas4, out var disp4) > 0)
							{
								Store(num7, num18, bas4, disp4, array3[num17].Item2);
							}
						}
					}
				}
				if (Done())
				{
					break;
				}
				void Store(long pos, int size, int num20, int num21, object val)
				{
					stores.RemoveAll(((long Pos, int Size, int Base, int Disp, object Val) x) => pos - x.Pos > 160);
					stores.Add((pos, size, num20, num21, val));
					Dictionary<int, object> mine = (from x in stores
						where x.Size == size && x.Base == num20
						group x by x.Disp).ToDictionary((IGrouping<int, (long Pos, int Size, int Base, int Disp, object Val)> g) => g.Key, (IGrouping<int, (long Pos, int Size, int Base, int Disp, object Val)> g) => g.Last().Val);
					int count = 32 / size;
					foreach (int d0 in mine.Keys.Where((int num22) => num22 <= num21 && num21 < num22 + 32))
					{
						if (Enumerable.Range(0, count).All((int num22) => mine.ContainsKey(d0 + num22 * size)))
						{
							byte[] k = Enumerable.Range(0, count).SelectMany((int num22) => (!(mine[d0 + num22 * size] is byte[] array5)) ? At((long)mine[d0 + num22 * size], size) : array5).ToArray();
							string how2;
							if (size == 4)
							{
								how2 = "8 x mov dword, imm32";
							}
							else if (size == 8)
							{
								how2 = "4 x mov qword, imm64";
							}
							else
							{
								how2 = ((size == 16) ? "2 x 16 bytes from .rdata" : "32 bytes from .rdata");
							}
							Try(k, how2);
						}
					}
				}
			}
			long num19 = sections.Where((SectionHeader s) => s.Name == ".text").Sum((Func<SectionHeader, long>)((SectionHeader s) => s.SizeOfRawData));
			string text = $"code in {string.Join(", ", list.Select((SectionHeader s) => s.Name))}, .text {num19 >> 20} MB";
			string text3;
			if (found == null)
			{
				if (tried.Count < 65536 && unread >= 0)
				{
					string text2 = Protector(sections);
					if (text2 == null)
					{
						text3 = $"not found in the exe's code ({tried.Count} candidates)";
					}
					else
					{
						text3 = ((text2 == "code outside .text") ? ("not found: the exe looks protected (" + text + "), a static scan can't see the key") : $"not found: the exe is protected by {text2} ({text}), a static scan can't see the key");
					}
				}
				else
				{
					text3 = $"not found: the scan stopped at its limit ({tried.Count} candidates)";
				}
			}
			else
			{
				text3 = "found in the exe: " + pattern;
			}
			how = text3;
			return found;
		}
		finally
		{
			if (f != null)
			{
				((IDisposable)f).Dispose();
			}
		}
		byte[] At(long off, int size)
		{
			byte[] array5 = new byte[size];
			long position = f.Position;
			f.Position = off;
			f.ReadAtLeast(array5, size, throwOnEndOfStream: false);
			f.Position = position;
			return array5;
		}
		bool Done()
		{
			if (found == null && tried.Count < 65536)
			{
				return unread < 0;
			}
			return true;
		}
		long FileOf(long rva)
		{
			SectionHeader sectionHeader = sections.FirstOrDefault((SectionHeader s) => rva >= s.VirtualAddress && rva < s.VirtualAddress + s.SizeOfRawData);
			if (sectionHeader.SizeOfRawData <= 0)
			{
				return -1L;
			}
			return sectionHeader.PointerToRawData + rva - sectionHeader.VirtualAddress;
		}
		void Try(byte[] k, string text4)
		{
			if (!Done() && k.Distinct().Count() >= 16 && tried.Add(Convert.ToHexString(k)))
			{
				FAesKey fAesKey = new FAesKey("0x" + Convert.ToHexString(k));
				if (opens(fAesKey))
				{
					FAesKey fAesKey2 = fAesKey;
					pattern = text4;
					found = fAesKey2;
				}
			}
		}
	}

	public static string? Protector(string exePath)
	{
		if (!File.Exists(exePath))
		{
			return null;
		}
		try
		{
			using FileStream fileStream = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			if (fileStream.Length > int.MaxValue)
			{
				return null;
			}
			using PEReader pEReader = new PEReader(fileStream);
			return Protector(pEReader.PEHeaders.SectionHeaders);
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is BadImageFormatException) ? true : false)
		{
			return null;
		}
	}

	private static string? Protector(IReadOnlyList<SectionHeader> sections)
	{
		HashSet<string> hashSet = sections.Select((SectionHeader s) => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
		if (hashSet.Contains(".bind"))
		{
			return "SteamStub";
		}
		if (hashSet.Contains(".themida") || hashSet.Contains(".winlice"))
		{
			return "Themida";
		}
		if (hashSet.Any((string n) => n.StartsWith(".vmp", StringComparison.OrdinalIgnoreCase)))
		{
			return "VMProtect";
		}
		if (hashSet.Any((string n) => n.StartsWith(".enigma", StringComparison.OrdinalIgnoreCase)))
		{
			return "Enigma";
		}
		long num = sections.Where((SectionHeader s) => (s.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0).Sum((Func<SectionHeader, long>)((SectionHeader s) => s.SizeOfRawData));
		if (sections.Where((SectionHeader s) => s.Name == ".text").Sum((Func<SectionHeader, long>)((SectionHeader s) => s.SizeOfRawData)) >= num / 2)
		{
			return null;
		}
		return "code outside .text";
	}

	private static int Mem(byte[] b, int p, int rexB, out int bas, out int disp)
	{
		bas = (disp = 0);
		int num = b[p] >> 6;
		int num2 = b[p] & 7;
		int num3 = p + 1;
		if (num == 3)
		{
			return 0;
		}
		if (num2 == 4)
		{
			byte b2 = b[num3++];
			if (((b2 >> 3) & 7) != 4)
			{
				return 0;
			}
			num2 = b2 & 7;
			if (num == 0 && num2 == 5)
			{
				return 0;
			}
		}
		else if (num == 0 && num2 == 5)
		{
			return 0;
		}
		bas = num2 | (rexB << 3);
		switch (num)
		{
		case 1:
			disp = (sbyte)b[num3++];
			break;
		case 2:
			disp = BitConverter.ToInt32(b, num3);
			num3 += 4;
			break;
		}
		return num3 - p;
	}
}
