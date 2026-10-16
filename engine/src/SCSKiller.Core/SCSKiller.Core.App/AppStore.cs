using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using SCSKiller.Core.Games;

namespace SCSKiller.Core.App;

public sealed class AppStore(string dataDir)
{
	private sealed record Discovered(string Build, List<Game> Games, Dictionary<string, ShippingPick>? Shipping = null);

	private sealed record LibraryView(LibrarySort Sort);

	internal sealed class PathGate : IDisposable
	{
		private sealed class Held(FileStream stream)
		{
			public readonly FileStream Stream = stream;

			public int Count = 1;
		}

		private readonly string _file;

		internal static Action<string>? Waits;

		[ThreadStatic]
		private static Dictionary<string, Held>? _held;

		public PathGate(string path, TimeSpan? wait = null, CancellationToken ct = default(CancellationToken))
		{
			_file = Path.GetFullPath(path) + ".lock";
			if (_held == null)
			{
				_held = new Dictionary<string, Held>(StringComparer.OrdinalIgnoreCase);
			}
			if (_held.TryGetValue(_file, out Held value))
			{
				value.Count++;
				return;
			}
			Directory.CreateDirectory(Path.GetDirectoryName(_file));
			TimeSpan timeSpan = wait ?? TimeSpan.FromMinutes(10L);
			bool flag = false;
			Stopwatch stopwatch = Stopwatch.StartNew();
			while (true)
			{
				try
				{
					ct.ThrowIfCancellationRequested();
					_held[_file] = new Held(new FileStream(_file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
					break;
				}
				catch (IOException ex) when ((uint)((ex.HResult & 0xFFFF) - 32) <= 1u && stopwatch.Elapsed < timeSpan)
				{
					if (!flag)
					{
						Waits?.Invoke(_file);
					}
					flag = true;
				}
				ct.WaitHandle.WaitOne(20);
			}
		}

		public void Dispose()
		{
			if (_held.TryGetValue(_file, out Held value) && --value.Count == 0)
			{
				value.Stream.Dispose();
				_held.Remove(_file);
			}
		}
	}

	private static readonly Lazy<string> _defaultDir = new Lazy<string>(() => Resolve(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SCSKiller"), PortableRoot(AppContext.BaseDirectory), out _onOldFolder));

	private static IDisposable? _onOldFolder;

	internal static TimeSpan CopyWait = TimeSpan.FromSeconds(30L);

	internal const string MigratedFile = "migrated";

	public static readonly JsonSerializerOptions Json = new JsonSerializerOptions
	{
		WriteIndented = true,
		Converters = { (JsonConverter)new JsonStringEnumConverter() }
	};

	private const char EncodedPrefix = '%';

	private static readonly char[] BadNameChars = Path.GetInvalidFileNameChars();

	private readonly ConditionalWeakTable<GameRecord, JsonObject> _held = new ConditionalWeakTable<GameRecord, JsonObject>();

	public static string DefaultDir => Moved ?? _defaultDir.Value;

	public static string? Moved
	{
		get
		{
			string environmentVariable = Environment.GetEnvironmentVariable("SCSKILLER_DATA_DIR");
			if (environmentVariable == null || environmentVariable.Length <= 0)
			{
				return null;
			}
			return Path.GetFullPath(environmentVariable);
		}
	}

	public static bool IsPortable => PortableRoot(AppContext.BaseDirectory) != null;

	public string DataDir { get; } = dataDir;

	public static Settings DefaultSettings => new Settings(Environment.ProcessorCount, WarmPriority.BelowNormal, DriverUpdateMode.Ask, 8, PauseWhileGaming: true);

	private string InisFile => Path.Combine(DataDir, "recorder-inis.json");

	internal Action<string>? LoadingGame { get; set; }

	internal static string? PortableRoot(string baseDir)
	{
		return new string[2]
		{
			"..",
			Path.Combine("..", "..")
		}.Select((string up) => Path.GetFullPath(Path.Combine(baseDir, up))).FirstOrDefault((string d) => File.Exists(Path.Combine(d, ".portable")));
	}

	internal static string CopyMutex(string portable)
	{
		return "Global\\SCSKiller-portable-copy-" + PortableId(portable);
	}

	private static string PortableId(string portable)
	{
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(portable)).ToUpperInvariant()))).Substring(0, 16);
	}

	internal static string Resolve(string local, string? portable, out IDisposable? onOldFolder)
	{
		onOldFolder = null;
		if (portable == null)
		{
			return local;
		}
		string text = Path.Combine(portable, "data");
		if (File.Exists(Path.Combine(text, "migrated")))
		{
			return text;
		}
		string text2 = PortableId(portable);
		string name = "Global\\SCSKiller-portable-old-folder-" + text2;
		Mutex mutex;
		try
		{
			mutex = new Mutex(initiallyOwned: false, CopyMutex(portable));
		}
		catch (Exception)
		{
			onOldFolder = Hold(name);
			return local;
		}
		using (mutex)
		{
			bool flag;
			try
			{
				flag = mutex.WaitOne(CopyWait);
			}
			catch (AbandonedMutexException)
			{
				flag = true;
			}
			if (!flag)
			{
				onOldFolder = Hold(name);
				return local;
			}
			string text3 = text + ".partial";
			try
			{
				if (File.Exists(Path.Combine(text, "migrated")))
				{
					return text;
				}
				if (Held(name))
				{
					onOldFolder = Hold(name);
					return local;
				}
				if (Directory.Exists(text3))
				{
					Directory.Delete(text3, recursive: true);
				}
				Directory.CreateDirectory(text3);
				if (Directory.Exists(local))
				{
					foreach (string item in Directory.EnumerateFiles(local, "*", SearchOption.AllDirectories))
					{
						string relativePath = Path.GetRelativePath(local, item);
						bool flag2 = relativePath.StartsWith("armed" + Path.DirectorySeparatorChar) || relativePath.StartsWith("refused" + Path.DirectorySeparatorChar);
						if (!flag2)
						{
							bool flag3;
							switch (relativePath)
							{
							case "recorder.key":
							case "recorder.revoked":
							case "update-staged.json":
							case "applying":
								flag3 = true;
								break;
							default:
								flag3 = false;
								break;
							}
							flag2 = flag3;
						}
						if (!flag2 && !relativePath.EndsWith(".lock"))
						{
							string text4 = Path.Combine(text3, relativePath);
							Directory.CreateDirectory(Path.GetDirectoryName(text4));
							File.Copy(item, text4);
						}
					}
				}
				JsonObject jsonObject = Load<JsonObject>(Path.Combine(text3, "settings.json"));
				if (jsonObject != null)
				{
					string[] array = new string[3] { "StartWithWindows", "RecordAllGames", "InstallUpdatesAutomatically" };
					foreach (string propertyName in array)
					{
						jsonObject[propertyName] = false;
					}
					Save(Path.Combine(text3, "settings.json"), jsonObject);
				}
				File.WriteAllText(Path.Combine(text3, "migrated"), "");
				if (Directory.Exists(text))
				{
					int num = 1;
					while (Directory.Exists($"{text}.stale-{num}") || File.Exists($"{text}.stale-{num}"))
					{
						num++;
					}
					Directory.Move(text, $"{text}.stale-{num}");
				}
				Directory.Move(text3, text);
				return text;
			}
			catch (Exception)
			{
				try
				{
					Directory.Delete(text3, recursive: true);
				}
				catch (Exception)
				{
				}
				onOldFolder = Hold(name);
				return local;
			}
			finally
			{
				mutex.ReleaseMutex();
			}
		}
		static bool Held(string name2)
		{
			try
			{
				if (!Mutex.TryOpenExisting(name2, out Mutex result))
				{
					return false;
				}
				result.Dispose();
				return true;
			}
			catch (UnauthorizedAccessException)
			{
				return true;
			}
		}
		static IDisposable? Hold(string name2)
		{
			try
			{
				return new Mutex(initiallyOwned: false, name2);
			}
			catch (Exception)
			{
				return null;
			}
		}
	}

	public Settings LoadSettings()
	{
		return Load<Settings>(Path.Combine(DataDir, "settings.json")) ?? DefaultSettings;
	}

	public void SaveSettings(Settings s)
	{
		Save(Path.Combine(DataDir, "settings.json"), s);
	}

	public WindowBounds? LoadWindow()
	{
		try
		{
			WindowBounds windowBounds = Load<WindowBounds>(Path.Combine(DataDir, "window.json"));
			return ((object)windowBounds != null && windowBounds.Width > 0 && windowBounds.Height > 0) ? windowBounds : null;
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return null;
		}
	}

	public void SaveWindow(WindowBounds w)
	{
		Save(Path.Combine(DataDir, "window.json"), w);
	}

	public Dictionary<string, IniOwner> LoadInis()
	{
		return new Dictionary<string, IniOwner>(Load<Dictionary<string, IniOwner>>(InisFile) ?? new Dictionary<string, IniOwner>(), StringComparer.OrdinalIgnoreCase);
	}

	public void UpdateInis(Action<Dictionary<string, IniOwner>> change, TimeSpan? wait = null)
	{
		Locked(InisFile, () =>
		{
			Dictionary<string, IniOwner> dictionary = LoadInis();
			change(dictionary);
			Save(InisFile, dictionary);
		}, wait);
	}

	public Dictionary<string, Evaluation> LoadScan()
	{
		return Load<Dictionary<string, Evaluation>>(Path.Combine(DataDir, "scan.json")) ?? new Dictionary<string, Evaluation>();
	}

	public void SaveScan(Dictionary<string, Evaluation> scan)
	{
		Save(Path.Combine(DataDir, "scan.json"), scan);
	}

	public List<Game> LoadDiscovered(string build)
	{
		Discovered discovered = Load<Discovered>(Path.Combine(DataDir, "discovered.json"));
		if ((object)discovered == null || !(discovered.Build == build))
		{
			return new List<Game>();
		}
		return discovered.Games;
	}

	public void SaveDiscovered(string build, List<Game> games, Dictionary<string, ShippingPick>? shipping = null)
	{
		Save(Path.Combine(DataDir, "discovered.json"), new Discovered(build, games, shipping));
	}

	public Dictionary<string, ShippingPick> LoadShippingPicks(string build)
	{
		Discovered discovered = Load<Discovered>(Path.Combine(DataDir, "discovered.json"));
		if ((object)discovered != null)
		{
			Dictionary<string, ShippingPick> shipping = discovered.Shipping;
			if (shipping != null && discovered.Build == build)
			{
				return shipping;
			}
		}
		return new Dictionary<string, ShippingPick>();
	}

	public KeptList? LoadList()
	{
		return Load<KeptList>(Path.Combine(DataDir, "games.json"));
	}

	public void SaveList(KeptList list)
	{
		Save(Path.Combine(DataDir, "games.json"), list);
	}

	public List<ManualEntry> LoadManualGames()
	{
		return (Load<List<JsonElement>>(Path.Combine(DataDir, "manual-games.json")) ?? new List<JsonElement>()).Select((JsonElement e) =>
		{
			try
			{
				return e.Deserialize<ManualEntry>(Json);
			}
			catch (JsonException)
			{
				return (ManualEntry)null;
			}
		}).OfType<ManualEntry>().ToList();
	}

	public void SaveManualGames(List<ManualEntry> games)
	{
		Save(Path.Combine(DataDir, "manual-games.json"), games);
	}

	public AddedLibrary LoadAddedLibrary()
	{
		return Load<AddedLibrary>(Path.Combine(DataDir, "added-library.json")) ?? new AddedLibrary();
	}

	public void SaveAddedLibrary(AddedLibrary library)
	{
		Save(Path.Combine(DataDir, "added-library.json"), library);
	}

	public LibrarySort LoadLibrarySort()
	{
		try
		{
			return Load<LibraryView>(Path.Combine(DataDir, "library-view.json"))?.Sort ?? LibrarySort.Status;
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return LibrarySort.Status;
		}
	}

	public void SaveLibrarySort(LibrarySort sort)
	{
		Save(Path.Combine(DataDir, "library-view.json"), new LibraryView(sort));
	}

	public Dictionary<string, string> LoadDismissed()
	{
		return Load<Dictionary<string, string>>(Path.Combine(DataDir, "dismissed.json")) ?? new Dictionary<string, string>();
	}

	public void SaveDismissed(Dictionary<string, string> dismissed)
	{
		Save(Path.Combine(DataDir, "dismissed.json"), dismissed);
	}

	public Dictionary<string, string> LoadNotified()
	{
		return Load<Dictionary<string, string>>(Path.Combine(DataDir, "notified.json")) ?? new Dictionary<string, string>();
	}

	public void SaveNotified(Dictionary<string, string> notified)
	{
		Save(Path.Combine(DataDir, "notified.json"), notified);
	}

	public Dictionary<GpuVendor, double> LoadWarmRates()
	{
		return Load<Dictionary<GpuVendor, double>>(Path.Combine(DataDir, "warmrates.json")) ?? new Dictionary<GpuVendor, double>();
	}

	public void SaveWarmRates(Dictionary<GpuVendor, double> rates)
	{
		Save(Path.Combine(DataDir, "warmrates.json"), rates);
	}

	public Dictionary<string, DateTimeOffset> LoadFeedTimes()
	{
		return Load<Dictionary<string, DateTimeOffset>>(Path.Combine(DataDir, "feeds.json")) ?? new Dictionary<string, DateTimeOffset>();
	}

	public void SaveFeedTimes(Dictionary<string, DateTimeOffset> times)
	{
		Save(Path.Combine(DataDir, "feeds.json"), times);
	}

	public PackageTries LoadPackageTries()
	{
		PackageTries packageTries = Load<PackageTries>(Path.Combine(DataDir, "package-tries.json"));
		if (packageTries == null || packageTries.At == null)
		{
			return new PackageTries();
		}
		return packageTries;
	}

	public void SavePackageTries(PackageTries tries)
	{
		Save(Path.Combine(DataDir, "package-tries.json"), tries);
	}

	public Dictionary<string, DateTimeOffset> LoadContentTimes()
	{
		return Load<Dictionary<string, DateTimeOffset>>(Path.Combine(DataDir, "content-signed.json")) ?? new Dictionary<string, DateTimeOffset>();
	}

	public void SaveContentTimes(Dictionary<string, DateTimeOffset> times)
	{
		Save(Path.Combine(DataDir, "content-signed.json"), times);
	}

	public NewsSeen LoadNewsSeen()
	{
		return Load<NewsSeen>(Path.Combine(DataDir, "news-seen.json")) ?? new NewsSeen();
	}

	public void SaveNewsSeen(NewsSeen seen)
	{
		Save(Path.Combine(DataDir, "news-seen.json"), seen);
	}

	public string GameDir(string gameId)
	{
		string text = Path.Combine(DataDir, "games");
		string text2 = gameId.Replace(':', '_');
		string text3 = Path.Combine(text, IsPlainName(text2) ? text2 : ("%" + Segment(gameId)));
		if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(text3)), Path.GetFullPath(text), StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentException("game id " + gameId + " isn't a folder name");
		}
		return text3;
	}

	private static bool IsPlainName(string name)
	{
		bool flag = name.Length > 0 && name[0] != '%' && name.IndexOfAny(BadNameChars) < 0;
		if (flag)
		{
			char c = name[name.Length - 1];
			bool flag2 = ((c == ' ' || c == '.') ? true : false);
			flag = !flag2;
		}
		return flag;
	}

	public static string GameId(string folderName)
	{
		if (!folderName.StartsWith('%'))
		{
			return folderName;
		}
		return Uri.UnescapeDataString(folderName.Substring(1));
	}

	public static string Segment(string name)
	{
		StringBuilder stringBuilder = new StringBuilder(name.Length);
		int num = name.Length - MemoryExtensions.TrimEnd(name, ". ").Length;
		for (int i = 0; i < name.Length; i++)
		{
			char c = name[i];
			if (c == '%' || i >= name.Length - num || Array.IndexOf(BadNameChars, c) >= 0)
			{
				StringBuilder stringBuilder2 = stringBuilder;
				StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(1, 1, stringBuilder2);
				handler.AppendLiteral("%");
				handler.AppendFormatted((int)c, "X2");
				stringBuilder2.Append(ref handler);
			}
			else
			{
				stringBuilder.Append(c);
			}
		}
		return stringBuilder.ToString();
	}

	public GameRecord LoadGame(string gameId)
	{
		LoadingGame?.Invoke(gameId);
		GameRecord gameRecord = Load<GameRecord>(Path.Combine(GameDir(gameId), "state.json")) ?? new GameRecord();
		Plan plan = gameRecord.Plan;
		if ((object)plan != null && Path.GetDirectoryName(plan.FilePath) != GameDir(gameId))
		{
			string text = Path.Combine(GameDir(gameId), Path.GetFileName(plan.FilePath));
			if (File.Exists(text))
			{
				gameRecord.Plan = plan with
				{
					FilePath = text
				};
			}
		}
		_held.AddOrUpdate(gameRecord, JsonSerializer.SerializeToNode(gameRecord, Json).AsObject());
		return gameRecord;
	}

	public void SaveGame(string gameId, GameRecord r, TimeSpan? wait = null)
	{
		string path = Path.Combine(GameDir(gameId), "state.json");
		JsonObject now = JsonSerializer.SerializeToNode(r, Json).AsObject();
		Locked(path, () =>
		{
			JsonObject jsonObject2;
			if (_held.TryGetValue(r, out JsonObject value))
			{
				JsonObject jsonObject = Load<JsonObject>(path);
				if (jsonObject != null)
				{
					jsonObject2 = Merge(jsonObject, value, now);
					goto IL_003e;
				}
			}
			jsonObject2 = now;
			goto IL_003e;
			IL_003e:
			JsonObject value2 = jsonObject2;
			WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(value2, Json));
		}, wait);
		_held.AddOrUpdate(r, (JsonObject)now.DeepClone());
	}

	public void PruneKeyFiles(string gameId, TimeSpan? age = null)
	{
		string path = Path.Combine(GameDir(gameId), "state.json");
		Locked(path, () =>
		{
			GameRecord gameRecord = Load<GameRecord>(path);
			KeyFiles.Prune(GameDir(gameId), age ?? TimeSpan.FromHours(1), gameRecord?.PlanKeysFile, gameRecord?.WarmKeysFile);
		});
	}

	internal static void Locked(string path, Action f, TimeSpan? wait = null)
	{
		using (new PathGate(path, wait))
		{
			f();
		}
	}

	private static JsonObject Merge(JsonObject latest, JsonObject was, JsonObject now)
	{
		foreach (var (propertyName, jsonNode2) in now)
		{
			JsonNode jsonNode3 = was[propertyName];
			if (JsonNode.DeepEquals(jsonNode3, jsonNode2))
			{
				continue;
			}
			if (jsonNode2 is JsonArray a && latest[propertyName] is JsonArray a2)
			{
				HashSet<string> hashSet = Strings(jsonNode3);
				HashSet<string> hashSet2 = Strings(a);
				HashSet<string> hashSet3 = hashSet;
				latest[propertyName] = new JsonArray(new ReadOnlySpan<JsonNode>(Strings(a2).Except(hashSet3.Except(hashSet2)).Union(hashSet2.Except(hashSet3)).Select((Func<string, JsonNode>)((string s) => JsonValue.Create(s)))
					.ToArray()));
			}
			else
			{
				latest[propertyName] = jsonNode2?.DeepClone();
			}
		}
		return latest;
		static HashSet<string> Strings(JsonNode? jsonNode4)
		{
			if (jsonNode4 is JsonArray source)
			{
				HashSet<string> hashSet4 = new HashSet<string>();
				foreach (string item in source.Select((JsonNode i) => i.GetValue<string>()))
				{
					hashSet4.Add(item);
				}
				return hashSet4;
			}
			return new HashSet<string>();
		}
	}

	private static T? Load<T>(string path) where T : class
	{
		try
		{
			if (!File.Exists(path))
			{
				return null;
			}
			using FileStream utf8Json = Retry(() => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
			return JsonSerializer.Deserialize<T>(utf8Json, Json);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static void Save<T>(string path, T value)
	{
		WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(value, Json));
	}

	internal static void WriteAtomic(string path, byte[] bytes)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path));
		string tmp = $"{path}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.tmp";
		File.WriteAllBytes(tmp, bytes);
		try
		{
			Retry(() =>
			{
				File.Move(tmp, path, overwrite: true);
				return 0;
			});
		}
		finally
		{
			if (File.Exists(tmp))
			{
				File.Delete(tmp);
			}
		}
		KeyFiles.Forget(path);
	}

	private static TR Retry<TR>(Func<TR> f)
	{
		int num = 0;
		bool flag = default;
		while (true)
		{
			try
			{
				return f();
			}
			catch (Exception ex) when (((Func<bool>)delegate
			{
				// Could not convert BlockContainer to single expression
				flag = num < 100;
				if (flag)
				{
					flag = ((ex is IOException || ex is UnauthorizedAccessException) ? true : false);
				}
				return flag;
			}).Invoke())
			{
				Thread.Sleep(25);
			}
			num++;
		}
	}
}
