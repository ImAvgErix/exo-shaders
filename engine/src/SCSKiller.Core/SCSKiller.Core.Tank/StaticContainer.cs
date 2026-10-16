using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Microsoft.Win32.SafeHandles;

namespace SCSKiller.Core.Tank;

public static class StaticContainer
{
	public readonly record struct Chunk(int Encoded, int Decoded);

	public sealed record Blob(long Offset, int HeaderSize, Chunk[] Chunks)
	{
		public long Encoded => ((IEnumerable<Chunk>)Chunks).Sum((Func<Chunk, long>)((Chunk c) => c.Encoded));

		public long Length => HeaderSize + Encoded;
	}

	public const string Dir = "data";

	public const string BuildConfig = ".build.config";

	private const int MaxChunks = 65536;

	public const int MaxAsset = 67108864;

	private const int MaxText = 1048576;

	public static IEnumerable<FileInfo> Files(string installDir)
	{
		if (!Directory.Exists(Path.Combine(installDir, "data")))
		{
			return Array.Empty<FileInfo>();
		}
		return (from f in new DirectoryInfo(Path.Combine(installDir, "data")).EnumerateFiles("data.*.*", new EnumerationOptions
			{
				IgnoreInaccessible = true,
				AttributesToSkip = FileAttributes.None
			})
			where IsDataName(f.Name)
			select f).OrderBy((FileInfo f) => f.Name, StringComparer.Ordinal);
	}

	private static bool IsDataName(string n)
	{
		if (n.Length == 12 && n.StartsWith("data.") && char.IsAsciiDigit(n[5]) && char.IsAsciiDigit(n[6]) && char.IsAsciiDigit(n[7]) && n[8] == '.' && char.IsAsciiDigit(n[9]) && char.IsAsciiDigit(n[10]))
		{
			return char.IsAsciiDigit(n[11]);
		}
		return false;
	}

	public static string? SmallText(string path)
	{
		try
		{
			using FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			long length = fileStream.Length;
			if (length > 1048576)
			{
				return null;
			}
			byte[] array = new byte[length];
			int count = fileStream.ReadAtLeast(array, array.Length, throwOnEndOfStream: false);
			using StreamReader streamReader = new StreamReader(new MemoryStream(array, 0, count));
			return streamReader.ReadToEnd();
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return null;
		}
	}

	public static string? BuildConfigText(string installDir)
	{
		return SmallText(Path.Combine(installDir, "data", ".build.config"));
	}

	public static Dictionary<string, string> ReadBuildConfig(string installDir)
	{
		Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.Ordinal);
		string[] array = (BuildConfigText(installDir) ?? "").Split('\n');
		foreach (string text in array)
		{
			if (!text.StartsWith('#'))
			{
				int num = text.IndexOf('=');
				if (num > 0)
				{
					dictionary.TryAdd(text.Substring(0, num).Trim(), text.Substring(num + 1).Trim());
				}
			}
		}
		return dictionary;
	}

	public static IEnumerable<Blob> Blobs(SafeFileHandle h, long length, Action<long>? stopped = null)
	{
		Blob blob;
		for (long at = 0L; at < length; at += blob.Length)
		{
			blob = BlobAt(h, at, length);
			if ((object)blob == null)
			{
				stopped?.Invoke(at);
				break;
			}
			yield return blob;
		}
	}

	public static Blob? BlobAt(SafeFileHandle h, long at, long length)
	{
		Span<byte> span = stackalloc byte[12];
		if (at < 0 || at > length - 16 || RandomAccess.Read(h, span, at) < 12 || !((ReadOnlySpan<byte>)span).StartsWith("BLTE"u8))
		{
			return null;
		}
		int num = BinaryPrimitives.ReadInt32BigEndian(span.Slice(4));
		int num2 = (int)(BinaryPrimitives.ReadUInt32BigEndian(span.Slice(8)) & 0xFFFFFF);
		int num3 = ((span[8] == 16) ? 40 : 24);
		if (num2 < 1 || num2 > 65536 || num != 12 + num2 * num3 || num > length - at)
		{
			return null;
		}
		byte[] array = new byte[num2 * num3];
		if (RandomAccess.Read(h, array, at + 12) < array.Length)
		{
			return null;
		}
		Chunk[] array2 = new Chunk[num2];
		long num4 = num;
		for (int i = 0; i < num2; i++)
		{
			Span<byte> span2 = array.AsSpan(i * num3);
			uint num5 = BinaryPrimitives.ReadUInt32BigEndian(span2);
			uint num6 = BinaryPrimitives.ReadUInt32BigEndian(span2.Slice(4));
			uint num7 = num5;
			if (num7 < 1 || num7 > int.MaxValue || num6 > int.MaxValue)
			{
				return null;
			}
			array2[i] = new Chunk((int)num7, (int)num6);
			num4 += num7;
		}
		if (num4 > length - at)
		{
			return null;
		}
		return new Blob(at, num, array2);
	}

	public static int Peek(SafeFileHandle h, Blob b, Span<byte> dest)
	{
		int num = Math.Min(b.Chunks[0].Encoded, 16384);
		byte[] array = new byte[num];
		if (RandomAccess.Read(h, array, b.Offset + b.HeaderSize) < num)
		{
			return 0;
		}
		switch (array[0])
		{
		case 78:
		{
			int num2 = Math.Min(dest.Length, num - 1);
			array.AsSpan(1, num2).CopyTo(dest);
			return num2;
		}
		case 90:
			try
			{
				using ZLibStream zLibStream = new ZLibStream(new MemoryStream(array, 1, num - 1), CompressionMode.Decompress);
				return zLibStream.ReadAtLeast(dest, dest.Length, throwOnEndOfStream: false);
			}
			catch (InvalidDataException)
			{
				return 0;
			}
		default:
			return 0;
		}
	}

	public static byte[]? Decode(SafeFileHandle h, Blob b)
	{
		long num = ((IEnumerable<Chunk>)b.Chunks).Sum((Func<Chunk, long>)((Chunk c) => c.Decoded));
		bool flag = ((num > 67108864 || num == 0L) ? true : false);
		if (flag || b.Encoded > 68157440)
		{
			return null;
		}
		byte[] array = new byte[b.Encoded];
		if (RandomAccess.Read(h, array, b.Offset + b.HeaderSize) < array.Length)
		{
			return null;
		}
		byte[] array2 = new byte[num];
		int num2 = 0;
		int num3 = 0;
		Chunk[] chunks = b.Chunks;
		for (int num4 = 0; num4 < chunks.Length; num4++)
		{
			Chunk chunk = chunks[num4];
			Span<byte> span = array.AsSpan(num2, chunk.Encoded);
			switch (span[0])
			{
			case 78:
				if (span.Length - 1 != chunk.Decoded)
				{
					return null;
				}
				span.Slice(1).CopyTo(array2.AsSpan(num3));
				break;
			case 90:
				try
				{
					using ZLibStream zLibStream = new ZLibStream(new MemoryStream(array, num2 + 1, chunk.Encoded - 1), CompressionMode.Decompress);
					if (zLibStream.ReadAtLeast(array2.AsSpan(num3, chunk.Decoded), chunk.Decoded, throwOnEndOfStream: false) != chunk.Decoded)
					{
						return null;
					}
				}
				catch (InvalidDataException)
				{
					return null;
				}
				break;
			default:
				return null;
			}
			num2 += chunk.Encoded;
			num3 += chunk.Decoded;
		}
		return array2;
	}

	public static SafeFileHandle Open(string path)
	{
		return File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.None, 0L);
	}
}
