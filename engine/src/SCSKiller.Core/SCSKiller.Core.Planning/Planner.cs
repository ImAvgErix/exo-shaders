using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using SCSKiller.Core.FromSoft;
using SCSKiller.Core.RedEngine;

namespace SCSKiller.Core.Planning;

public sealed class Planner(string? packDir = null, string? sharedPackDir = null) : IPlanner
{
	public const int Version = 42;

	public const string D3D11VsRecord = "a 5-min recording covers those";

	public const string NoRecording = "no recording needed";

	public const string Untested = "not tested on this engine version yet: playing with recording on improves it";

	public const string UntestedNote = "not tested on this engine version yet";

	public const string Record = "turn on recording and play for about 5 minutes";

	public const string Dx12Only = "helps only when played on DirectX 12 (the game may run on DirectX 11)";

	public const double MinRulesShare = 0.9;

	public const long MaxRulesItems = 1000000L;

	private readonly ConditionalWeakTable<Plan, string> seeded = new ConditionalWeakTable<Plan, string>();

	public const string SkippedFile = "skipped.txt";

	private static readonly int[] FromPrevStage = new int[4] { 2, 3, 4, 5 };

	private static readonly Dictionary<int, uint[]> Fmt = new Dictionary<int, uint[]>
	{
		[3] = new uint[4] { 41u, 16u, 6u, 2u },
		[1] = new uint[4] { 42u, 17u, 7u, 3u },
		[2] = new uint[4] { 43u, 18u, 8u, 4u }
	};

	public MiddlewarePacks? Packs { get; } = (packDir == null) ? null : new MiddlewarePacks(packDir);

	public MiddlewarePacks? SharedPacks { get; } = (sharedPackDir == null) ? null : new MiddlewarePacks(sharedPackDir);

	public IProgress<string>? Log { get; set; }

	public RulesFile? Rules { get; init; }

	public long? MemoryBudget { get; init; }

	public TimeSpan? TimeBudget { get; init; }

	public long? CacheBudget { get; init; }

	public string PackFingerprint(Game game, string? shared = null)
	{
		string text = Packs?.Fingerprint(game);
		string text2 = shared ?? SharedFingerprint(game);
		return text + ((text2 != null && text2.Length > 0) ? ("|shared:" + text2) : "");
	}

	public string SharedFingerprint(Game game)
	{
		return SharedPacks?.Fingerprint(game) ?? "";
	}

	public static bool SeedsPacks(EngineInfo engine)
	{
		return engine.GraphicsApi.Contains("D3D12");
	}

	public int PackPipelines(MiddlewareDll dll, bool? amd = null)
	{
		return new MiddlewarePacks[2] { Packs, SharedPacks }.SelectMany((MiddlewarePacks p) => p?.Keys(dll, amd) ?? Array.Empty<string>()).Distinct().Count();
	}

	internal static bool D3D11Cache(VendorCaps caps)
	{
		return D3D11Stages(caps) != null;
	}

	public static Stage[]? D3D11Stages(VendorCaps caps)
	{
		switch (caps.Profile)
		{
		case "nvidia-1":
		case "intel-1":
			return new Stage[6]
			{
				Stage.Vertex,
				Stage.Pixel,
				Stage.Domain,
				Stage.Hull,
				Stage.Geometry,
				Stage.Compute
			};
		case "amd-1":
			return new Stage[2]
			{
				Stage.Pixel,
				Stage.Compute
			};
		default:
			return null;
		}
	}

	public static bool D3D11CoversVs(VendorCaps caps)
	{
		Stage[]? array = D3D11Stages(caps);
		if (array == null)
		{
			return true;
		}
		return array.Contains(Stage.Vertex, null);
	}

	internal static bool RtCollectionCache(VendorCaps caps)
	{
		return caps.RtCacheGranularity == RtCacheGranularity.Collection;
	}

	public static bool LeftOutWithoutRt(PsoDb.Rec r)
	{
		bool flag = PsoDb.IsStateObject(r.Tag);
		if (!flag)
		{
			char tag = r.Tag;
			bool flag2 = ((tag == 'H' || tag == 'Y') ? true : false);
			flag = flag2;
		}
		return flag;
	}

	internal static bool IsD3D11(ShaderInfo s)
	{
		bool flag = s.Stage <= Stage.Compute;
		if (flag)
		{
			string shaderModel = s.ShaderModel;
			bool flag2;
			switch (shaderModel.Substring(shaderModel.Length - 3))
			{
			case "4_0":
			case "4_1":
			case "5_0":
				flag2 = true;
				break;
			default:
				flag2 = false;
				break;
			}
			flag = flag2;
		}
		return flag;
	}

	public PlanCheck Check(Game game, EngineInfo engine, Recording? recording, VendorCaps caps)
	{
		if (engine.Unsupported != null)
		{
			return new PlanCheck(Readiness.Unsupported, engine.Unsupported);
		}
		if (engine.Encrypted)
		{
			return new PlanCheck(Readiness.Unsupported, "encrypted game files");
		}
		string graphicsApi = engine.GraphicsApi;
		bool flag = graphicsApi.StartsWith("D3D11") && D3D11Cache(caps);
		bool flag2 = !D3D11CoversVs(caps) && (!flag || !(recording != null) || !HasDraws11(recording));
		string text = "compiles every DirectX 11 shader" + (flag2 ? " but the vertex shaders" : "");
		if (!graphicsApi.Contains("D3D12"))
		{
			if (!flag)
			{
				return new PlanCheck(Readiness.Unsupported, "runs on " + (graphicsApi.StartsWith("D3D11") ? ("DirectX 11" + graphicsApi.Substring(5)) : graphicsApi));
			}
			return new PlanCheck(Readiness.Ready, text + graphicsApi.Substring(5) + (flag2 ? "; a 5-min recording covers those" : ""));
		}
		PlanCheck planCheck = CheckD3D12(engine, recording, caps, (graphicsApi.StartsWith("D3D12") | flag) ? "" : "; helps only when played on DirectX 12 (the game may run on DirectX 11)");
		if (!flag)
		{
			return planCheck;
		}
		return new PlanCheck(Readiness.Ready, (planCheck.Readiness == Readiness.Ready) ? (planCheck.Reason + "; also " + text + " (the game may run on either)") : (text + " (the game may run on either); for DirectX 12, " + planCheck.Reason));
	}

	private static PlanCheck CheckD3D12(EngineInfo engine, Recording? recording, VendorCaps caps, string maybe)
	{
		if (!caps.CacheKeyedByExeName)
		{
			return new PlanCheck(Readiness.Unsupported, "not supported on this GPU yet");
		}
		if (caps.StateIndependentCache && (RootSig.Verified(engine) || engine.Version.EndsWith("+RTS0")))
		{
			return new PlanCheck(Readiness.Ready, "no recording needed" + maybe);
		}
		if (recording != null && File.Exists(recording.DbPath) && new FileInfo(recording.DbPath).Length > 0)
		{
			if (!caps.StateIndependentCache && !HasDraws(recording))
			{
				return new PlanCheck(Readiness.NeedsRecording, "the recording has no draws: play into the game world" + maybe);
			}
			return new PlanCheck(Readiness.Ready, "planned from a recording" + maybe);
		}
		if (caps.StateIndependentCache && RootSig.RuleFor(engine) != null)
		{
			return new PlanCheck(Readiness.Ready, "not tested on this engine version yet: playing with recording on improves it" + maybe);
		}
		return new PlanCheck(Readiness.NeedsRecording, "turn on recording and play for about 5 minutes" + maybe);
	}

	private static bool HasDraws11(Recording recording)
	{
		if (File.Exists(recording.DbPath))
		{
			return PsoDb.Read(recording.DbPath).Any((PsoDb.Rec r) => r.Tag == 'D');
		}
		return false;
	}

	public static string? RecordedApi(string db)
	{
		try
		{
			bool flag = false;
			foreach (PsoDb.Rec item in PsoDb.Read(db))
			{
				char tag = item.Tag;
				if ((tag == 'C' || tag == 'G' || tag == 'S') ? true : false)
				{
					return "D3D12";
				}
				flag |= item.Tag == 'D';
			}
			return flag ? "D3D11" : null;
		}
		catch (Exception ex) when ((ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException) ? true : false)
		{
			return null;
		}
	}

	private static bool HasDraws(Recording recording)
	{
		foreach (PsoDb.Rec item in PsoDb.Read(recording.DbPath))
		{
			try
			{
				char tag = item.Tag;
				bool flag = ((tag == 'G' || tag == 'S') ? true : false);
				if (flag && PsoDb.Parse(item).Stages.ContainsKey(1))
				{
					return true;
				}
			}
			catch (Exception ex) when ((ex is InvalidDataException || ex is ArgumentException || ex is KeyNotFoundException) ? true : false)
			{
			}
		}
		return false;
	}

	public Plan Build(Game game, EngineInfo engine, ShaderIndex index, Recording? recording, VendorCaps caps, string outDir, IProgress<string>? log, CancellationToken ct, bool maximum = false, bool rayTracing = true)
	{
		try
		{
			return Build(game, engine, index, recording, caps, outDir, log, ct, maximum, rayTracing, pairs: true);
		}
		catch (PlanTooBigException ex)
		{
			ForgetMemos();
			log?.Report("warning: " + ex.Message + ": planning only the recorded pipelines");
			Plan plan = Build(game, engine, index, recording, caps, outDir, log, ct, maximum, rayTracing, pairs: false);
			if (plan.Stats.Recorded + plan.Stats.MiddlewareItems > 0)
			{
				return plan;
			}
			File.Delete(plan.FilePath);
			throw;
		}
		finally
		{
			ForgetMemos();
		}
	}

	private Plan Build(Game game, EngineInfo engine, ShaderIndex index, Recording? recording, VendorCaps caps, string outDir, IProgress<string>? log, CancellationToken ct, bool maximum, bool rayTracing, bool pairs)
	{
		PlanBudget budget = new PlanBudget(MemoryBudget ?? PlanBudget.DefaultMemory(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes), TimeBudget ?? PlanBudget.DefaultTime);
		RulesFile rulesFile = Rules ?? RulesFile.Current;
		if (rulesFile.Hash(engine) == RulesFile.Embedded.Hash(engine))
		{
			return Kept(One(rulesFile, outDir, log));
		}
		(Plan, string) p;
		try
		{
			p = One(rulesFile, outDir, log);
		}
		catch (Exception ex) when ((!(ex is OperationCanceledException) && !(ex is PlanTooBigException)) || 1 == 0)
		{
			Keep("build no plan (" + ex.Message + ")");
			return Kept(One(RulesFile.Embedded, outDir, log));
		}
		string text = Path.Combine(outDir, "embedded-rules");
		try
		{
			(Plan, string) tuple;
			try
			{
				tuple = One(RulesFile.Embedded, text, null);
			}
			catch
			{
				try
				{
					File.Delete(p.Item1.FilePath);
				}
				catch (Exception ex2) when ((ex2 is IOException || ex2 is UnauthorizedAccessException) ? true : false)
				{
				}
				throw;
			}
			long generated = p.Item1.Stats.Generated;
			long generated2 = tuple.Item1.Stats.Generated;
			long num = generated;
			string text2;
			if ((double)num < 0.9 * (double)generated2)
			{
				text2 = $"under {0.9:P0} of the {generated2:N0} the app's own rules plan";
			}
			else
			{
				text2 = ((num > Math.Max(1000000L, generated2)) ? $"over the cap of {1000000L:N0}" : null);
			}
			if (text2 == null)
			{
				return Kept(p);
			}
			Keep($"plan {num:N0} item{((num == 1) ? "" : "s")}, {text2}");
			File.Move(tuple.Item1.FilePath, p.Item1.FilePath, overwrite: true);
			return Kept((Plan: tuple.Item1 with
			{
				FilePath = p.Item1.FilePath
			}, Fingerprint: tuple.Item2));
		}
		finally
		{
			if (Directory.Exists(text))
			{
				Directory.Delete(text, recursive: true);
			}
		}
		void Keep(string why)
		{
			log?.Report($"warning: planning rules: the server's rules for {engine.Family} {engine.Version} {why}: this game keeps the app's own rules");
		}
		Plan Kept((Plan Plan, string? Fingerprint) tuple2)
		{
			log?.Report(budget.ToString());
			string item = tuple2.Fingerprint;
			if (item != null)
			{
				seeded.AddOrUpdate(tuple2.Plan, item);
			}
			return tuple2.Plan;
		}
		(Plan Plan, string? Fingerprint) One(RulesFile r, string dir, IProgress<string>? l)
		{
			PlanBuilder planBuilder = new PlanBuilder(game, engine, index, recording, caps, dir, l, ct, maximum, Packs, SharedPacks, r, budget, CacheBudget, rayTracing, pairs);
			return (Plan: planBuilder.Build(), Fingerprint: planBuilder.PackFingerprint);
		}
	}

	public string? SeededFingerprint(Plan plan)
	{
		if (!seeded.TryGetValue(plan, out string value))
		{
			return null;
		}
		return value;
	}

	internal static (uint[] Rt, uint Dsv) Targets(ShaderInfo ps)
	{
		IReadOnlyList<SigElement> outputs = ps.Outputs;
		List<SigElement> list = outputs.Where((SigElement o) => o.SysValue == 64 || o.Semantic.Equals("SV_Target", StringComparison.OrdinalIgnoreCase)).ToList();
		uint[] array = new uint[(list.Count != 0) ? (list.Max((SigElement o) => o.Index) + 1) : 0];
		foreach (SigElement item in list)
		{
			uint[] array2 = array;
			int index = item.Index;
			array2[index] = item.CompType switch
			{
				1 => 3u, 
				2 => 4u, 
				_ => 10u, 
			};
		}
		return (Rt: array, Dsv: outputs.Any((SigElement o) =>
		{
			int sysValue = o.SysValue;
			return (sysValue == 65 || (uint)(sysValue - 67) <= 1u) ? true : false;
		}) ? 40u : 0u);
	}

	public static List<PsoDb.Rec> PlanBody(string? planFile)
	{
		try
		{
			return (planFile != null && File.Exists(planFile)) ? PlanFile.Read(planFile).Records.ToList() : new List<PsoDb.Rec>();
		}
		catch (Exception ex) when ((ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is JsonException) ? true : false)
		{
			return new List<PsoDb.Rec>();
		}
	}

	public static IEnumerable<(string Key, PsoDb.Rec Rec)> PlanInputs(IReadOnlyList<PsoDb.Rec> body)
	{
		Dictionary<string, string> nv = new Dictionary<string, string>();
		foreach (PsoDb.Rec item in body.Where((PsoDb.Rec r) => r.Tag == 'N'))
		{
			nv[PsoDb.NvState.Parse(item).Target] = item.Key;
		}
		return from r in body.Where((PsoDb.Rec r) =>
			{
				char tag = r.Tag;
				bool flag = ((tag == 'B' || tag == 'N') ? true : false);
				return !flag;
			})
			select (nv.GetValueOrDefault(r.Key, r.Key), r: r);
	}

	public void Materialize(Plan plan, Game game, EngineInfo engine, IEngineReader reader, Recording? recording, string workDir, CancellationToken ct)
	{
		Directory.CreateDirectory(workDir);
		string text = Path.Combine(workDir, "scskiller.db");
		if (recording != null)
		{
			PsoDb.CopyRaw(recording.DbPath, text);
		}
		else
		{
			File.WriteAllBytes(text, default(ReadOnlySpan<byte>));
		}
		HashSet<string> inMain = new HashSet<string>();
		HashSet<string> hashSet = new HashSet<string>();
		foreach (PsoDb.Rec item in PsoDb.Read(text))
		{
			inMain.Add((item.Tag == 'B') ? PsoDb.Hex(item.Payload.AsSpan(0, 20)) : item.Key);
			if (item.Tag == 'L')
			{
				hashSet.Add(HashOnly.Target(item));
			}
		}
		List<PsoDb.Rec> source = PlanFile.Read(plan.FilePath).Records.ToList();
		List<PsoDb.Rec> list = source.Where((PsoDb.Rec r) =>
		{
			char tag = r.Tag;
			return ((tag == 'C' || tag == 'G' || tag == 'S') ? true : false) && !inMain.Contains(r.Key);
		}).ToList();
		List<RtCollections.ItemFields> list2 = (from r in source
			where r.Tag == 'Y'
			select RtCollections.ParseItem(r.Payload)).ToList();
		List<RedRayTracing.ItemFields> list3 = (from r in source
			where r.Tag == 'H'
			select RedRayTracing.ParseItem(r.Payload)).ToList();
		HashSet<string> rtLibs = list2.Select((RtCollections.ItemFields y) => y.Library).Concat(list3.SelectMany((RedRayTracing.ItemFields h) => new string[2] { h.ClosestHit, h.AnyHit }).OfType<string>()).ToHashSet();
		Dictionary<string, byte[]> rtBytes = new Dictionary<string, byte[]>();
		if (rtLibs.Count > 0)
		{
			foreach (PsoDb.Rec item2 in PsoDb.Read(text))
			{
				if (item2.Tag == 'B' && rtLibs.Contains(PsoDb.Hex(item2.Payload.AsSpan(0, 20))))
				{
					rtBytes[PsoDb.Hex(item2.Payload.AsSpan(0, 20))] = item2.Payload[20..];
				}
			}
		}
		HashSet<string> hashSet2 = list.Select(PsoDb.Parse).SelectMany((PsoDb.Pso t) => t.Stages.Values.Append(t.Rs)).Concat((from r in source
			where r.Tag == 'P'
			select PsoDb.ParseItem(r.Payload)).SelectMany(((string Template, string Rs, SortedDictionary<int, string> Stages) i) => i.Stages.Values.Append(i.Rs)))
			.Concat(rtLibs)
			.Concat(Rehydrate.References(from r in PsoDb.Read(text)
				where PsoDb.IsStateObject(r.Tag)
				select r))
			.ToHashSet();
		hashSet2.ExceptWith(inMain);
		hashSet2.ExceptWith(from r in source
			where r.Tag == 'B'
			select PsoDb.Hex(r.Payload.AsSpan(0, 20)));
		hashSet2.UnionWith(Rehydrate.References(source.Where((PsoDb.Rec r) =>
		{
			switch (r.Tag)
			{
			case '1':
			case '2':
			case '3':
				return true;
			default:
				return false;
			}
		})));
		hashSet2.Remove(PsoDb.Zero);
		BufferedStream gen = new BufferedStream(File.Create(Path.Combine(workDir, "scskiller_gen.db")), 1048576);
		HashSet<string> written;
		HashSet<string> usedTemplates;
		HashSet<string> keptKeys;
		try
		{
			HashSet<string> planBlobs = (from r in source
				where r.Tag == 'B'
				select PsoDb.Hex(r.Payload.AsSpan(0, 20))).ToHashSet();
			written = new HashSet<string>(inMain);
			foreach (PsoDb.Rec item3 in source.Where((PsoDb.Rec r) => r.Tag == 'B' && !inMain.Contains(PsoDb.Hex(r.Payload.AsSpan(0, 20)))))
			{
				PsoDb.Write(gen, 'B', item3.Payload);
				written.Add(PsoDb.Hex(item3.Payload.AsSpan(0, 20)));
			}
			reader.ReadShaders(game, engine, hashSet2, (string h, byte[] b) =>
			{
				lock (gen)
				{
					PsoDb.WriteBlob(gen, h, b);
					written.Add(h);
					if (rtLibs.Contains(h))
					{
						rtBytes[h] = b;
					}
				}
			}, ct);
			List<PsoDb.Rec> list4 = source.Where((PsoDb.Rec r) => r.Tag == 'M').ToList();
			long num = 0L;
			if (list4.Count > 0)
			{
				MiddlewarePacks.MaterializeResult materializeResult = MiddlewarePacks.Materialize(list4, game, planBlobs, written, inMain, gen);
				Log?.Report($"middleware packs: {materializeResult.Written} of {materializeResult.Entries} entries written, {materializeResult.AlreadyRecorded} already in the recording" + ((materializeResult.FilledFromDll > 0) ? $" ({materializeResult.FilledFromDll} with their shaders from the DLL)" : "") + ((materializeResult.Skipped > 0) ? $", {materializeResult.Skipped} skipped (shader not in this install's {string.Join(", ", materializeResult.SkippedByDll.Select((KeyValuePair<string, int> keyValuePair) => $"{keyValuePair.Key} ({keyValuePair.Value})"))})" : ""));
				num += materializeResult.Skipped - (materializeResult.DropFromMain?.Count ?? 0);
			}
			List<PsoDb.Rec> list5 = source.Where((PsoDb.Rec r) =>
			{
				switch (r.Tag)
				{
				case '1':
				case '2':
				case '3':
				case 'P':
					return true;
				default:
					return false;
				}
			}).ToList();
			List<PsoDb.Rec> list6 = list5.Where(Resolved).ToList();
			usedTemplates = (from r in list6
				where r.Tag == 'P'
				select PsoDb.ParseItem(r.Payload).Template).ToHashSet();
			long num2 = 0L;
			long num3 = 0L;
			long num4 = 0L;
			long num5 = 0L;
			keptKeys = new HashSet<string>();
			List<uint> list7 = new List<uint>();
			List<PsoDb.Rec> list8 = new List<PsoDb.Rec>();
			foreach (PsoDb.Rec item4 in PsoDb.Read(text))
			{
				num2++;
				RtCollections.Recorded recorded = RtCollections.Read(item4);
				if ((object)recorded != null)
				{
					list7.Add(recorded.Payload);
				}
				if (item4.Tag == 'N' || PsoDb.IsStateObject(item4.Tag))
				{
					list8.Add(item4);
				}
				if (Stays(item4))
				{
					num4++;
				}
				else if (item4.Tag == 'L')
				{
					num3++;
				}
				else if (hashSet.Contains(item4.Key))
				{
					num5++;
				}
			}
			if (num4 < num2)
			{
				keptKeys.Clear();
				string text2 = text + ".tmp";
				try
				{
					using (BufferedStream s = new BufferedStream(File.Create(text2), 1048576))
					{
						foreach (PsoDb.Rec item5 in PsoDb.Read(text))
						{
							if (Stays(item5))
							{
								PsoDb.Write(s, item5.Tag, item5.Payload);
							}
						}
					}
					File.Move(text2, text, overwrite: true);
				}
				finally
				{
					File.Delete(text2);
				}
			}
			List<PsoDb.Rec> list9 = list.Where(Keep).ToList();
			num += num2 - num3 - num4 + list.Count - list9.Count + list5.Count - list6.Count;
			foreach (PsoDb.Rec item6 in list9)
			{
				PsoDb.Write(gen, item6.Tag, item6.Payload);
			}
			foreach (PsoDb.Rec item7 in list6)
			{
				PsoDb.Write(gen, item7.Tag, item7.Payload);
			}
			HashSet<string> keptPsos = (from r in list9.Concat(list6)
				select r.Key).ToHashSet();
			foreach (PsoDb.Rec item8 in source.Where((PsoDb.Rec r) => r.Tag == 'N' && keptPsos.Contains(PsoDb.NvState.Parse(r).Target)))
			{
				PsoDb.Write(gen, item8.Tag, item8.Payload);
			}
			foreach (PsoDb.Rec item9 in from r in RtCollections.OptionFixes(list8)
				where keptKeys.Contains(PsoDb.NvState.Parse(r).Target)
				select r)
			{
				PsoDb.Write(gen, item9.Tag, item9.Payload);
			}
			uint options = RtCollections.RecordedOptions(list8);
			Dictionary<string, uint> own = list2.Select((RtCollections.ItemFields y) => y.Library).Distinct().Where(rtBytes.ContainsKey)
				.ToDictionary((string h) => h, (string h) => RtCollections.OwnPayload(rtBytes[h]));
			List<uint> pipelinePayloads = (from p in (from y in list2
					where y.Payload == 0
					select own.GetValueOrDefault(y.Library)).Concat(list7)
				where p != 0
				select p).Distinct().Order().ToList();
			foreach (RtCollections.ItemFields item10 in list2)
			{
				int num6 = 0;
				if (rtBytes.TryGetValue(item10.Library, out byte[] value))
				{
					foreach (uint item11 in RtCollections.Payloads(item10.Payload, own[item10.Library], pipelinePayloads))
					{
						byte[] array = RtCollections.Collection(value, item10.Library, item10.Global, item10.LocalRayGen, item10.LocalOther, item11, item10.Attributes, item10.Depth, item10.Flags);
						if (array != null)
						{
							PsoDb.Write(gen, 'R', array);
							RtCollections.Nv? nv = RtCollections.WithOptions(item10.Nv, options);
							if (nv.HasValue)
							{
								RtCollections.Nv valueOrDefault = nv.GetValueOrDefault();
								PsoDb.Rec rec = new PsoDb.NvState(new PsoDb.Rec('R', array).Key, valueOrDefault.Slot, valueOrDefault.Space, 2u, valueOrDefault.Options).ToRec();
								PsoDb.Write(gen, rec.Tag, rec.Payload);
							}
							num6++;
						}
					}
				}
				if (num6 == 0)
				{
					num++;
				}
			}
			foreach (RedRayTracing.ItemFields item12 in list3)
			{
				if (rtBytes.TryGetValue(item12.ClosestHit, out byte[] value2) && (item12.AnyHit == null || rtBytes.ContainsKey(item12.AnyHit)))
				{
					byte[] array2 = ((engine.Family == "FromSoftware") ? SoulsRayTracing.Collection(value2, item12, (item12.AnyHit != null) ? rtBytes[item12.AnyHit] : Array.Empty<byte>()) : RedRayTracing.Collection(value2, item12, (item12.AnyHit != null) ? rtBytes[item12.AnyHit] : Array.Empty<byte>()));
					if (array2 != null)
					{
						PsoDb.Write(gen, 'R', array2);
						RtCollections.Nv? nv = RtCollections.WithOptions(item12.Nv, options);
						if (nv.HasValue)
						{
							RtCollections.Nv valueOrDefault2 = nv.GetValueOrDefault();
							PsoDb.Rec rec2 = new PsoDb.NvState(new PsoDb.Rec('R', array2).Key, valueOrDefault2.Slot, valueOrDefault2.Space, 2u, valueOrDefault2.Options).ToRec();
							PsoDb.Write(gen, rec2.Tag, rec2.Payload);
						}
						continue;
					}
				}
				num++;
			}
			File.WriteAllText(Path.Combine(workDir, "skipped.txt"), $"{num} {num5}");
			if (num > 0)
			{
				Log?.Report($"{num} PSO{((num == 1) ? "" : "s")} skipped: a shader not in this install (not replayed, not counted as failed)" + ((num5 > 0) ? $"; {num5} of them flagged by the community recording: built at run time or by a mod, only a recording on this PC has them" : ""));
			}
		}
		finally
		{
			if (gen != null)
			{
				((IDisposable)gen).Dispose();
			}
		}
		bool Keep(PsoDb.Rec r)
		{
			if (!usedTemplates.Contains(r.Key))
			{
				return Resolved(r);
			}
			return true;
		}
		bool Resolved(PsoDb.Rec r)
		{
			return Rehydrate.References(new _003C_003Ez__ReadOnlySingleElementList<PsoDb.Rec>(r)).IsSubsetOf(written);
		}
		bool Stays(PsoDb.Rec r)
		{
			char tag = r.Tag;
			if ((tag == 'B' || tag == 'W') ? true : false)
			{
				return true;
			}
			if (r.Tag == 'L' || !Keep(r) || (PsoDb.IsStateObject(r.Tag) && !PsoDb.ParseStateObject(r).Depends.All(keptKeys.Contains)))
			{
				return false;
			}
			keptKeys.Add(r.Key);
			return true;
		}
	}

	public static long SkippedIn(string workDir)
	{
		return SkippedCount(workDir, 0);
	}

	public static long NeedsRecordingIn(string workDir)
	{
		return SkippedCount(workDir, 1);
	}

	private static long SkippedCount(string workDir, int at)
	{
		try
		{
			long result;
			return long.TryParse(File.ReadAllText(Path.Combine(workDir, "skipped.txt")).Split(' ').ElementAtOrDefault(at), out result) ? result : 0;
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return 0L;
		}
	}

	internal static uint TopologyType(int gsInput)
	{
		switch (gsInput)
		{
		case 1:
			return 1u;
		case 2:
		case 6:
			return 2u;
		case 3:
		case 7:
			return 3u;
		default:
			return 0u;
		}
	}

	internal static bool Rasterizable(ShaderInfo s)
	{
		if (s.Outputs.Count != 0)
		{
			return s.Outputs.Any((SigElement o) => o.SysValue == 1);
		}
		return true;
	}

	internal static bool Positioned(IReadOnlyDictionary<Stage, ShaderInfo> st)
	{
		ShaderInfo shaderInfo = (from x in new Stage[4]
			{
				Stage.Geometry,
				Stage.Domain,
				Stage.Mesh,
				Stage.Vertex
			}.Where(st.ContainsKey)
			select st[x]).FirstOrDefault();
		if ((object)shaderInfo != null)
		{
			return Rasterizable(shaderInfo);
		}
		return true;
	}

	internal static bool SameRs(params ShaderInfo[] s)
	{
		// Exo Shaders: was s.Select(RootSignature).OfType<string>().Distinct().Count() <= 1; the same test without
		// allocating, as it runs for every candidate pairing.
		string first = null;
		foreach (ShaderInfo x in s)
		{
			string rs = x.RootSignature;
			if (rs == null)
			{
				continue;
			}
			if (first == null)
			{
				first = rs;
			}
			else if (!string.Equals(first, rs))
			{
				return false;
			}
		}
		return true;
	}

	internal static bool SameRs(ShaderInfo a, ShaderInfo b)
	{
		string x = a.RootSignature;
		string y = b.RootSignature;
		return x == null || y == null || string.Equals(x, y);
	}

	internal static bool SameRs(ShaderInfo a, ShaderInfo b, ShaderInfo c)
	{
		return SameRs(a, b) && SameRs(a, c) && SameRs(b, c);
	}

	internal static void Push(Dictionary<string, List<ShaderInfo>> d, string k, ShaderInfo v)
	{
		if (!d.TryGetValue(k, out List<ShaderInfo> value))
		{
			value = (d[k] = new List<ShaderInfo>());
		}
		value.Add(v);
	}

	// Exo Shaders: a handful of distinct shapes, asked for once per stage set: the same strings, from a small table.
	private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, string> Shapes = new System.Collections.Concurrent.ConcurrentDictionary<long, string>();

	internal static string Shape(params Stage[] stages)
	{
		if (stages.Length > 8)
		{
			return string.Join('+', stages.Select((Stage s) => (int)s).Order());
		}
		Span<int> v = stackalloc int[stages.Length];
		for (int i = 0; i < stages.Length; i++)
		{
			int x = (int)stages[i];
			if ((uint)x >= 127u)
			{
				return string.Join('+', stages.Select((Stage s) => (int)s).Order());
			}
			v[i] = x;
		}
		v.Sort();
		long key = stages.Length;
		for (int i = 0; i < v.Length; i++)
		{
			key = (key << 7) | (long)v[i];
		}
		if (!Shapes.TryGetValue(key, out string value))
		{
			value = string.Join('+', stages.Select((Stage s) => (int)s).Order());
			Shapes.TryAdd(key, value);
		}
		return value;
	}

	internal static string Shape(IReadOnlyDictionary<int, string> stages)
	{
		return string.Join('+', stages.Keys);
	}

	internal static string CountsKey(SortedDictionary<Stage, ShaderInfo> st)
	{
		return string.Join(';', st.Select((KeyValuePair<Stage, ShaderInfo> s) => $"{(int)s.Key}:{s.Value.Counts}"));
	}

	// Exo Shaders: Sig is a pure function of a shader's (immutable) input or output list, and the planner asks it for
	// the same lists over and over (every candidate pairing): memoized per list instance, same strings as before.
	// Reference-keyed tables, emptied after each plan build (and past a cap), so nothing stays behind in an idle service.
	private static readonly RefMemo<string> SigCache = new RefMemo<string>();

	private static readonly RefMemo<string> SigCacheAll = new RefMemo<string>();

	private static readonly Func<IReadOnlyList<SigElement>, string> SigOf = (IReadOnlyList<SigElement> e) => SigUncached(e);

	private static readonly Func<IReadOnlyList<SigElement>, string> SigOfAll = (IReadOnlyList<SigElement> e) => SigUncached(e, dropSys: false);

	internal static string Sig(IReadOnlyList<SigElement> elems, bool dropSys = true)
	{
		return dropSys ? SigCache.Get(elems, SigOf) : SigCacheAll.Get(elems, SigOfAll);
	}

	internal static void ForgetMemos()
	{
		SigCache.Clear();
		SigCacheAll.Clear();
		UpperSemantics.Clear();
		OutputsByKey.Clear();
		PerPrimitiveOutputs.Clear();
	}

	private static int SigCount(IReadOnlyList<SigElement> elems)
	{
		int n = 0;
		for (int i = 0; i < elems.Count; i++)
		{
			if (elems[i].SysValue == 0 && !PerPrimitive(elems[i]))
			{
				n++;
			}
		}
		return n;
	}

	internal static string SigUncached(IReadOnlyList<SigElement> elems, bool dropSys = true)
	{
		return string.Join(';', from e in (from e in elems
				where (!dropSys || e.SysValue == 0) && !PerPrimitive(e)
				select (S: e.Semantic.ToUpperInvariant(), Index: e.Index, Register: e.Register, CompType: e.CompType)).OrderBy(((string S, int Index, int Register, int CompType) e) => e.S, StringComparer.Ordinal).ThenBy(((string S, int Index, int Register, int CompType) e) => e.Index).ThenBy(((string S, int Index, int Register, int CompType) e) => e.Register)
				.ThenBy(((string S, int Index, int Register, int CompType) e) => e.CompType)
			select $"{e.S}/{e.Index}/{e.Register}/{e.CompType}");
	}

	// Exo Shaders: a shader's outputs by (semantic, index), built once per output list instead of once per pairing.
	private static readonly RefMemo<Dictionary<(string, int), SigElement>> OutputsByKey = new RefMemo<Dictionary<(string, int), SigElement>>();

	private static readonly Func<IReadOnlyList<SigElement>, Dictionary<(string, int), SigElement>> ByKey = (IReadOnlyList<SigElement> outputs) =>
	{
		Dictionary<(string, int), SigElement> d = new Dictionary<(string, int), SigElement>();
		foreach (SigElement output in outputs)
		{
			d[(output.Semantic.ToUpperInvariant(), output.Index)] = output;
		}
		return d;
	};

	internal static bool Links(ShaderInfo src, ShaderInfo dst, bool exact = false)
	{
		Dictionary<(string, int), SigElement> dictionary = OutputsByKey.Get(src.Outputs, ByKey);
		bool flag = ShaderModel4or5(dst.ShaderModel);
		bool flag2 = flag;
		Stage stage = dst.Stage;
		flag = (uint)(stage - 3) <= 1u;
		bool flag3 = flag;
		IReadOnlyList<SigElement> inputs = dst.Inputs;
		string[] upper = UpperSemantics.Get(inputs, UpperOf);
		for (int i = 0; i < inputs.Count; i++)
		{
			SigElement input = inputs[i];
			if (flag2 && input.SysValue == 1 && dictionary.TryGetValue((upper[i], input.Index), out var value) && value.Register != input.Register)
			{
				flag = false;
			}
			else
			{
				if (input.SysValue != 0 && !FromPrevStage.Contains(input.SysValue) && !flag3)
				{
					continue;
				}
				if (!dictionary.TryGetValue((upper[i], input.Index), out var value2) || (exact ? (input.Mask != value2.Mask) : ((input.Mask & ~value2.Mask) != 0)))
				{
					flag = false;
				}
				else
				{
					if (input.ReadMask == byte.MaxValue || value2.ReadMask == byte.MaxValue || (input.ReadMask & ~value2.ReadMask) == 0)
					{
						continue;
					}
					flag = false;
				}
			}
			goto IL_01d9;
		}
		return true;
		IL_01d9:
		return flag;
	}

	internal static bool PerPrimitive(SigElement e)
	{
		return e.Register >= 65536;
	}

	// Exo Shaders: each element's upper-cased semantic, once per list (was ToUpperInvariant per element per pairing).
	private static readonly RefMemo<string[]> UpperSemantics = new RefMemo<string[]>();

	private static readonly Func<IReadOnlyList<SigElement>, string[]> UpperOf = (IReadOnlyList<SigElement> elems) =>
	{
		string[] r = new string[elems.Count];
		for (int i = 0; i < r.Length; i++)
		{
			r[i] = elems[i].Semantic.ToUpperInvariant();
		}
		return r;
	};

	/// <summary>Exo Shaders: ShaderModel.Split('_')[1] is "4" or "5" ("vs_5_0"), without the split.</summary>
	private static bool ShaderModel4or5(string sm)
	{
		int i = sm.IndexOf('_');
		if (i < 0)
		{
			return false;
		}
		ReadOnlySpan<char> rest = sm.AsSpan(i + 1);
		int j = rest.IndexOf('_');
		ReadOnlySpan<char> seg = (j < 0) ? rest : rest.Slice(0, j);
		return seg.SequenceEqual("4") || seg.SequenceEqual("5");
	}

	// Exo Shaders: a mesh shader's per-primitive outputs by (semantic, index), built once per shader, not once per pairing.
	private static readonly RefMemo<Dictionary<(string, int), SigElement>> PerPrimitiveOutputs = new RefMemo<Dictionary<(string, int), SigElement>>();

	private static readonly Func<IReadOnlyList<SigElement>, Dictionary<(string, int), SigElement>> PerPrimitiveOf = (IReadOnlyList<SigElement> outputs) => outputs.Where(PerPrimitive).DistinctBy((SigElement e) => (e.Semantic.ToUpperInvariant(), Index: e.Index)).ToDictionary((SigElement e) => (e.Semantic.ToUpperInvariant(), Index: e.Index));

	internal static bool MeshFeeds(ShaderInfo ms, ShaderInfo ps)
	{
		Dictionary<(string, int), SigElement> dictionary = PerPrimitiveOutputs.Get(ms.Outputs, PerPrimitiveOf);
		IReadOnlyList<SigElement> inputs = ps.Inputs;
		List<SigElement> list = new List<SigElement>(inputs.Count);
		string[] upper = UpperSemantics.Get(inputs, UpperOf);
		for (int i = 0; i < inputs.Count; i++)
		{
			SigElement input = inputs[i];
			if (!dictionary.TryGetValue((upper[i], input.Index), out var value))
			{
				list.Add(input);
			}
			else if (input.SysValue == 0 && value.CompType != input.CompType)
			{
				return false;
			}
		}
		// Exo Shaders: Sig joins one token per kept element and a token is never empty, so a different number of kept
		// elements means different signatures: skip building the string then.
		if (SigCount(list) != SigCount(ms.Outputs))
		{
			return false;
		}
		if (SigUncached(list) == Sig(ms.Outputs))
		{
			return Links(ms, ps, exact: true);
		}
		return false;
	}

	internal static bool Launches(ShaderInfo a, ShaderInfo m)
	{
		return a.Payload == m.Payload;
	}

	internal static IEnumerable<(ShaderInfo Hs, ShaderInfo Ds)> TessPairs(IReadOnlyList<ShaderInfo> map)
	{
		ILookup<string, ShaderInfo> dss = map.Where((ShaderInfo d) => d.Stage == Stage.Domain).ToLookup((ShaderInfo d) => Sig(d.Inputs));
		foreach (ShaderInfo h in map.Where((ShaderInfo shaderInfo) => shaderInfo.Stage == Stage.Hull))
		{
			foreach (ShaderInfo item in dss[Sig(h.Outputs)].Where((ShaderInfo d) => Links(h, d) && SameRs(h, d)))
			{
				yield return (Hs: h, Ds: item);
			}
		}
	}

	public static List<PsoDb.LayoutElem> VsLayout(ShaderInfo vs)
	{
		List<PsoDb.LayoutElem> list = new List<PsoDb.LayoutElem>();
		uint num = 0u;
		foreach (SigElement item in from e in vs.Inputs
			where e.SysValue == 0
			orderby e.Register
			select e)
		{
			int num2 = Math.Max(1, 32 - BitOperations.LeadingZeroCount(item.Mask));
			list.Add(new PsoDb.LayoutElem(item.Semantic, item.Index, Fmt.GetValueOrDefault(item.CompType, Fmt[3])[num2 - 1], num));
			num += (uint)(4 * num2);
		}
		return list;
	}
}

/// <summary>
/// Exo Shaders: a reference-keyed memo for pure functions of immutable lists (Planner.Sig and friends). Lock-free reads;
/// the size is counted with an interlocked counter (ConcurrentDictionary.Count takes every lock).
/// </summary>
internal sealed class RefMemo<TValue>
{
	private const int Cap = 2000000;

	private readonly System.Collections.Concurrent.ConcurrentDictionary<object, TValue> map = new System.Collections.Concurrent.ConcurrentDictionary<object, TValue>(ReferenceEqualityComparer.Instance);

	private int count;

	public TValue Get<TKey>(TKey key, Func<TKey, TValue> make) where TKey : class
	{
		if (map.TryGetValue(key, out TValue value))
		{
			return value;
		}
		value = make(key);
		if (Interlocked.Increment(ref count) > Cap)
		{
			Clear();
		}
		map.TryAdd(key, value);
		return value;
	}

	public void Clear()
	{
		map.Clear();
		Interlocked.Exchange(ref count, 0);
	}
}