using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.Compression;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Vendors;

[CompilerGenerated]
internal class Program
{
	private static async Task<int> Main(string[] args)
	{
		Console.OutputEncoding = Encoding.UTF8;
		RouteFailover.Product = "SCSKiller-CLI";
		bool flag = args.Length == 0;
		if (!flag)
		{
			bool flag2;
			switch (args[0])
			{
			case "-h":
			case "--help":
			case "help":
				flag2 = true;
				break;
			default:
				flag2 = false;
				break;
			}
			flag = flag2;
		}
		if (flag)
		{
			Console.WriteLine("usage: scskiller <command>\r\n  scan [--rescan]                             discover games and show their status (--rescan: redo engine detection)\r\n  add <exe> [<install folder>]                add a game no store lists, by its exe (with its folder: the recorder may go there)\r\n  status [game]                               all games, or one in detail\r\n  compile <game|--all-ready> [--threads N] [--idle | --when-idle] [--careful | --fast]\r\n                                              --idle: background priority; --when-idle: also only while nobody uses the PC;\r\n                                              --careful (AMD): this and later compiles of the game compile its recorded\r\n                                              pipelines in passes on few threads, so the driver keeps more of them (slower);\r\n                                              --fast: back to one pass\r\n  queue [command]                             debug shell for the compile queue: the command, then one per line on stdin:\r\n                                              list | add <game> | move <game> <index> | remove <game> | start | stop | wait | quit\r\n  rewarm-stale [--if-driver-changed]          what the scheduled task runs; follows the driver-update setting\r\n  cache get | cache set <GB|unlimited|default>\r\n  cache clear <game> [--game-precache]        delete the game's shader caches: driver, Windows (D3DSCache) (asks first);\r\n                                              --game-precache: also the caches the game writes itself (*.ushaderprecache,\r\n                                              Dawn's bin\\pipelineLibrary.cache, RE Engine's shader.cache2 and\r\n                                              piplinelist.bin, GTA V Enhanced's and HELLDIVERS 2's pipeline libraries),\r\n                                              which it rebuilds at its next start\r\n  nvidia-snapshot                             read-only: every global DRS setting, Auto Shader Compilation, its idle task\r\n  nvidia-auto-shader off|low|medium|high      switch NVIDIA's Auto Shader Compilation (admin, asks first)\r\n    (cache set / nvidia-auto-shader, as the app runs them elevated: --yes skips the question, --result <file>\r\n     writes the outcome as a JSON line, --for-user <SID> refuses a different account's elevation)\r\n  record install|uninstall <game>\r\n  record clear <game>                         delete the game's recording (the recorder's data files and SCSKiller's copy; asks first)\r\n  record alongside <game> on|off              let the recorder chain to a mod's d3d12.dll (renamed, put back on removal)\r\n  key <game> <hex>                            give an encrypted game's AES key (verified, stored locally only)\r\n  index <game> --out <dir>                    debug: dump the engine reader's shader index\r\n  rehydrate <game> <hash-only.db> --out <db> [--expect <content hash>]\r\n                                              add the shader bytes a hash-only recording references, from the install\r\n  reference export <game> [--out <file>]      the game build's shader list for the community server's `admin reference import`:\r\n                                              the index's shaders and those this PC's recording saw (default <game id>.reference.json)\r\n  task register|unregister                    the logon/idle \"re-warm after a driver update\" task\r\n  fetch-codecs <dir>                          packaging: the pinned Oodle/zlib DLLs in <dir>, downloaded if missing\r\n  --version                                   the CLI's version\r\n<game> is an id (steam:2909400) or a case-insensitive part of the name.");
			return 0;
		}
		switch (args[0])
		{
		case "-v":
		case "--version":
		case "version":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (flag)
		{
			Console.WriteLine(AppVersion.Current);
			return 0;
		}
		if (args[0] != "fetch-codecs" && Busy.Applying(AppStore.DefaultDir, DateTimeOffset.UtcNow))
		{
			Console.Error.WriteLine("an update is being installed, try again in a minute");
			try
			{
				string text = Opt("--result");
				if (text != null)
				{
					Elevated.WriteResult(text, ok: false, "an update is being installed, try again in a minute");
				}
			}
			catch (ArgumentException)
			{
			}
			return 0;
		}
		int num3;
		try
		{
			int num;
			switch (args[0])
			{
			case "scan":
				num = await Scan(args.Contains("--rescan"));
				break;
			case "status":
				num = await Status((args.Length > 1) ? args[1] : null);
				break;
			case "add":
				num = await AddGame();
				break;
			case "compile":
				num = await Compile();
				break;
			case "queue":
				num = await QueueShell();
				break;
			case "rewarm-stale":
				num = await RewarmStale(args.Contains("--if-driver-changed"));
				break;
			case "cache":
			{
				int num2 = ((!(args.ElementAtOrDefault(1) == "clear")) ? Cache() : (await CacheClear()));
				num = num2;
				break;
			}
			case "record":
				num = await Record();
				break;
			case "index":
				num = await Index();
				break;
			case "rehydrate":
				num = await RehydrateCommand();
				break;
			case "reference":
				if (args.ElementAtOrDefault(1) == "export")
				{
					num = await ReferenceExport();
					break;
				}
				goto default;
			case "key":
				num = await Key();
				break;
			case "task":
				num = TaskCommand();
				break;
			case "proton-env":
				num = await ProtonEnv();
				break;
			case "nvidia-snapshot":
				num = NvidiaSnapshot();
				break;
			case "nvidia-auto-shader":
				num = NvidiaAutoShader();
				break;
			case "fetch-codecs":
				num = FetchCodecs(args.ElementAtOrDefault(1) ?? throw new ArgumentException("usage: scskiller fetch-codecs <dir>"));
				break;
			default:
				num = Fail("unknown command '" + args[0] + "'\nusage: scskiller <command>\r\n  scan [--rescan]                             discover games and show their status (--rescan: redo engine detection)\r\n  add <exe> [<install folder>]                add a game no store lists, by its exe (with its folder: the recorder may go there)\r\n  status [game]                               all games, or one in detail\r\n  compile <game|--all-ready> [--threads N] [--idle | --when-idle] [--careful | --fast]\r\n                                              --idle: background priority; --when-idle: also only while nobody uses the PC;\r\n                                              --careful (AMD): this and later compiles of the game compile its recorded\r\n                                              pipelines in passes on few threads, so the driver keeps more of them (slower);\r\n                                              --fast: back to one pass\r\n  queue [command]                             debug shell for the compile queue: the command, then one per line on stdin:\r\n                                              list | add <game> | move <game> <index> | remove <game> | start | stop | wait | quit\r\n  rewarm-stale [--if-driver-changed]          what the scheduled task runs; follows the driver-update setting\r\n  cache get | cache set <GB|unlimited|default>\r\n  cache clear <game> [--game-precache]        delete the game's shader caches: driver, Windows (D3DSCache) (asks first);\r\n                                              --game-precache: also the caches the game writes itself (*.ushaderprecache,\r\n                                              Dawn's bin\\pipelineLibrary.cache, RE Engine's shader.cache2 and\r\n                                              piplinelist.bin, GTA V Enhanced's and HELLDIVERS 2's pipeline libraries),\r\n                                              which it rebuilds at its next start\r\n  nvidia-snapshot                             read-only: every global DRS setting, Auto Shader Compilation, its idle task\r\n  nvidia-auto-shader off|low|medium|high      switch NVIDIA's Auto Shader Compilation (admin, asks first)\r\n    (cache set / nvidia-auto-shader, as the app runs them elevated: --yes skips the question, --result <file>\r\n     writes the outcome as a JSON line, --for-user <SID> refuses a different account's elevation)\r\n  record install|uninstall <game>\r\n  record clear <game>                         delete the game's recording (the recorder's data files and SCSKiller's copy; asks first)\r\n  record alongside <game> on|off              let the recorder chain to a mod's d3d12.dll (renamed, put back on removal)\r\n  key <game> <hex>                            give an encrypted game's AES key (verified, stored locally only)\r\n  index <game> --out <dir>                    debug: dump the engine reader's shader index\r\n  rehydrate <game> <hash-only.db> --out <db> [--expect <content hash>]\r\n                                              add the shader bytes a hash-only recording references, from the install\r\n  reference export <game> [--out <file>]      the game build's shader list for the community server's `admin reference import`:\r\n                                              the index's shaders and those this PC's recording saw (default <game id>.reference.json)\r\n  task register|unregister                    the logon/idle \"re-warm after a driver update\" task\r\n  fetch-codecs <dir>                          packaging: the pinned Oodle/zlib DLLs in <dir>, downloaded if missing\r\n  --version                                   the CLI's version\r\n<game> is an id (steam:2909400) or a case-insensitive part of the name.");
				break;
			}
			num3 = num;
		}
		catch (Exception ex2)
		{
			flag = ((ex2 is ArgumentException || ex2 is InvalidOperationException || ex2 is IOException || ex2 is UnauthorizedAccessException) ? true : false);
			num3 = Fail(flag ? ex2.Message : ex2.ToString());
		}
		string text2 = null;
		try
		{
			text2 = Opt("--result");
		}
		catch (ArgumentException ex3)
		{
			num3 = Fail(ex3.Message);
		}
		if (text2 != null)
		{
			Elevated.WriteResult(text2, num3 == 0, Outcome.Message ?? "");
		}
		return num3;
		async Task<int> AddGame()
		{
			if (args.Length < 2)
			{
				return Fail("add <exe> [<install folder>]");
			}
			ScsKiller scsKiller = await Open();
			var (game2, existed) = scsKiller.AddManualGame(args[1], args.ElementAtOrDefault(2));
			await scsKiller.ScanAsync(CancellationToken.None);
			Console.WriteLine($"{(existed ? "already listed" : "added")}: {game2.Id}  {game2.Name}  {game2.ExePath}");
			return 0;
		}
		static void BackgroundPriority()
		{
			using Process process = Process.GetCurrentProcess();
			process.PriorityClass = ProcessPriorityClass.Idle;
		}
		int Cache()
		{
			IGpuVendorBackend gpuVendorBackend = RunningGpu();
			Console.WriteLine($"GPU: {gpuVendorBackend.Gpu.Name} ({gpuVendorBackend.Vendor}), driver {gpuVendorBackend.Gpu.DriverVersion}, profile {gpuVendorBackend.Caps.Profile}");
			if (args.Length > 1 && args[1] == "set")
			{
				if (args.Length < 3)
				{
					return Fail("cache set <GB|unlimited|default>");
				}
				if (!gpuVendorBackend.Caps.CacheSizeConfigurable)
				{
					return Fail("this GPU's cache size is not configurable");
				}
				string text3 = args[2].ToLowerInvariant();
				CacheLimit cacheLimit;
				if (!(text3 == "default"))
				{
					if (text3 == "unlimited")
					{
						cacheLimit = new CacheLimit(null, IsDriverDefault: false);
					}
					else
					{
						if (!double.TryParse(text3, CultureInfo.InvariantCulture, out var result) || !(result > 0.0))
						{
							throw new ArgumentException("size must be a number of GB, 'unlimited' or 'default'");
						}
						cacheLimit = new CacheLimit((long)(result * 1073741824.0), IsDriverDefault: false);
					}
				}
				else
				{
					cacheLimit = new CacheLimit(null, IsDriverDefault: true);
				}
				CacheLimit cacheLimit2 = cacheLimit;
				if (!Elevated.IsAdmin)
				{
					return Fail("changing the driver's shader cache size needs an elevated (administrator) prompt");
				}
				string text4;
				if (cacheLimit2.IsDriverDefault)
				{
					text4 = "the driver default";
				}
				else
				{
					text4 = ((!cacheLimit2.Bytes.HasValue) ? "unlimited" : Format.Bytes(cacheLimit2.Bytes));
				}
				if (!args.Contains("--yes") && !Confirm("Set the global NVIDIA shader cache size to " + text4 + "? This changes a driver setting for every game."))
				{
					return 1;
				}
				gpuVendorBackend.SetCacheLimit(cacheLimit2);
				Outcome.Message = "Shader cache limit set to " + text4 + ".";
			}
			CacheUsage cacheUsage = gpuVendorBackend.GetCacheUsage();
			if (gpuVendorBackend is AmdBackend)
			{
				Console.WriteLine($"cache: {(cacheUsage.UpperBound ? "<= " : "")}{Format.Bytes(cacheUsage.BytesOnDisk)} on disk = {AmdBackend.CacheDir} {Format.Bytes(AmdBackend.Bytes(AmdBackend.CacheDir))} (D3D12) + {AmdBackend.D3D11CacheDir} {Format.Bytes(AmdBackend.Bytes(AmdBackend.D3D11CacheDir))} (D3D11)");
			}
			else
			{
				Console.WriteLine($"cache: {cacheUsage.Path} {(cacheUsage.UpperBound ? "<= " : "")}{Format.Bytes(cacheUsage.BytesOnDisk)} on disk");
			}
			CacheLimit cacheLimit3 = gpuVendorBackend.GetCacheLimit();
			string? text5;
			if (cacheLimit3 == null)
			{
				text5 = "not readable";
			}
			else
			{
				text5 = ((!cacheLimit3.Bytes.HasValue) ? "unlimited" : Format.Bytes(cacheLimit3.Bytes));
			}
			Console.WriteLine("limit: " + text5 + (((object)cacheLimit3 != null && cacheLimit3.IsDriverDefault) ? " (driver default)" : ""));
			if (gpuVendorBackend is NvidiaBackend nvidiaBackend)
			{
				(uint Id, uint? Raw, uint DriverDefault) tuple = nvidiaBackend.ReadCacheSetting();
				uint item = tuple.Id;
				uint? item2 = tuple.Raw;
				uint item3 = tuple.DriverDefault;
				DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(42, 4);
				defaultInterpolatedStringHandler.AppendLiteral("       setting 0x");
				defaultInterpolatedStringHandler.AppendFormatted(item, "X8");
				defaultInterpolatedStringHandler.AppendLiteral(" '");
				defaultInterpolatedStringHandler.AppendFormatted("Shader disk cache maximum size");
				defaultInterpolatedStringHandler.AppendLiteral("' = ");
				string value;
				if (item2.HasValue)
				{
					uint valueOrDefault = item2.GetValueOrDefault();
					value = $"0x{valueOrDefault:X8}";
				}
				else
				{
					value = "not set";
				}
				defaultInterpolatedStringHandler.AppendFormatted(value);
				defaultInterpolatedStringHandler.AppendLiteral(", driver default 0x");
				defaultInterpolatedStringHandler.AppendFormatted(item3, "X8");
				Console.WriteLine(defaultInterpolatedStringHandler.ToStringAndClear());
			}
			return 0;
		}
		async Task<int> CacheClear()
		{
			if (args.Length < 3)
			{
				return Fail("cache clear <game>");
			}
			ScsKiller scsKiller = await Open();
			GameState gameState = Match(scsKiller.Games, args[2]);
			bool flag3 = args.Contains("--game-precache");
			IReadOnlyList<CachePart> readOnlyList = scsKiller.GameCaches(gameState.Game.Id, flag3);
			string text3 = scsKiller.DriverCacheGap(gameState.Game.Id);
			if (readOnlyList.Count == 0)
			{
				return Fail("no shader-cache files of " + gameState.Game.Name + " were found" + ((text3 != null) ? (" (" + text3 + ")") : ""));
			}
			foreach (CachePart item4 in readOnlyList)
			{
				Console.WriteLine(item4.Name + "  " + Format.Bytes(item4.Bytes));
				foreach (string file in item4.Files)
				{
					Console.WriteLine("  " + file);
				}
			}
			if (text3 != null)
			{
				Console.WriteLine("note: " + text3);
			}
			if (!flag3)
			{
				List<CachePart> list = scsKiller.GameCaches(gameState.Game.Id, gamePrecache: true).Where((CachePart p) =>
				{
					string name = p.Name;
					return (name == "Game's pipeline cache" || name == "Game's shader precache") ? true : false;
				}).ToList();
				if (list != null && list.Count > 0)
				{
					Console.WriteLine("kept: the game's own caches (" + string.Join(", ", list.Select((CachePart p) => p.Name + " " + Format.Bytes(p.Bytes))) + "); --game-precache deletes them too");
				}
			}
			if (!Confirm($"Delete {gameState.Game.Name}'s shader caches ({string.Join(", ", readOnlyList.Select((CachePart p) => p.Name + " " + Format.Bytes(p.Bytes)))}, the files above)? " + "Its next run is cold; it shows as not warmed afterwards."))
			{
				return 1;
			}
			scsKiller.ClearGameCache(gameState.Game.Id, flag3);
			Console.WriteLine("cleared " + gameState.Game.Name + "'s shader caches");
			return 0;
		}
		static string CarefulLine(CarefulCompile? c, PlanStats? plan)
		{
			if ((object)c != null)
			{
				string obj = (c.On ? $"on: the recorded pipelines compile in passes on at most {4} threads, the rest at full speed" : "off (compile <game> --careful turns it on)");
				string text3;
				if (c.Recorded != 0 || (object)plan == null || plan.D3D11Shaders <= 0 || plan.Recorded != 0L)
				{
					if (c.Recorded != 0)
					{
						TimeSpan? estimate = c.Estimate;
						if (estimate.HasValue)
						{
							TimeSpan valueOrDefault = estimate.GetValueOrDefault();
							text3 = "; a careful compile takes about " + ScsKiller.Duration(valueOrDefault);
						}
						else
						{
							text3 = "";
						}
					}
					else
					{
						text3 = "; no recorded DirectX 12 pipelines, so nothing compiles carefully";
					}
				}
				else
				{
					text3 = "; only for DirectX 12: this game's DirectX 11 shaders always compile at full speed";
				}
				return obj + text3;
			}
			return "- (AMD only)";
		}
		async Task<int> Compile()
		{
			if (args.Length < 2)
			{
				return Fail("compile <game|--all-ready> [--threads N] [--idle | --when-idle] [--careful | --fast]");
			}
			Only(new string[6] { "--all-ready", "--threads", "--idle", "--when-idle", "--careful", "--fast" });
			string text3 = Opt("--threads");
			int? num4;
			if (text3 == null)
			{
				num4 = null;
			}
			else
			{
				if (!int.TryParse(text3, out var result) || result <= 0)
				{
					throw new ArgumentException("--threads takes a number of threads");
				}
				num4 = result;
			}
			int? threads = num4;
			ScsKiller scsKiller = await Open();
			if (threads.HasValue)
			{
				scsKiller.ThreadsOverride = threads;
			}
			bool flag3 = args.Contains("--when-idle");
			scsKiller.Background = args.Contains("--idle");
			if (scsKiller.Background | flag3)
			{
				BackgroundPriority();
			}
			List<GameState> list = ((args[1] == "--all-ready") ? scsKiller.Games.Where((GameState s) =>
			{
				GameStatus status = s.Status;
				bool flag4 = ((status == GameStatus.Ready || status == GameStatus.Stale) ? true : false);
				return flag4 && s.NoStutter == null && !s.CompileUnreached;
			}).ToList() : new List<GameState>(1) { Match(scsKiller.Games, args[1]) });
			if (list != null && list.Count == 1)
			{
				GameState gameState = list[0];
				if ((object)gameState != null && gameState.StatusReason == "the compile can't reach this game: it reads its shaders from a different driver cache than the one SCSKiller fills. Clear its cache to free the space")
				{
					return Fail(gameState.Game.Name + ": " + gameState.StatusReason);
				}
			}
			if (args.Contains("--careful") || args.Contains("--fast"))
			{
				foreach (GameState g in list)
				{
					scsKiller.SetCarefulCompile(g.Game.Id, args.Contains("--careful"));
					string name = g.Game.Name;
					GameState gameState2 = scsKiller.Games.Single((GameState s) => s.Game.Id == g.Game.Id);
					Console.WriteLine(name + ": " + CarefulLine(gameState2.Careful, gameState2.Plan));
				}
			}
			return await RunQueue(scsKiller, list, flag3);
		}
		static bool Confirm(string question)
		{
			Console.Write(question + " [y/N] ");
			bool isInputRedirected = Console.IsInputRedirected;
			string text3 = (isInputRedirected ? Elevated.ReadAnswer(Console.OpenStandardInput()) : Console.ReadLine()?.Trim());
			if (isInputRedirected)
			{
				Console.WriteLine(text3);
			}
			if (string.Equals(text3, "y", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			Fail((text3 == null) ? "cancelled: no answer on stdin (pipe y to confirm)" : ("cancelled: the answer was '" + text3 + "', not y"));
			return false;
		}
		static int Fail(string message)
		{
			Console.Error.WriteLine("error: " + message);
			Outcome.Message = message;
			return 1;
		}
		static int FetchCodecs(string dir)
		{
			try
			{
				Console.WriteLine(Codecs.Ensure(OodleHelper.OodleFileName, Codecs.DownloadOodle, dir));
				Console.WriteLine(Codecs.Ensure(ZlibHelper.DllName, Codecs.DownloadZlib, dir));
				return 0;
			}
			catch (Exception ex4) when ((ex4 is IOException || ex4 is UnauthorizedAccessException || ex4 is InvalidDataException) ? true : false)
			{
				return Fail(ex4.Message);
			}
		}
		static string Hms(TimeSpan? t)
		{
			return t?.ToString("h\\:mm\\:ss") ?? "-";
		}
		async Task<int> Index()
		{
			if (args.Length >= 2)
			{
				string outDir = Opt("--out");
				if (outDir != null)
				{
					(ScsKiller, GameState, IEngineReader, EngineInfo, ShaderIndex) tuple = await OpenIndex(args[1], new Progress<string>(Console.WriteLine));
					EngineInfo item = tuple.Item4;
					ShaderIndex index = tuple.Item5;
					Directory.CreateDirectory(outDir);
					await File.WriteAllTextAsync(Path.Combine(outDir, "index.json"), JsonSerializer.Serialize(new
					{
						engine = item,
						index = index
					}, AppStore.Json));
					Console.WriteLine($"{index.Shaders.Count} shaders, {index.Maps.Count} shader maps, content hash {index.ContentHash} -> {outDir}");
					return 0;
				}
			}
			return Fail("index <game> --out <dir>");
		}
		async Task<int> Key()
		{
			if (args.Length < 3)
			{
				return Fail("key <game> <hex>");
			}
			ScsKiller k = await Open();
			GameState g = Match(k.Games, args[1]);
			if (!k.SetEncryptionKey(g.Game.Id, args[2]))
			{
				return Fail("that key doesn't open " + g.Game.Name + "'s files");
			}
			await k.RescanAsync(CancellationToken.None);
			Console.WriteLine("key accepted for " + g.Game.Name + ": " + k.Games.First((GameState s) => s.Game.Id == g.Game.Id).StatusReason);
			return 0;
		}
		static GameState Match(IReadOnlyList<GameState> games, string query)
		{
			GameState gameState = games.FirstOrDefault((GameState s) => s.Game.Id.Equals(query, StringComparison.OrdinalIgnoreCase)) ?? games.FirstOrDefault((GameState s) => s.Game.Name.Equals(query, StringComparison.OrdinalIgnoreCase));
			if (gameState != null)
			{
				return gameState;
			}
			List<GameState> list = games.Where((GameState s) => s.Game.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
			return list.Count switch
			{
				1 => list[0], 
				0 => throw new ArgumentException("no game matches '" + query + "'"), 
				_ => throw new ArgumentException("'" + query + "' matches several games: " + string.Join(", ", list.Select((GameState s) => s.Game.Name + " (" + s.Game.Id + ")"))), 
			};
		}
		static string MiddlewareLine(IReadOnlyList<MiddlewareTag> tags, bool dlls)
		{
			return string.Join(" · ", tags.Select((MiddlewareTag t) => Format.Middleware(t) + (dlls ? (" [" + string.Join(", ", t.Dlls) + "]") : "")));
		}
		int NvidiaAutoShader()
		{
			if (!Enum.TryParse<AutoShaderCompilation>(args.ElementAtOrDefault(1), ignoreCase: true, out var result) || !Enum.IsDefined(result) || int.TryParse(args[1], out var _))
			{
				return Fail("nvidia-auto-shader off|low|medium|high");
			}
			if (!(RunningGpu() is NvidiaBackend nvidiaBackend))
			{
				return Fail("no NVIDIA GPU");
			}
			if (!Elevated.IsAdmin)
			{
				return Fail("switching NVIDIA Auto Shader Compilation needs an elevated (administrator) prompt");
			}
			string text3 = Opt("--for-user");
			if (text3 != null && text3 != Elevated.CurrentUserSid)
			{
				return Fail("Windows elevated as a different account (" + WindowsIdentity.GetCurrent().Name + "), so the NVIDIA task would compile for that account, not for you. Sign in to Windows with an administrator account, or ask an administrator to add yours to Administrators.");
			}
			if (!args.Contains("--yes") && !Confirm($"Set NVIDIA Auto Shader Compilation to {result}? This changes a global driver setting and the driver's idle task."))
			{
				return 1;
			}
			Outcome.Message = nvidiaBackend.SetAutoShaderCompilation(result);
			Console.WriteLine(Outcome.Message + " now: " + (nvidiaBackend.GetAutoShaderCompilation()?.ToString() ?? "not readable"));
			return 0;
		}
		static int NvidiaSnapshot()
		{
			if (!(ScsKiller.DetectGpu() is NvidiaBackend nvidiaBackend))
			{
				return Fail("no NVIDIA GPU");
			}
			Console.WriteLine("driver " + nvidiaBackend.Gpu.DriverVersion);
			Console.WriteLine($"Auto Shader Compilation: {nvidiaBackend.GetAutoShaderCompilation()?.ToString() ?? "not readable"} (setting 0x{15389065u:X8})");
			Console.WriteLine("NvOSC.exe: " + (NvidiaBackend.NvOscPath() ?? "not found"));
			Console.WriteLine("base profile:");
			foreach (var (value, value2, value3, value4) in NvidiaBackend.BaseProfileSettings())
			{
				Console.WriteLine($"  0x{value:X8} type {value3} = 0x{value4:X8}  {value2}");
			}
			string[] array = new string[2]
			{
				"\\Users\\" + Environment.UserName + "\\NVIDIA Shader Compiler",
				"\\NVIDIA Shader Compiler"
			};
			foreach (string text3 in array)
			{
				using Process process = Process.Start(new ProcessStartInfo("schtasks.exe", new _003C_003Ez__ReadOnlyArray<string>(new string[6] { "/Query", "/TN", text3, "/V", "/FO", "LIST" }))
				{
					RedirectStandardOutput = true,
					RedirectStandardError = true
				});
				string text4 = process.StandardOutput.ReadToEnd();
				process.StandardError.ReadToEnd();
				process.WaitForExit();
				Console.WriteLine("task " + text3 + ": " + ((process.ExitCode == 0) ? text4.Trim() : "not registered"));
			}
			Console.WriteLine($"NvOSC.exe running: {Process.GetProcessesByName("NvOSC").Length != 0}");
			return 0;
		}
		void Only(params string[] known)
		{
			string text3 = args.Skip(1).FirstOrDefault((string a) => a.StartsWith("--") && !known.Contains(a));
			if (text3 != null)
			{
				throw new ArgumentException("unknown option '" + text3 + "'");
			}
		}
		static async Task<ScsKiller> Open(bool rescan = false)
		{
			ScsKiller k = ScsKiller.CreateDefault();
			k.Log = new StderrLog();
			Account account = new Account(AppStore.DefaultDir);
			k.DbEntitled = () => account.SignedIn;
			await (rescan ? k.RescanAsync(CancellationToken.None) : k.ScanAsync(CancellationToken.None));
			return k;
		}
		static async Task<(ScsKiller K, GameState G, IEngineReader Reader, EngineInfo Engine, ShaderIndex Index)> OpenIndex(string query, IProgress<string>? log = null)
		{
			ScsKiller scsKiller = await Open();
			GameState gameState = Match(scsKiller.Games, query);
			IEngineReader engineReader = ScsKiller.DefaultReaders();
			EngineInfo engineInfo = gameState.Engine ?? engineReader.Detect(gameState.Game) ?? throw new InvalidOperationException(gameState.Game.Name + ": engine not supported");
			return (K: scsKiller, G: gameState, Reader: engineReader, Engine: engineInfo, Index: engineReader.Index(gameState.Game, engineInfo, log, CancellationToken.None));
		}
		string? Opt(string name)
		{
			int num4 = Array.IndexOf(args, name);
			if (num4 < 0)
			{
				return null;
			}
			if (num4 + 1 >= args.Length || args[num4 + 1].StartsWith("--"))
			{
				throw new ArgumentException(name + " needs a value");
			}
			return args[num4 + 1];
		}
		static void PrintQueueChanges(ScsKiller k)
		{
			DateTime lastPrint = DateTime.MinValue;
			Dictionary<string, QueueStage> lastStage = new Dictionary<string, QueueStage>();
			k.QueueChanged += (QueueItem q) =>
			{
				lock (lastStage)
				{
					if (!lastStage.TryGetValue(q.GameId, out var value) || value != q.Stage || !(DateTime.Now - lastPrint < TimeSpan.FromSeconds(2L)))
					{
						lastStage[q.GameId] = q.Stage;
						lastPrint = DateTime.Now;
						Console.WriteLine($"{DateTime.Now:HH:mm:ss} {q.GameId} {q.Stage}{ScsKiller.ProgressText(q)}{((q.Error != null) ? (" - " + q.Error) : "")}");
					}
				}
			};
		}
		async Task<int> ProtonEnv()
		{
			if (args.Length < 2)
			{
				return Fail("proton-env <game> [--warm]");
			}
			if (!Wine.Running)
			{
				return Fail("proton-env is for games under Wine (Proton)");
			}
			ScsKiller scsKiller = await Open();
			GameState gameState = Match(scsKiller.Games, args[1]);
			DirectoryInfo directoryInfo = Directory.CreateDirectory(Path.Combine(scsKiller.Store.GameDir(gameState.Game.Id), "proton-cache"));
			string[] array = new string[2] { "nvidia", "vkd3d" };
			foreach (string path in array)
			{
				directoryInfo.CreateSubdirectory(path);
			}
			string text3 = Wine.UnixPath(directoryInfo.FullName);
			if (text3 == null)
			{
				return Fail("no Linux path for " + directoryInfo.FullName);
			}
			var (list, list2) = Wine.CacheEnvironment(text3, Path.GetFileName(gameState.Game.ExePath));
			foreach (var (text4, text5) in args.Contains("--warm") ? list : list2)
			{
				Console.WriteLine(text4 + "=" + text5);
			}
			return 0;
		}
		async Task<int> QueueShell()
		{
			ScsKiller k = await Open();
			PrintQueueChanges(k);
			IEnumerable<string> enumerable = ((args.Length <= 1) ? Array.Empty<string>() : new string[1] { string.Join(' ', args[1..]) }).Concat(StdinLines());
			foreach (string item5 in enumerable)
			{
				string[] array = item5.Split(' ', StringSplitOptions.RemoveEmptyEntries);
				try
				{
					switch (array.ElementAtOrDefault(0))
					{
					case "list":
					{
						IReadOnlyList<QueueItem> queue = k.Queue;
						List<QueueItem> list = queue.Where((QueueItem q) => !q.PlanCheck).ToList();
						for (int num4 = 0; num4 < list.Count; num4++)
						{
							DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(2, 4);
							defaultInterpolatedStringHandler.AppendFormatted(num4, 3);
							defaultInterpolatedStringHandler.AppendLiteral(" ");
							defaultInterpolatedStringHandler.AppendFormatted(list[num4].Stage, -13);
							defaultInterpolatedStringHandler.AppendLiteral(" ");
							defaultInterpolatedStringHandler.AppendFormatted(list[num4].GameId);
							string note = list[num4].Note;
							defaultInterpolatedStringHandler.AppendFormatted((note != null) ? ("  (" + note + ")") : "");
							Console.WriteLine(defaultInterpolatedStringHandler.ToStringAndClear());
						}
						Console.WriteLine(k.QueueRunning ? "queue running" : "queue not running");
						string text3 = ScsKiller.PlanCheckLine(queue);
						if (text3 != null)
						{
							Console.WriteLine(text3);
						}
						goto end_IL_0120;
					}
					case "add":
						if (array.Length <= 1)
						{
							break;
						}
						k.Enqueue(Id(array, 1, array.Length));
						goto end_IL_0120;
					case "remove":
						if (array.Length <= 1)
						{
							break;
						}
						k.Remove(Id(array, 1, array.Length));
						goto end_IL_0120;
					case "move":
					{
						if (array.Length <= 2 || !int.TryParse(array[^1], out var result))
						{
							break;
						}
						k.MoveInQueue(Id(array, 1, array.Length - 1), result);
						goto end_IL_0120;
					}
					case "start":
						k.StartQueue();
						goto end_IL_0120;
					case "stop":
						k.StopQueue();
						goto end_IL_0120;
					case "wait":
						await k.WhenQueueIdle();
						goto end_IL_0120;
					case "quit":
						k.StopQueue();
						await k.WhenQueueIdle();
						return 0;
					}
					Console.WriteLine("list | add <game> | move <game> <index> | remove <game> | start | stop | wait | quit");
					end_IL_0120:;
				}
				catch (ArgumentException ex4)
				{
					Console.WriteLine("error: " + ex4.Message);
				}
			}
			await k.WhenQueueIdle();
			return 0;
			string Id(string[] w, int from, int to)
			{
				return Match(k.Games, string.Join(' ', w[from..to])).Game.Id;
			}
		}
		async Task<int> Record()
		{
			bool flag3 = args.Length > 3 && args[1] == "alongside";
			if (flag3)
			{
				string text3 = args[3];
				bool flag4 = ((text3 == "on" || text3 == "off") ? true : false);
				flag3 = flag4;
			}
			bool alongside = flag3;
			flag3 = !alongside;
			if (flag3)
			{
				bool flag4 = args.Length < 3;
				if (!flag4)
				{
					bool flag5;
					switch (args[1])
					{
					case "install":
					case "uninstall":
					case "clear":
						flag5 = true;
						break;
					default:
						flag5 = false;
						break;
					}
					flag4 = !flag5;
				}
				flag3 = flag4;
			}
			if (flag3)
			{
				return Fail("record install|uninstall|clear <game> | record alongside <game> on|off");
			}
			ScsKiller scsKiller = await Open();
			GameState g = Match(scsKiller.Games, args[2]);
			if (args[1] == "clear")
			{
				if (g.RecordingBytes == 0L)
				{
					return Fail(g.Game.Name + " has no recording");
				}
				if (!Confirm($"Delete {g.Game.Name}'s recording ({Format.Bytes(g.RecordingBytes)})? The recorded pipelines are removed; they're compiled again only if " + "the game creates them again while recording, and the compile plan goes back to what the game's files give."))
				{
					return 1;
				}
				scsKiller.ClearRecording(g.Game.Id);
				Console.WriteLine("cleared " + g.Game.Name + "'s recording");
				return 0;
			}
			if (alongside)
			{
				string text4 = scsKiller.RecorderMod(g.Game.Id);
				scsKiller.SetRecordAlongsideMod(g.Game.Id, args[3] == "on");
				GameState gameState = scsKiller.Games.Single((GameState x) => x.Game.Id == g.Game.Id);
				string text5 = $"record alongside {text4 ?? "a mod"}: {args[3]}; recorder {(gameState.RecorderInstalled ? "installed" : "not installed")}";
				string text6 = ((gameState.RecorderSkip == "a mod's DLL is in the recorder's place and stops working renamed") ? ScsKiller.NotChainableReason(gameState.RecorderMod) : (gameState.RecorderSkip ?? gameState.RecorderNote));
				Console.WriteLine(text5 + ((text6 != null) ? (" (" + text6 + ")") : ""));
				return 0;
			}
			if (args[1] == "install")
			{
				scsKiller.InstallRecorder(g.Game.Id);
			}
			else
			{
				scsKiller.UninstallRecorder(g.Game.Id);
			}
			Console.WriteLine("recorder " + args[1] + "ed: " + Path.GetDirectoryName(g.Game.ExePath));
			return 0;
		}
		async Task<int> ReferenceExport()
		{
			if (args.Length < 3)
			{
				return Fail("reference export <game> [--out <file>]");
			}
			(ScsKiller, GameState, IEngineReader, EngineInfo, ShaderIndex) tuple = await OpenIndex(args[2]);
			ScsKiller item = tuple.Item1;
			GameState item2 = tuple.Item2;
			ShaderIndex index = tuple.Item5;
			string path = Path.Combine(item.Store.GameDir(item2.Game.Id), "recording.db");
			IEnumerable<string> enumerable2;
			if (!File.Exists(path))
			{
				IEnumerable<string> enumerable = Array.Empty<string>();
				enumerable2 = enumerable;
			}
			else
			{
				enumerable2 = from r in PsoDb.Read(path)
					where r.Tag == 'B' && r.Payload.Length > 20 && !Dxbc.IsRootSignatureOnly(r.Payload.AsSpan(20))
					select Convert.ToHexStringLower(r.Payload.AsSpan(0, 20));
			}
			IEnumerable<string> second = enumerable2;
			string[] shaders = index.Shaders.Keys.Concat(second).Distinct().Order(StringComparer.Ordinal)
				.ToArray();
			string outFile = Opt("--out") ?? (item2.Game.Id.Replace(':', '_') + ".reference.json");
			string contentHash = index.ContentHash;
			string version = item2.Game.Version;
			await File.WriteAllTextAsync(outFile, JsonSerializer.Serialize(new
			{
				content_hash = contentHash,
				store_build_key = ((version != null) ? (item2.Game.Id + "@" + version) : null),
				shaders = shaders
			}));
			Console.WriteLine($"{shaders.Length} shaders ({index.Shaders.Count} indexed), content hash {index.ContentHash} -> {outFile}");
			return 0;
		}
		async Task<int> RehydrateCommand()
		{
			if (args.Length >= 3)
			{
				string outDb = Opt("--out");
				if (outDb != null)
				{
					(ScsKiller, GameState, IEngineReader, EngineInfo, ShaderIndex) tuple = await OpenIndex(args[1]);
					GameState g = tuple.Item2;
					IEngineReader item = tuple.Item3;
					EngineInfo item2 = tuple.Item4;
					ShaderIndex item3 = tuple.Item5;
					Console.WriteLine("index content hash " + item3.ContentHash);
					RehydrateResult rehydrateResult = Rehydrate.Run(args[2], outDb, g.Game, item2, item, item3, Opt("--expect"), default, (IReadOnlySet<string> h) => Middleware.Blobs(g.Game, h));
					string text3 = $"referenced {rehydrateResult.Referenced}, already in db {rehydrateResult.AlreadyPresent}, found {rehydrateResult.Found} ({rehydrateResult.FoundInMiddleware} in middleware DLLs), missing {rehydrateResult.Missing.Count}";
					bool? contentHashMatches = rehydrateResult.ContentHashMatches;
					string text4;
					if (contentHashMatches.HasValue)
					{
						bool valueOrDefault = contentHashMatches == true;
						text4 = ", content hash " + (valueOrDefault ? "matches" : "DIFFERS");
					}
					else
					{
						text4 = "";
					}
					Console.WriteLine(text3 + text4 + $" -> {outDb} ({rehydrateResult.OutputBytes:N0} bytes)");
					foreach (string item6 in rehydrateResult.Missing)
					{
						Console.WriteLine("  missing " + item6);
					}
					return (!rehydrateResult.Complete) ? 2 : 0;
				}
			}
			return Fail("rehydrate <game> <hash-only.db> --out <db> [--expect <content hash>]");
		}
		static async Task<int> RewarmStale(bool ifDriverChanged)
		{
			ScsKiller scsKiller = await Open();
			IReadOnlyList<GameState> readOnlyList = (ifDriverChanged ? scsKiller.DriverStaleGames() : scsKiller.StaleGames());
			if (readOnlyList.Count == 0)
			{
				Console.WriteLine("nothing to re-warm");
				return 0;
			}
			switch (scsKiller.Settings.OnDriverUpdate)
			{
			case DriverUpdateMode.Off:
				Console.WriteLine($"{readOnlyList.Count} game(s) need a re-warm; automatic re-warm is off.");
				return 0;
			case DriverUpdateMode.Ask:
			{
				if (!scsKiller.ShouldNotifyStale())
				{
					Console.WriteLine($"{readOnlyList.Count} game(s) need a re-warm; the user skipped them for this driver.");
					return 0;
				}
				string text3 = ScheduledTask.AppExe();
				if (text3 != null)
				{
					Process.Start(new ProcessStartInfo(text3, "--driver-updated")
					{
						UseShellExecute = false
					})?.Dispose();
					Console.WriteLine($"{readOnlyList.Count} game(s) need a re-warm: notified via {text3}");
					return 0;
				}
				Console.WriteLine($"{readOnlyList.Count} game(s) need a re-warm (driver {scsKiller.Vendor.Gpu.DriverVersion}):");
				foreach (GameState item7 in readOnlyList)
				{
					Row(item7);
				}
				Console.WriteLine("Run 'scskiller compile <game>' or open SCSKiller to re-warm them.");
				return 0;
			}
			default:
				BackgroundPriority();
				return await RunQueue(scsKiller, ifDriverChanged ? scsKiller.IdleRewarmGames() : readOnlyList, whenIdle: true);
			}
		}
		static void Row(GameState s)
		{
			DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(6, 9);
			defaultInterpolatedStringHandler.AppendFormatted<string>(StatusName(s), -19);
			defaultInterpolatedStringHandler.AppendLiteral(" ");
			defaultInterpolatedStringHandler.AppendFormatted<string>(s.Game.Id, -24);
			defaultInterpolatedStringHandler.AppendLiteral(" ");
			defaultInterpolatedStringHandler.AppendFormatted(s.Game.Name);
			defaultInterpolatedStringHandler.AppendFormatted((s.AntiCheat != AntiCheat.None) ? $"  [{s.AntiCheat}]" : "");
			KnownStutter knownStutter = s.KnownStutter;
			defaultInterpolatedStringHandler.AppendFormatted(((object)knownStutter != null) ? ("  [known stutter: " + knownStutter.Severity.ToString().ToLowerInvariant() + "]") : "");
			defaultInterpolatedStringHandler.AppendFormatted((s.NoStutter != null) ? "  [no shader stutter]" : "");
			defaultInterpolatedStringHandler.AppendFormatted(s.RecorderInstalled ? "  [recorder]" : "");
			long? cacheOnDisk = s.CacheOnDisk;
			string value;
			if (cacheOnDisk.HasValue)
			{
				long valueOrDefault = cacheOnDisk.GetValueOrDefault();
				value = "  [cache " + Format.Bytes(valueOrDefault) + "]";
			}
			else
			{
				value = "";
			}
			defaultInterpolatedStringHandler.AppendFormatted(value);
			defaultInterpolatedStringHandler.AppendLiteral("  - ");
			defaultInterpolatedStringHandler.AppendFormatted(ScsKiller.NeverRecorded(s) ? ("Played, nothing recorded. " + s.RecorderRefused) : s.StatusReason);
			Console.WriteLine(defaultInterpolatedStringHandler.ToStringAndClear());
		}
		static async Task<int> RunQueue(ScsKiller k, IReadOnlyList<GameState> targets, bool whenIdle = false)
		{
			if (targets.Count == 0)
			{
				Console.WriteLine("nothing to compile");
				return 0;
			}
			PrintQueueChanges(k);
			bool cancelled = false;
			Console.CancelKeyPress += (object? _, ConsoleCancelEventArgs e) =>
			{
				e.Cancel = true;
				cancelled = true;
				Console.WriteLine("stopping (in-flight compiles finish so the driver writes its cache)...");
				foreach (QueueItem item8 in k.Queue.Where((QueueItem q) => q.Stage == QueueStage.Waiting))
				{
					k.Remove(item8.GameId);
				}
				k.StopQueue();
			};
			foreach (GameState target in targets)
			{
				if (whenIdle)
				{
					k.EnqueueWhenIdle(target.Game.Id);
				}
				else
				{
					k.Enqueue(target.Game.Id);
				}
			}
			if (whenIdle)
			{
				Console.WriteLine($"waiting until the PC has been idle for {k.IdleAfter.TotalMinutes:0} min (pauses on input)");
			}
			else
			{
				k.StartQueue();
			}
			await k.WhenQueueIdle();
			return (cancelled || k.Queue.Count <= 0 || !k.Queue.All((QueueItem q) => q.Stage == QueueStage.Done)) ? 1 : 0;
		}
		IGpuVendorBackend RunningGpu()
		{
			string text3 = Opt("--adapter-luid");
			if (text3 == null)
			{
				return ScsKiller.DetectGpu();
			}
			if (long.TryParse(text3, NumberStyles.HexNumber, null, out var luid))
			{
				DxgiAdapter dxgiAdapter = GpuBackends.Adapters().FirstOrDefault((DxgiAdapter a) => a.Gpu.AdapterLuid == luid);
				if ((object)dxgiAdapter != null)
				{
					return ScsKiller.DetectGpu(GpuBackends.ChoiceOf(dxgiAdapter));
				}
			}
			throw new InvalidOperationException("the GPU SCSKiller runs on isn't listed any more (a driver reload): restart SCSKiller and try again");
		}
		static async Task<int> Scan(bool rescan)
		{
			ScsKiller scsKiller = await Open(rescan);
			Console.WriteLine($"GPU: {scsKiller.Vendor.Gpu.Name}, driver {scsKiller.Vendor.Gpu.DriverVersion} ({scsKiller.Vendor.Caps.Profile})");
			foreach (GameState game3 in scsKiller.Games)
			{
				Row(game3);
			}
			return 0;
		}
		static async Task<int> Status(string? query)
		{
			ScsKiller scsKiller = await Open();
			if (query == null)
			{
				foreach (GameState game4 in scsKiller.Games)
				{
					Row(game4);
					IReadOnlyList<MiddlewareTag> middleware = game4.Middleware;
					if (middleware != null && middleware.Count > 0)
					{
						Console.WriteLine($"{"",15}middleware: {MiddlewareLine(middleware, dlls: false)}");
					}
				}
				return 0;
			}
			GameState gameState = Match(scsKiller.Games, query);
			GameRecord gameRecord = scsKiller.Store.LoadGame(gameState.Game.Id);
			DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(334, 29);
			defaultInterpolatedStringHandler.AppendLiteral("");
			defaultInterpolatedStringHandler.AppendFormatted(gameState.Game.Name);
			defaultInterpolatedStringHandler.AppendLiteral(" (");
			defaultInterpolatedStringHandler.AppendFormatted(gameState.Game.Id);
			defaultInterpolatedStringHandler.AppendLiteral(", ");
			defaultInterpolatedStringHandler.AppendFormatted(gameState.Game.Store);
			defaultInterpolatedStringHandler.AppendLiteral(")\r\n  install    ");
			defaultInterpolatedStringHandler.AppendFormatted(gameState.Game.InstallDir);
			defaultInterpolatedStringHandler.AppendLiteral("\r\n  exe        ");
			defaultInterpolatedStringHandler.AppendFormatted(gameState.Game.ExePath);
			defaultInterpolatedStringHandler.AppendLiteral("\r\n  runs as    ");
			string launchedExeName = gameRecord.LaunchedExeName;
			defaultInterpolatedStringHandler.AppendFormatted((launchedExeName != null) ? $"{launchedExeName} (seen {gameRecord.LaunchedExeSeenAt:yyyy-MM-dd HH:mm})" : "not seen yet");
			defaultInterpolatedStringHandler.AppendLiteral("; warms stage ");
			defaultInterpolatedStringHandler.AppendFormatted(ScsKiller.WarmExeName(gameState.Game, gameRecord));
			defaultInterpolatedStringHandler.AppendLiteral("\r\n  engine     ");
			EngineInfo engine = gameState.Engine;
			defaultInterpolatedStringHandler.AppendFormatted(((object)engine != null) ? (string.Join(' ', new string[3]
			{
				Format.Engine(engine) + (engine.VersionGuessed ? " (guessed)" : ""),
				engine.Fork,
				engine.GraphicsApi
			}.OfType<string>()) + (engine.Encrypted ? " encrypted" : "")) : "-");
			defaultInterpolatedStringHandler.AppendLiteral("\r\n  status     ");
			defaultInterpolatedStringHandler.AppendFormatted(StatusName(gameState));
			defaultInterpolatedStringHandler.AppendLiteral(": ");
			defaultInterpolatedStringHandler.AppendFormatted(gameState.StatusReason);
			defaultInterpolatedStringHandler.AppendLiteral("\r\n  anti-cheat ");
			defaultInterpolatedStringHandler.AppendFormatted(gameState.AntiCheat);
			defaultInterpolatedStringHandler.AppendLiteral("\r\n  shaders    ");
			defaultInterpolatedStringHandler.AppendFormatted(gameState.ShaderCount?.ToString() ?? "-");
			defaultInterpolatedStringHandler.AppendLiteral("\r\n  plan       ");
			PlanStats plan = gameState.Plan;
			defaultInterpolatedStringHandler.AppendFormatted(((object)plan != null) ? $"{plan.Recorded} recorded + {plan.Generated} generated + {plan.D3D11Shaders} DirectX 11 shaders{((plan.D3D11Draws > 0) ? $" ({plan.D3D11Draws} vertex shaders with recorded layouts)" : "")} + {plan.MiddlewareItems} middleware, {plan.RootSignatures} root sigs, rule verified: {plan.RootSigRuleVerified}" : "-");
			defaultInterpolatedStringHandler.AppendLiteral("\r\n  stutter    ");
			KnownStutter knownStutter = gameState.KnownStutter;
			object value;
			if ((object)knownStutter == null)
			{
				string noStutter = gameState.NoStutter;
				value = ((noStutter != null) ? ("none: " + noStutter) : "-");
			}
			else
			{
				value = $"known, {knownStutter.Severity.ToString().ToLowerInvariant()}: {knownStutter.Reason} ({knownStutter.Source}, checked {knownStutter.Date})";
			}
			defaultInterpolatedStringHandler.AppendFormatted((string?)value);
			defaultInterpolatedStringHandler.AppendLiteral("\r\n  middleware ");
			IReadOnlyList<MiddlewareTag> middleware2 = gameState.Middleware;
			defaultInterpolatedStringHandler.AppendFormatted((middleware2 != null && middleware2.Count > 0) ? MiddlewareLine(middleware2, dlls: true) : "-");
			defaultInterpolatedStringHandler.AppendLiteral("\r\n  estimate   ");
			long? estimatedCacheBytes = gameState.EstimatedCacheBytes;
			string value2;
			if (estimatedCacheBytes.HasValue)
			{
				long valueOrDefault = estimatedCacheBytes.GetValueOrDefault();
				value2 = Format.Bytes(valueOrDefault);
			}
			else
			{
				value2 = "-";
			}
			defaultInterpolatedStringHandler.AppendFormatted(value2);
			defaultInterpolatedStringHandler.AppendLiteral(" of cache, ");
			defaultInterpolatedStringHandler.AppendFormatted(Hms(gameState.EstimatedWarmTime));
			defaultInterpolatedStringHandler.AppendLiteral("\r\n  careful    ");
			defaultInterpolatedStringHandler.AppendFormatted(CarefulLine(gameState.Careful, gameState.Plan));
			defaultInterpolatedStringHandler.AppendLiteral("\r\n  warmed     ");
			DateTimeOffset? warmedAt = gameState.WarmedAt;
			string value3;
			if (warmedAt.HasValue)
			{
				DateTimeOffset valueOrDefault2 = warmedAt.GetValueOrDefault();
				string text3 = $"{valueOrDefault2:yyyy-MM-dd HH:mm} for driver {gameState.WarmedDriverVersion} in {Hms(gameState.LastWarmTime)}";
				string text4 = ScsKiller.WarmCounts(gameState.LastWarmFailed.GetValueOrDefault(), gameState.LastWarmSkipped.GetValueOrDefault(), gameState.LastWarmCrashed.GetValueOrDefault(), gameState.LastWarmDriverSkipped.GetValueOrDefault(), gameState.LastWarmUnbuilt.GetValueOrDefault());
				value3 = text3 + ((text4 != null) ? ("; " + text4) : "");
			}
			else
			{
				value3 = "never";
			}
			defaultInterpolatedStringHandler.AppendFormatted(value3);
			defaultInterpolatedStringHandler.AppendLiteral("\r\n  cache      ");
			estimatedCacheBytes = gameState.CacheOnDisk;
			string value4;
			if (estimatedCacheBytes.HasValue)
			{
				long valueOrDefault3 = estimatedCacheBytes.GetValueOrDefault();
				value4 = Format.Bytes(valueOrDefault3) + " on disk (driver-cache files its warms or the game had open)";
			}
			else
			{
				value4 = "-";
			}
			defaultInterpolatedStringHandler.AppendFormatted(value4);
			defaultInterpolatedStringHandler.AppendLiteral("\r\n  keys       ");
			defaultInterpolatedStringHandler.AppendFormatted((gameRecord.CacheKeys.Count > 0) ? string.Join(", ", gameRecord.CacheKeys.Order()) : "-");
			AgsRegistration agsRegistration = scsKiller.WarmAgs(gameState.Game.Id);
			string? value5;
			if ((object)agsRegistration != null)
			{
				value5 = $" (compiles register its AGS app name {agsRegistration.App}, key {scsKiller.AgsKey(gameState.Game.Id)}: the exe name's case doesn't matter)";
			}
			else
			{
				value5 = ((AmdAppCache.IsNameHashed(gameRecord.CacheKeys, ScsKiller.WarmExeName(gameState.Game, gameRecord)) == false) ? " (an app profile's key, not the exe name's hash: the name's case doesn't matter)" : "");
			}
			defaultInterpolatedStringHandler.AppendFormatted(value5);
			defaultInterpolatedStringHandler.AppendLiteral("\r\n  recorder   ");
			defaultInterpolatedStringHandler.AppendFormatted(gameState.RecorderInstalled ? "installed" : "not installed");
			string recorderRefused = gameState.RecorderRefused;
			defaultInterpolatedStringHandler.AppendFormatted((recorderRefused != null) ? ("; " + (ScsKiller.NeverRecorded(gameState) ? "Played, nothing recorded. " : "") + recorderRefused) : "");
			defaultInterpolatedStringHandler.AppendLiteral("\r\n  recording  ");
			defaultInterpolatedStringHandler.AppendFormatted((gameState.RecordingBytes > 0) ? (((gameState.RecordingBytes < 1048576) ? $"{Math.Max(1L, gameState.RecordingBytes >> 10)} KB" : Format.Bytes(gameState.RecordingBytes)) + " (the game folder's files and SCSKiller's copy)") : "-");
			defaultInterpolatedStringHandler.AppendLiteral(", limit ");
			defaultInterpolatedStringHandler.AppendFormatted(ScsKiller.LimitText(scsKiller.Settings.RecordingLimitMB));
			defaultInterpolatedStringHandler.AppendLiteral(" per game");
			defaultInterpolatedStringHandler.AppendFormatted(gameState.RecordingPaused ? ("; " + ScsKiller.PausedNote(scsKiller.Settings)) : "");
			defaultInterpolatedStringHandler.AppendLiteral("\r\n  last play  ");
			SessionStats lastSession = gameState.LastSession;
			defaultInterpolatedStringHandler.AppendFormatted(((object)lastSession != null) ? $"{Hms(lastSession.Duration)}, {lastSession.Requests} pipelines: {lastSession.FromGameLibrary} from the game's library, {lastSession.CacheHits} cache hits, {lastSession.Compiles} compiles (worst {lastSession.WorstCompileMs:0.0} ms){((lastSession.StartupCompiles > 0) ? $", {lastSession.StartupCompiles} compiles while the game started or loaded" : "")}{((lastSession.RayQueryRecompiles > 0) ? $", {lastSession.RayQueryRecompiles} ray-traced pipelines the driver partly recompiles every launch" : "")}{((lastSession.StateObjectsReady + lastSession.StateObjectsCompiled + lastSession.StateObjectsStartupCompiled > 0) ? $"; ray tracing state objects: {lastSession.StateObjectsReady} ready, {lastSession.StateObjectsCompiled} compiled{((lastSession.StateObjectsStartupCompiled > 0) ? $", {lastSession.StateObjectsStartupCompiled} compiled while the game started or loaded" : "")}" : "")}" : "-");
			defaultInterpolatedStringHandler.AppendLiteral("\r\n  1st launch ");
			LaunchCheck firstLaunch = gameRecord.FirstLaunch;
			defaultInterpolatedStringHandler.AppendFormatted(((object)firstLaunch != null) ? $"{firstLaunch.At.LocalDateTime:yyyy-MM-dd HH:mm}, after the last compile: {firstLaunch.Hits} cache hits, {firstLaunch.Compiles} compiles ({firstLaunch.Compiled * 100.0:0.0}% compiled)" : "-");
			Console.WriteLine(defaultInterpolatedStringHandler.ToStringAndClear());
			return 0;
		}
		static string StatusName(GameState s)
		{
			if (!ScsKiller.NeedsOfflineSession(s))
			{
				return s.Status.ToString();
			}
			return "NeedsOfflineSession";
		}
		static IEnumerable<string> StdinLines()
		{
			while (true)
			{
				string text3 = Console.ReadLine();
				if (text3 == null)
				{
					break;
				}
				yield return text3;
			}
		}
		int TaskCommand()
		{
			string text3 = args.ElementAtOrDefault(1);
			if (text3 == "register")
			{
				ScheduledTask.Register(ScheduledTask.TaskExe() ?? Environment.ProcessPath);
				Console.WriteLine("registered task SCSKiller\\RewarmStale");
				return 0;
			}
			if (text3 == "unregister")
			{
				ScheduledTask.Unregister();
				Console.WriteLine("removed task SCSKiller\\RewarmStale");
				return 0;
			}
			return Fail("task register|unregister");
		}
	}
}
