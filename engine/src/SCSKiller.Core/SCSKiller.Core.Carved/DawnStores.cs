using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace SCSKiller.Core.Carved;

public static class DawnStores
{
	public sealed record Root(long Offset, int Size, string Sha, byte[] Blob);

	public sealed record Stores(string ShaderFile, string RootFile, string PsoFile, IReadOnlyDictionary<uint, long> Shaders, IReadOnlyDictionary<uint, Root> Roots, IReadOnlyList<(uint Root, uint[] Stages)> Pipelines, int Skipped);

	private const int Header = 16;

	private const int Framing = 24;

	private const int Record = 680;

	private const int RecordPayload = 664;

	private const long MaxPsoFile = 268435456L;

	private static readonly int[] OtherStages = new int[4] { 56, 72, 88, 104 };

	public static Stores? Read(string installDir)
	{
		string path = Path.Combine(installDir, "bin");
		string text = Path.Combine(path, "rawshader.store2");
		string text2 = Path.Combine(path, "rawroot.store2");
		string text3 = Path.Combine(path, "rawpso.store2");
		if (!File.Exists(text3) || !File.Exists(text2) || !File.Exists(text))
		{
			return null;
		}
		try
		{
			Dictionary<uint, (long Offset, int Size)> shaders = Entries(text);
			byte[] p;
			if (shaders != null)
			{
				Dictionary<uint, (long, int)> dictionary = Entries(text2);
				if (dictionary != null)
				{
					Dictionary<uint, Root> dictionary2 = new Dictionary<uint, Root>();
					using (SafeFileHandle handle = Open(text2))
					{
						foreach (KeyValuePair<uint, (long, int)> item3 in dictionary)
						{
							item3.Deconstruct(out var key, out var value);
							(long, int) tuple = value;
							uint key2 = key;
							long item = tuple.Item1;
							int item2 = tuple.Item2;
							byte[] array = new byte[item2];
							if (RandomAccess.Read(handle, array, item) != item2)
							{
								return null;
							}
							int num = Dxbc.HeaderSize(array);
							if (num == 0 || num > item2)
							{
								return null;
							}
							Span<byte> span = array.AsSpan(0, num);
							if (!Dxbc.Valid(span) || Dxbc.Kind(span) >= 0 || Dxbc.Part(span, "RTS0"u8).IsEmpty)
							{
								return null;
							}
							dictionary2[key2] = new Root(item, num, Convert.ToHexStringLower(SHA1.HashData(span)), span.ToArray());
						}
					}
					using (SafeFileHandle safeFileHandle = Open(text3))
					{
						long length = RandomAccess.GetLength(safeFileHandle);
						if (length > 268435456)
						{
							goto IL_01f6;
						}
						uint? num2 = Frame(safeFileHandle, length);
						if (!num2.HasValue)
						{
							goto IL_01f6;
						}
						uint valueOrDefault = num2.GetValueOrDefault();
						if (length - 24 - 4 != (long)valueOrDefault * 680L)
						{
							goto IL_01f6;
						}
						p = new byte[length];
						if (RandomAccess.Read(safeFileHandle, p, 0L) != length)
						{
							return null;
						}
						goto end_IL_01b1;
						IL_01f6:
						return null;
						end_IL_01b1:;
					}
					List<(uint, uint[])> list = new List<(uint, uint[])>();
					int num3 = 0;
					int at;
					for (at = 20; at < p.Length - 8; at += 680)
					{
						uint num4 = U(at + 16);
						uint num5 = U(at + 24);
						uint num6 = U(at + 40);
						if (U(at + 8) != 664 || !dictionary2.ContainsKey(num4) || !shaders.ContainsKey(num5) || (num6 != 0 && !shaders.ContainsKey(num6)))
						{
							return null;
						}
						if (OtherStages.Any((int o) => shaders.ContainsKey(U(at + o))))
						{
							num3++;
							continue;
						}
						list.Add((num4, (num6 != 0) ? new uint[2] { num5, num6 } : new uint[1] { num5 }));
					}
					return new Stores(text, text2, text3, shaders.ToDictionary((KeyValuePair<uint, (long Offset, int Size)> e) => e.Key, (KeyValuePair<uint, (long Offset, int Size)> e) => e.Value.Offset), dictionary2, list, num3);
				}
			}
			return null;
			uint U(long num7)
			{
				return BitConverter.ToUInt32(p, (int)num7);
			}
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static uint? Frame(SafeFileHandle h, long len)
	{
		Span<byte> span = stackalloc byte[20];
		Span<byte> span2 = stackalloc byte[8];
		if (len < 28 || RandomAccess.Read(h, span, 0L) != span.Length || RandomAccess.Read(h, span2, len - 8) != 8 || BitConverter.ToUInt64(span.Slice(8)) != (ulong)(len - 24) || BitConverter.ToUInt64(span2) != BitConverter.ToUInt64(span))
		{
			return null;
		}
		return BitConverter.ToUInt32(span.Slice(16));
	}

	private static SafeFileHandle Open(string path)
	{
		return File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.None, 0L);
	}

	private static Dictionary<uint, (long Offset, int Size)>? Entries(string path)
	{
		using SafeFileHandle safeFileHandle = Open(path);
		long length = RandomAccess.GetLength(safeFileHandle);
		uint? num = Frame(safeFileHandle, length);
		if (num.HasValue)
		{
			uint valueOrDefault = num.GetValueOrDefault();
			Dictionary<uint, (long, int)> dictionary = new Dictionary<uint, (long, int)>();
			Span<byte> span = stackalloc byte[12];
			long num2 = 20L;
			long num3 = length - 8;
			for (uint num4 = 0u; num4 < valueOrDefault; num4++)
			{
				if (num2 + 12 > num3 || RandomAccess.Read(safeFileHandle, span, num2) != 12)
				{
					return null;
				}
				uint num5 = BitConverter.ToUInt32(span);
				uint num6 = BitConverter.ToUInt32(span.Slice(4));
				uint num7 = BitConverter.ToUInt32(span.Slice(8));
				uint num8 = num6;
				uint key = num5;
				num2 += 12;
				if (num7 != 0 || num8 > num3 - num2 || num8 > 67108864 || !dictionary.TryAdd(key, (num2, (int)num8)))
				{
					return null;
				}
				num2 += num8;
			}
			return (num2 == num3) ? dictionary : null;
		}
		return null;
	}
}
