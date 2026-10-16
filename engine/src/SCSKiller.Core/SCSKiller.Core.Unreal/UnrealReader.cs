using System;
using System.CodeDom.Compiler;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.RegularExpressions.Generated;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Pak;
using CUE4Parse.UE4.Pak.Objects;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Shaders;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.VirtualFileSystem;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.App;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;

namespace SCSKiller.Core.Unreal;

public sealed class UnrealReader(string? dataDir = null) : IEngineReader
{
	private sealed class KeyCheck(IReadOnlyList<string> paths, EGame game) : IDisposable
	{
		private readonly (AbstractAesVfsReader Reader, GameFile? Probe)?[] opened = new (AbstractAesVfsReader, GameFile)?[paths.Count];

		private readonly bool[] tried = new bool[paths.Count];

		private (AbstractAesVfsReader Reader, GameFile? Probe)? Open(int i)
		{
			if (tried[i])
			{
				return opened[i];
			}
			tried[i] = true;
			AbstractAesVfsReader abstractAesVfsReader = null;
			try
			{
				abstractAesVfsReader = OpenContainer(paths[i], new VersionContainer(game));
				if (abstractAesVfsReader.IsEncrypted)
				{
					return opened[i] = (abstractAesVfsReader, null);
				}
				abstractAesVfsReader.Mount(StringComparer.OrdinalIgnoreCase);
				GameFile item = abstractAesVfsReader.Files.Values.Where((GameFile f) =>
				{
					bool flag = f.IsEncrypted;
					if (flag)
					{
						string extension = f.Extension;
						bool flag2 = ((extension == "uasset" || extension == "umap") ? true : false);
						flag = flag2;
					}
					return flag;
				}).MinBy((GameFile f) => f.Size) ?? abstractAesVfsReader.Files.Values.Where((GameFile f) => f.IsEncrypted && f.Extension == "ushaderbytecode").MinBy((GameFile f) => f.Size);
				return opened[i] = (abstractAesVfsReader, item);
			}
			catch (Exception ex) when (!(ex is OutOfMemoryException))
			{
				abstractAesVfsReader?.Dispose();
				return null;
			}
		}

		private static bool Takes((AbstractAesVfsReader Reader, GameFile? Probe) c, FAesKey key)
		{
			try
			{
				if (c.Reader.IsEncrypted)
				{
					return c.Reader.TestAesKey(key);
				}
				GameFile item = c.Probe;
				if (item == null)
				{
					return false;
				}
				c.Reader.AesKey = key;
				byte[] array = item.Read();
				bool result;
				if (item.Extension == "ushaderbytecode")
				{
					bool flag = array.Length >= 4;
					if (flag)
					{
						flag = BitConverter.ToUInt32(array) switch
						{
							1u => OpenV1(array) != null, 
							2u => LibraryEnd(array, 20, ioStore: false) == array.Length || LibraryEnd(array, 8, ioStore: false) == array.Length, 
							_ => false, 
						};
					}
					result = flag;
				}
				else
				{
					result = array.Length >= 4 && BitConverter.ToUInt32(array) == 2653586369u;
				}
				return result;
			}
			catch (Exception ex) when (!(ex is OutOfMemoryException))
			{
				return false;
			}
		}

		public bool Opens(FAesKey key)
		{
			for (int i = 0; i < paths.Count; i++)
			{
				(AbstractAesVfsReader, GameFile)? tuple = Open(i);
				if (tuple.HasValue)
				{
					(AbstractAesVfsReader, GameFile) valueOrDefault = tuple.GetValueOrDefault();
					if (Takes(valueOrDefault, key))
					{
						return true;
					}
				}
			}
			return false;
		}

		public void Dispose()
		{
			(AbstractAesVfsReader, GameFile)?[] array = opened;
			for (int i = 0; i < array.Length; i++)
			{
				array[i]?.Item1.Dispose();
			}
		}
	}

	private sealed class LibraryOpener
	{
		private int opened;

		private ExceptionDispatchInfo? first;

		public Archive? Open(AbstractVfsFileProvider provider, GameFile file, out Exception? error)
		{
			error = null;
			try
			{
				Archive? archive = UnrealReader.Open(provider, file);
				if (archive != null)
				{
					opened++;
				}
				return archive;
			}
			catch (Exception ex) when ((!(ex is OperationCanceledException) && !(ex is OutOfMemoryException)) || 1 == 0)
			{
				error = ex;
				if (first == null)
				{
					first = ExceptionDispatchInfo.Capture(ex);
				}
				return null;
			}
		}

		public void ThrowIfNoneOpened()
		{
			if (opened == 0)
			{
				first?.Throw();
			}
		}
	}

	internal sealed class ByteBudget(long limit)
	{
		public readonly struct Held(ByteBudget budget, long bytes) : IDisposable
		{
			public void Dispose()
			{
				lock (budget)
				{
					budget.used -= bytes;
					Monitor.PulseAll(budget);
				}
			}
		}

		private long used;

		public static ByteBudget FromFreeMemory()
		{
			GCMemoryInfo gCMemoryInfo = GC.GetGCMemoryInfo();
			return new ByteBudget(Math.Clamp((gCMemoryInfo.TotalAvailableMemoryBytes - gCMemoryInfo.MemoryLoadBytes) / 4, 268435456L, 2147483648L));
		}

		public Held Take(long bytes)
		{
			lock (this)
			{
				while (used > 0 && used + bytes > limit)
				{
					Monitor.Wait(this);
				}
				used += bytes;
			}
			return new Held(this, bytes);
		}
	}

	private sealed class NoMappings : ITypeMappingsProvider
	{
		public TypeMappings? MappingsForGame { get; } = new TypeMappings(new Dictionary<string, Struct>(), new Dictionary<string, Dictionary<long, string>>());

		public void Load(string path, StringComparer? comparer = null)
		{
		}

		public void Load(byte[] bytes, StringComparer? comparer = null)
		{
		}

		public void Reload()
		{
		}
	}

	private sealed record Library(GameFile File, string Name, string Platform);

	internal sealed record Archive(string[] MapHashes, (int Off, int Num)[] Maps, uint[] Indices, int Count, Func<IEnumerable<(int, byte[])>>[] Codes, string[]? Keys = null, string[]? Hashes = null, SafeFileHandle? Pak = null) : IDisposable
	{
		public void Dispose()
		{
			Pak?.Dispose();
		}
	}

	private readonly AppStore store = new AppStore(dataDir ?? AppStore.DefaultDir);

	private readonly UnrealKeys keys = new UnrealKeys(dataDir ?? AppStore.DefaultDir);

	private readonly ConcurrentDictionary<string, string> projects = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	private static readonly ConcurrentDictionary<EGame, IAesVfsReader.CustomEncryptionDelegate?> decryptions = new ConcurrentDictionary<EGame, IAesVfsReader.CustomEncryptionDelegate>();

	private static readonly byte[] RegistryGuid = Convert.FromHexString("E79E7F713A49B0E93291B3880781381B");

	private const int MaxUserCache = 67108864;

	private static readonly string[] ShaderMapOwners = new string[4] { "Material", "MaterialInstanceConstant", "NiagaraScript", "ComputeGraph" };

	private const long LargeHeaderMax = 536870912L;

	private const long LargeRange = 4194304L;

	internal const EGame Newest = EGame.GAME_UE5_8;

	private static readonly Version NewestVersion = Version.Parse(VersionOf(EGame.GAME_UE5_8));

	private static readonly string PastNewest = $"newer than {NewestVersion}";

	private static readonly string[] KuroNames = new string[2] { "wutheringwaves", "wutheringwavesgame" };

	private static readonly (EGame Fork, EGame Containers, string[] Names)[] OlderBase = new (EGame, EGame, string[])[2]
	{
		(EGame.GAME_DeadIsland2, EGame.GAME_UE4_27, new string[2] { "deadisland2", "deadisland" }),
		(EGame.GAME_WutheringWaves, EGame.GAME_UE4_27, KuroNames)
	};

	public Func<Game, IEnumerable<string>>? UserCaches { get; set; }

	public long? MemoryBudget { get; init; }

	public EngineInfo? Detect(Game game)
	{
		string notes;
		return Detect(game, out notes);
	}

	public EngineInfo? Detect(Game game, out string notes)
	{
		notes = "";
		(string, EGame, EGame?, string, bool, EGame, string)? tuple = Locate(game);
		if (!tuple.HasValue)
		{
			return null;
		}
		var (paks, eGame, eGame2, text, guessed, eGame3, text2) = tuple.GetValueOrDefault();
		projects[game.InstallDir] = text;
		EGame game2 = eGame2 ?? eGame;
		(List<string>, List<string>, List<string>, Dictionary<string, string>, int?, string) tuple3 = Survey(paks, game2, text, null);
		string why = "";
		FAesKey fAesKey = null;
		if (text2 == null && tuple3.Item1.Count == 0 && tuple3.Item3.Count > 0)
		{
			using (KeyCheck keyCheck = new KeyCheck(tuple3.Item3, game2))
			{
				fAesKey = keys.Get(game, keyCheck.Opens, out why, new string[2] { eGame2?.ToString(), text });
			}
			if (fAesKey != null)
			{
				tuple3 = Survey(paks, game2, text, fAesKey);
			}
		}
		if (text2 == null && tuple3.Item5 > 24)
		{
			text2 = PastNewest;
		}
		if (text2 == null && !eGame2.HasValue && eGame3 > eGame)
		{
			eGame = Refine(eGame, eGame3, tuple3.Item5, tuple3.Item6, paks, fAesKey, game.ExePath, ref guessed);
		}
		else if (text2 == null && !eGame2.HasValue && eGame == EGame.GAME_UE5_5 && HasVerseCells(paks, fAesKey))
		{
			eGame = EGame.GAME_UE5_6;
		}
		if (text2 == null && !eGame2.HasValue)
		{
			EGame? eGame4 = RegistryEngine(tuple3.Item5);
			if (eGame4.HasValue)
			{
				EGame valueOrDefault = eGame4.GetValueOrDefault();
				if (valueOrDefault != eGame)
				{
					guessed = true;
				}
			}
		}
		ReleaseMemory();
		bool flag = text2 == null && tuple3.Item1.Count == 0 && tuple3.Item3.Count > 0;
		List<string> list;
		if (tuple3.Item1.Count > 0)
		{
			(list, _, _, _, _, _) = tuple3;
		}
		else
		{
			list = tuple3.Item2;
		}
		List<string> list2 = list;
		object obj;
		if (text2 == null)
		{
			if (!flag)
			{
				if (list2.Count != 0)
				{
					obj = ((!list2.Any((string p) => p.StartsWith("PCD3D_"))) ? ("no D3D shaders (" + string.Join(", ", list2) + ")") : null);
				}
				else
				{
					obj = "no shaders found (no shader library, no global shader cache)";
				}
			}
			else
			{
				string text3 = UnrealKeys.Protector(game.ExePath);
				obj = "encrypted game files (needs the game's AES key" + ((text3 != null) ? ("; its exe is protected (" + text3 + "), so SCSKiller can't read the key from it") : "") + ")";
			}
		}
		else
		{
			obj = "Unreal " + text2 + " isn't supported yet";
		}
		string unsupported = (string)obj;
		string text4 = text2 ?? VersionOf(eGame);
		(IReadOnlyList<string>, string) tuple5 = UnrealRhi.LaunchMenu(game);
		string? text5 = UnrealRhi.UserDir(game, text);
		string launch = UnrealRhi.LaunchOptions(game);
		string userDir = text5;
		(string Api, string Why) tuple6 = UnrealRhi.Resolve(text4.StartsWith('4') ? 4 : 5, list2, tuple3.Item4, text, userDir, launch, out var engineDefault, tuple5.Item1, tuple5.Item2, eGame2?.ToString(), UnrealRhi.IsWinGdk(game.ExePath));
		string item = tuple6.Api;
		string item2 = tuple6.Why;
		notes = ((why.Length > 0) ? (item2 + "; AES key: " + why) : item2);
		bool flag2 = UnrealRhi.RayTracingOff(tuple3.Item4, text, userDir, launch);
		return new EngineInfo("Unreal", text4, eGame2?.ToString(), item, flag, unsupported, flag2 || UnrealRhi.RtPipelinesOff(tuple3.Item4, text), flag2, UnrealRhi.AftermathShaderDebug(tuple3.Item4, text, launch) && ShipsAftermath(paks), guessed, engineDefault, null, UnrealRhi.MaxAnisotropy(text4.StartsWith('4') ? 4 : 5, tuple3.Item4, text, userDir, launch));
	}

	internal static bool ShipsAftermath(string paks)
	{
		InlineArray9<string> buffer = default;
		buffer[0] = paks;
		buffer[1] = "..";
		buffer[2] = "..";
		buffer[3] = "..";
		buffer[4] = "Engine";
		buffer[5] = "Binaries";
		buffer[6] = "ThirdParty";
		buffer[7] = "NVIDIA";
		buffer[8] = "NVaftermath";
		string fullPath = Path.GetFullPath(Path.Combine(buffer));
		try
		{
			return Directory.Exists(fullPath) && Directory.EnumerateFiles(fullPath, "GFSDK_Aftermath_Lib*.dll", SearchOption.AllDirectories).Any();
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return false;
		}
	}

	public bool SetKey(Game game, string key)
	{
		(string, EGame, EGame?, string, bool, EGame, string)? tuple = Locate(game);
		if (tuple.HasValue)
		{
			(string, EGame, EGame?, string, bool, EGame, string) valueOrDefault = tuple.GetValueOrDefault();
			(string, EGame, EGame?, string, bool, EGame, string) tuple2 = valueOrDefault;
			string item = tuple2.Item1;
			EGame item2 = tuple2.Item2;
			EGame? item3 = tuple2.Item3;
			string item4 = tuple2.Item4;
			(List<string>, List<string>, List<string>, Dictionary<string, string>, int?, string) tuple3 = Survey(item, item3 ?? item2, item4, null);
			if (tuple3.Item3.Count == 0)
			{
				return false;
			}
			using KeyCheck keyCheck = new KeyCheck(tuple3.Item3, item3 ?? item2);
			return keys.Set(game, key, keyCheck.Opens);
		}
		return false;
	}

	public string DetectStamp(Game game, EngineInfo? engine)
	{
		if (!projects.TryGetValue(game.InstallDir, out string value))
		{
			string text = PaksDir(game.InstallDir);
			if (text != null)
			{
				value = (projects[game.InstallDir] = ProjectOf(text));
			}
		}
		if (value != null)
		{
			return UnrealRhi.Stamp(game, value) + "|" + keys.KeyStamp(game) + keys.PublicStamp(game, new string[1] { value });
		}
		return "";
	}

	private static string ProjectOf(string paks)
	{
		return Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(paks)));
	}

	private static (string Paks, EGame Base, EGame? Fork, string Project, bool Guessed, EGame UpTo, string? Newer)? Locate(Game game)
	{
		string text = PaksDir(game.InstallDir);
		if (text == null)
		{
			return null;
		}
		EGame eGame = DetectEngine(game, text, out EGame upTo, out bool fromContainers, out bool guessed, out string newer);
		EGame? item = ((newer == null) ? DetectFork(eGame, upTo, Path.GetFileName(game.InstallDir.TrimEnd(new char[2] { '\\', '/' })), Path.GetFileNameWithoutExtension(game.ExePath), fromContainers, game.Name) : ((EGame?)null));
		int item2;
		if (item.HasValue)
		{
			EGame valueOrDefault = item.GetValueOrDefault();
			item2 = (int)(valueOrDefault & (EGame)0xFFFF0000u);
		}
		else
		{
			item2 = (int)eGame;
		}
		return (text, (EGame)item2, item, ProjectOf(text), guessed && !item.HasValue, upTo, newer);
	}

	private static AbstractAesVfsReader OpenContainer(string path, VersionContainer versions)
	{
		AbstractAesVfsReader abstractAesVfsReader = (path.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase) ? ((AbstractAesVfsReader)new IoStoreReader(path, EIoStoreTocReadOptions.ReadDirectoryIndex, versions)) : ((AbstractAesVfsReader)new PakFileReader(path, versions)));
		if (versions.Game != EGame.GAME_MarvelRivals || !(abstractAesVfsReader is IoStoreReader))
		{
			abstractAesVfsReader.CustomEncryption = Decryption(versions.Game);
		}
		return abstractAesVfsReader;
	}

	private static bool Opens(IAesVfsReader r, FAesKey key)
	{
		try
		{
			return r.TestAesKey(key);
		}
		catch (Exception ex) when (!(ex is OutOfMemoryException))
		{
			return false;
		}
	}

	private static IAesVfsReader.CustomEncryptionDelegate? Decryption(EGame game)
	{
		return decryptions.GetOrAdd(game, (EGame g) =>
		{
			using DefaultFileProvider defaultFileProvider = new DefaultFileProvider(".", SearchOption.TopDirectoryOnly, new VersionContainer(g));
			return defaultFileProvider.CustomEncryption;
		});
	}

	private static (List<string> Libraries, List<string> Globals, List<string> Encrypted, Dictionary<string, string> Configs, int? Registry, string? LibraryToc) Survey(string paks, EGame game, string project, FAesKey? key)
	{
		InitCodecs();
		VersionContainer versions = new VersionContainer(game);
		List<string> list = new List<string>();
		List<string> list2 = new List<string>();
		List<string> list3 = new List<string>();
		Dictionary<string, (long, string)> dictionary = new Dictionary<string, (long, string)>(StringComparer.OrdinalIgnoreCase);
		(long, int)? tuple = null;
		string text = null;
		foreach (string item in from f in Directory.EnumerateFiles(paks)
			where f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase)
			select f)
		{
			if (item.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase))
			{
				byte b = TocHeader(item)[80];
				if ((b & 8) == 0)
				{
					continue;
				}
				if ((b & 2) == 0)
				{
					int count = list.Count;
					TocShaderFiles(item, list, list2);
					if (list.Count > count && (text == null || new FileInfo(item).Length < new FileInfo(text).Length))
					{
						text = item;
					}
					continue;
				}
				if (key == null)
				{
					list3.Add(item);
					continue;
				}
			}
			AbstractAesVfsReader abstractAesVfsReader;
			try
			{
				abstractAesVfsReader = OpenContainer(item, versions);
			}
			catch (Exception)
			{
				continue;
			}
			int num = 0;
			using (abstractAesVfsReader)
			{
				if (abstractAesVfsReader.IsEncrypted && (key == null || !Opens(abstractAesVfsReader, key)))
				{
					list3.Add(item);
					continue;
				}
				abstractAesVfsReader.AesKey = key;
				try
				{
					abstractAesVfsReader.Mount(StringComparer.OrdinalIgnoreCase);
				}
				catch (Exception)
				{
					goto end_IL_011e;
				}
				num = abstractAesVfsReader.FileCount;
				foreach (var (text3, gameFile2) in abstractAesVfsReader.Files)
				{
					(long, string) value;
					if ((gameFile2.Extension == "ushaderbytecode" || IsGlobalCache(gameFile2)) && gameFile2.IsEncrypted && abstractAesVfsReader.AesKey == null)
					{
						list3.Add(item);
					}
					else if (gameFile2.Extension == "ushaderbytecode")
					{
						list.Add(gameFile2.NameWithoutExtension.Split('-')[^1]);
					}
					else if (IsGlobalCache(gameFile2))
					{
						list2.Add(gameFile2.NameWithoutExtension.Substring("GlobalShaderCache-".Length));
					}
					else if (gameFile2.Extension == "ini" && UnrealRhi.IsConfig(text3, project) && (!dictionary.TryGetValue(text3, out value) || value.Item1 < abstractAesVfsReader.ReadOrder))
					{
						try
						{
							dictionary[text3] = (abstractAesVfsReader.ReadOrder, Encoding.UTF8.GetString(gameFile2.Read()));
						}
						catch (Exception)
						{
						}
					}
					else if (text3.Equals(project + "/AssetRegistry.bin", StringComparison.OrdinalIgnoreCase) && !(tuple?.Item1 >= abstractAesVfsReader.ReadOrder))
					{
						int? num2 = RegistryVersion(gameFile2);
						if (num2.HasValue)
						{
							int valueOrDefault = num2.GetValueOrDefault();
							tuple = (abstractAesVfsReader.ReadOrder, valueOrDefault);
						}
					}
				}
				goto IL_0357;
				end_IL_011e:;
			}
			continue;
			IL_0357:
			if (num > 1000)
			{
				GC.Collect();
			}
		}
		return (Libraries: list.Distinct().Order(StringComparer.Ordinal).ToList(), Globals: list2.Distinct().Order(StringComparer.Ordinal).ToList(), Encrypted: list3.Distinct().Order(StringComparer.Ordinal).ToList(), Configs: dictionary.ToDictionary((KeyValuePair<string, (long Order, string Text)> c) => c.Key, (KeyValuePair<string, (long Order, string Text)> c) => c.Value.Text, StringComparer.OrdinalIgnoreCase), Registry: tuple?.Item2, LibraryToc: text);
	}

	internal static int? RegistryVersion(GameFile file)
	{
		try
		{
			FPakEntry fPakEntry = file as FPakEntry;
			bool flag = fPakEntry?.IsCompressed ?? false;
			if (flag)
			{
				FPakCompressedBlock fPakCompressedBlock = default;
				int num2;
				if (fPakEntry.CompressionBlockSize <= 1048576)
				{
					FPakCompressedBlock[] compressionBlocks = fPakEntry.CompressionBlocks;
					int num;
					if (compressionBlocks != null && compressionBlocks.Length >= 1)
					{
						fPakCompressedBlock = compressionBlocks[0];
						num = 1;
					}
					else
					{
						num = 0;
					}
					num2 = ((num == 0) ? 1 : 0);
				}
				else
				{
					num2 = 1;
				}
				bool flag2 = (byte)num2 != 0;
				if (!flag2)
				{
					long num3 = fPakCompressedBlock.CompressedEnd - fPakCompressedBlock.CompressedStart;
					bool flag3 = ((num3 < 0 || num3 > 1048576) ? true : false);
					flag2 = flag3;
				}
				flag = flag2;
			}
			if (flag)
			{
				return null;
			}
			byte[] array = file.Read(new FByteBulkDataHeader(EBulkDataFlags.BULKDATA_None, 0, 20u, 0L, default));
			return (array.Length >= 20 && ((ReadOnlySpan<byte>)array.AsSpan(0, 16)).SequenceEqual((ReadOnlySpan<byte>)RegistryGuid)) ? new int?(BitConverter.ToInt32(array, 16)) : ((int?)null);
		}
		catch (Exception ex) when (!(ex is OutOfMemoryException))
		{
			return null;
		}
	}

	private static int? LibraryHashWidth(string utoc, FAesKey? key)
	{
		try
		{
			if (!Sane(utoc))
			{
				return null;
			}
			using IoStoreReader ioStoreReader = new IoStoreReader(utoc, EIoStoreTocReadOptions.ReadDirectoryIndex, new VersionContainer(EGame.GAME_UE5_5));
			if (ioStoreReader.IsEncrypted)
			{
				ioStoreReader.AesKey = key ?? throw new InvalidDataException("encrypted");
			}
			ioStoreReader.Mount(StringComparer.OrdinalIgnoreCase);
			GameFile gameFile = ioStoreReader.Files.Values.Where((GameFile f) => f.Extension == "ushaderbytecode").MinBy((GameFile f) => f.Size);
			if (gameFile == null || gameFile.Size > 67108864)
			{
				return null;
			}
			byte[] array = gameFile.Read();
			if (array.Length < 4 || BitConverter.ToUInt32(array) != 1)
			{
				return null;
			}
			bool num = LibraryEnd(array, 8, ioStore: true) == array.Length;
			bool flag = LibraryEnd(array, 20, ioStore: true) == array.Length;
			bool flag2 = num;
			return (flag2 == flag) ? ((int?)null) : new int?(flag2 ? 8 : 20);
		}
		catch (Exception ex) when (!(ex is OutOfMemoryException))
		{
			return null;
		}
	}

	private static string? LaneSuffix((uint Min, uint Max)? lanes)
	{
		if (lanes.HasValue)
		{
			(uint, uint) valueOrDefault = lanes.GetValueOrDefault();
			if (valueOrDefault.Item1 > 32 || valueOrDefault.Item2 < 32)
			{
				return $" wave{valueOrDefault.Item1}{((valueOrDefault.Item2 != valueOrDefault.Item1) ? $"-{valueOrDefault.Item2}" : "")}";
			}
		}
		return null;
	}

	private static void ReleaseMemory()
	{
		GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
	}

	private static void Collect()
	{
		GC.Collect(2, GCCollectionMode.Forced, blocking: false);
	}

	public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
	{
		return Index(game, engine, log, null, ct);
	}

	public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
	{
		try
		{
			using DefaultFileProvider provider = Mount(PaksDir(game.InstallDir) ?? throw new DirectoryNotFoundException("no Content/Paks under " + game.InstallDir), GameOf(engine), keys.Stored(game));
			return IndexCore(provider, game, engine, log, ct, progress);
		}
		finally
		{
			Collect();
		}
	}

	internal ShaderIndex IndexCore(DefaultFileProvider provider, Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct, IProgress<(int Done, int Total)>? progress = null)
	{
		bool ue5 = engine.Version.StartsWith('5') || engine.Version.StartsWith('6');
		bool ue58 = Version.TryParse(engine.Version, out Version result) && result >= new Version(5, 8);
		if (!Libraries(provider).Any())
		{
			return IndexInline(provider, game, log, ct, ue5, ue58, progress);
		}
		ConcurrentDictionary<string, ShaderInfo> shaders = new ConcurrentDictionary<string, ShaderInfo>();
		ShaderContainer.WideCounts wide = new ShaderContainer.WideCounts();
		List<ShaderMap> list = new List<ShaderMap>();
		List<string> list2 = new List<string>();
		Dictionary<string, (string, string)> dictionary = new Dictionary<string, (string, string)>();
		ConcurrentDictionary<string, string> lanes = new ConcurrentDictionary<string, string>();
		Dictionary<string, (string, string)> dictionary2 = new Dictionary<string, (string, string)>();
		int num = 0;
		string why = null;
		LibraryOpener libraryOpener = new LibraryOpener();
		using (IncrementalHash incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1))
		{
			foreach (Library lib in Libraries(provider))
			{
				Stopwatch stopwatch = Stopwatch.StartNew();
				Archive arc = libraryOpener.Open(provider, lib.File, out Exception error);
				try
				{
					if (error != null)
					{
						log?.Report(lib.File.Path + ": unreadable, skipped (" + error.Message + ")");
						continue;
					}
					if (arc == null)
					{
						log?.Report(lib.File.Path + ": unsupported shader archive layout, skipped");
						continue;
					}
					string[] sha = new string[arc.Count];
					int bad = 0;
					int failed = 0;
					Parallel.ForEach(arc.Codes, new ParallelOptions
					{
						TaskScheduler = TaskScheduler.Current,
						CancellationToken = ct
					}, (Func<IEnumerable<(int, byte[])>> work) =>
					{
						IEnumerable<(int, byte[])> enumerable;
						try
						{
							enumerable = work();
						}
						catch (InvalidDataException ex)
						{
							Interlocked.Increment(ref failed);
							Interlocked.CompareExchange(ref why, ex.Message, null);
							return;
						}
						foreach (var (num5, code) in enumerable)
						{
							var (text, readOnlyMemory) = Hash(code);
							sha[num5] = text;
							if (!shaders.ContainsKey(text))
							{
								try
								{
									ShaderInfo shaderInfo = ShaderContainer.Parse(readOnlyMemory.Span, text, ShaderContainer.UeCounts(code, ue5, ue58));
									if ((object)shaderInfo != null && shaders.TryAdd(text, shaderInfo) && !ue5)
									{
										wide.See(code, shaderInfo);
									}
									string text2 = LaneSuffix(Dxbc.WaveLanes(readOnlyMemory.Span));
									if (text2 != null)
									{
										lanes[text] = text2;
									}
								}
								catch (Exception ex2) when ((ex2 is ArgumentException || ex2 is IndexOutOfRangeException) ? true : false)
								{
									Interlocked.Increment(ref bad);
								}
							}
						}
					});
					num += failed;
					if (!list2.Contains(lib.Platform))
					{
						list2.Add(lib.Platform);
					}
					incrementalHash.AppendData(Encoding.UTF8.GetBytes(lib.Name + "/" + lib.Platform + "\n"));
					string[] array = arc.Keys;
					if (array != null)
					{
						for (int num2 = 0; num2 < array.Length; num2++)
						{
							if (sha[num2] != null)
							{
								dictionary[array[num2]] = (sha[num2], lib.Platform);
							}
						}
					}
					string[] hashes = arc.Hashes;
					if (hashes != null)
					{
						for (int num3 = 0; num3 < Math.Min(hashes.Length, sha.Length); num3++)
						{
							if (sha[num3] != null && shaders.ContainsKey(sha[num3]))
							{
								dictionary2[hashes[num3]] = (sha[num3], lib.Platform);
							}
						}
					}
					for (int num4 = 0; num4 < arc.MapHashes.Length; num4++)
					{
						List<string> list3 = (from k in Enumerable.Range(arc.Maps[num4].Off, arc.Maps[num4].Num)
							select sha[arc.Indices[k]] into h
							where h != null
							select h).ToList();
						list.Add(new ShaderMap(arc.MapHashes[num4], lib.Name, lib.Platform, list3.Where((string h) => !Wave(h)).ToList()));
						foreach (IGrouping<string, string> item in from h in list3.Where(Wave)
							group h by lib.Platform + lanes[h])
						{
							list.Add(new ShaderMap(arc.MapHashes[num4], lib.Name, item.Key, item.ToList()));
							if (!list2.Contains(item.Key))
							{
								list2.Add(item.Key);
							}
						}
						incrementalHash.AppendData(Encoding.UTF8.GetBytes(arc.MapHashes[num4] + ":" + string.Join(',', list3) + "\n"));
					}
					log?.Report($"{lib.File.Name}: {((arc.Keys != null) ? "version 1 (no maps)" : $"{arc.MapHashes.Length} shader maps")}, {arc.Count} shaders{((bad > 0) ? $", {bad} unparseable" : "")}{((failed > 0) ? $", {failed} code blocks that don't decompress, skipped" : "")} ({stopwatch.Elapsed.TotalSeconds:F1}s)");
				}
				finally
				{
					if ((object)arc != null)
					{
						((IDisposable)arc).Dispose();
					}
				}
			}
			libraryOpener.ThrowIfNoneOpened();
			if (shaders.IsEmpty && num > 0)
			{
				throw new InvalidDataException($"none of the game's shader code decompresses ({num} blocks): {why}");
			}
			if (dictionary.Count > 0)
			{
				Stopwatch stopwatch2 = Stopwatch.StartNew();
				List<ShaderMap> list4 = ReferencedMaps(provider, dictionary, ct, out var scanned);
				foreach (ShaderMap item2 in list4)
				{
					incrementalHash.AppendData(Encoding.UTF8.GetBytes(item2.Hash + ":" + string.Join(',', item2.Shaders) + "\n"));
				}
				list.AddRange(list4);
				log?.Report($"shader maps from package references: {list4.Count} of {scanned} scanned packages, {list4.SelectMany((ShaderMap m) => m.Shaders).Distinct().Count()} of {dictionary.Values.Select(((string Sha, string Platform) v) => v.Sha).Distinct().Count()} archive shaders referenced ({stopwatch2.Elapsed.TotalSeconds:F1}s)");
			}
			if (wide.Apply(shaders))
			{
				log?.Report("resource counts: 10-byte layout with a 16-bit SRV count (the shaders' bindings agree)");
			}
			list.AddRange(ShippedPipelines(provider, dictionary2, log));
			list.AddRange(UserPipelines(game, dictionary2, log));
			return new ShaderIndex(Convert.ToHexStringLower(incrementalHash.GetHashAndReset()), list2, shaders, list);
		}
		bool Wave(string? h)
		{
			if (h != null)
			{
				return lanes.ContainsKey(h);
			}
			return false;
		}
	}

	private static List<ShaderMap> ShippedPipelines(AbstractFileProvider provider, Dictionary<string, (string Sha, string Platform)> byHash, IProgress<string>? log)
	{
		List<ShaderMap> list = new List<ShaderMap>();
		foreach (GameFile item in provider.Files.Values.Where((GameFile f) => f.Path.EndsWith(".stable.upipelinecache", StringComparison.OrdinalIgnoreCase)).OrderBy((GameFile f) => f.Path, StringComparer.Ordinal))
		{
			byte[] array = null;
			List<StablePipelineCache.Pso> list2;
			try
			{
				list2 = StablePipelineCache.Read(array = item.Read());
			}
			catch (Exception)
			{
				list2 = null;
			}
			string value;
			if (list2 == null)
			{
				if (log == null)
				{
					continue;
				}
				uint? num = StablePipelineCache.Version(array);
				if (num.HasValue)
				{
					uint valueOrDefault = num.GetValueOrDefault();
					if (valueOrDefault > 31)
					{
						value = $"{item.Name}: pipeline cache version {valueOrDefault}, newer than Unreal {NewestVersion}'s ({31u}): its pipelines aren't compiled from it";
						goto IL_0149;
					}
				}
				value = item.Name + ": not a pipeline cache this reads, skipped";
				goto IL_0149;
			}
			list.AddRange(Pipelines(item.Name, item.NameWithoutExtension, "PipelineCache", list2, byHash, log));
			continue;
			IL_0149:
			log.Report(value);
		}
		return list;
	}

	private List<ShaderMap> UserPipelines(Game game, Dictionary<string, (string Sha, string Platform)> byHash, IProgress<string>? log)
	{
		try
		{
			return UserPipelines(UserCaches?.Invoke(game).ToList() ?? new List<string>(), byHash, log, 67108864L);
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) ? true : false)
		{
			return new List<ShaderMap>();
		}
	}

	internal static List<ShaderMap> UserPipelines(IEnumerable<string> files, Dictionary<string, (string Sha, string Platform)> byHash, IProgress<string>? log, long max = 67108864L)
	{
		List<ShaderMap> list = new List<ShaderMap>();
		foreach (string file in files)
		{
			string fileName = Path.GetFileName(file);
			byte[] array;
			try
			{
				using FileStream fileStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
				long length = fileStream.Length;
				if (length > max)
				{
					log?.Report($"{fileName}: {length:N0} bytes, too big for a user pipeline cache, skipped");
					continue;
				}
				array = new byte[length];
				fileStream.ReadExactly(array);
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
				log?.Report(fileName + ": unreadable, skipped (" + ex.Message + ")");
				continue;
			}
			List<StablePipelineCache.Pso> list2 = StablePipelineCache.Read(array);
			if (list2 == null)
			{
				log?.Report(fileName + ": not a pipeline cache this reads, skipped");
			}
			else
			{
				list.AddRange(Pipelines("user " + fileName, Path.GetFileNameWithoutExtension(fileName), "UserPipelineCache", list2, byHash, log));
			}
		}
		return list;
	}

	private static IEnumerable<ShaderMap> Pipelines(string what, string name, string library, List<StablePipelineCache.Pso> psos, Dictionary<string, (string Sha, string Platform)> byHash, IProgress<string>? log)
	{
		List<StablePipelineCache.Pso> list = psos.Where((StablePipelineCache.Pso p) => p.Type == StablePipelineCache.PsoType.Graphics).ToList();
		List<StablePipelineCache.Pso> list2 = list.Where((StablePipelineCache.Pso p) => p.Shaders.Length != 0 && p.Shaders.All(byHash.ContainsKey)).ToList();
		log?.Report($"{what}: {psos.Count} PSOs, {list.Count} graphics{((list2.Count < list.Count) ? $" ({list.Count - list2.Count} name a shader no library has)" : "")}");
		return list2.Select((StablePipelineCache.Pso p) => new ShaderMap($"{name}:{p.Key:x8}", library, byHash[p.Shaders[0]].Platform, p.Shaders.Select((string h) => byHash[h].Sha).ToList(), IsPipeline: true));
	}

	public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
	{
		try
		{
			using DefaultFileProvider provider = Mount(PaksDir(game.InstallDir) ?? throw new DirectoryNotFoundException("no Content/Paks under " + game.InstallDir), GameOf(engine), keys.Stored(game));
			ReadShadersCore(provider, game, sha1s, sink, ct);
		}
		finally
		{
			Collect();
		}
	}

	internal void ReadShadersCore(DefaultFileProvider provider, Game game, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
	{
		if (!Libraries(provider).Any())
		{
			ReadInline(provider, game, sha1s, sink, ct);
			return;
		}
		ConcurrentDictionary<string, bool> left = new ConcurrentDictionary<string, bool>(sha1s.Select((string s) => KeyValuePair.Create(s, value: true)));
		LibraryOpener libraryOpener = new LibraryOpener();
		foreach (Library item in Libraries(provider))
		{
			if (left.IsEmpty)
			{
				break;
			}
			Exception error;
			using Archive archive = libraryOpener.Open(provider, item.File, out error);
			if (archive == null)
			{
				continue;
			}
			Parallel.ForEach(archive.Codes, new ParallelOptions
			{
				TaskScheduler = TaskScheduler.Current,
				CancellationToken = ct
			}, (Func<IEnumerable<(int, byte[])>> work) =>
			{
				IEnumerable<(int, byte[])> enumerable;
				try
				{
					enumerable = work();
				}
				catch (InvalidDataException)
				{
					return;
				}
				foreach (var item2 in enumerable)
				{
					var (text, readOnlyMemory) = Hash(item2.Item2);
					if (left.TryRemove(text, out var _))
					{
						lock (left)
						{
							sink(text, readOnlyMemory.ToArray());
						}
					}
				}
			});
		}
		libraryOpener.ThrowIfNoneOpened();
	}

	internal ShaderIndex IndexInline(DefaultFileProvider provider, Game game, IProgress<string>? log, CancellationToken ct, bool ue5 = false, bool ue58 = false, IProgress<(int Done, int Total)>? progress = null)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		provider.MappingsContainer = new NoMappings();
		List<GameFile> list = provider.Files.Values.Where((GameFile f) => f.IsUePackage || IsGlobalCache(f)).ToList();
		ConcurrentDictionary<string, ShaderInfo> shaders = new ConcurrentDictionary<string, ShaderInfo>();
		ShaderContainer.WideCounts wide = new ShaderContainer.WideCounts();
		ConcurrentBag<ShaderMap> maps = new ConcurrentBag<ShaderMap>();
		ConcurrentDictionary<string, string> where = new ConcurrentDictionary<string, string>();
		ConcurrentBag<string> stamps = new ConcurrentBag<string>();
		long read = 0L;
		int carved = 0;
		int unparsed = 0;
		int unreadable = 0;
		int undecoded = 0;
		int bad = 0;
		ByteBudget budget = ByteBudget.FromFreeMemory();
		PlanBudget limit = new PlanBudget(MemoryBudget ?? PlanBudget.DefaultMemory(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes), TimeSpan.MaxValue)
		{
			What = "the shader read",
			Why = "this game's packages hold more shaders than a compile can read"
		};
		ForEachPackage(list, limit, progress, ct, (GameFile f) =>
		{
			bool flag = IsGlobalCache(f);
			bool? flag2 = (flag ? new bool?(true) : OwnsShaderMaps(provider, f));
			if (flag2 == false)
			{
				return;
			}
			if (!flag2.HasValue)
			{
				Interlocked.Increment(ref unparsed);
			}
			using (budget.Take(PackageSize(provider, f)))
			{
				byte[][] array;
				string stamp;
				try
				{
					array = PackageParts(provider, f, out stamp);
				}
				catch (Exception)
				{
					Interlocked.Increment(ref unreadable);
					return;
				}
				Interlocked.Add(ref read, ((IEnumerable<byte[]>)array).Sum((Func<byte[], long>)((byte[] p) => p.Length)));
				Interlocked.Increment(ref carved);
				stamps.Add(stamp);
				Dictionary<int, List<string>> lists = new Dictionary<int, List<string>>();
				Interlocked.Add(ref undecoded, InlineShaders.Carve(array, (InlineShaders.Entry e) =>
				{
					var (text5, readOnlyMemory) = Hash(e.Code);
					if (!lists.TryGetValue(e.Map, out List<string> value2))
					{
						value2 = (lists[e.Map] = new List<string>());
					}
					value2.Add(text5);
					where.TryAdd(text5, $"{e.Format} {e.Offset} {f.Path}");
					if (shaders.ContainsKey(text5))
					{
						return;
					}
					try
					{
						ShaderInfo shaderInfo = ShaderContainer.Parse(readOnlyMemory.Span, text5, ShaderContainer.UeCounts(e.Code, ue5, ue58));
						if ((object)shaderInfo != null && shaders.TryAdd(text5, shaderInfo) && !ue5)
						{
							wide.See(e.Code, shaderInfo);
						}
					}
					catch (Exception ex2) when ((ex2 is ArgumentException || ex2 is IndexOutOfRangeException) ? true : false)
					{
						Interlocked.Increment(ref bad);
					}
				}));
				foreach (var (value, source) in lists)
				{
					maps.Add(new ShaderMap($"{f.Path}#{value}", flag ? "Global" : f.Path, "", source.Distinct().ToList()));
				}
			}
		});
		wide.Apply(shaders);
		List<string> d3d = (from f in list.Where(IsGlobalCache)
			select f.NameWithoutExtension.Substring("GlobalShaderCache-".Length) into p
			where p.StartsWith("PCD3D_")
			select p).Distinct().ToList();
		List<ShaderMap> list2 = Canonical(maps.Select((ShaderMap m) => m with
		{
			Platform = ((d3d.Count == 1) ? d3d[0] : (m.Shaders.Any((string h) =>
			{
				if (shaders.TryGetValue(h, out ShaderInfo value) && value.Stage != Stage.Library)
				{
					string[] array = value.ShaderModel.Split('_');
					if (array != null && array.Length >= 2)
					{
						return array[1] == "6";
					}
					return false;
				}
				return false;
			}) ? "PCD3D_SM6" : "PCD3D_SM5"))
		}));
		string path = Path.Combine(store.GameDir(game.Id), "inline.idx");
		Directory.CreateDirectory(Path.GetDirectoryName(path));
		using (StreamWriter streamWriter = new StreamWriter(new GZipStream(File.Create(path), CompressionLevel.Fastest)))
		{
			foreach (var (text3, text4) in where)
			{
				streamWriter.WriteLine(text3 + " " + text4);
			}
		}
		byte[] inArray = SHA1.HashData(Encoding.UTF8.GetBytes(string.Join('\n', stamps.Order(StringComparer.Ordinal))));
		IEnumerable<string> values = from s in shaders.Values
			group s by s.Stage into g
			orderby g.Key
			select $"{g.Key} {g.Count()}";
		log?.Report($"shaders inside packages: carved {carved} of {list.Count} packages ({unparsed} by an unparsed header, {unreadable} unreadable; {read >> 20} MB), {list2.Count} maps, {shaders.Count} shaders ({string.Join(", ", values)}), {shaders.Values.Count((ShaderInfo s) => s.Counts == new ResourceCounts(0, 0, 0, 0))} without resource counts{((undecoded + bad > 0) ? $", {undecoded} undecoded, {bad} unparseable" : "")} ({stopwatch.Elapsed.TotalSeconds:F1}s)");
		return new ShaderIndex(Convert.ToHexStringLower(inArray), list2.Select((ShaderMap m) => m.Platform).Distinct().Order(StringComparer.Ordinal)
			.ToList(), shaders, list2);
	}

	internal void ReadInline(DefaultFileProvider provider, Game game, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
	{
		string path = Path.Combine(store.GameDir(game.Id), "inline.idx");
		if (!File.Exists(path))
		{
			IndexInline(provider, game, null, ct);
		}
		Dictionary<string, (char Format, int Offset, string Path)> at = new Dictionary<string, (char, int, string)>();
		using (StreamReader streamReader = new StreamReader(new GZipStream(File.OpenRead(path), CompressionMode.Decompress)))
		{
			for (string text = streamReader.ReadLine(); text != null; text = streamReader.ReadLine())
			{
				string[] array = text.Split(' ', 4);
				if (array != null && array.Length == 4)
				{
					string text2 = array[0];
					string text3 = array[1];
					string s = array[2];
					string item = array[3];
					if (sha1s.Contains(text2))
					{
						at[text2] = (text3[0], int.Parse(s), item);
					}
				}
			}
		}
		ByteBudget budget = ByteBudget.FromFreeMemory();
		ForEachPackage((from a in at
			group a by a.Value.Path).ToList(), null, null, ct, (IGrouping<string, KeyValuePair<string, (char Format, int Offset, string Path)>> g) =>
		{
			if (!provider.Files.TryGetValue(g.Key, out GameFile value))
			{
				return;
			}
			using (budget.Take(PackageSize(provider, value)))
			{
				byte[][] array2;
				string stamp;
				try
				{
					array2 = PackageParts(provider, value, out stamp);
				}
				catch (Exception)
				{
					return;
				}
				foreach (KeyValuePair<string, (char, int, string)> item2 in g)
				{
					item2.Deconstruct(out stamp, out var value2);
					(char, int, string) tuple = value2;
					string text4 = stamp;
					var (format, num, _) = tuple;
					int offset;
					byte[] d;
					if (array2.Length != 2 || num < array2[0].Length)
					{
						byte[] obj = array2[0];
						int num2 = num;
						offset = num2;
						d = obj;
					}
					else
					{
						byte[] obj2 = array2[1];
						int num2 = num - array2[0].Length;
						offset = num2;
						d = obj2;
					}
					byte[] array3 = InlineShaders.Decode(d, offset, format);
					if (array3 != null)
					{
						var (text5, readOnlyMemory) = Hash(array3);
						if (text5 == text4)
						{
							lock (at)
							{
								sink(text4, readOnlyMemory.ToArray());
							}
						}
					}
				}
			}
		});
	}

	private static bool? OwnsShaderMaps(IVfsFileProvider provider, GameFile f)
	{
		long num = Math.Min(f.Size, 65536L);
		while (true)
		{
			try
			{
				FArchive uasset = ((num == f.Size) ? f.CreateReader() : f.CreateReader(new FByteBulkDataHeader(EBulkDataFlags.BULKDATA_None, 0, (uint)num, 0L, default)));
				IEnumerable<string> source;
				if (f is FIoStoreEntry fIoStoreEntry)
				{
					IoPackage p = new IoPackage(uasset, fIoStoreEntry.IoStoreReader.ContainerHeader, (Func<FByteBulkDataHeader?, FArchive?>?)null, (Func<FByteBulkDataHeader?, FArchive?>?)null, provider);
					source = from e in p.ExportMap
						where e.ClassIndex.IsScriptImport || e.ClassIndex.IsExport
						select p.ResolveObjectIndex(e.ClassIndex)?.Name.Text ?? "";
				}
				else
				{
					Package p2 = new Package(uasset, (FArchive?)null, (Func<FByteBulkDataHeader?, FArchive?>?)null, (Func<FByteBulkDataHeader?, FArchive?>?)null, (IFileProvider?)provider, true);
					source = p2.ExportMap.Select((FObjectExport e) =>
					{
						string result = ((!e.ClassIndex.IsImport) ? ((!e.ClassIndex.IsExport) ? "" : p2.ExportMap[e.ClassIndex.Index - 1].ObjectName.Text) : p2.ImportMap[-e.ClassIndex.Index - 1].ObjectName.Text);
						return result;
					});
				}
				return source.Any((string c) => ShaderMapOwners.Any((string o) => c.EndsWith(o, StringComparison.Ordinal)));
			}
			catch (Exception) when (num < f.Size)
			{
			}
			catch (Exception)
			{
				return null;
			}
			num = f.Size;
		}
	}

	private static byte[][] PackageParts(DefaultFileProvider provider, GameFile f, out string stamp)
	{
		provider.Files.FindPayloads(f, out GameFile uexp, out IReadOnlyList<GameFile> _, out IReadOnlyList<GameFile> _);
		stamp = ((uexp == null) ? At(f) : (At(f) + "|" + At(uexp)));
		if (uexp == null)
		{
			return new byte[1][] { f.Read() };
		}
		return new byte[2][]
		{
			f.Read(),
			uexp.Read()
		};
		static string At(GameFile g)
		{
			if (g is VfsEntry vfsEntry)
			{
				return $"{g.Path}|{vfsEntry.Vfs.Name}:{vfsEntry.Offset}:{g.Size}";
			}
			return $"{g.Path}|{g.Size}";
		}
	}

	private static long PackageSize(DefaultFileProvider provider, GameFile f)
	{
		provider.Files.FindPayloads(f, out GameFile uexp, out IReadOnlyList<GameFile> _, out IReadOnlyList<GameFile> _);
		return f.Size + (uexp?.Size ?? 0);
	}

	internal static void ForEachPackage<T>(IReadOnlyCollection<T> items, PlanBudget? limit, IProgress<(int Done, int Total)>? progress, CancellationToken ct, Action<T> body)
	{
		int count = items.Count;
		int total = count;
		int done = 0;
		if (limit != null)
		{
			limit.Progress = () => $"{Volatile.Read(in done):N0} of {total:N0} packages";
		}
		PlanTooBigException over = null;
		CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
		try
		{
			try
			{
				Parallel.ForEach(items, new ParallelOptions
				{
					TaskScheduler = TaskScheduler.Current,
					CancellationToken = stop.Token,
					MaxDegreeOfParallelism = Environment.ProcessorCount
				}, () =>
				{
					ThreadPriority priority = Thread.CurrentThread.Priority;
					Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
					return priority;
				}, (T item, ParallelLoopState _, ThreadPriority was) =>
				{
					try
					{
						limit?.Check();
					}
					catch (PlanTooBigException value)
					{
						Interlocked.CompareExchange(ref over, value, null);
						stop.Cancel();
						return was;
					}
					body(item);
					int num = Interlocked.Increment(ref done);
					if ((long)num * 100L / total != (long)(num - 1) * 100L / total)
					{
						progress?.Report((num, total));
					}
					return was;
				}, (ThreadPriority was) =>
				{
					Thread.CurrentThread.Priority = was;
				});
			}
			catch (Exception ex) when (((Func<bool>)delegate
			{
				// Could not convert BlockContainer to single expression
				bool flag = over != null;
				if (flag)
				{
					flag = ((ex is OperationCanceledException || ex is AggregateException) ? true : false);
				}
				return flag;
			}).Invoke())
			{
			}
			ct.ThrowIfCancellationRequested();
			if (over != null)
			{
				ExceptionDispatchInfo.Throw(over);
			}
		}
		finally
		{
			if (stop != null)
			{
				((IDisposable)stop).Dispose();
			}
		}
	}

	private static IEnumerable<Library> Libraries(AbstractFileProvider provider)
	{
		return provider.Files.Values.Where((GameFile f) => f.Extension == "ushaderbytecode").OrderBy((GameFile f) => f.Path, StringComparer.Ordinal).Select((GameFile f) =>
		{
			string[] array = f.NameWithoutExtension.Split('-');
			string platform = array[^1];
			return new Library(f, string.Join('-', array[1..].Reverse().SkipWhile((string p) => p == platform).Reverse()), platform);
		});
	}

	internal static FShaderCodeArchive ReadLibrary(string path, byte[] bytes, EGame game)
	{
		uint num = ((bytes.Length >= 4) ? BitConverter.ToUInt32(bytes) : 0);
		EGame eGame = PickLayout(path, bytes, bytes.Length, game);
		if (num == 2)
		{
			(long, int)? tuple = FormatName(bytes, bytes.Length, eGame);
			if (tuple.HasValue)
			{
				(long, int) valueOrDefault = tuple.GetValueOrDefault();
				Span<byte> span = bytes.AsSpan(0, (int)valueOrDefault.Item1);
				Span<byte> span2 = bytes.AsSpan((int)(valueOrDefault.Item1 + valueOrDefault.Item2));
				int num2 = 0;
				byte[] array = new byte[span.Length + span2.Length];
				span.CopyTo(new Span<byte>(array).Slice(num2, span.Length));
				num2 += span.Length;
				span2.CopyTo(new Span<byte>(array).Slice(num2, span2.Length));
				num2 += span2.Length;
				bytes = array;
			}
		}
		try
		{
			return new FShaderCodeArchive(new FByteArchive(path, bytes, new VersionContainer(eGame)));
		}
		catch (Exception ex) when (!(ex is OutOfMemoryException))
		{
			throw new InvalidDataException($"{path}: not a shader library this reads as UE {VersionOf(eGame)} ({ex.Message})", ex);
		}
	}

	private static (long At, int Length)? FormatName(byte[] b, long length, EGame pick)
	{
		(long, long)? tuple = Layout(b, (pick >= EGame.GAME_UE5_8) ? 8 : 20, ioStore: false);
		if (tuple.HasValue)
		{
			(long, long) valueOrDefault = tuple.GetValueOrDefault();
			long num = length - valueOrDefault.Item2;
			switch (num)
			{
			case 6L:
			case 7L:
			case 8L:
			case 9L:
			case 10L:
			case 11L:
			case 12L:
			case 13L:
			case 14L:
			case 15L:
			case 16L:
			case 17L:
			case 18L:
			case 19L:
			case 20L:
			case 21L:
			case 22L:
			case 23L:
			case 24L:
			case 25L:
			case 26L:
			case 27L:
			case 28L:
			case 29L:
			case 30L:
			case 31L:
			case 32L:
			case 33L:
			case 34L:
			case 35L:
			case 36L:
			case 37L:
			case 38L:
			case 39L:
			case 40L:
			case 41L:
			case 42L:
			case 43L:
			case 44L:
			case 45L:
			case 46L:
			case 47L:
			case 48L:
			case 49L:
			case 50L:
			case 51L:
			case 52L:
			case 53L:
			case 54L:
			case 55L:
			case 56L:
			case 57L:
			case 58L:
			case 59L:
			case 60L:
			case 61L:
			case 62L:
			case 63L:
			case 64L:
			case 65L:
			case 66L:
			case 67L:
			case 68L:
				if (valueOrDefault.Item1 + num <= b.Length && BitConverter.ToInt32(b, (int)valueOrDefault.Item1) == num - 4 && b[valueOrDefault.Item1 + num - 1] == 0)
				{
					return (valueOrDefault.Item1, (int)num);
				}
				break;
			}
		}
		return null;
	}

	private static EGame PickLayout(string path, byte[] bytes, long length, EGame game)
	{
		uint version = ((bytes.Length >= 4) ? BitConverter.ToUInt32(bytes) : 0u);
		bool flag = game >= EGame.GAME_UE5_0;
		EGame[] array = ((!flag) ? new EGame[1] { game } : new EGame[2]
		{
			game,
			(game >= EGame.GAME_UE5_8) ? EGame.GAME_UE5_7 : EGame.GAME_UE5_8
		});
		uint num = version;
		bool flag2 = num - 1 <= 1;
		bool flag3 = !flag2 || (version == 1 && !flag);
		if (!flag3)
		{
			bool flag4 = ((game == EGame.GAME_ArenaBreakoutMobile || game == EGame.GAME_MarvelRivals) ? true : false);
			flag3 = flag4;
		}
		if (!flag3)
		{
			return array.Where((EGame g) => End(g) == length).Concat(array.Where((EGame g) => End(g).HasValue)).Cast<EGame?>()
				.FirstOrDefault() ?? throw new InvalidDataException($"{path}: not a shader library this reads as UE {VersionOf(game)}{(flag ? (" or " + VersionOf(array[1])) : "")} (its counts run past the file)");
		}
		return game;
		long? End(EGame g)
		{
			return LibraryEnd(bytes, (g >= EGame.GAME_UE5_8) ? 8 : 20, version == 1);
		}
	}

	internal static long? LibraryEnd(byte[] b, int width, bool ioStore)
	{
		return Layout(b, width, ioStore)?.End;
	}

	private static (long Header, long End)? Layout(byte[] b, int width, bool ioStore)
	{
		int[] array;
		if (!ioStore)
		{
			array = new int[6] { 0, 0, 16, 17, 16, 4 };
			array[0] = width;
			array[1] = width;
		}
		else
		{
			array = new int[7] { 0, 0, 12, 8, 8, 16, 4 };
			array[0] = width;
			array[1] = width;
		}
		int[] array2 = array;
		long num = 4L;
		long num2 = 0L;
		for (int i = 0; i < array2.Length; i++)
		{
			if (num + 4 > b.Length)
			{
				return null;
			}
			int num3 = BitConverter.ToInt32(b, (int)num);
			long num4 = num + 4;
			if (num3 < 0 || (long)num3 * (long)array2[i] > b.Length - num4)
			{
				return null;
			}
			num = num4 + (long)num3 * (long)array2[i];
			if (!ioStore && i == 3)
			{
				for (long num5 = num4; num5 < num; num5 += 17)
				{
					num2 += BitConverter.ToUInt32(b, (int)num5 + 8);
				}
			}
		}
		return (num, num + num2);
	}

	internal static Archive? Open(AbstractVfsFileProvider provider, GameFile file)
	{
		if (file is FPakEntry entry && file.Size > int.MaxValue)
		{
			return OpenLarge(entry, provider.Versions.Game);
		}
		FShaderCodeArchive arc = ReadLibrary(file.Path, file.Read(), provider.Versions.Game);
		FRHIShaderLibrary serializedShaders = arc.SerializedShaders;
		if (serializedShaders != null)
		{
			FSerializedShaderArchive fSerializedShaderArchive = serializedShaders as FSerializedShaderArchive;
			if (fSerializedShaderArchive == null)
			{
				FIoStoreShaderCodeArchive fIoStoreShaderCodeArchive = serializedShaders as FIoStoreShaderCodeArchive;
				if (fIoStoreShaderCodeArchive != null)
				{
					List<IoStoreReader> readers = provider.MountedVfs.OfType<IoStoreReader>().ToList();
					return new Archive(fIoStoreShaderCodeArchive.ShaderMapHashes.Select((FSHAHash h) => h.ToString().ToLowerInvariant()).ToArray(), fIoStoreShaderCodeArchive.ShaderMapEntries.Select((FIoStoreShaderMapEntry e) => ((int)e.ShaderIndicesOffset, (int)e.NumShaders)).ToArray(), fIoStoreShaderCodeArchive.ShaderIndices, fIoStoreShaderCodeArchive.ShaderEntries.Length, ((IEnumerable<FIoStoreShaderGroupEntry>)fIoStoreShaderCodeArchive.ShaderGroupEntries).Select((Func<FIoStoreShaderGroupEntry, int, Func<IEnumerable<(int, byte[])>>>)((FIoStoreShaderGroupEntry g, int gi) => () =>
					{
						FIoChunkId id = fIoStoreShaderCodeArchive.ShaderGroupIoHashes[gi];
						byte[] array = readers.First((IoStoreReader r) => r.DoesChunkExist(id)).Read(id);
						byte[] group = ((g.CompressedSize == g.UncompressedSize) ? array : Decompress(array[..(int)g.CompressedSize], (int)g.UncompressedSize));
						List<(int s, int o)> members = (from s in fIoStoreShaderCodeArchive.ShaderIndices.Skip((int)g.ShaderIndicesOffset).Take((int)g.NumShaders)
							select (s: (int)s, o: (int)fIoStoreShaderCodeArchive.ShaderEntries[s].UncompressedOffsetInGroup) into x
							orderby x.o
							select x).ToList();
						return members.Select(((int s, int o) m, int k) => (s: m.s, group[m.o..((k + 1 < members.Count) ? members[k + 1].o : ((int)g.UncompressedSize))]));
					})).ToArray(), null, fIoStoreShaderCodeArchive.ShaderHashes.Select((FSHAHash h) => h.ToString().ToLowerInvariant()).ToArray());
				}
				return null;
			}
			return new Archive(fSerializedShaderArchive.ShaderMapHashes.Select((FSHAHash h) => h.ToString().ToLowerInvariant()).ToArray(), fSerializedShaderArchive.ShaderMapEntries.Select((FShaderMapEntry e) => ((int)e.ShaderIndicesOffset, (int)e.NumShaders)).ToArray(), fSerializedShaderArchive.ShaderIndices, fSerializedShaderArchive.ShaderEntries.Length, Enumerable.Range(0, fSerializedShaderArchive.ShaderEntries.Length).Select((Func<int, Func<IEnumerable<(int, byte[])>>>)((int i) => () =>
			{
				FShaderCodeEntry fShaderCodeEntry = fSerializedShaderArchive.ShaderEntries[i];
				byte[] item = ((fShaderCodeEntry.Size == fShaderCodeEntry.UncompressedSize) ? arc.ShaderCode[i] : Decompress(arc.ShaderCode[i], (int)fShaderCodeEntry.UncompressedSize));
				arc.ShaderCode[i] = null;
				return new _003C_003Ez__ReadOnlySingleElementList<(int, byte[])>((i, item));
			})).ToArray(), null, fSerializedShaderArchive.ShaderHashes.Select((FSHAHash h) => h.ToString().ToLowerInvariant()).ToArray());
		}
		return OpenV1(file.Read());
	}

	private static Archive OpenLarge(FPakEntry entry, EGame game)
	{
		long size = entry.Size;
		long start;
		SafeFileHandle file;
		if (entry.Vfs is PakFileReader pakFileReader)
		{
			string path = pakFileReader.Path;
			start = entry.Offset + entry.StructSize;
			file = ((entry.IsEncrypted || entry.IsCompressed) ? null : File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.None, 0L));
			try
			{
				long num = Math.Min(size, 536870912L);
				long num2 = Math.Min(num, 1048576L);
				byte[] array;
				while (true)
				{
					array = Read(0L, num2);
					(long, long)? tuple = Layout(array, (game >= EGame.GAME_UE5_8) ? 8 : 20, ioStore: false);
					if ((tuple.HasValue && tuple.GetValueOrDefault().Item1 + 68 <= num2) || num2 == num)
					{
						break;
					}
					num2 = Math.Min(num, num2 * 4);
				}
				uint num3 = BitConverter.ToUInt32(array);
				if (num3 != 2)
				{
					throw new InvalidDataException($"{entry.Path}: a {Format.Bytes(size)} shader library of version {num3}, not read");
				}
				EGame eGame = PickLayout(entry.Path, array, size, game);
				FByteArchive fByteArchive = new FByteArchive(entry.Path, array, new VersionContainer(eGame))
				{
					Position = 4L
				};
				FSerializedShaderArchive fSerializedShaderArchive;
				try
				{
					fSerializedShaderArchive = new FSerializedShaderArchive(fByteArchive);
				}
				catch (Exception ex) when (!(ex is OutOfMemoryException))
				{
					throw new InvalidDataException(entry.Path + ": not a shader library (" + ex.Message + ")", ex);
				}
				long code = ((Stream)(object)fByteArchive).Position + (FormatName(array, size, eGame)?.Length ?? 0);
				ulong end = (ulong)(size - code);
				FShaderCodeEntry[] entries = fSerializedShaderArchive.ShaderEntries;
				List<int[]> source = ((file != null) ? (from i in Enumerable.Range(0, entries.Length)
					select new int[1] { i }).ToList() : Ranges());
				return new Archive(fSerializedShaderArchive.ShaderMapHashes.Select((FSHAHash h) => h.ToString().ToLowerInvariant()).ToArray(), fSerializedShaderArchive.ShaderMapEntries.Select((FShaderMapEntry e) => ((int)e.ShaderIndicesOffset, (int)e.NumShaders)).ToArray(), fSerializedShaderArchive.ShaderIndices, entries.Length, ((IEnumerable<int[]>)source).Select((Func<int[], Func<IEnumerable<(int, byte[])>>>)((int[] g) => () =>
				{
					if (!Fits(g[0]))
					{
						throw new InvalidDataException($"{entry.Path}: shader {g[0]}'s code runs past the library");
					}
					long from = (long)entries[g[0]].Offset;
					byte[] raw = Read(code + from, g.Max((int i) => (long)(entries[i].Offset + entries[i].Size)) - from);
					return g.Select((int i) =>
					{
						FShaderCodeEntry fShaderCodeEntry = entries[i];
						byte[] array2 = ((g.Length == 1) ? raw : raw.AsSpan((int)((long)fShaderCodeEntry.Offset - from), (int)fShaderCodeEntry.Size).ToArray());
						return (i: i, (fShaderCodeEntry.Size == fShaderCodeEntry.UncompressedSize) ? array2 : Decompress(array2, (int)fShaderCodeEntry.UncompressedSize));
					}).ToList();
				})).ToArray(), null, fSerializedShaderArchive.ShaderHashes.Select((FSHAHash h) => h.ToString().ToLowerInvariant()).ToArray(), file);
				bool Fits(int i)
				{
					if (entries[i].Size <= int.MaxValue && entries[i].Size <= end)
					{
						return entries[i].Offset <= end - entries[i].Size;
					}
					return false;
				}
				List<int[]> Ranges()
				{
					IEnumerable<int> source2 = Enumerable.Range(0, entries.Length);
					List<int[]> list = (from i in source2
						where !Fits(i)
						select new int[1] { i }).ToList();
					int[] array2 = (from i in source2.Where(Fits)
						orderby entries[i].Offset
						select i).ToArray();
					int num4 = 0;
					while (num4 < array2.Length)
					{
						int num5;
						for (num5 = num4 + 1; num5 < array2.Length && (long)(entries[array2[num5]].Offset + entries[array2[num5]].Size - entries[array2[num4]].Offset) <= 4194304L; num5++)
						{
						}
						list.Add(array2[num4..num5]);
						num4 = num5;
					}
					return list;
				}
			}
			catch
			{
				file?.Dispose();
				throw;
			}
		}
		throw new InvalidDataException(entry.Path + ": a " + Format.Bytes(size) + " shader library outside a pak, not read");
		byte[] Read(long at, long n)
		{
			if (file != null)
			{
				byte[] array2 = new byte[n];
				ReadAt(file, array2, start + at);
				return array2;
			}
			byte[] array3;
			try
			{
				array3 = entry.Read(new FByteBulkDataHeader(EBulkDataFlags.BULKDATA_None, 0, checked((uint)n), at, default));
			}
			catch (Exception ex2) when ((!(ex2 is OutOfMemoryException) && !(ex2 is InvalidDataException)) || 1 == 0)
			{
				throw new InvalidDataException(entry.Path + ": " + ex2.Message, ex2);
			}
			if (array3.Length != n)
			{
				throw new InvalidDataException($"{entry.Path}: {array3.Length} bytes read at {at}, not {n}");
			}
			return array3;
		}
	}

	private static void ReadAt(SafeFileHandle file, byte[] into, long offset)
	{
		int num;
		for (int i = 0; i < into.Length; i += num)
		{
			if ((num = RandomAccess.Read(file, into.AsSpan(i), offset + i)) == 0)
			{
				throw new InvalidDataException("the pak ends early");
			}
		}
	}

	internal static Archive? OpenV1(byte[] b)
	{
		if (b.Length >= 8 && BitConverter.ToUInt32(b, 0) == 1)
		{
			int num = BitConverter.ToInt32(b, 4);
			if (num > 0 && 8 + 37L * (long)num <= b.Length)
			{
				long num2 = 8 + 37L * (long)num;
				string[] array = new string[num];
				Func<IEnumerable<(int, byte[])>>[] array2 = new Func<IEnumerable<(int, byte[])>>[num];
				for (int i = 0; i < num; i++)
				{
					int num3 = 8 + 37 * i;
					long num4 = BitConverter.ToInt64(b, num3 + 20);
					int num5 = BitConverter.ToInt32(b, num3 + 28);
					int num6 = BitConverter.ToInt32(b, num3 + 32);
					int usize = num6;
					int size = num5;
					long num7 = num4;
					if (num7 < 0 || size <= 0 || usize < size || num2 + num7 + size > b.Length)
					{
						return null;
					}
					array[i] = Convert.ToHexStringLower(b.AsSpan(num3, 20));
					num6 = (int)(num2 + num7);
					num5 = i;
					int k = num5;
					int start = num6;
					array2[i] = () => new _003C_003Ez__ReadOnlySingleElementList<(int, byte[])>((k, (size == usize) ? b[start..(start + size)] : Decompress(b[start..(start + size)], usize)));
				}
				return new Archive(Array.Empty<string>(), Array.Empty<(int, int)>(), Array.Empty<uint>(), num, array2, array);
			}
		}
		return null;
	}

	private static List<ShaderMap> ReferencedMaps(DefaultFileProvider provider, Dictionary<string, (string Sha, string Platform)> byKey, CancellationToken ct, out int scanned)
	{
		provider.MappingsContainer = new NoMappings();
		Dictionary<ulong, List<(byte[] Key, string Sha, string Platform)>> prefix = new Dictionary<ulong, List<(byte[], string, string)>>();
		foreach (var (s, tuple2) in byKey)
		{
			byte[] array = Convert.FromHexString(s);
			ulong key = BitConverter.ToUInt64(array);
			if (!prefix.TryGetValue(key, out List<(byte[], string, string)> value))
			{
				value = (prefix[key] = new List<(byte[], string, string)>());
			}
			value.Add((array, tuple2.Item1, tuple2.Item2));
		}
		ConcurrentBag<ShaderMap> maps = new ConcurrentBag<ShaderMap>();
		int n = 0;
		ByteBudget budget = ByteBudget.FromFreeMemory();
		Parallel.ForEach(provider.Files.Values.Where((GameFile f) => f.IsUePackage || IsGlobalCache(f)).ToList(), new ParallelOptions
		{
			TaskScheduler = TaskScheduler.Current,
			CancellationToken = ct,
			MaxDegreeOfParallelism = Environment.ProcessorCount
		}, (GameFile f) =>
		{
			bool flag = IsGlobalCache(f);
			if (!flag && OwnsShaderMaps(provider, f) == false)
			{
				return;
			}
			using (budget.Take(PackageSize(provider, f)))
			{
				byte[][] array2;
				try
				{
					array2 = PackageParts(provider, f, out string _);
				}
				catch (Exception)
				{
					return;
				}
				Interlocked.Increment(ref n);
				List<string> list2 = new List<string>();
				string platform = "";
				byte[][] array3 = array2;
				foreach (byte[] array4 in array3)
				{
					for (int j = 0; j + 20 <= array4.Length; j++)
					{
						if (prefix.TryGetValue(BitConverter.ToUInt64(array4, j), out List<(byte[], string, string)> value2))
						{
							foreach (var item in value2)
							{
								if (((ReadOnlySpan<byte>)array4.AsSpan(j, 20)).SequenceEqual((ReadOnlySpan<byte>)item.Item1))
								{
									list2.Add(item.Item2);
									platform = item.Item3;
									j += 19;
									break;
								}
							}
						}
					}
				}
				if (list2.Count > 0)
				{
					maps.Add(new ShaderMap(f.Path, flag ? "Global" : f.Path, platform, list2.Distinct().ToList()));
				}
			}
		});
		scanned = n;
		return Canonical(maps);
	}

	public static List<ShaderMap> Canonical(IEnumerable<ShaderMap> maps)
	{
		return maps.OrderBy((ShaderMap m) => m.Hash, StringComparer.Ordinal).ThenBy((ShaderMap m) => m.Platform, StringComparer.Ordinal).ThenBy((ShaderMap m) => string.Join(',', m.Shaders), StringComparer.Ordinal)
			.ToList();
	}

	private static (string, ReadOnlyMemory<byte>) Hash(byte[] code)
	{
		int num = ShaderContainer.Offset(code, out var len);
		Memory<byte> memory = ((num < 0) ? code.AsMemory() : code.AsMemory(num, len));
		return (Convert.ToHexStringLower(SHA1.HashData(memory.Span)), memory);
	}

	internal static byte[] Decompress(byte[] src, int size)
	{
		CompressionMethod[] array = new CompressionMethod[4]
		{
			CompressionMethod.Oodle,
			CompressionMethod.LZ4,
			CompressionMethod.Zlib,
			CompressionMethod.Zstd
		};
		foreach (CompressionMethod method in array)
		{
			try
			{
				return Compression.Decompress(src, size, method);
			}
			catch
			{
			}
		}
		InvalidDataException ex = new InvalidDataException($"cannot decompress {src.Length} -> {size} bytes with Oodle/LZ4/Zlib/Zstd (starts {Convert.ToHexString(src.AsSpan(0, Math.Min(8, src.Length)))})");
		string text;
		if (OodleBlock(src))
		{
			if (Codecs.NativeOodle)
			{
				text = (Codecs.OodleMissing ? "Couldn't download the Oodle codec. Check your antivirus or firewall." : null);
			}
			else
			{
				text = "This CPU lacks AVX2 and BMI2, which the Oodle codec needs.";
			}
		}
		else
		{
			text = null;
		}
		throw (text == null) ? ex : new InvalidDataException(text, ex);
	}

	private static bool OodleBlock(byte[] b)
	{
		if (b.Length >= 2 && (b[0] & 0x3F) == 12)
		{
			return (b[1] & 0x7F) < 13;
		}
		return false;
	}

	private static void InitCodecs()
	{
		Codecs.Load();
	}

	private static DefaultFileProvider Mount(string paks, EGame game, FAesKey? key)
	{
		InitCodecs();
		DefaultFileProvider defaultFileProvider = new DefaultFileProvider(paks, SearchOption.TopDirectoryOnly, new VersionContainer(game), StringComparer.OrdinalIgnoreCase);
		try
		{
			defaultFileProvider.Initialize();
			defaultFileProvider.Mount();
			if (key != null)
			{
				defaultFileProvider.SubmitKeys((from g in (from r in defaultFileProvider.UnloadedVfs
						where r.IsEncrypted && Opens(r, key)
						select r.EncryptionKeyGuid).Distinct()
					select KeyValuePair.Create(g, key)).ToList());
				foreach (IAesVfsReader mountedVf in defaultFileProvider.MountedVfs)
				{
					if (mountedVf.EncryptedFileCount > 0)
					{
						IAesVfsReader aesVfsReader = mountedVf;
						if (aesVfsReader.AesKey == null)
						{
							FAesKey fAesKey = (aesVfsReader.AesKey = key);
						}
					}
				}
			}
			try
			{
				defaultFileProvider.PostMount();
			}
			catch (Exception ex) when (!(ex is OutOfMemoryException))
			{
			}
			return defaultFileProvider;
		}
		catch
		{
			defaultFileProvider.Dispose();
			throw;
		}
	}

	private static string? PaksDir(string installDir)
	{
		if (!Directory.Exists(installDir))
		{
			return null;
		}
		return (from p in Directory.EnumerateDirectories(installDir, "Paks", new EnumerationOptions
			{
				RecurseSubdirectories = true,
				IgnoreInaccessible = true,
				AttributesToSkip = FileAttributes.None
			})
			where Path.GetFileName(Path.GetDirectoryName(p)) == "Content"
			select (p: p, size: (from f in Directory.EnumerateFiles(p)
				where f.EndsWith(".pak") || f.EndsWith(".ucas")
				select f).Sum((string f) => new FileInfo(f).Length)) into x
			where x.size > 0
			orderby x.size descending
			select x.p).FirstOrDefault();
	}

	private static string VersionOf(EGame g)
	{
		Match match = Regex.Match(g.ToString(), "^GAME_UE(\\d)_(\\d+)$");
		if (match == null || !match.Success)
		{
			return g.ToString();
		}
		return $"{match.Groups[1]}.{match.Groups[2]}";
	}

	private static EGame GameOf(EngineInfo e)
	{
		if (e.Fork != null || (Version.TryParse(e.Version, out Version result) && result <= NewestVersion))
		{
			return Enum.Parse<EGame>(e.Fork ?? ("GAME_UE" + e.Version.Replace('.', '_')));
		}
		throw new InvalidDataException("Unreal " + e.Version + " isn't supported yet");
	}

	private static EGame DetectEngine(Game game, string paks, out EGame upTo, out bool fromContainers, out bool guessed, out string? newer)
	{
		string exePath = game.ExePath;
		upTo = (EGame)0u;
		fromContainers = false;
		guessed = false;
		newer = null;
		int num = Directory.EnumerateFiles(paks, "*.utoc").Select(TocVersion).DefaultIfEmpty(0)
			.Max();
		if (num > 10)
		{
			string pastNewest = PastNewest;
			fromContainers = true;
			newer = pastNewest;
			return EGame.GAME_UE5_8;
		}
		if (File.Exists(exePath) && GameFiles.DetectAntiCheat(game) == AntiCheat.None)
		{
			(int, int)? tuple = BuildString(exePath);
			if (tuple.HasValue)
			{
				var (num2, minor) = tuple.GetValueOrDefault();
				if (num2 != 4 || (num < 4 && !PackagesAreUe5(paks)))
				{
					return Known(num2, minor, out newer);
				}
			}
		}
		fromContainers = true;
		EGame eGame;
		switch (num)
		{
		case 4:
		case 5:
			eGame = EGame.GAME_UE5_3;
			break;
		case 7:
		case 8:
			eGame = EGame.GAME_UE5_8;
			break;
		default:
			eGame = (EGame)0u;
			break;
		}
		upTo = eGame;
		if (num > 0)
		{
			if (num <= 5)
			{
				if (num > 2)
				{
					if (num == 3)
					{
						return EGame.GAME_UE4_27;
					}
					return EGame.GAME_UE5_1;
				}
				return EGame.GAME_UE4_26;
			}
			if (num <= 8)
			{
				if (num == 6)
				{
					return EGame.GAME_UE5_4;
				}
				return EGame.GAME_UE5_5;
			}
			return EGame.GAME_UE5_8;
		}
		switch (Directory.EnumerateFiles(paks, "*.pak").Select(PakVersion).DefaultIfEmpty(0)
			.Max())
		{
		case 1:
		case 2:
		case 3:
		case 4:
		case 5:
		case 6:
		case 7:
			return EGame.GAME_UE4_21;
		case 8:
			return EGame.GAME_UE4_24;
		case 9:
			return EGame.GAME_UE4_25;
		case 10:
			return EGame.GAME_UE4_26;
		default:
		{
			guessed = true;
			if (PackagesAreUe5(paks))
			{
				upTo = EGame.GAME_UE5_8;
				return EGame.GAME_UE5_1;
			}
			(int, int)? tuple = PeVersion(exePath);
			if (tuple.HasValue)
			{
				(int, int) valueOrDefault = tuple.GetValueOrDefault();
				if (valueOrDefault.Item1 == 5)
				{
					int item = valueOrDefault.Item2;
					return Known(5, item, out newer);
				}
			}
			return EGame.GAME_UE4_27;
		}
		}
	}

	private static (int Major, int Minor)? PeVersion(string exePath)
	{
		try
		{
			(int, int)? result;
			if (File.Exists(exePath))
			{
				FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(exePath);
				if (versionInfo != null && versionInfo.FileMajorPart > 0)
				{
					result = (versionInfo.FileMajorPart, versionInfo.FileMinorPart);
					goto IL_003c;
				}
			}
			result = null;
			goto IL_003c;
			IL_003c:
			return result;
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return null;
		}
	}

	private static EGame Known(int major, int minor, out string? newer)
	{
		newer = ((new Version(major, minor) > NewestVersion) ? $"{major}.{minor}" : null);
		if (newer == null)
		{
			return Clamp(major, minor);
		}
		return EGame.GAME_UE5_8;
	}

	private static EGame Refine(EGame baseGame, EGame upTo, int? registry, string? libraryToc, string paks, FAesKey? key, string exePath, ref bool guessed)
	{
		EGame eGame = ((baseGame == EGame.GAME_UE5_1) ? EGame.GAME_UE5_0 : baseGame);
		int? num = ((upTo >= EGame.GAME_UE5_8 && libraryToc != null) ? LibraryHashWidth(libraryToc, key) : ((int?)null));
		if (num == 8)
		{
			eGame = EGame.GAME_UE5_8;
		}
		if (num == 20 && upTo >= EGame.GAME_UE5_8)
		{
			upTo = EGame.GAME_UE5_7;
		}
		EGame? eGame2 = RegistryEngine(registry);
		EGame? eGame3;
		if (eGame2.HasValue)
		{
			EGame valueOrDefault = eGame2.GetValueOrDefault();
			if (valueOrDefault >= eGame && valueOrDefault <= upTo)
			{
				eGame3 = valueOrDefault;
				goto IL_00a4;
			}
		}
		eGame3 = ((num == 8) ? new EGame?(EGame.GAME_UE5_8) : ((EGame?)null));
		goto IL_00a4;
		IL_00a4:
		EGame? eGame4 = eGame3;
		if (eGame4.HasValue)
		{
			EGame valueOrDefault2 = eGame4.GetValueOrDefault();
			guessed = false;
			return valueOrDefault2;
		}
		if (eGame == EGame.GAME_UE5_5 && HasVerseCells(paks, key))
		{
			eGame = (baseGame = EGame.GAME_UE5_6);
		}
		(int, int)? tuple = PeVersion(exePath);
		if (tuple.HasValue)
		{
			(int, int) valueOrDefault3 = tuple.GetValueOrDefault();
			if (valueOrDefault3.Item1 == 5)
			{
				int item = valueOrDefault3.Item2;
				Version version = new Version(5, item);
				if (version >= Version.Parse(VersionOf(eGame)) && version <= Version.Parse(VersionOf(upTo)))
				{
					return Clamp(5, item);
				}
			}
		}
		return baseGame;
	}

	private static EGame? RegistryEngine(int? registry)
	{
		switch (registry)
		{
		case 18:
		case 19:
		case 20:
		case 21:
			return EGame.GAME_UE5_6;
		case 22:
			return EGame.GAME_UE5_7;
		case 23:
		case 24:
			return EGame.GAME_UE5_8;
		default:
			return null;
		}
	}

	internal static bool PackagesAreUe5(string paks)
	{
		InitCodecs();
		foreach (string item in from p in Directory.EnumerateFiles(paks, "*.pak")
			orderby new FileInfo(p).Length
			select p)
		{
			try
			{
				using AbstractAesVfsReader abstractAesVfsReader = OpenContainer(item, new VersionContainer(EGame.GAME_UE4_27));
				if (abstractAesVfsReader.IsEncrypted)
				{
					continue;
				}
				abstractAesVfsReader.Mount(StringComparer.OrdinalIgnoreCase);
				GameFile gameFile = abstractAesVfsReader.Files.Values.FirstOrDefault((GameFile f) =>
				{
					string extension = f.Extension;
					bool flag = ((extension == "uasset" || extension == "umap") ? true : false);
					if (flag && !f.IsEncrypted)
					{
						long size = f.Size;
						if (size >= 8)
						{
							return size < 1048576;
						}
						return false;
					}
					return false;
				});
				if (gameFile == null)
				{
					continue;
				}
				byte[] array = gameFile.Read();
				return BitConverter.ToUInt32(array) == 2653586369u && BitConverter.ToInt32(array, 4) <= -8;
			}
			catch (Exception)
			{
			}
		}
		return false;
	}

	internal static (int Major, int Minor)? BuildString(string exePath)
	{
		try
		{
			using FileStream fileStream = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
			byte[] array = new byte[4194304];
			byte[] bytes = Encoding.Unicode.GetBytes("++UE");
			int num = 0;
			int num2;
			while ((num2 = fileStream.Read(array, num, array.Length - num)) > 0)
			{
				int num3 = num + num2;
				int num4 = ((ReadOnlySpan<byte>)array.AsSpan(0, num3)).IndexOf((ReadOnlySpan<byte>)bytes);
				while (num4 >= 0)
				{
					Match match = Regex.Match(Encoding.Unicode.GetString(array, num4, Math.Min(120, num3 - num4) & -2), "^\\+\\+UE[4-9]\\+[^\\x00]{0,40}?([4-9])\\.([0-9]{1,2})(?![0-9])");
					if (match != null && match.Success)
					{
						return (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
					}
					int num5 = ((ReadOnlySpan<byte>)array.AsSpan(num4 + 1, num3 - num4 - 1)).IndexOf((ReadOnlySpan<byte>)bytes);
					num4 = ((num5 < 0) ? (-1) : (num4 + 1 + num5));
				}
				num = Math.Min(num3, 120);
				Buffer.BlockCopy(array, num3 - num, array, 0, num);
			}
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
		}
		return null;
	}

	private static EGame Clamp(int major, int minor)
	{
		while (minor >= 0)
		{
			if (Enum.TryParse<EGame>($"GAME_UE{major}_{minor}", out var result))
			{
				return result;
			}
			minor--;
		}
		if (major != 5)
		{
			return EGame.GAME_UE4_27;
		}
		return EGame.GAME_UE5_0;
	}

	private static int PakVersion(string pak)
	{
		try
		{
			using PakFileReader pakFileReader = new PakFileReader(pak);
			return (int)pakFileReader.Info.Version;
		}
		catch (Exception)
		{
			return 0;
		}
	}

	private static bool HasVerseCells(string paks, FAesKey? key)
	{
		try
		{
			string text = (from p in Directory.EnumerateFiles(paks, "*.utoc")
				where (TocHeader(p)[80] & 8) != 0
				select p).MinBy((string p) => new FileInfo(p).Length);
			if (text == null || !Sane(text))
			{
				return false;
			}
			using IoStoreReader ioStoreReader = new IoStoreReader(text, EIoStoreTocReadOptions.ReadDirectoryIndex, new VersionContainer(EGame.GAME_UE5_5));
			if (ioStoreReader.IsEncrypted)
			{
				ioStoreReader.AesKey = key ?? throw new InvalidDataException("encrypted");
			}
			ioStoreReader.Mount(StringComparer.OrdinalIgnoreCase);
			return BitConverter.ToUInt64(ioStoreReader.Files.Values.First((GameFile f) => f.Extension == "uasset").Read(new FByteBulkDataHeader(EBulkDataFlags.BULKDATA_None, 0, 76u, 0L, default)), 68) == 3244556288u;
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static void TocShaderFiles(string utoc, List<string> libraries, List<string> globals)
	{
		using FileStream fileStream = new FileStream(utoc, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
		byte[] array = new byte[4194304];
		int num = 0;
		int num2;
		while ((num2 = fileStream.Read(array, num, array.Length - num)) > 0)
		{
			int num3 = num + num2;
			foreach (Match item in ShaderFileName().Matches(Encoding.Latin1.GetString(array, 0, num3)))
			{
				if (item.Groups[1].Success)
				{
					libraries.Add(item.Groups[1].Value.Split('-')[^1]);
				}
				else
				{
					globals.Add(item.Groups[2].Value);
				}
			}
			num = Math.Min(num3, 512);
			Buffer.BlockCopy(array, num3 - num, array, 0, num);
		}
	}

	[GeneratedCode("System.Text.RegularExpressions.Generator", "10.0.14.42308")]
	private static Regex ShaderFileName()
	{
		return _003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__ShaderFileName_5.Instance;
	}

	private static bool IsGlobalCache(GameFile f)
	{
		if (f.Extension == "bin")
		{
			return f.Name.StartsWith("GlobalShaderCache-", StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}

	private static int TocVersion(string utoc)
	{
		return TocHeader(utoc)[16];
	}

	private static bool Sane(string utoc)
	{
		byte[] h = TocHeader(utoc);
		long length = new FileInfo(utoc).Length;
		if (U(24) * 22 <= length && U(28) * Math.Max(U(32), 1L) <= length && U(32) <= 64 && U(36) <= 16 && U(40) <= 256 && U(48) <= length && U(84) * 4 <= length)
		{
			return U(96) * 4 <= length;
		}
		return false;
		long U(int o)
		{
			return BitConverter.ToUInt32(h, o);
		}
	}

	private static byte[] TocHeader(string utoc)
	{
		using FileStream fileStream = File.OpenRead(utoc);
		byte[] array = new byte[100];
		fileStream.ReadAtLeast(array, array.Length, throwOnEndOfStream: false);
		return array;
	}

	internal static EGame? DetectFork(EGame baseGame, EGame upTo, string folder, string exeName, bool fromContainers = false, string title = "")
	{
		uint lo = (uint)(baseGame & (EGame)0xFFFF0000u);
		uint hi = Math.Max(lo, (uint)(upTo & (EGame)0xFFFF0000u));
		List<string> names = new string[2]
		{
			Norm(folder),
			Norm(Regex.Replace(exeName, "-Win(64|GDK)-Shipping$", "", RegexOptions.IgnoreCase))
		}.Where((string n) => n.Length >= 4).ToList();
		EGame? result = ((IEnumerable<(EGame, string)>)(from g in Enum.GetValues<EGame>().Where((EGame g) =>
			{
				if ((g & (EGame)0xFFFFu) != 0)
				{
					uint num = (uint)(g & (EGame)0xFFFF0000u);
					if (num >= lo)
					{
						return num <= hi;
					}
				}
				return false;
			})
			select (g: g, n: Norm(g.ToString().Substring(5))) into x
			where x.n.Length >= 4 && names.Any((string n) => n.StartsWith(x.n) || x.n.StartsWith(n))
			where x.g != EGame.GAME_WutheringWaves || names.Any(((IEnumerable<string>)KuroNames).Contains<string>)
			orderby names.Contains(x.n) descending, (x.g & (EGame)0xFFFF0000u) == (EGame)lo descending, x.n.Length descending
			select x)).Select((Func<(EGame, string), EGame?>)(((EGame g, string n) x) => x.g)).FirstOrDefault();
		if (!result.HasValue)
		{
			if (!fromContainers)
			{
				return null;
			}
			return OlderBase.Where(((EGame Fork, EGame Containers, string[] Names) o) => lo == (uint)o.Containers && names.Append(Norm(title)).Any(((IEnumerable<string>)o.Names).Contains<string>)).Select((Func<(EGame, EGame, string[]), EGame?>)(((EGame Fork, EGame Containers, string[] Names) o) => o.Fork)).FirstOrDefault();
		}
		return result;
		static string Norm(string s)
		{
			return Regex.Replace(Regex.Replace(s, "\\b(XX|XIX|XVIII|XVII|XVI|XV|XIV|XIII|XII|XI|X|IX|VIII|VII|VI|V|IV|III|II)\\b", (Match m) => Array.IndexOf(new string[21]
			{
				"", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX",
				"X", "XI", "XII", "XIII", "XIV", "XV", "XVI", "XVII", "XVIII", "XIX",
				"XX"
			}, m.Value.ToUpperInvariant()).ToString(), RegexOptions.IgnoreCase), "[^A-Za-z0-9]", "").ToLowerInvariant();
		}
	}
}
