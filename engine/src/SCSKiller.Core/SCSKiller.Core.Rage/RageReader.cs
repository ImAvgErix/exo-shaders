using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Northlight;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.Rage;

public sealed class RageReader : IEngineReader
{
	public sealed record Loc(string Path, long Offset, int Size);

	internal sealed class Cursor(SafeFileHandle h) : IDisposable
	{
		public readonly SafeFileHandle Handle = h;

		public readonly long Length = RandomAccess.GetLength(h);

		public long Pos;

		private readonly byte[] window = new byte[1048576];

		private long windowAt = -1L;

		private int windowLen;

		public static Cursor? Open(string path)
		{
			try
			{
				return new Cursor(File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.None, 0L));
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
				return null;
			}
		}

		public void Read(Span<byte> to)
		{
			int num = 0;
			while (num < to.Length)
			{
				if (windowAt < 0 || Pos < windowAt || Pos >= windowAt + windowLen)
				{
					windowAt = Pos;
					windowLen = RandomAccess.Read(Handle, window, Pos);
					if (windowLen <= 0)
					{
						windowAt = -1L;
						throw new EndOfStreamException();
					}
				}
				int num2 = (int)Math.Min(to.Length - num, windowAt + windowLen - Pos);
				window.AsSpan((int)(Pos - windowAt), num2).CopyTo(to.Slice(num));
				num += num2;
				Pos += num2;
			}
		}

		public byte U8()
		{
			Span<byte> to = stackalloc byte[1];
			Read(to);
			return to[0];
		}

		public int I32()
		{
			Span<byte> span = stackalloc byte[4];
			Read(span);
			return BinaryPrimitives.ReadInt32LittleEndian(span);
		}

		public ulong U64()
		{
			Span<byte> span = stackalloc byte[8];
			Read(span);
			return BinaryPrimitives.ReadUInt64LittleEndian(span);
		}

		public void Dispose()
		{
			Handle.Dispose();
		}
	}

	internal sealed record Library(List<string?>[] Shaders, List<Effect> Effects)
	{
		public static Library? Read(Cursor fs, long at, Func<long, byte[], string>? container)
		{
			try
			{
				fs.Pos = at + 4;
				List<string>[] array = new List<string>[Slots.Length];
				Span<byte> to = stackalloc byte[28];
				for (int i = 0; i < Slots.Length; i++)
				{
					uint num = (uint)fs.I32();
					if (num > 65536)
					{
						return null;
					}
					array[i] = new List<string>((int)num);
					for (int j = 0; j < num; j++)
					{
						byte b = fs.U8();
						fs.Pos += b + 1;
						int num2 = fs.I32();
						if (num2 < 32 || num2 > 67108864 || fs.Pos + num2 > fs.Length)
						{
							return null;
						}
						long pos = fs.Pos;
						if (container == null)
						{
							fs.Read(to);
							if (!((ReadOnlySpan<byte>)to.Slice(0, 4)).SequenceEqual("DXBC"u8) || BinaryPrimitives.ReadInt32LittleEndian(to.Slice(24)) != num2)
							{
								return null;
							}
							fs.Pos = pos + num2;
							array[i].Add(null);
						}
						else
						{
							byte[] array2 = new byte[num2];
							fs.Read(array2);
							array[i].Add((Dxbc.Valid(array2) && BinaryPrimitives.ReadInt32LittleEndian(array2.AsSpan(24)) == num2) ? container(pos, array2) : null);
						}
						fs.Pos += 152L;
						int num3 = fs.I32();
						if (num3 < 0 || fs.Pos + num3 > fs.Length)
						{
							return null;
						}
						fs.Pos += num3;
					}
				}
				if (fs.U64() != 0L)
				{
					return null;
				}
				uint n = (uint)fs.I32();
				long pos2 = fs.Pos;
				if (array.All((List<string> x) => x.Count == 0))
				{
					return null;
				}
				long num4 = 16777216L;
				while (true)
				{
					byte[] array3 = new byte[(int)Math.Min(fs.Length - pos2, Math.Min(num4, 268435456L))];
					RandomAccess.Read(fs.Handle, array3, pos2);
					(List<Effect>, int)? tuple = ParseEffects(array3, n, array);
					if (tuple.HasValue)
					{
						(List<Effect>, int) valueOrDefault = tuple.GetValueOrDefault();
						fs.Pos = pos2 + valueOrDefault.Item2;
						return new Library(array, valueOrDefault.Item1);
					}
					if (array3.Length < num4 || num4 >= 268435456)
					{
						break;
					}
					num4 *= 4;
				}
				return null;
			}
			catch (Exception ex) when ((ex is EndOfStreamException || ex is IOException || ex is ArgumentOutOfRangeException) ? true : false)
			{
				return null;
			}
		}

		private static (List<Effect> List, int End)? ParseEffects(byte[] b, uint n, List<string?>[] shaders)
		{
			List<Effect> list = new List<Effect>();
			int p = 0;
			for (int i = 0; i < n; i++)
			{
				Effect effect = Effect.Parse(b, ref p);
				if ((object)effect == null || effect.Stages.Where((uint[] idx, int s) => idx.Any((uint x) => x >= shaders[s].Count)).Any())
				{
					return null;
				}
				list.Add(effect);
			}
			return (list, p);
		}
	}

	internal sealed record Effect(string Name, uint[][] Stages, List<byte[]>? Passes)
	{
		private static readonly byte[] PropList = "proplst\0"u8.ToArray();

		public static Effect? Parse(byte[] b, ref int p)
		{
			if (p + 1 > b.Length)
			{
				return null;
			}
			byte b2 = b[p];
			if (b2 < 2 || p + b2 + 1 + 4 > b.Length || b[p + b2] != 0)
			{
				return null;
			}
			string name = Encoding.Latin1.GetString(b, p + 1, b2 - 1);
			p += 1 + b2 + 4;
			uint[][] array = new uint[Slots.Length][];
			for (int i = 0; i < Slots.Length; i++)
			{
				if (p + 4 > b.Length)
				{
					return null;
				}
				int num = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p));
				if (num < 0 || num > 65536 || p + 4 + 4L * (long)num > b.Length)
				{
					return null;
				}
				array[i] = new uint[num];
				for (int j = 0; j < num; j++)
				{
					array[i][j] = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 4 + 4 * j));
				}
				p += 4 + 4 * num;
			}
			int num2 = ((ReadOnlySpan<byte>)b.AsSpan(p)).IndexOf((ReadOnlySpan<byte>)PropList);
			if (num2 < 0)
			{
				return null;
			}
			List<byte[]> passes = PassTable(b.AsSpan(p, num2), array);
			p += num2 + PropList.Length;
			int? num3 = U32(b, ref p);
			if (num3.HasValue)
			{
				int valueOrDefault = num3.GetValueOrDefault();
				for (int k = 0; k < valueOrDefault; k++)
				{
					if (!Sized(b, ref p))
					{
						return null;
					}
				}
				if (!Sized(b, ref p))
				{
					return null;
				}
				return new Effect(name, array, passes);
			}
			return null;
		}

		private static int? U32(byte[] b, ref int p)
		{
			if (p + 4 > b.Length)
			{
				return null;
			}
			int num = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p));
			p += 4;
			if (num < 0)
			{
				return null;
			}
			return num;
		}

		private static bool Sized(byte[] b, ref int p)
		{
			int? num = U32(b, ref p);
			if (num.HasValue)
			{
				int valueOrDefault = num.GetValueOrDefault();
				if ((long)p + (long)valueOrDefault <= b.Length)
				{
					p += valueOrDefault;
					return true;
				}
			}
			return false;
		}

		internal static List<byte[]>? PassTable(ReadOnlySpan<byte> body, uint[][] stages)
		{
			List<byte[]> list = null;
			for (int i = 8; i + 8 <= body.Length; i += 4)
			{
				int num = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(i));
				if (num < 1 || num > 512 || i + 8 + 4 * num > body.Length)
				{
					continue;
				}
				long num2 = 0L;
				bool flag = true;
				for (int j = 0; (j < num) & flag; j++)
				{
					byte b = body[i + 4 + 4 * j + 3];
					flag = b > 0;
					num2 += b;
				}
				int num3 = i + 4 + 4 * num;
				if (!flag || BinaryPrimitives.ReadInt32LittleEndian(body.Slice(num3)) != num2 || num3 + 4 + 24 * num2 > body.Length)
				{
					continue;
				}
				List<byte[]> list2 = new List<byte[]>((int)num2);
				for (int k = 0; (k < num2) & flag; k++)
				{
					byte[] array = body.Slice(num3 + 4 + 24 * k, 6).ToArray();
					for (int l = 0; (l < Slots.Length) & flag; l++)
					{
						flag = array[l] <= stages[l].Length;
					}
					flag &= array[0] != 0 || array[5] != 0;
					list2.Add(array);
				}
				if (flag)
				{
					if (list != null)
					{
						return null;
					}
					list = list2;
				}
			}
			return list;
		}
	}

	public const string Family = "RAGE";

	public const string Version = "SGD2+RTS0";

	private const string UpdateDir = "update";

	private const long DetectBudget = 536870912L;

	private const int MaxShaders = 65536;

	private const int MaxShaderSize = 67108864;

	private const int MaxEffectsBytes = 268435456;

	private const int Block = 512;

	private static readonly Stage[] Slots = new Stage[6]
	{
		Stage.Vertex,
		Stage.Pixel,
		Stage.Geometry,
		Stage.Domain,
		Stage.Hull,
		Stage.Compute
	};

	private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>>();

	public EngineInfo? Detect(Game game)
	{
		long read = 0L;
		foreach (FileInfo item in from f in Archives(game)
			orderby f.Length
			select f)
		{
			Cursor fs = Cursor.Open(item.FullName);
			try
			{
				if (fs != null)
				{
					if (Scan(fs, 536870912 - read, ref read, (long at) => Library.Read(fs, at, null) != null))
					{
						return new EngineInfo("RAGE", "SGD2+RTS0", null, "D3D12", Encrypted: false, null);
					}
					if (read >= 536870912)
					{
						break;
					}
				}
			}
			finally
			{
				if (fs != null)
				{
					((IDisposable)fs).Dispose();
				}
			}
		}
		return null;
	}

	public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		Dictionary<string, ShaderInfo> shaders = new Dictionary<string, ShaderInfo>();
		Dictionary<string, Loc> locs = new Dictionary<string, Loc>();
		RootSigCarriers rootSigs = new RootSigCarriers();
		Dictionary<string, string> platformOf = new Dictionary<string, string>();
		List<ShaderMap> maps = new List<ShaderMap>();
		HashSet<string> seen = new HashSet<string>();
		int libraries = 0;
		int effects = 0;
		int undecoded = 0;
		int passes = 0;
		int mismatched = 0;
		int unlinked = 0;
		int malformed = 0;
		int bad = 0;
		long read = 0L;
		using IncrementalHash incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
		foreach (FileInfo f in Archives(game).OrderBy((FileInfo fileInfo) => fileInfo.Name, StringComparer.OrdinalIgnoreCase))
		{
			Cursor fs = Cursor.Open(f.FullName);
			try
			{
				if (fs == null)
				{
					continue;
				}
				string rel = Path.GetRelativePath(game.InstallDir, f.FullName);
				bool found = false;
				Scan(fs, long.MaxValue, ref read, (long at) =>
				{
					ct.ThrowIfCancellationRequested();
					Library library = Library.Read(fs, at, (long off, byte[] c) =>
					{
						string text3 = Convert.ToHexStringLower(SHA1.HashData(c));
						if (!locs.TryAdd(text3, new Loc(f.FullName, off, c.Length)))
						{
							return text3;
						}
						rootSigs.See(c, text3, Dxbc.Kind(c));
						try
						{
							ShaderInfo shaderInfo = ShaderContainer.Parse(c, text3, new ResourceCounts(0, 0, 0, 0));
							if ((object)shaderInfo != null)
							{
								shaders[text3] = shaderInfo with
								{
									Counts = CarvedReader.Counts(shaderInfo.Bindings)
								};
								string text4 = CarvedReader.LanePlatform(Dxbc.WaveLanes(c));
								if (text4 != null)
								{
									platformOf[text3] = text4;
								}
							}
						}
						catch (Exception ex) when ((ex is ArgumentException || ex is IndexOutOfRangeException) ? true : false)
						{
							int num3 = bad;
							bad = num3 + 1;
						}
						return text3;
					});
					if (library == null)
					{
						return false;
					}
					found = true;
					int num = libraries;
					libraries = num + 1;
					foreach (Effect effect in library.Effects)
					{
						num = effects;
						effects = num + 1;
						if (effect.Passes == null)
						{
							if (effect.Stages.Any((uint[] x) => x.Length != 0))
							{
								num = undecoded;
								undecoded = num + 1;
							}
						}
						else
						{
							foreach (byte[] pass in effect.Passes)
							{
								num = passes;
								passes = num + 1;
								List<string> list2 = new List<string>();
								bool flag = true;
								for (int num2 = 0; (num2 < Slots.Length) & flag; num2++)
								{
									if (pass[num2] != 0)
									{
										string text = library.Shaders[num2][(int)effect.Stages[num2][pass[num2] - 1]];
										if (text == null || !shaders.TryGetValue(text, out ShaderInfo value))
										{
											flag = false;
										}
										else if (value.Stage != Slots[num2])
										{
											num = mismatched;
											mismatched = num + 1;
											flag = false;
										}
										else
										{
											list2.Add(text);
										}
									}
								}
								if (flag && Pipeline(pass))
								{
									bool? flag2 = ((pass[2] != 0 || pass[3] != 0) ? new bool?(true) : NorthlightReader.Feeds(list2.Select((string h) => shaders[h]).ToList()));
									if (flag2.HasValue)
									{
										if (flag2 != true)
										{
											num = unlinked;
											unlinked = num + 1;
										}
										else
										{
											string text2 = CarvedReader.Sha1Hex(string.Join(',', list2.Order(StringComparer.Ordinal)));
											if (seen.Add(text2))
											{
												maps.Add(new ShaderMap(text2, rel + ":" + effect.Name, list2.Select((string x) => platformOf.GetValueOrDefault(x, "D3D12")).FirstOrDefault((string p) => p != "D3D12", "D3D12"), list2, IsPipeline: true));
											}
										}
									}
									else
									{
										num = malformed;
										malformed = num + 1;
									}
								}
							}
						}
					}
					return true;
				}, ct);
				if (found)
				{
					incrementalHash.AppendData(Encoding.UTF8.GetBytes($"{rel}|{f.Length}|{f.LastWriteTimeUtc.Ticks}\n"));
				}
			}
			finally
			{
				if (fs != null)
				{
					((IDisposable)fs).Dispose();
				}
			}
		}
		if (libraries == 0)
		{
			throw new InvalidDataException("update: no SGD2 shader library in its archives: scan the game again");
		}
		rootSigs.Apply(shaders);
		located[game.Id] = locs;
		log?.Report($"{libraries} shader libraries ({(double)read / 1000000000.0:F2} GB scanned): {shaders.Count} shaders (" + string.Join(", ", from s in shaders.Values
			group s by s.Stage into g
			orderby g.Count() descending
			select $"{g.Count()} {g.Key}") + "), " + $"{shaders.Values.Select((ShaderInfo s) => s.RootSignature).OfType<string>().Distinct().Count()} embedded root signatures, {shaders.Values.Count((ShaderInfo s) => s.InlineRayTracing)} tracing rays inline; " + $"{effects} effects, {passes} passes -> {maps.Count} distinct pipelines" + ((undecoded > 0) ? $"; {undecoded} effects without a pass table" : "") + ((mismatched > 0) ? $"; warning: {mismatched} passes name a shader of another stage" : "") + ((unlinked > 0) ? $"; {unlinked} passes left out: their VS doesn't feed their PS" : "") + ((malformed > 0) ? $"; {malformed} passes left out: a VS writes one output twice" : "") + ((bad > 0) ? $"; {bad} unparseable" : "") + $" ({stopwatch.Elapsed.TotalSeconds:F1}s)");
		string contentHash = Convert.ToHexStringLower(incrementalHash.GetHashAndReset());
		List<string> list = new List<string>();
		list.Add("D3D12");
		list.AddRange(platformOf.Values.Distinct().Order(StringComparer.Ordinal));
		return new ShaderIndex(contentHash, new _003C_003Ez__ReadOnlyList<string>(list), shaders, maps, rootSigs.Blobs);
	}

	public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
	{
		if (!located.TryGetValue(game.Id, out IReadOnlyDictionary<string, Loc> locs))
		{
			Index(game, engine, null, ct);
			locs = located[game.Id];
		}
		foreach (IGrouping<string, (string, Loc)> item in from s in sha1s.Where(locs.ContainsKey)
			select (Sha: s, At: locs[s]) into x
			group x by x.At.Path)
		{
			SafeFileHandle safeFileHandle;
			try
			{
				safeFileHandle = File.OpenHandle(item.Key, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.None, 0L);
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
				continue;
			}
			using (safeFileHandle)
			{
				foreach (var (text, loc) in item.OrderBy(((string Sha, Loc At) x) => x.At.Offset))
				{
					ct.ThrowIfCancellationRequested();
					byte[] array = new byte[loc.Size];
					if (RandomAccess.Read(safeFileHandle, array, loc.Offset) == array.Length && Convert.ToHexStringLower(SHA1.HashData(array)) == text)
					{
						sink(text, array);
					}
				}
			}
		}
	}

	private static bool Pipeline(byte[] pass)
	{
		if (pass[5] == 0)
		{
			if (pass[0] != 0)
			{
				return pass[3] == 0 == (pass[4] == 0);
			}
			return false;
		}
		return ((ReadOnlySpan<byte>)pass.AsSpan(0, 5)).IndexOfAnyExcept((byte)0) < 0;
	}

	private static IEnumerable<FileInfo> Archives(Game game)
	{
		DirectoryInfo directoryInfo = new DirectoryInfo(Path.Combine(game.InstallDir, "update"));
		if (!directoryInfo.Exists)
		{
			return Array.Empty<FileInfo>();
		}
		return directoryInfo.EnumerateFiles("*.rpf", new EnumerationOptions
		{
			IgnoreInaccessible = true,
			AttributesToSkip = FileAttributes.None
		});
	}

	private static bool Scan(Cursor fs, long budget, ref long read, Func<long, bool> library, CancellationToken ct = default(CancellationToken))
	{
		byte[] array = new byte[8388608];
		bool result = false;
		long num = 0L;
		long num2 = 0L;
		while (num < fs.Length && num2 < budget)
		{
			ct.ThrowIfCancellationRequested();
			int num3 = RandomAccess.Read(fs.Handle, array, num);
			if (num3 <= 0)
			{
				break;
			}
			num2 += num3;
			read += num3;
			long num4 = num + num3 - num3 % 512;
			for (int i = 0; i + 4 <= num3; i += 512)
			{
				if (((ReadOnlySpan<byte>)array.AsSpan(i, 4)).SequenceEqual("SGD2"u8) && library(num + i))
				{
					result = true;
					num4 = (fs.Pos + 512 - 1) / 512 * 512;
					break;
				}
			}
			if (num4 <= num)
			{
				num4 = num + 512;
			}
			num = num4;
		}
		return result;
	}
}
