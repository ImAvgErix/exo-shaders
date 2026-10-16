using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.App;

namespace SCSKiller.Core.Decima;

public sealed class DecimaKeys(string dataDir)
{
	private const int ScanVersion = 1;

	private const int Step = 4194304;

	private const long DecodeBudget = 4294967296L;

	private string KeyFile(Game g)
	{
		return Path.Combine(new AppStore(dataDir).GameDir(g.Id), "decima.key");
	}

	private string ScanFile(Game g)
	{
		return Path.Combine(new AppStore(dataDir).GameDir(g.Id), "decima.scan");
	}

	private static string Stamp(string exe)
	{
		FileInfo fileInfo = new FileInfo(exe);
		if (fileInfo == null || !fileInfo.Exists)
		{
			return "";
		}
		return $"{1}:{fileInfo.Length}:{fileInfo.LastWriteTimeUtc.Ticks}";
	}

	public DecimaArchive.Salts? Get(Game game, IReadOnlyList<string> archives, out string why)
	{
		DecimaArchive.Salts salts = Stored(game);
		if (salts != null && Opens(salts, archives))
		{
			why = "stored salts";
			return salts;
		}
		string text = Stamp(game.ExePath);
		string[] array = Remembered(game);
		if (array != null && array.Length >= 2)
		{
			string text2 = array[0];
			string text3 = array[1];
			if (text2 == text)
			{
				why = text3;
				return null;
			}
		}
		try
		{
			Codecs.Load(zlib: false);
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException) ? true : false)
		{
			why = "the Oodle codec can't be loaded";
			return null;
		}
		Stopwatch stopwatch = Stopwatch.StartNew();
		DecimaArchive.Salts salts2;
		try
		{
			salts2 = Scan(game.ExePath, archives, out why);
		}
		catch (Exception ex2) when ((ex2 is IOException || ex2 is UnauthorizedAccessException) ? true : false)
		{
			salts2 = null;
			why = "the game's exe can't be read";
		}
		why += $" ({stopwatch.Elapsed.TotalSeconds:F1} s)";
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(KeyFile(game)));
			if (salts2 != null)
			{
				string path = KeyFile(game);
				byte[] iv = salts2.Iv;
				byte[] data = salts2.Data;
				int num = 0;
				byte[] array2 = new byte[iv.Length + data.Length];
				ReadOnlySpan<byte> readOnlySpan = new ReadOnlySpan<byte>(iv);
				readOnlySpan.CopyTo(new Span<byte>(array2).Slice(num, readOnlySpan.Length));
				num += readOnlySpan.Length;
				ReadOnlySpan<byte> readOnlySpan2 = new ReadOnlySpan<byte>(data);
				readOnlySpan2.CopyTo(new Span<byte>(array2).Slice(num, readOnlySpan2.Length));
				num += readOnlySpan2.Length;
				File.WriteAllText(path, Convert.ToHexString(new ReadOnlySpan<byte>(array2)));
			}
			else
			{
				File.WriteAllLines(ScanFile(game), new string[2] { text, why });
			}
		}
		catch (Exception ex3) when ((ex3 is IOException || ex3 is UnauthorizedAccessException) ? true : false)
		{
		}
		return salts2;
	}

	private string[] Remembered(Game game)
	{
		try
		{
			return File.Exists(ScanFile(game)) ? File.ReadAllLines(ScanFile(game)) : Array.Empty<string>();
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return Array.Empty<string>();
		}
	}

	public string KeyStamp(Game game)
	{
		if (!File.Exists(KeyFile(game)))
		{
			return "";
		}
		return File.GetLastWriteTimeUtc(KeyFile(game)).Ticks.ToString();
	}

	public DecimaArchive.Salts? Stored(Game game)
	{
		string text;
		try
		{
			text = (File.Exists(KeyFile(game)) ? File.ReadAllText(KeyFile(game)).Trim() : "");
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return null;
		}
		if (text.Length != 56 || !text.All(Uri.IsHexDigit))
		{
			return null;
		}
		return new DecimaArchive.Salts(Convert.FromHexString(text.Substring(0, 24)), Convert.FromHexString(text.Substring(24)));
	}

	public static bool Opens(DecimaArchive.Salts salts, IEnumerable<string> archives)
	{
		foreach (string archive in archives)
		{
			using DecimaArchive decimaArchive = DecimaArchive.Open(archive, salts);
			if (decimaArchive == null || decimaArchive.Length == 0L)
			{
				continue;
			}
			try
			{
				decimaArchive.Read(0L, new byte[1]);
				decimaArchive.Read(decimaArchive.Length - 1, new byte[1]);
				return true;
			}
			catch (InvalidDataException)
			{
				return false;
			}
		}
		return false;
	}

	public static DecimaArchive.Salts? Scan(string exePath, IReadOnlyList<string> archives, out string how)
	{
		byte[] head = new byte[40];
		long length;
		using (SafeFileHandle handle = File.OpenHandle(archives[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.None, 0L))
		{
			length = RandomAccess.GetLength(handle);
			if (RandomAccess.Read(handle, head, 0L) != 40 || BinaryPrimitives.ReadUInt32LittleEndian(head) != 556810320)
			{
				how = "not found: the archive isn't an encrypted one";
				return null;
			}
		}
		byte[] block = new byte[16];
		BinaryPrimitives.WriteUInt32LittleEndian(block, BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4)));
		byte[] m = new byte[16];
		byte[] array = Find(exePath, 12, () => false, (ReadOnlySpan<byte> c) =>
		{
			c.CopyTo(block.AsSpan(4));
			DecimaArchive.Murmur3(block, m);
			return (BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(8)) ^ BinaryPrimitives.ReadInt64LittleEndian(m)) == length;
		});
		if (array == null)
		{
			how = "not found: no header salt in the exe";
			return null;
		}
		DecimaArchive.Salts salts = new DecimaArchive.Salts(array, new byte[16]);
		(string, int, int)? tuple = null;
		foreach (string archive in archives)
		{
			using DecimaArchive decimaArchive = DecimaArchive.Open(archive, salts);
			(int, int)? tuple2 = decimaArchive?.Compressed();
			if (tuple2.HasValue)
			{
				(int, int) valueOrDefault = tuple2.GetValueOrDefault();
				if (!tuple.HasValue || valueOrDefault.Item2 < tuple.Value.Item3)
				{
					tuple = (archive, valueOrDefault.Item1, valueOrDefault.Item2);
				}
			}
		}
		(byte[], int, byte[])? tuple3 = null;
		if (tuple.HasValue)
		{
			var (path, i, _) = tuple.GetValueOrDefault();
			using DecimaArchive decimaArchive2 = DecimaArchive.Open(path, salts);
			try
			{
				tuple3 = decimaArchive2?.Raw(i);
			}
			catch (InvalidDataException)
			{
			}
		}
		if (tuple3.HasValue)
		{
			var (packed, want, array2) = tuple3.GetValueOrDefault();
			if (packed.Length >= 2)
			{
				byte[] hash = new byte[16];
				DecimaArchive.Murmur3(array2, hash);
				byte[] array3 = new byte[packed.Length];
				byte[] array4 = new byte[want];
				byte[] array5 = new byte[16];
				byte[] k = array5;
				byte[] into = array4;
				byte[] src = array3;
				long budget = 4294967296L;
				byte[] array6 = Find(exePath, 16, () => budget <= 0, (ReadOnlySpan<byte> c) =>
				{
					if (!Random(c))
					{
						return false;
					}
					for (int j = 0; j < 16; j++)
					{
						m[j] = (byte)(hash[j] ^ c[j]);
					}
					MD5.HashData(m, k);
					bool flag = ((packed[0] ^ k[0]) & 0x3F) != 12;
					if (!flag)
					{
						int num = (packed[1] ^ k[1]) & 0x7F;
						bool flag2 = ((num == 6 || (uint)(num - 10) <= 2u) ? true : false);
						flag = !flag2;
					}
					if (flag)
					{
						return false;
					}
					budget -= Math.Max(Math.Max(packed.Length, want), 4096);
					packed.CopyTo(src, 0);
					DecimaArchive.Salts.XorRepeated(src, k);
					try
					{
						DecimaArchive.Decompress(src, src.Length, into, want);
						return true;
					}
					catch (InvalidDataException)
					{
						return false;
					}
				});
				if (array6 == null)
				{
					how = ((budget <= 0) ? "not found: too many data salt candidates in the exe" : "not found: no data salt in the exe");
					return null;
				}
				how = "found in the exe";
				return new DecimaArchive.Salts(array, array6);
			}
		}
		how = "not found: no compressed chunk to check a data salt with";
		return null;
	}

	private static bool Random(ReadOnlySpan<byte> c)
	{
		Span<ulong> span = stackalloc ulong[4];
		int num = 0;
		ReadOnlySpan<byte> readOnlySpan = c;
		for (int i = 0; i < readOnlySpan.Length; i++)
		{
			byte b = readOnlySpan[i];
			if ((span[b >> 6] & (ulong)(1L << (int)b)) == 0L)
			{
				span[b >> 6] |= (ulong)(1L << (int)b);
				num++;
			}
		}
		return num >= 12;
	}

	private static byte[]? Find(string path, int count, Func<bool> stop, Func<ReadOnlySpan<byte>, bool> match)
	{
		using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.None, 0L);
		long length = RandomAccess.GetLength(handle);
		byte[] array = new byte[4194304 + count];
		for (long num = 0L; num < length; num += 4194304)
		{
			int num2 = RandomAccess.Read(handle, array, num);
			for (int i = 0; i + count <= num2 && i < 4194304; i += 4)
			{
				if (stop())
				{
					return null;
				}
				if (match(array.AsSpan(i, count)))
				{
					return array.AsSpan(i, count).ToArray();
				}
			}
		}
		return null;
	}
}
