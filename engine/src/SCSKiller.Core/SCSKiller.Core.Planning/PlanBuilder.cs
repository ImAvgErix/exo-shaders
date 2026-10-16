using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using SCSKiller.Core.App;
using SCSKiller.Core.Carved;
using SCSKiller.Core.FromSoft;
using SCSKiller.Core.RedEngine;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.Planning;

internal sealed class PlanBuilder
{
	private readonly Game game;

	private readonly EngineInfo engine;

	private readonly ShaderIndex index;

	private readonly Recording? recording;

	private readonly VendorCaps caps;

	private readonly string outDir;

	private readonly IProgress<string>? log;

	private readonly CancellationToken ct;

	private readonly bool maximum;

	private readonly bool rayTracing;

	private readonly bool pairs;

	private readonly MiddlewarePacks? packs;

	private readonly MiddlewarePacks? sharedPacks;

	private readonly PlanBudget budget;

	private readonly long budgetPsos;

	private readonly IReadOnlyDictionary<string, ShaderInfo> bc;

	private readonly List<(string Platform, bool Pooled, IReadOnlyList<string> Shas, bool IsPipeline, string? Rs)> maps;

	private readonly bool mapRoots;

	private string? mapRs;

	private readonly Dictionary<string, string> platOfSha = new Dictionary<string, string>();

	private readonly RulesFile rules;

	private readonly PlanRule rule;

	private readonly uint maxSrvs;

	private readonly List<PsoDb.Rec> recs = new List<PsoDb.Rec>();

	private readonly List<PsoDb.Rec> stateObjects = new List<PsoDb.Rec>();

	private readonly List<PsoDb.Rec> nvRecs = new List<PsoDb.Rec>();

	private readonly HashSet<string> layered = new HashSet<string>();

	private readonly RecordedBlobs recBlobs = new RecordedBlobs();

	private readonly Dictionary<string, List<(string Key, string Rs, bool Layout)>> templates = new Dictionary<string, List<(string, string, bool)>>();

	private readonly Dictionary<string, PsoDb.Rec> recByKey = new Dictionary<string, PsoDb.Rec>();

	private readonly Dictionary<(uint Topology, string Shape), (string Key, string Rs, bool Layout)> gsTopo = new Dictionary<(uint, string), (string, string, bool)>();

	private readonly Dictionary<string, List<string>> rsByCounts = new Dictionary<string, List<string>>();

	private readonly HashSet<string> have = new HashSet<string>();

	private readonly Dictionary<string, int> plats = new Dictionary<string, int>();

	private byte[]? samplers;

	private readonly Dictionary<string, HashSet<string>> settings = new Dictionary<string, HashSet<string>>();

	private readonly Dictionary<string, byte[]> samplerSets = new Dictionary<string, byte[]>();

	private int builtOk;

	private int builtN;

	private int ownOk;

	private int ownN;

	private bool verified;

	private bool build;

	private bool synth;

	private bool dx12;

	private string plat = "";

	private int embeddedRs;

	private string? plat11;

	private List<string> d3d11 = new List<string>();

	private readonly List<(string Hs, string Ds)> tess11 = new List<(string, string)>();

	private readonly List<PsoDb.Draw11> draws11 = new List<PsoDb.Draw11>();

	private List<PsoDb.Draw11> items11 = new List<PsoDb.Draw11>();

	private readonly Dictionary<string, string?> rsCache = new Dictionary<string, string>();

	private readonly Dictionary<string, byte[]> rsBlobs = new Dictionary<string, byte[]>();

	private readonly Dictionary<string, RootSig.Desc> rsDescs = new Dictionary<string, RootSig.Desc>();

	private readonly List<byte[]> items = new List<byte[]>();

	private readonly List<PsoDb.Rec> synthesized = new List<PsoDb.Rec>();

	private readonly Dictionary<string, string> synthByShape = new Dictionary<string, string>();

	private readonly HashSet<string> usedTemplates = new HashSet<string>();

	private readonly HashSet<string> usedRs = new HashSet<string>();

	private readonly HashSet<string> seen = new HashSet<string>();

	private readonly SortedDictionary<string, int> stats = new SortedDictionary<string, int>();

	private readonly long[] unitsBy = new long[3];

	private double layoutCoverage;

	private long? cacheLeftOut;

	private int extrasRoom = int.MaxValue;

	private readonly List<PsoDb.Rec> packEntries = new List<PsoDb.Rec>();

	private readonly Dictionary<string, byte[]> packRs = new Dictionary<string, byte[]>();

	private long packNew;

	private long packShared;

	private RtCollections.Nv? rasterNv;

	private string? unserializable;

	private string? unbuilt;

	private bool unbuildable;

	private HashSet<string>? learnedParts;

	private readonly Dictionary<string, HashSet<string>> learnedVs = new Dictionary<string, HashSet<string>>();

	private int unpaired;

	private const int CrossProductRatio = 50;

	private (long Pairs, int Vs, int Ps)? crossPool;

	private readonly List<(long Pairs, int Vs, int Ps)> crossPools = new List<(long, int, int)>();

	private readonly Dictionary<string, RootSig.Ranges?> rsRanges = new Dictionary<string, RootSig.Ranges>();

	private readonly Dictionary<(string Rs, string Sha, int Stage), bool> covered = new Dictionary<(string, string, int), bool>();

	private readonly Dictionary<string, string> uncoveredExample = new Dictionary<string, string>();

	private readonly Dictionary<string, string> ownUncoveredExample = new Dictionary<string, string>();

	private string? unknownRs;

	public const long ExtraBudget = 2147483648L;

	public const long ExtraBytes = 22528L;

	public const long PsoBytes = 28672L;

	public const long CacheBudget = 17179869184L;

	public const long CachePsos = 599186L;

	private const int AgsSpace = 2147420894;

	private const int SharedMaps = 50;

	private readonly List<byte[]> rtItems = new List<byte[]>();

	private readonly List<byte[]> hitGroupItems = new List<byte[]>();

	private readonly HashSet<string> rtUnbuildable = new HashSet<string>();

	public string? PackFingerprint { get; private set; }

	private uint RtOptions => RtCollections.RecordedOptions(stateObjects.Concat(nvRecs));

	private string NoRs
	{
		get
		{
			if (!unbuildable)
			{
				return "no_rs";
			}
			return "rs_unserializable";
		}
	}

	public PlanBuilder(Game game, EngineInfo engine, ShaderIndex index, Recording? recording, VendorCaps caps, string outDir, IProgress<string>? log, CancellationToken ct, bool maximum = false, MiddlewarePacks? packs = null, MiddlewarePacks? sharedPacks = null, RulesFile? rules = null, PlanBudget? budget = null, long? cacheBudget = null, bool rayTracing = true, bool pairs = true)
	{
		this.pairs = pairs;
		bool flag = maximum;
		bool flag2 = rayTracing;
		this.game = game;
		this.engine = engine;
		this.index = index;
		this.recording = recording;
		this.caps = caps;
		this.outDir = outDir;
		this.log = log;
		this.ct = ct;
		this.maximum = flag;
		this.packs = packs;
		this.sharedPacks = sharedPacks;
		this.rayTracing = flag2;
		this.budget = budget ?? new PlanBudget(long.MaxValue, TimeSpan.MaxValue);
		budgetPsos = (cacheBudget ?? CacheBudgetOf(caps)) / 28672;
		this.budget.Progress = () => $"{seen.Count:N0} stage sets";
		this.rules = rules ?? RulesFile.Current;
		bc = index.Shaders;
		maps = index.Maps.Select((ShaderMap m) => (Platform: m.Platform, Pooled: false, Shas: m.Shaders, IsPipeline: m.IsPipeline, Rs: m.IsPipeline ? m.RootSignature : null)).ToList();
		maps.AddRange(from m in index.Maps
			where m.Library == "Global"
			group m by m.Platform into g
			select ((string Key, bool, IReadOnlyList<string>, bool, string))(Key: g.Key, true, g.SelectMany((ShaderMap m) => m.Shaders).Distinct().ToList(), false, null));
		mapRoots = maps.Any(((string Platform, bool Pooled, IReadOnlyList<string> Shas, bool IsPipeline, string Rs) m) => m.Rs != null);
		foreach (ShaderMap map in index.Maps)
		{
			foreach (string shader in map.Shaders)
			{
				platOfSha[shader] = map.Platform;
			}
		}
		rule = this.rules.RuleFor(engine) ?? RulesFile.Embedded.Rules.Single((PlanRule r) => r.Id == "ue-ff7");
		maxSrvs = RootSig.MaxSrvsFor(rule, bc.Values);
	}

	public Plan Build()
	{
		using (recBlobs)
		{
			ReadRecording();
			Decide();
			if (dx12)
			{
				RuntimeBuilt();
			}
			if (!pairs)
			{
				log?.Report("Only the recorded pipelines planned: no shader pairings or ray tracing");
				goto IL_00f2;
			}
			if (dx12)
			{
				UnitPolicy unitPolicy = UnitPolicy.For(caps);
				if ((object)unitPolicy != null)
				{
					PerStage(unitPolicy);
					goto IL_0079;
				}
			}
			StageSets(Emit);
			goto IL_0079;
			IL_00f2:
			if (Planner.SeedsPacks(engine) && (packs ?? sharedPacks) != null)
			{
				SeedMiddleware();
			}
			return Write();
			IL_0079:
			if (dx12 && !rayTracing)
			{
				log?.Report("Ray tracing skipped (your setting): no state objects or collections planned");
			}
			else if (dx12 && Planner.RtCollectionCache(caps))
			{
				try
				{
					RtPlan();
				}
				catch (RootSig.SerializeException ex)
				{
					rtItems.Clear();
					log?.Report("warning: ray tracing: no collections: the runtime won't serialize the rule's global root signature (" + ex.Message + ")");
				}
			}
			goto IL_00f2;
		}
	}

	private void SeedMiddleware()
	{
		List<MiddlewareDll> list = Middleware.Detect(game);
		if (list.Count == 0)
		{
			return;
		}
		if (recs.Count > 0 && packs != null && game.Store != Store.Manual)
		{
			MiddlewarePacks? middlewarePacks = packs;
			List<PsoDb.Rec> records = recs;
			RecordedBlobs blobs = recBlobs;
			IReadOnlyDictionary<string, ShaderInfo> readOnlyDictionary = bc;
			List<MiddlewareDll> dlls = list;
			string id = game.Id;
			string? gpu;
			if (caps.Profile.StartsWith("amd"))
			{
				gpu = "amd";
			}
			else
			{
				gpu = (caps.Profile.StartsWith("nvidia") ? "nvidia" : null);
			}
			foreach (MiddlewarePacks.Promoted item3 in middlewarePacks.Promote(records, blobs, readOnlyDictionary, dlls, id, gpu, layered))
			{
				log?.Report($"middleware: {item3.Records} recorded PSOs are {item3.Dll.Name}'s ({item3.Dll.Vendor}, {item3.ContentHash.Substring(0, 12)}), {item3.New} new in its pack");
			}
		}
		HashSet<string> hashSet = new HashSet<string>();
		HashSet<string> hashSet2 = recs.Select((PsoDb.Rec r) => r.Key).ToHashSet();
		bool amd = caps.Profile.StartsWith("amd");
		Dictionary<string, bool> memo = new Dictionary<string, bool>();
		int num = 0;
		string fingerprint = "";
		string fingerprint2 = "";
		List<MiddlewarePacks.Seeded> item = packs?.Seed(list, out fingerprint) ?? new List<MiddlewarePacks.Seeded>();
		List<MiddlewarePacks.Seeded> item2 = sharedPacks?.Seed(list, out fingerprint2) ?? new List<MiddlewarePacks.Seeded>();
		PackFingerprint = fingerprint + ((fingerprint2.Length > 0) ? ("|shared:" + fingerprint2) : "");
		(List<MiddlewarePacks.Seeded>, bool)[] array = new (List<MiddlewarePacks.Seeded>, bool)[2]
		{
			(item, false),
			(item2, true)
		};
		for (int num2 = 0; num2 < array.Length; num2++)
		{
			var (list2, flag) = array[num2];
			foreach (MiddlewarePacks.Seeded item4 in list2)
			{
				MiddlewareImage image = Middleware.Scan(item4.Dll.Path);
				int num3 = 0;
				int num4 = 0;
				int num5 = 0;
				int num6 = 0;
				foreach (PsoDb.Rec entry in item4.Pack.Entries)
				{
					if (!hashSet.Add(entry.Key))
					{
						num5++;
						continue;
					}
					if (!MiddlewarePacks.Runs(entry, image, amd, memo))
					{
						num6++;
						continue;
					}
					PsoDb.Pso pso = PsoDb.Parse(entry);
					packEntries.Add(MiddlewarePacks.Wrap(entry, item4.Dll.Name, item4.ContentHash));
					if (item4.Pack.RootSignatures.TryGetValue(pso.Rs, out byte[] value))
					{
						packRs[pso.Rs] = value;
					}
					num3++;
					if (hashSet2.Contains(entry.Key))
					{
						num4++;
						continue;
					}
					packNew++;
					if (flag)
					{
						packShared++;
					}
				}
				num += num6;
				log?.Report($"middleware: {item4.Dll.Name} ({item4.Dll.Vendor}, {item4.ContentHash.Substring(0, 12)}): {num3} {(flag ? "shared " : "")}pack PSOs ({num4} already in the recording, {num3 - num4} new" + ((num5 > 0) ? $"; {num5} seeded already" : "") + ((num6 > 0) ? $"; {num6} with a wave size this GPU doesn't run left out" : "") + "; from " + string.Join(", ", item4.Pack.Header.Sources) + ")");
			}
		}
		if (num > 0)
		{
			stats["vendor_extension"] = stats.GetValueOrDefault("vendor_extension") + num;
		}
	}

	private void RuntimeBuilt()
	{
		HashSet<string> shas = (from h in recs.SelectMany((PsoDb.Rec r) => PsoDb.Parse(r).Stages.Values)
			where !bc.ContainsKey(h)
			select h).ToHashSet();
		if (shas.Count == 0)
		{
			return;
		}
		shas.ExceptWith((from d in Middleware.Detect(game)
			where d.Packable
			select d).SelectMany((MiddlewareDll d) => Middleware.Scan(d.Path).Containers.Keys));
		if (shas.Count != 0)
		{
			int value = recs.Count((PsoDb.Rec r) => PsoDb.Parse(r).Stages.Values.Any(shas.Contains));
			log?.Report($"recorded: {value} PSOs use {shas.Count} shaders in no file of the install (built at run time, or an overlay's): {shas.Count(recBlobs.ContainsKey)} of those shaders are in this PC's recording and replay from it; a shared recording flags those PSOs as needing a recording");
		}
	}

	private void ReadRecording()
	{
		if (recording != null)
		{
			foreach (PsoDb.Rec item in recBlobs.Read(recording.DbPath))
			{
				char tag = item.Tag;
				if ((tag == 'C' || tag == 'G' || tag == 'S') ? true : false)
				{
					recs.Add(item);
				}
				else
				{
					if (!rayTracing && Planner.LeftOutWithoutRt(item))
					{
						continue;
					}
					if (PsoDb.IsStateObject(item.Tag))
					{
						stateObjects.Add(item);
					}
					else if (item.Tag == 'N')
					{
						nvRecs.Add(item);
					}
					else if (item.Tag == 'W' && item.Payload.Length == 40)
					{
						layered.Add(PsoDb.Hex(item.Payload.AsSpan(0, 20)));
					}
					else if (item.Tag == 'D')
					{
						try
						{
							draws11.Add(PsoDb.ParseDraw11(item.Payload));
						}
						catch (Exception ex) when ((ex is InvalidDataException || ex is ArgumentException || ex is OverflowException || ex is IndexOutOfRangeException) ? true : false)
						{
						}
					}
				}
			}
		}
		HashSet<string> source = recs.Select((PsoDb.Rec r) => PsoDb.Parse(r).Rs).Concat(stateObjects.SelectMany((PsoDb.Rec r) => PsoDb.ParseStateObject(r).RootSignatures)).ToHashSet();
		HashSet<string> bad = source.Where((string h) => recBlobs.TryGetValue(h, out byte[] value6) && !Dxbc.RootSignatureValid(value6)).ToHashSet();
		if (bad.Count > 0)
		{
			foreach (string item2 in bad)
			{
				recBlobs.Remove(item2);
			}
			int value = recs.RemoveAll((PsoDb.Rec r) => bad.Contains(PsoDb.Parse(r).Rs)) + stateObjects.RemoveAll((PsoDb.Rec r) => PsoDb.ParseStateObject(r).RootSignatures.Any(bad.Contains));
			log?.Report($"warning: recorded: {bad.Count} malformed root signatures left out with the {value} records naming them");
		}
		int num = 0;
		foreach (PsoDb.Rec rec in recs)
		{
			PsoDb.Pso pso = PsoDb.Parse(rec);
			if (layered.Contains(rec.Key) || !pso.Stages.Values.All(bc.ContainsKey))
			{
				have.Add(pso.Tuple);
				continue;
			}
			SortedDictionary<Stage, ShaderInfo> sortedDictionary = Infos(pso.Stages);
			string text = pso.Stages.Values.Select((string h) => bc[h].RootSignature).FirstOrDefault((string h) => h != null);
			if (text != null)
			{
				ownN++;
				if (text == pso.Rs)
				{
					ownOk++;
				}
			}
			if (recBlobs.TryGetValue(pso.Rs, out byte[] value2))
			{
				builtN++;
				byte[] value3 = RootSig.Samplers(value2);
				try
				{
					if (((ReadOnlySpan<byte>)RootSig.Serialize(RootSig.Build(rule, sortedDictionary, MeshTier(pso.Stages), maxSrvs), value3).AsSpan()).SequenceEqual((ReadOnlySpan<byte>)value2) || (MeshTier(pso.Stages) && ((ReadOnlySpan<byte>)RootSig.Serialize(RootSig.Build(rule, sortedDictionary, meshTier: false, maxSrvs), value3).AsSpan()).SequenceEqual((ReadOnlySpan<byte>)value2)))
					{
						builtOk++;
					}
					if (samplers == null)
					{
						samplers = value3;
					}
					(string, string)? tuple = SamplerVariants.Setting(value2);
					if (tuple.HasValue)
					{
						(string, string) valueOrDefault = tuple.GetValueOrDefault();
						HashSet<string> hashSet3;
						if (!settings.TryGetValue(valueOrDefault.Item1, out HashSet<string> value4))
						{
							HashSet<string> hashSet = (settings[valueOrDefault.Item1] = new HashSet<string>());
							hashSet3 = hashSet;
						}
						else
						{
							hashSet3 = value4;
						}
						hashSet3.Add(valueOrDefault.Item2);
						samplerSets.TryAdd(valueOrDefault.Item2, value3);
					}
				}
				catch (RootSig.SerializeException)
				{
					num++;
				}
			}
			have.Add(pso.Tuple);
			foreach (string item3 in from h in pso.Stages.Values.Where(platOfSha.ContainsKey)
				select platOfSha[h].Split(" wave")[0])
			{
				plats[item3] = plats.GetValueOrDefault(item3) + 1;
			}
			if (!rsByCounts.TryGetValue(Planner.CountsKey(sortedDictionary), out List<string> value5))
			{
				value5 = (rsByCounts[Planner.CountsKey(sortedDictionary)] = new List<string>());
			}
			if (!value5.Contains(pso.Rs))
			{
				value5.Add(pso.Rs);
			}
			(string, string, bool) tuple2 = (rec.Key, pso.Rs, pso.HasLayout);
			recByKey[rec.Key] = rec;
			string text2 = Planner.Shape(pso.Stages);
			Add(text2 + "|" + PsOut(pso.Stages), tuple2);
			Add(text2, tuple2);
			if (pso.Stages.ContainsKey(5))
			{
				gsTopo.TryAdd((pso.Topology, text2), tuple2);
			}
		}
		if (num > 0)
		{
			log?.Report($"recorded: the rule's rebuild of {num} root signatures doesn't serialize with their static samplers: counted as not rebuilt (they replay as recorded)");
		}
		void Add(string shape, (string, string, bool) t)
		{
			if (!templates.TryGetValue(shape, out List<(string, string, bool)> value6))
			{
				value6 = (templates[shape] = new List<(string, string, bool)>());
			}
			value6.Add(t);
		}
	}

	internal static RtCollections.Nv? RasterNv(VendorCaps caps, string family, IReadOnlyCollection<PsoDb.Rec> recs, IEnumerable<PsoDb.Rec> nvRecs)
	{
		if (caps.Profile.StartsWith("nvidia"))
		{
			RtCollections.Nv? result = LearnedRasterNv(recs, nvRecs);
			if (!result.HasValue)
			{
				if (!(family == "REDengine 3"))
				{
					return null;
				}
				return new RtCollections.Nv(12u, 1u, 0u);
			}
			return result;
		}
		return null;
	}

	internal static RtCollections.Nv? LearnedRasterNv(IEnumerable<PsoDb.Rec> recs, IEnumerable<PsoDb.Rec> nvRecs)
	{
		HashSet<string> keys = (from r in recs.Where((PsoDb.Rec r) =>
			{
				char tag = r.Tag;
				return (tag == 'C' || tag == 'G' || tag == 'S') ? true : false;
			})
			select r.Key).ToHashSet();
		IGrouping<(uint, uint), PsoDb.NvState> grouping = (from n in (from n in nvRecs.Where((PsoDb.Rec r) => r.Tag == 'N').Select(PsoDb.NvState.Parse)
				where keys.Contains(n.Target)
				group n by n.Target into g
				select g.Last() into n
				where n.Slot != uint.MaxValue
				select n).ToList()
			group n by (Slot: n.Slot, Space: n.Space)).MaxBy((IGrouping<(uint Slot, uint Space), PsoDb.NvState> g) => g.Count());
		if (grouping == null || !((double)grouping.Count() >= 0.99 * (double)keys.Count))
		{
			return null;
		}
		return new RtCollections.Nv(grouping.Key.Item1, grouping.Key.Item2, (from n in grouping
			group n by n.Options).MaxBy((IGrouping<uint, PsoDb.NvState> g) => g.Count()).Key);
	}

	private void Decide()
	{
		bool flag = builtN > 0;
		verified = (flag ? ((double)builtOk >= 0.99 * (double)builtN) : RootSig.Verified(rules.RuleFor(engine), engine));
		build = (flag ? verified : (rules.RuleFor(engine) != null));
		if (samplers == null)
		{
			samplers = RootSig.StaticSamplers(rule);
		}
		synth = caps.StateIndependentCache;
		dx12 = engine.GraphicsApi.Contains("D3D12");
		ref string reference = ref plat;
		string text;
		if (dx12)
		{
			text = ((plats.Count > 0) ? plats.MaxBy((KeyValuePair<string, int> p) => p.Value).Key : (new string[2] { "PCD3D_SM6", "PCD3D_SM5" }.FirstOrDefault(((IEnumerable<string>)index.Platforms).Contains<string>) ?? index.Platforms.FirstOrDefault() ?? ""));
		}
		else
		{
			text = "";
		}
		reference = text;
		embeddedRs = bc.Values.Count((ShaderInfo s) => s.RootSignature != null);
		if (!build && embeddedRs == 0 && !mapRoots && !maps.Any(((string Platform, bool Pooled, IReadOnlyList<string> Shas, bool IsPipeline, string Rs) m) => m.Pooled))
		{
			Learned();
		}
		rasterNv = RasterNv(caps, engine.Family, recs, nvRecs);
		RtCollections.Nv? nv = rasterNv;
		if (nv.HasValue)
		{
			RtCollections.Nv valueOrDefault = nv.GetValueOrDefault();
			log?.Report($"NVAPI: synthesized PSOs get shader-extension slot {valueOrDefault.Slot} space {valueOrDefault.Space} (options {valueOrDefault.Options})");
		}
		bool flag2 = embeddedRs > 0 && ownN == builtN;
		if (dx12)
		{
			log?.Report($"recorded: {recs.Count} PSOs{((stateObjects.Count > 0) ? $" + {stateObjects.Count} ray tracing state objects (replayed as recorded)" : "")}, platform {plat}; " + (flag2 ? "" : ($"root sigs rebuilt from shader counts: {builtOk}/{builtN} exact -> " + (build ? "building" : "learned lookup"))) + ((maxSrvs != 0) ? $" (SRV tables of {maxSrvs}, not Unreal {engine.Version}'s {rule.RootSignature.Stock?.MaxSrvs}: {((maxSrvs == 128) ? "the bindless fork's" : "its shaders bind more")})" : "") + ((embeddedRs > 0) ? $"{(flag2 ? "" : "; ")}{embeddedRs} shaders carry their own root signature{((ownN > 0) ? $", {ownOk}/{ownN} recorded PSOs created with it" : "")}" : "") + (synth ? ", synthesized templates allowed" : ""));
		}
		plat11 = (index.Platforms.Contains("PCD3D_SM5") ? "PCD3D_SM5" : null);
		if (!engine.GraphicsApi.StartsWith("D3D11"))
		{
			return;
		}
		Stage[] stages11 = Planner.D3D11Stages(caps);
		if (stages11 == null)
		{
			return;
		}
		List<List<ShaderInfo>> source = (from m in index.Maps
			where m.Platform == (plat11 ?? m.Platform)
			select (from h in m.Shaders
				where bc.TryGetValue(h, out ShaderInfo value) && Planner.IsD3D11(value)
				select bc[h]).ToList()).ToList();
		d3d11 = (from s in source.SelectMany((List<ShaderInfo> m) => m).Where((ShaderInfo s) =>
			{
				Stage stage = s.Stage;
				return (uint)(stage - 3) > 1u && (stages11.Contains(s.Stage, null) || (s.Stage == Stage.Vertex && s.Inputs.All((SigElement i) => i.SysValue != 0)));
			})
			select s.Sha1).Distinct().ToList();
		if (!stages11.Contains(Stage.Vertex, null) && draws11.Count > 0)
		{
			Draws11.Result result = Draws11.Plan(draws11, Info, from s in source.SelectMany((List<ShaderInfo> m) => m)
				where s.Stage == Stage.Vertex
				select s.Sha1);
			items11 = result.Items;
			log?.Report($"D3D11 vertex stages: {result.Exact} from {draws11.Count} recorded draws, {result.Projected} more for {result.VssProjected} unrecorded vertex shaders from the recorded layouts");
		}
		if (!stages11.Contains(Stage.Hull, null))
		{
			return;
		}
		HashSet<string> paired = new HashSet<string>();
		foreach (var (shaderInfo, shaderInfo2) in source.SelectMany(Planner.TessPairs))
		{
			if (paired.Add(shaderInfo.Sha1) | paired.Add(shaderInfo2.Sha1))
			{
				tess11.Add((shaderInfo.Sha1, shaderInfo2.Sha1));
			}
		}
		int num = (from s in source.SelectMany((List<ShaderInfo> m) => m).Where((ShaderInfo s) =>
			{
				Stage stage = s.Stage;
				return (uint)(stage - 3) <= 1u && !paired.Contains(s.Sha1);
			})
			select s.Sha1).Distinct().Count();
		if (num > 0)
		{
			stats["d3d11_tess_unpaired"] = num;
		}
		ShaderInfo? Info(string h)
		{
			if (bc.TryGetValue(h, out ShaderInfo value))
			{
				return value;
			}
			try
			{
				byte[] value2;
				return recBlobs.TryGetValue(h, out value2) ? ShaderContainer.Parse(value2, h, new ResourceCounts(0, 0, 0, 0)) : null;
			}
			catch (Exception)
			{
				return null;
			}
		}
	}

	private string? RootSigOf(SortedDictionary<int, string> stages)
	{
		unbuildable = false;
		if (mapRs != null)
		{
			return mapRs;
		}
		string text = stages.Values.Select((string h) => bc[h].RootSignature).FirstOrDefault((string r) => r != null);
		if (text != null)
		{
			return text;
		}
		if (!RootSig.InStageSets(rule, stages.Keys.Select((int k) => (Stage)k)))
		{
			return null;
		}
		if (!build)
		{
			if (!rsByCounts.TryGetValue(Planner.CountsKey(Infos(stages)), out List<string> value))
			{
				return null;
			}
			return value.FirstOrDefault((string r) => Uncovered(r, stages) == null, value[0]);
		}
		RootSig.Desc desc;
		try
		{
			desc = RootSig.Build(rule, Infos(stages), MeshTier(stages), maxSrvs);
		}
		catch (RootSig.SerializeException ex) when (rule.RootSignature?.Re != null)
		{
			if (unbuilt == null)
			{
				unbuilt = ex.Message;
			}
			Count("rs_unbuilt");
			return null;
		}
		if (!rsCache.TryGetValue(desc.Key, out string value2))
		{
			try
			{
				byte[] array = RootSig.Serialize(desc, samplers);
				value2 = (rsCache[desc.Key] = PsoDb.Hex(SHA1.HashData(array)));
				rsBlobs[value2] = array;
				rsDescs[value2] = desc;
			}
			catch (RootSig.SerializeException ex2)
			{
				value2 = (rsCache[desc.Key] = null);
				if (unserializable == null)
				{
					unserializable = ex2.Message;
				}
			}
		}
		unbuildable = value2 == null;
		return value2;
	}

	private static string Part(ShaderInfo s)
	{
		return $"{(int)s.Stage}:{s.Counts}";
	}

	private void Learned()
	{
		learnedParts = new HashSet<string>();
		foreach (string[] item in rsByCounts.Keys.Select((string k) => k.Split(';')))
		{
			learnedParts.UnionWith(item);
			if (item == null || item.Length != 2)
			{
				continue;
			}
			string text = item[0];
			string text2 = item[1];
			if (text.StartsWith($"{1}:") && text2.StartsWith($"{2}:"))
			{
				HashSet<string> hashSet3;
				if (!learnedVs.TryGetValue(text2, out HashSet<string> value))
				{
					HashSet<string> hashSet = (learnedVs[text2] = new HashSet<string>());
					hashSet3 = hashSet;
				}
				else
				{
					hashSet3 = value;
				}
				hashSet3.Add(text);
			}
		}
	}

	private string? Uncovered(string rs, SortedDictionary<int, string> stages)
	{
		if (!rsRanges.TryGetValue(rs, out RootSig.Ranges value))
		{
			try
			{
				Dictionary<string, RootSig.Ranges?> dictionary = rsRanges;
				string key = rs;
				object obj;
				if (!rsBlobs.TryGetValue(rs, out byte[] value2) && !recBlobs.TryGetValue(rs, out value2))
				{
					IReadOnlyDictionary<string, byte[]>? rootSignatures = index.RootSignatures;
					if (rootSignatures == null || !rootSignatures.TryGetValue(rs, out value2))
					{
						obj = null;
						goto IL_0093;
					}
				}
				obj = RootSig.Parse(value2);
				goto IL_0093;
				IL_0093:
				value = (RootSig.Ranges)obj;
				dictionary[key] = (RootSig.Ranges)obj;
			}
			catch (Exception ex) when ((ex is InvalidDataException || ex is ArgumentException || ex is IndexOutOfRangeException) ? true : false)
			{
				rsRanges[rs] = null;
			}
		}
		if (value == null)
		{
			if (unknownRs == null)
			{
				unknownRs = rs;
			}
			return "rs_unknown";
		}
		foreach (var (num2, text2) in stages)
		{
			if (covered.TryGetValue((rs, text2, num2), out var value3))
			{
				if (!value3)
				{
					return Kind();
				}
				continue;
			}
			string text3 = RootSig.Uncovered(value, (Stage)num2, bc[text2]);
			covered[(rs, text2, num2)] = text3 == null;
			if (text3 != null)
			{
				(Own() ? ownUncoveredExample : uncoveredExample).TryAdd(text3, text2);
				return Kind();
			}
		}
		return null;
		string Kind()
		{
			if (!Own())
			{
				return "rs_uncovered";
			}
			return "own_rs_uncovered";
		}
		bool Own()
		{
			if (!(rs == mapRs))
			{
				return stages.Values.Any((string h) => bc[h].RootSignature == rs);
			}
			return true;
		}
	}

	private List<PsoDb.LayoutElem>? LayoutFor(SortedDictionary<int, string> stages, bool templateHasLayout)
	{
		if (!stages.TryGetValue(1, out string value))
		{
			return null;
		}
		if (!templateHasLayout && !bc[value].Inputs.Any((SigElement e) => e.SysValue == 0))
		{
			return null;
		}
		return Planner.VsLayout(bc[value]);
	}

	private void Count(string k)
	{
		stats[k] = stats.GetValueOrDefault(k) + 1;
	}

	private bool FirstVisit(SortedDictionary<int, string> stages, out string? rs)
	{
		budget.Tick();
		rs = null;
		if (mapRoots)
		{
			rs = RootSigOf(stages);
			return seen.Add(PsoDb.Tuple(rs ?? "", stages));
		}
		if (!seen.Add(PsoDb.Tuple("", stages)))
		{
			return false;
		}
		rs = RootSigOf(stages);
		return true;
	}

	private void Emit(SortedDictionary<int, string> stages, string shape, string psOut)
	{
		if (!FirstVisit(stages, out string rs))
		{
			return;
		}
		ShaderInfo shaderInfo = (stages.TryGetValue(5, out string value) ? bc[value] : null);
		uint topo;
		if (stages.ContainsKey(4))
		{
			topo = 4u;
		}
		else
		{
			topo = ((shaderInfo == null) ? 3u : Planner.TopologyType(shaderInfo.GsInputPrimitive));
		}
		object obj;
		if (!(shaderInfo != null))
		{
			obj = templates.GetValueOrDefault(shape + "|" + psOut) ?? templates.GetValueOrDefault(shape);
		}
		else
		{
			List<(string, string, bool)> list = (from t in gsTopo
				where (topo == 0 || t.Key.Topology == topo) && t.Key.Shape == shape
				select t.Value).ToList();
			obj = ((list != null && list.Count > 0) ? list : null);
		}
		List<(string, string, bool)> list2 = (List<(string, string, bool)>)obj;
		if (rs == null || (list2 == null && (!synth || topo == 0)))
		{
			PlanBuilder planBuilder = this;
			string k;
			if (rs == null)
			{
				k = NoRs;
			}
			else
			{
				k = ((shaderInfo != null) ? "no_gs_template" : "no_template");
			}
			planBuilder.Count(k);
			return;
		}
		if (have.Contains(PsoDb.Tuple(rs, stages)))
		{
			Count("already_recorded");
			return;
		}
		string text = Uncovered(rs, stages);
		if (text != null)
		{
			Count(text);
			return;
		}
		usedRs.Add(rs);
		Count("generated");
		if (shaderInfo != null && list2 != null)
		{
			foreach (var item4 in list2)
			{
				items.Add(PsoDb.Item(item4.Item1, rs, stages, LayoutFor(stages, item4.Item3)));
				usedTemplates.Add(item4.Item1);
			}
			return;
		}
		if (list2 != null)
		{
			(string, string, bool) tuple = list2.FirstOrDefault(((string Key, string Rs, bool Layout) c) => c.Rs == rs, list2[0]);
			items.Add(PsoDb.Item(tuple.Item1, rs, stages, LayoutFor(stages, tuple.Item3)));
			usedTemplates.Add(tuple.Item1);
			return;
		}
		(uint[], uint) tuple2 = Targets(stages);
		uint[] item = tuple2.Item1;
		uint item2 = tuple2.Item2;
		string key = $"{shape}|{topo}|{string.Join(',', item)}|{item2}";
		List<PsoDb.LayoutElem> layout = LayoutFor(stages, templateHasLayout: true);
		if (synthByShape.TryGetValue(key, out string value2))
		{
			items.Add(PsoDb.Item(value2, rs, stages, layout));
			return;
		}
		PsoDb.Rec item3 = new PsoDb.Rec('S', PsoDb.Stream(rs, stages, layout, topo, item, item2));
		synthByShape[key] = item3.Key;
		synthesized.Add(item3);
	}

	private void PerStage(UnitPolicy policy)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		ExactLayouts facts = ExactLayouts.Build(recs, recBlobs, policy, bc, budget.Tick);
		UnitCover unitCover = UnitCover.Seeded(facts, (string h) => bc.ContainsKey(h) || recBlobs.ContainsKey(h));
		HashSet<Unit> hashSet = unitCover.Covered.ToHashSet();
		Dictionary<string, Resolved<List<List<PsoDb.LayoutElem>>>> layouts = new Dictionary<string, Resolved<List<List<PsoDb.LayoutElem>>>>();
		Dictionary<string, Resolved<List<string>>> shapes = new Dictionary<string, Resolved<List<string>>>();
		Dictionary<string, List<uint>> topos = new Dictionary<string, List<uint>>();
		List<(UnitCover.Candidate C, Provenance Vs, Provenance Ps, bool Cross)> cands = new List<(UnitCover.Candidate, Provenance, Provenance, bool)>();
		int pairs = 0;
		Resolved<List<List<PsoDb.LayoutElem>>> none = new Resolved<List<List<PsoDb.LayoutElem>>>(new List<List<PsoDb.LayoutElem>>(), Provenance.Exact);
		HashSet<string> pairedElsewhere = new HashSet<string>();
		StageSets((SortedDictionary<int, string> stages, string _, string _) =>
		{
			if (!FirstVisit(stages, out string rs))
			{
				if (!crossPool.HasValue && crossPools.Count > 0)
				{
					pairedElsewhere.Add(PsoDb.Tuple(mapRoots ? (rs ?? "") : "", stages));
				}
			}
			else if (rs == null)
			{
				Count(NoRs);
			}
			else
			{
				if (!have.Contains(PsoDb.Tuple(rs, stages)))
				{
					string text = Uncovered(rs, stages);
					if (text != null)
					{
						Count(text);
						return;
					}
				}
				if (rsBlobs.TryGetValue(rs, out byte[] value3))
				{
					facts.AddRootSignature(rs, value3);
				}
				Resolved<List<List<PsoDb.LayoutElem>>> resolved;
				Resolved<List<List<PsoDb.LayoutElem>>> value5;
				if (!stages.TryGetValue(1, out string value4))
				{
					resolved = none;
				}
				else if (!layouts.TryGetValue(value4, out value5))
				{
					Resolved<List<List<PsoDb.LayoutElem>>> resolved2 = (layouts[value4] = facts.Layouts(bc[value4]));
					resolved = resolved2;
				}
				else
				{
					resolved = value5;
				}
				Resolved<List<List<PsoDb.LayoutElem>>> resolved4 = resolved;
				Resolved<List<string>> resolved5;
				Resolved<List<string>> value7;
				if (!stages.TryGetValue(2, out string value6))
				{
					resolved5 = new Resolved<List<string>>(new List<string>(), Provenance.Exact);
				}
				else if (!shapes.TryGetValue(value6, out value7))
				{
					Resolved<List<string>> resolved6 = (shapes[value6] = facts.Shapes(bc[value6]));
					resolved5 = resolved6;
				}
				else
				{
					resolved5 = value7;
				}
				Resolved<List<string>> resolved8 = resolved5;
				List<uint> value8 = new List<uint>();
				if (value4 != null)
				{
					string key = $"{value4}|{stages.GetValueOrDefault(5)}|{stages.ContainsKey(4)}";
					if (!topos.TryGetValue(key, out value8))
					{
						value8 = (topos[key] = facts.Topologies(stages).Value);
					}
				}
				if (value6 != null && (value4 != null || stages.ContainsKey(25)))
				{
					pairs++;
				}
				cands.Add((new UnitCover.Candidate(stages, rs, resolved4.Value, value8, resolved8.Value), resolved4.Provenance, resolved8.Provenance, crossPool.HasValue));
			}
		});
		if (pairedElsewhere.Count > 0)
		{
			for (int num = 0; num < cands.Count; num++)
			{
				if (cands[num].Cross && pairedElsewhere.Contains(PsoDb.Tuple(mapRoots ? cands[num].C.Rs : "", cands[num].C.Stages)))
				{
					List<(UnitCover.Candidate C, Provenance Vs, Provenance Ps, bool Cross)> list = cands;
					int num2 = num;
					(UnitCover.Candidate, Provenance, Provenance, bool) value = cands[num];
					value.Item4 = false;
					list[num2] = value;
				}
			}
		}
		List<(UnitCover.Candidate, Provenance, Provenance, bool)> list2 = cands.OrderBy(((UnitCover.Candidate C, Provenance Vs, Provenance Ps, bool Cross) c) => (int)((c.Vs <= c.Ps) ? c.Ps : c.Vs)).ToList();
		Dictionary<UnitCover.Candidate, (Provenance, Provenance)> dictionary = new Dictionary<UnitCover.Candidate, (Provenance, Provenance)>(ReferenceEqualityComparer.Instance);
		foreach (var item in list2)
		{
			dictionary[item.Item1] = (item.Item2, item.Item3);
		}
		List<(UnitCover.Candidate, UnitPick)> list3 = unitCover.CoverAll(list2.Select(((UnitCover.Candidate C, Provenance Vs, Provenance Ps, bool Cross) c) => c.C).ToList(), budget.Tick);
		if (!caps.StateIndependentCache)
		{
			list3 = WithinBudget(list3, facts, hashSet, dictionary);
		}
		int count = list3.Count;
		int num3 = 0;
		int num4 = 0;
		int num5 = Math.Min(MaximumExtras(caps, count + have.Count), extrasRoom);
		if (maximum)
		{
			long? num6 = cacheLeftOut;
			if (!num6.HasValue || num6.GetValueOrDefault() <= 0)
			{
				ILookup<UnitCover.Candidate, (UnitCover.Candidate, UnitPick)> lookup = ((IEnumerable<(UnitCover.Candidate, UnitPick)>)list3).ToLookup((Func<(UnitCover.Candidate, UnitPick), UnitCover.Candidate>)(((UnitCover.Candidate Candidate, UnitPick Pick) p) => p.Candidate), (IEqualityComparer<UnitCover.Candidate>?)ReferenceEqualityComparer.Instance);
				List<(UnitCover.Candidate, UnitPick)> list4 = new List<(UnitCover.Candidate, UnitPick)>();
				int num7 = 0;
				foreach (var item2 in list2)
				{
					budget.Tick();
					IEnumerable<(UnitCover.Candidate, UnitPick)> enumerable = lookup[item2.Item1];
					if (enumerable.Any())
					{
						list4.AddRange(enumerable);
					}
					else if (!have.Contains(PsoDb.Tuple(item2.Item1.Rs, item2.Item1.Stages)))
					{
						if (item2.Item4)
						{
							num4++;
						}
						else if (num7++ < num5)
						{
							list4.Add((item2.Item1, FirstPick(item2.Item1)));
						}
						else
						{
							num3++;
						}
					}
				}
				list3 = list4;
			}
		}
		HashSet<Unit> hashSet2 = hashSet.ToHashSet();
		SortedDictionary<Stage, int> sortedDictionary = new SortedDictionary<Stage, int>();
		foreach (var (candidate, unitPick) in list3)
		{
			budget.Tick();
			var (provenance, provenance2) = dictionary[candidate];
			foreach (Unit item3 in UnitCover.Units(facts, candidate.Stages, candidate.Rs, unitPick.Layout, unitPick.Topology, unitPick.Shape))
			{
				if (hashSet2.Add(item3))
				{
					unitsBy[(int)((item3.Stage == Stage.Vertex) ? provenance : ((item3.Stage == Stage.Pixel) ? provenance2 : Provenance.Exact))]++;
					sortedDictionary[item3.Stage] = sortedDictionary.GetValueOrDefault(item3.Stage) + 1;
				}
			}
			SortedDictionary<int, string> sortedDictionary2 = (SortedDictionary<int, string>)candidate.Stages;
			bool flag = sortedDictionary2.TryGetValue(2, out var value2);
			uint[] rtFormats;
			byte[] rtWriteMasks;
			int[] array;
			if (flag && unitPick.Shape != "")
			{
				(rtFormats, rtWriteMasks, array) = facts.ShapeExample[unitPick.Shape];
			}
			else
			{
				uint[] array2 = Array.Empty<uint>();
				byte[] array3 = Array.Empty<byte>();
				int[] array4 = Array.Empty<int>();
				array = array4;
				rtWriteMasks = array3;
				rtFormats = array2;
			}
			uint num8 = ((!flag) ? 40u : Planner.Targets(bc[value2]).Dsv);
			ExactLayouts.LinkState linkState = ((flag && policy.ExportShape) ? facts.Link(bc[value2], unitPick.Shape) : null);
			if ((object)linkState != null && linkState.Dsv == 0 && num8 != 0)
			{
				linkState = linkState with
				{
					Dsv = num8
				};
			}
			synthesized.Add(new PsoDb.Rec('S', (linkState != null) ? PsoDb.Stream(candidate.Rs, sortedDictionary2, unitPick.Layout, unitPick.Topology, rtFormats, linkState.Dsv, null, -1, linkState.Blend, linkState.DepthStencil) : PsoDb.Stream(candidate.Rs, sortedDictionary2, unitPick.Layout, unitPick.Topology, rtFormats, num8, rtWriteMasks, (array.Length != 0) ? array[0] : (-1))));
			usedRs.Add(candidate.Rs);
			Count("generated");
		}
		List<ShaderInfo> list5 = (from h in maps.Where(((string Platform, bool Pooled, IReadOnlyList<string> Shas, bool IsPipeline, string Rs) m) => m.Platform == plat).SelectMany(((string Platform, bool Pooled, IReadOnlyList<string> Shas, bool IsPipeline, string Rs) m) => m.Shas).Distinct()
			where bc.TryGetValue(h, out ShaderInfo value3) && value3.Stage == Stage.Vertex
			select bc[h]).ToList();
		int num9 = list5.Count((ShaderInfo v) => facts.ReadLayouts.ContainsKey(v.Sha1));
		int num10 = list5.Count((ShaderInfo v) => !facts.ReadLayouts.ContainsKey(v.Sha1) && v.Inputs.All((SigElement i) => i.SysValue != 0));
		int num11 = list5.Count((ShaderInfo v) => !facts.ReadLayouts.ContainsKey(v.Sha1) && v.Inputs.Any((SigElement i) => i.SysValue == 0) && facts.LayoutsBySig.ContainsKey(ExactLayouts.SigKey(v)));
		layoutCoverage = ((list5.Count == 0) ? 1.0 : ((double)(num9 + num10 + num11) / (double)list5.Count));
		log?.Report($"per-stage ({policy.Name}): {cands.Count} stage sets ({pairs} with a PS), units {hashSet.Count} recorded + {hashSet2.Count - hashSet.Count} new ({string.Join(", ", sortedDictionary.Select((KeyValuePair<Stage, int> s) => $"{s.Key} {s.Value}"))}; exact/inferred/guessed {string.Join('/', unitsBy)}), cover {count} PSOs{(maximum ? $" + {list3.Count - count} for stage sets it doesn't need (maximum{((num3 > 0) ? $"; {num3} more left out, at most {num5} for this game" : "")}{((num4 > 0) ? $"; {num4} more left out from {crossPools.Count} pools whose signatures pair nearly every VS with every PS (largest {crossPools.Max().Pairs} pairs of {crossPools.Max().Vs} VS and {crossPools.Max().Ps} PS): only a recording says which the game draws" : "")})" : "")}; VS layouts resolved from the recording: {layoutCoverage:P0} of {list5.Count} (L0 {num9}, L0' no vertex input {num10}, L1 {num11}) ({stopwatch.Elapsed.TotalSeconds:F1}s)" + ((facts.ClassFixes.Count > 0) ? ("; guessed layouts with a mixed slot: " + string.Join(", ", facts.ClassFixes.Select((KeyValuePair<string, int> f) => $"{f.Key} {f.Value}"))) : ""));
		if (layoutCoverage < 0.8 && policy.ReadLayout)
		{
			log?.Report($"warning: only {layoutCoverage:P0} of the game's vertex shaders have a recorded input layout; the rest are inferred or guessed " + "(a guessed layout that's wrong costs a compile in game): a longer recording helps");
		}
	}

	private List<(UnitCover.Candidate Candidate, UnitPick Pick)> WithinBudget(List<(UnitCover.Candidate Candidate, UnitPick Pick)> picks, ExactLayouts facts, HashSet<Unit> recorded, Dictionary<UnitCover.Candidate, (Provenance Vs, Provenance Ps)> prov)
	{
		int num = 1 + OtherSamplerSets().Count;
		int num2 = (int)Math.Clamp((budgetPsos - recs.Count(Replays)) / num, 0L, 2147483647L);
		cacheLeftOut = Math.Max(0, picks.Count - num2);
		extrasRoom = Math.Max(0, num2 - picks.Count);
		if (cacheLeftOut == 0)
		{
			return picks;
		}
		Dictionary<Unit, int> served = new Dictionary<Unit, int>();
		HashSet<Unit> hashSet = new HashSet<Unit>();
		foreach (UnitCover.Candidate key in prov.Keys)
		{
			budget.Tick();
			UnitPick unitPick = FirstPick(key);
			foreach (string item2 in key.Shapes.DistinctBy((string s) => s != "").DefaultIfEmpty(unitPick.Shape))
			{
				foreach (List<PsoDb.LayoutElem> item3 in key.Layouts.DefaultIfEmpty(unitPick.Layout))
				{
					foreach (uint item4 in key.Topologies.DefaultIfEmpty(unitPick.Topology))
					{
						hashSet.UnionWith(UnitCover.Units(facts, key.Stages, key.Rs, item3, item4, item2));
					}
				}
			}
			foreach (string shape in key.Shapes)
			{
				hashSet.UnionWith(UnitCover.Units(facts, key.Stages, key.Rs, unitPick.Layout, unitPick.Topology, shape));
			}
			foreach (Unit item5 in hashSet)
			{
				served[item5] = served.GetValueOrDefault(item5) + 1;
			}
			hashSet.Clear();
		}
		HashSet<Unit> covered = recorded.ToHashSet();
		HashSet<string> compiled = recorded.Select((Unit u) => u.Shader).ToHashSet();
		List<((UnitCover.Candidate, UnitPick), bool, Provenance, long, int)> list = (from r in picks.Select(((UnitCover.Candidate Candidate, UnitPick Pick) p, int i) =>
			{
				budget.Tick();
				List<Unit> source = UnitCover.Units(facts, p.Candidate.Stages, p.Candidate.Rs, p.Pick.Layout, p.Pick.Topology, p.Pick.Shape).Where(covered.Add).ToList();
				bool item = source.Count((Unit u) => compiled.Add(u.Shader)) > 0;
				var (provenance, provenance2) = prov[p.Candidate];
				return (p: p, First: item, Prov: (provenance > provenance2) ? provenance : provenance2, Served: ((IEnumerable<Unit>)source).Sum((Func<Unit, long>)((Unit u) => served.GetValueOrDefault(u))), At: i);
			})
			orderby !r.First, r.Prov, r.Served descending, r.At
			select r).ToList().Take(num2).ToList();
		stats["cache_budget"] = (int)cacheLeftOut.Value;
		log?.Report($"cache budget ({caps.Profile}): {list.Count} of {picks.Count} cover PSOs kept ({list.Count((((UnitCover.Candidate Candidate, UnitPick Pick) p, bool First, Provenance Prov, long Served, int At) r) => r.First)} compile a shader the first time)" + ((num > 1) ? $" at {num} texture filtering settings" : "") + $" beside the recorded ones, {cacheLeftOut} left out: " + $"at {28L} KiB a PSO the plan stays under the {Format.Bytes(budgetPsos * 28672)} the driver{(caps.PerGameCacheCap.HasValue ? " keeps for one game" : "'s whole cache holds")}");
		return list.Select((((UnitCover.Candidate Candidate, UnitPick Pick) p, bool First, Provenance Prov, long Served, int At) r) => r.p).ToList();
	}

	public static int MaximumExtras(VendorCaps caps, long planned)
	{
		if (!caps.StateIndependentCache)
		{
			return (int)Math.Min(planned, Math.Min(2147483648L, caps.PerGameCacheCap ?? long.MaxValue) / 22528);
		}
		return int.MaxValue;
	}

	public static long CacheBudgetOf(VendorCaps caps)
	{
		return caps.PerGameCacheCap ?? 17179869184L;
	}

	private static UnitPick FirstPick(UnitCover.Candidate c)
	{
		bool flag = c.Stages.ContainsKey(1);
		return new UnitPick((flag && c.Layouts.Count > 0) ? c.Layouts[0] : new List<PsoDb.LayoutElem>(), flag ? ((c.Topologies.Count > 0) ? c.Topologies[0] : 3u) : 0u, (c.Stages.ContainsKey(2) && c.Shapes.Count > 0) ? c.Shapes[0] : "");
	}

	private bool VendorOnly(string h)
	{
		if (!caps.Profile.StartsWith("amd") && bc.TryGetValue(h, out ShaderInfo value))
		{
			return value.Bindings.Any((Binding b) => b.Space == 2147420894);
		}
		return false;
	}

	private bool OnPlatform(string mapPlat)
	{
		if (mapPlat == plat)
		{
			return true;
		}
		if (!caps.Profile.StartsWith("amd") || !mapPlat.StartsWith(plat + " wave"))
		{
			return false;
		}
		string[] array = mapPlat.Substring(plat.Length + 5).Split('-');
		if (uint.TryParse(array[0], out var result) && uint.TryParse(array[^1], out var result2) && result <= 64)
		{
			return result2 >= 64;
		}
		return false;
	}

	private void StageSets(Action<SortedDictionary<int, string>, string, string> sink)
	{
		HashSet<string> ags = new HashSet<string>();
		HashSet<string> unpairedShaders = new HashSet<string>();
		HashSet<string> fed = new HashSet<string>();
		List<ShaderInfo> list = new List<ShaderInfo>();
		foreach (var map in maps)
		{
			string item = map.Platform;
			bool item2 = map.Pooled;
			IReadOnlyList<string> item3 = map.Shas;
			bool item4 = map.IsPipeline;
			string item5 = map.Rs;
			ct.ThrowIfCancellationRequested();
			if (!OnPlatform(item))
			{
				continue;
			}
			List<string> list2 = item3.Where(bc.ContainsKey).ToList();
			List<ShaderInfo> list3 = (from key in list2.Where(Usable)
				select bc[key]).ToList();
			if (item4 && list3.Count < list2.Count)
			{
				continue;
			}
			if (item4)
			{
				SortedDictionary<int, string> sortedDictionary = new SortedDictionary<int, string>();
				foreach (ShaderInfo item8 in list3)
				{
					sortedDictionary[(int)item8.Stage] = item8.Sha1;
				}
				if (RootSig.InStageSets(rule, sortedDictionary.Keys.Select((int result) => (Stage)result)))
				{
					mapRs = item5;
					string rs;
					if (sortedDictionary.Count > 0 && Planner.Positioned(sortedDictionary.ToDictionary((KeyValuePair<int, string> x) => (Stage)x.Key, (KeyValuePair<int, string> x) => bc[x.Value])))
					{
						sink(sortedDictionary, Planner.Shape(sortedDictionary), PsOut(sortedDictionary));
					}
					else if (sortedDictionary.Count > 0 && FirstVisit(sortedDictionary, out rs))
					{
						PlanBuilder planBuilder = this;
						string k;
						if (rs != null && have.Contains(PsoDb.Tuple(rs, sortedDictionary)))
						{
							k = "already_recorded";
						}
						else
						{
							k = (unbuildable ? NoRs : "stream_output");
						}
						planBuilder.Count(k);
					}
					mapRs = null;
				}
				continue;
			}
			Dictionary<string, List<ShaderInfo>> srcs = new Dictionary<string, List<ShaderInfo>>();
			Dictionary<string, List<ShaderInfo>> dictionary = new Dictionary<string, List<ShaderInfo>>();
			foreach (ShaderInfo item9 in list3)
			{
				if (item9.Stage == Stage.Compute)
				{
					sink(new SortedDictionary<int, string> { [6] = item9.Sha1 }, Planner.Shape(Stage.Compute), "");
				}
				Stage stage = item9.Stage;
				if ((stage == Stage.Vertex || stage == Stage.Mesh) ? true : false)
				{
					if (item9.Stage == Stage.Vertex)
					{
						Planner.Push(srcs, Planner.Sig(item9.Outputs), item9);
					}
					if (Planner.Rasterizable(item9))
					{
						sink(new SortedDictionary<int, string> { [(int)item9.Stage] = item9.Sha1 }, Planner.Shape(item9.Stage), "");
					}
				}
				if (item9.Stage == Stage.Geometry)
				{
					Planner.Push(dictionary, Planner.Sig(item9.Outputs), item9);
				}
			}
			List<ShaderInfo> source = list3.Where((ShaderInfo shaderInfo2) => shaderInfo2.Stage == Stage.Mesh).ToList();
			List<ShaderInfo> rasterMesh = source.Where(Planner.Rasterizable).ToList();
			Dictionary<string, ILookup<string, (int At, ShaderInfo Vs)>> byPart = new Dictionary<string, ILookup<string, (int, ShaderInfo)>>();
			List<ShaderInfo> list4 = list3.Where((ShaderInfo shaderInfo2) => shaderInfo2.Stage == Stage.Pixel).ToList();
			Dictionary<string, int> fanIn = srcs.ToDictionary((KeyValuePair<string, List<ShaderInfo>> keyValuePair) => keyValuePair.Key, (KeyValuePair<string, List<ShaderInfo>> keyValuePair) => keyValuePair.Value.Count(Planner.Rasterizable));
			long num = ((IEnumerable<ShaderInfo>)list4).Sum((Func<ShaderInfo, long>)((ShaderInfo shaderInfo2) => fanIn.GetValueOrDefault(Planner.Sig(shaderInfo2.Inputs))));
			int num2 = srcs.Values.Sum((List<ShaderInfo> l) => l.Count);
			if (num > 50L * (long)(num2 + list4.Count))
			{
				List<(long Pairs, int Vs, int Ps)> list5 = crossPools;
				(long, int, int)? tuple = (crossPool = (num, num2, list4.Count));
				list5.Add(tuple.Value);
			}
			foreach (ShaderInfo p in list4)
			{
				budget.Tick();
				string text = Planner.Sig(p.Inputs);
				int num3 = 0;
				// Exo Shaders: the same pure tests, the cheap root-signature one first, and the mesh shaders' own
				// Rasterizable test once per map instead of once per pixel shader (rasterMesh keeps source's order).
				foreach (ShaderInfo item10 in (from s in Feeding(text, p)
					where Planner.Rasterizable(s) && Planner.SameRs(s, p) && Planner.Links(s, p)
					select s).Concat(rasterMesh.Where((ShaderInfo shaderInfo2) => Planner.SameRs(shaderInfo2, p) && Planner.MeshFeeds(shaderInfo2, p))))
				{
					num3++;
					sink(new SortedDictionary<int, string>
					{
						[(int)item10.Stage] = item10.Sha1,
						[2] = p.Sha1
					}, Planner.Shape(item10.Stage, Stage.Pixel), Planner.Sig(p.Outputs, dropSys: false));
				}
				foreach (ShaderInfo g in from src in dictionary.GetValueOrDefault(text, new List<ShaderInfo>())
					where Planner.Links(src, p)
					select src)
				{
					foreach (ShaderInfo item11 in from shaderInfo2 in srcs.GetValueOrDefault(Planner.Sig(g.Inputs), new List<ShaderInfo>())
						where shaderInfo2.Stage == Stage.Vertex && Planner.Links(shaderInfo2, g) && Planner.SameRs(shaderInfo2, g, p)
						select shaderInfo2)
					{
						num3++;
						sink(new SortedDictionary<int, string>
						{
							[1] = item11.Sha1,
							[5] = g.Sha1,
							[2] = p.Sha1
						}, Planner.Shape(Stage.Vertex, Stage.Pixel, Stage.Geometry), Planner.Sig(p.Outputs, dropSys: false));
					}
				}
				if (num3 > 0)
				{
					fed.Add(p.Sha1);
				}
				else if (!item2)
				{
					list.Add(p);
				}
			}
			crossPool = null;
			foreach (ShaderInfo g2 in list3.Where((ShaderInfo shaderInfo2) => shaderInfo2.Stage == Stage.Geometry && Planner.Rasterizable(shaderInfo2)))
			{
				foreach (ShaderInfo item12 in from shaderInfo2 in srcs.GetValueOrDefault(Planner.Sig(g2.Inputs), new List<ShaderInfo>())
					where shaderInfo2.Stage == Stage.Vertex && Planner.Links(shaderInfo2, g2) && Planner.SameRs(shaderInfo2, g2)
					select shaderInfo2)
				{
					sink(new SortedDictionary<int, string>
					{
						[1] = item12.Sha1,
						[5] = g2.Sha1
					}, Planner.Shape(Stage.Vertex, Stage.Geometry), "");
				}
			}
			foreach (ShaderInfo a in list3.Where((ShaderInfo shaderInfo2) => shaderInfo2.Stage == Stage.Amplification))
			{
				foreach (ShaderInfo m in source.Where((ShaderInfo shaderInfo2) => Planner.SameRs(a, shaderInfo2) && Planner.Launches(a, shaderInfo2)))
				{
					if (Planner.Rasterizable(m))
					{
						sink(new SortedDictionary<int, string>
						{
							[24] = a.Sha1,
							[25] = m.Sha1
						}, Planner.Shape(Stage.Amplification, Stage.Mesh), "");
					}
					foreach (ShaderInfo item13 in list3.Where((ShaderInfo shaderInfo2) => shaderInfo2.Stage == Stage.Pixel && Planner.Rasterizable(m) && Planner.MeshFeeds(m, shaderInfo2) && Planner.SameRs(a, m, shaderInfo2)))
					{
						sink(new SortedDictionary<int, string>
						{
							[24] = a.Sha1,
							[25] = m.Sha1,
							[2] = item13.Sha1
						}, Planner.Shape(Stage.Amplification, Stage.Mesh, Stage.Pixel), Planner.Sig(item13.Outputs, dropSys: false));
					}
				}
			}
			ILookup<string, ShaderInfo> lookup = list3.Where((ShaderInfo shaderInfo2) => shaderInfo2.Stage == Stage.Pixel).ToLookup((ShaderInfo shaderInfo2) => Planner.Sig(shaderInfo2.Inputs));
			ILookup<string, ShaderInfo> lookup2 = list3.Where((ShaderInfo shaderInfo2) => shaderInfo2.Stage == Stage.Geometry && Planner.TopologyType(shaderInfo2.GsInputPrimitive) == 3).ToLookup((ShaderInfo shaderInfo2) => Planner.Sig(shaderInfo2.Inputs));
			foreach (var item14 in Planner.TessPairs(list3))
			{
				ShaderInfo h = item14.Hs;
				ShaderInfo d = item14.Ds;
				List<(ShaderInfo, ShaderInfo)> list6 = new List<(ShaderInfo, ShaderInfo)>();
				foreach (ShaderInfo g3 in (Planner.Rasterizable(d) ? new ShaderInfo[1] : Array.Empty<ShaderInfo>()).Concat(lookup2[Planner.Sig(d.Outputs)].Where((ShaderInfo shaderInfo2) => Planner.Links(d, shaderInfo2) && Planner.Rasterizable(shaderInfo2))))
				{
					budget.Tick();
					ShaderInfo last = g3 ?? d;
					list6.Add((g3, null));
					list6.AddRange(from item8 in lookup[Planner.Sig(last.Outputs)]
						where Planner.Links(last, item8)
						select (g: g3, item8));
				}
				foreach (ShaderInfo v in from shaderInfo2 in srcs.GetValueOrDefault(Planner.Sig(h.Inputs), new List<ShaderInfo>())
					where shaderInfo2.Stage == Stage.Vertex && Planner.Links(shaderInfo2, h) && Planner.SameRs(shaderInfo2, h, d)
					select shaderInfo2)
				{
					foreach (var item15 in list6.Where(((ShaderInfo Gs, ShaderInfo Ps) t) =>
					{
						List<ShaderInfo> list8 = new List<ShaderInfo>();
						list8.Add(v);
						list8.Add(h);
						list8.Add(d);
						list8.AddRange(new ShaderInfo[2] { t.Gs, t.Ps }.OfType<ShaderInfo>());
						return Planner.SameRs(list8.ToArray());
					}))
					{
						ShaderInfo item6 = item15.Item1;
						ShaderInfo item7 = item15.Item2;
						SortedDictionary<int, string> sortedDictionary2 = new SortedDictionary<int, string>
						{
							[1] = v.Sha1,
							[4] = h.Sha1,
							[3] = d.Sha1
						};
						if (item6 != null)
						{
							sortedDictionary2[5] = item6.Sha1;
						}
						if (item7 != null)
						{
							sortedDictionary2[2] = item7.Sha1;
						}
						sink(sortedDictionary2, Planner.Shape(sortedDictionary2), (item7 != null) ? Planner.Sig(item7.Outputs, dropSys: false) : "");
					}
				}
			}
			List<ShaderInfo> Feeding(string pin, ShaderInfo shaderInfo2)
			{
				List<ShaderInfo> valueOrDefault = srcs.GetValueOrDefault(pin, new List<ShaderInfo>());
				if (learnedParts != null)
				{
					if (!byPart.TryGetValue(pin, out ILookup<string, (int At, ShaderInfo Vs)> l))
					{
						byPart[pin] = (l = valueOrDefault.Select((ShaderInfo item8, int i) => (i: i, v: item8)).ToLookup(((int i, ShaderInfo v) x) => Part(x.v)));
					}
					List<ShaderInfo> obj = (learnedVs.TryGetValue(Part(shaderInfo2), out HashSet<string> value) ? (from x in value.SelectMany((string key) => l[key])
						orderby x.At
						select x.Vs).ToList() : new List<ShaderInfo>());
					if (obj.Count < valueOrDefault.Count)
					{
						unpairedShaders.Add(shaderInfo2.Sha1);
					}
					return obj;
				}
				return valueOrDefault;
			}
		}
		List<ShaderInfo> source2 = (from key in maps.Where(((string Platform, bool Pooled, IReadOnlyList<string> Shas, bool IsPipeline, string Rs) tuple3) => tuple3.Pooled && OnPlatform(tuple3.Platform)).SelectMany(((string Platform, bool Pooled, IReadOnlyList<string> Shas, bool IsPipeline, string Rs) tuple3) => tuple3.Shas).Where(Usable)
			select bc[key]).ToList();
		Dictionary<string, List<ShaderInfo>> dictionary2 = new Dictionary<string, List<ShaderInfo>>();
		foreach (var (mapPlat, flag, source3, _, _) in maps)
		{
			if (flag || !OnPlatform(mapPlat))
			{
				continue;
			}
			foreach (string item16 in source3.Where((string text2) => Usable(text2) && bc[text2].Stage == Stage.Pixel))
			{
				Planner.Push(dictionary2, Planner.Sig(bc[item16].Inputs), bc[item16]);
			}
		}
		if (ags.Count > 0)
		{
			stats["vendor_extension"] = ags.Count;
		}
		ILookup<string, ShaderInfo> lookup3 = source2.Where((ShaderInfo shaderInfo2) => shaderInfo2.Stage == Stage.Vertex).ToLookup((ShaderInfo shaderInfo2) => Planner.Sig(shaderInfo2.Outputs));
		foreach (ShaderInfo g4 in source2.Where((ShaderInfo shaderInfo2) => shaderInfo2.Stage == Stage.Geometry))
		{
			List<ShaderInfo> source4 = lookup3[Planner.Sig(g4.Inputs)].Where((ShaderInfo src) => Planner.Links(src, g4)).ToList();
			foreach (ShaderInfo p2 in from dst in dictionary2.GetValueOrDefault(Planner.Sig(g4.Outputs), new List<ShaderInfo>())
				where Planner.Links(g4, dst)
				select dst)
			{
				budget.Tick();
				foreach (ShaderInfo item17 in source4.Where((ShaderInfo shaderInfo2) => Planner.SameRs(shaderInfo2, g4, p2)))
				{
					sink(new SortedDictionary<int, string>
					{
						[1] = item17.Sha1,
						[5] = g4.Sha1,
						[2] = p2.Sha1
					}, Planner.Shape(Stage.Vertex, Stage.Pixel, Stage.Geometry), Planner.Sig(p2.Outputs, dropSys: false));
				}
			}
		}
		ILookup<string, ShaderInfo> lookup4 = source2.Where((ShaderInfo shaderInfo2) => shaderInfo2.Stage == Stage.Vertex && Planner.Rasterizable(shaderInfo2)).ToLookup((ShaderInfo shaderInfo2) => Planner.Sig(shaderInfo2.Outputs));
		List<ShaderInfo> list7 = new List<ShaderInfo>();
		foreach (ShaderInfo p3 in list.Where((ShaderInfo shaderInfo2) => !fed.Contains(shaderInfo2.Sha1)).DistinctBy((ShaderInfo shaderInfo2) => shaderInfo2.Sha1))
		{
			int num4 = 0;
			foreach (ShaderInfo item18 in lookup4[Planner.Sig(p3.Inputs)].Where((ShaderInfo shaderInfo2) => Planner.Links(shaderInfo2, p3) && Planner.SameRs(shaderInfo2, p3)))
			{
				num4++;
				sink(new SortedDictionary<int, string>
				{
					[1] = item18.Sha1,
					[2] = p3.Sha1
				}, Planner.Shape(Stage.Vertex, Stage.Pixel), Planner.Sig(p3.Outputs, dropSys: false));
			}
			if (num4 == 0)
			{
				list7.Add(p3);
			}
		}
		list7.RemoveAll((ShaderInfo shaderInfo2) => shaderInfo2.RootSignature == null);
		if (caps.PerStageCache && list7.Count > 0)
		{
			ILookup<string, ShaderInfo> lookup5 = (from key in maps.Where(((string Platform, bool Pooled, IReadOnlyList<string> Shas, bool IsPipeline, string Rs) tuple3) => !tuple3.Pooled && OnPlatform(tuple3.Platform)).SelectMany(((string Platform, bool Pooled, IReadOnlyList<string> Shas, bool IsPipeline, string Rs) tuple3) => tuple3.Shas).Distinct()
				where Usable(key) && bc[key].Stage == Stage.Vertex && Planner.Rasterizable(bc[key])
				select bc[key]).ToLookup((ShaderInfo shaderInfo2) => Planner.Sig(shaderInfo2.Outputs));
			foreach (ShaderInfo p4 in list7)
			{
				ShaderInfo shaderInfo = lookup5[Planner.Sig(p4.Inputs)].FirstOrDefault((ShaderInfo shaderInfo2) => Planner.Links(shaderInfo2, p4) && Planner.SameRs(shaderInfo2, p4));
				if ((object)shaderInfo != null)
				{
					Count("orphan_ps_paired");
					sink(new SortedDictionary<int, string>
					{
						[1] = shaderInfo.Sha1,
						[2] = p4.Sha1
					}, Planner.Shape(Stage.Vertex, Stage.Pixel), Planner.Sig(p4.Outputs, dropSys: false));
				}
			}
		}
		if (engine.Family == "Unreal" && engine.Version.StartsWith("5."))
		{
			SharedAcrossMaps(sink, Usable);
		}
		if ((unpaired = unpairedShaders.Count) > 0)
		{
			stats["no_rs"] = stats.GetValueOrDefault("no_rs") + unpaired;
		}
		bool Usable(string text2)
		{
			if (!bc.TryGetValue(text2, out ShaderInfo value))
			{
				return false;
			}
			if (VendorOnly(text2))
			{
				ags.Add(text2);
				return false;
			}
			if (learnedParts == null || value.Stage == Stage.Library || learnedParts.Contains(Part(value)))
			{
				return true;
			}
			unpairedShaders.Add(text2);
			return false;
		}
	}

	private void SharedAcrossMaps(Action<SortedDictionary<int, string>, string, string> sink, Func<string, bool> usable)
	{
		List<(string Platform, bool Pooled, IReadOnlyList<string> Shas, bool IsPipeline, string Rs)> own = maps.Where(((string Platform, bool Pooled, IReadOnlyList<string> Shas, bool IsPipeline, string Rs) tuple) => !tuple.Pooled && !tuple.IsPipeline && OnPlatform(tuple.Platform)).ToList();
		Dictionary<string, int> inMaps = own.SelectMany(((string Platform, bool Pooled, IReadOnlyList<string> Shas, bool IsPipeline, string Rs) tuple) => tuple.Shas.Distinct()).CountBy((string h) => h).ToDictionary();
		List<ShaderInfo> source = (from h in inMaps.Keys.Where(usable)
			select bc[h]).ToList();
		List<ShaderInfo> source2 = source.Where((ShaderInfo s) => s.Stage == Stage.Pixel).ToList();
		ILookup<string, ShaderInfo> lookup = source2.ToLookup((ShaderInfo p) => Planner.Sig(p.Inputs));
		ILookup<string, ShaderInfo> lookup2 = source2.Where(Shared).ToLookup((ShaderInfo p) => Planner.Sig(p.Inputs));
		foreach (ShaderInfo v in source.Where((ShaderInfo s) => s.Stage == Stage.Vertex && Planner.Rasterizable(s)))
		{
			foreach (ShaderInfo item in lookup2[Planner.Sig(v.Outputs)].Where((ShaderInfo p) => Planner.SameRs(v, p) && Planner.Links(v, p)))
			{
				sink(new SortedDictionary<int, string>
				{
					[1] = v.Sha1,
					[2] = item.Sha1
				}, Planner.Shape(Stage.Vertex, Stage.Pixel), Planner.Sig(item.Outputs, dropSys: false));
			}
		}
		foreach (IGrouping<string, ShaderInfo> item2 in from shaderInfo in source
			where shaderInfo.Stage == Stage.Mesh && Planner.Rasterizable(shaderInfo)
			group shaderInfo by string.Join(';', shaderInfo.Outputs.Where(Planner.PerPrimitive).Select(Key).Order()))
		{
			HashSet<(string, int)> prim = item2.First().Outputs.Where(Planner.PerPrimitive).Select(Key).ToHashSet();
			ILookup<string, ShaderInfo> lookup3 = source2.Where((ShaderInfo p) => p.Inputs.Any((SigElement i) => prim.Contains(Key(i)))).ToLookup((ShaderInfo p) => Planner.SigUncached(p.Inputs.Where((SigElement i) => !prim.Contains(Key(i))).ToList()));
			foreach (ShaderInfo m in item2)
			{
				foreach (ShaderInfo item3 in from p in lookup[Planner.Sig(m.Outputs)].Concat(lookup3[Planner.Sig(m.Outputs)]).Distinct()
					where (Shared(m) || Shared(p)) && Planner.SameRs(m, p) && Planner.MeshFeeds(m, p)
					select p)
				{
					sink(new SortedDictionary<int, string>
					{
						[25] = m.Sha1,
						[2] = item3.Sha1
					}, Planner.Shape(Stage.Mesh, Stage.Pixel), Planner.Sig(item3.Outputs, dropSys: false));
				}
			}
		}
		static (string, int) Key(SigElement e)
		{
			return (e.Semantic.ToUpperInvariant(), e.Index);
		}
		bool Shared(ShaderInfo s)
		{
			if (inMaps[s.Sha1] > 1)
			{
				return inMaps[s.Sha1] * 50 >= own.Count;
			}
			return false;
		}
	}

	private void RtPlan()
	{
		List<string> list = (from h in maps.Where(((string Platform, bool Pooled, IReadOnlyList<string> Shas, bool IsPipeline, string Rs) m) => m.Platform == plat).SelectMany(((string Platform, bool Pooled, IReadOnlyList<string> Shas, bool IsPipeline, string Rs) m) => m.Shas).Distinct()
			where bc.TryGetValue(h, out ShaderInfo value6) && value6.Stage == Stage.Library
			select h).ToList();
		if (list.Count == 0)
		{
			return;
		}
		if (engine.NoRtPipelines)
		{
			log?.Report($"ray tracing: {list.Count} DXIL libraries; the game's config turns ray tracing pipelines off (r.RayTracing or r.RayTracing.AllowPipeline): none synthesized");
			return;
		}
		PlanRule planRule = rules.RtFor(engine);
		if ((object)planRule != null)
		{
			RtRule? rt = planRule.Rt;
			bool? flag;
			if ((object)rt == null)
			{
				flag = null;
			}
			else
			{
				string[]? array = rt.Select;
				flag = ((array != null) ? new bool?(array.Contains("red3HitGroups")) : ((bool?)null));
			}
			bool? flag2 = flag;
			if (flag2 == true)
			{
				Red3HitGroups(list.Count);
				return;
			}
		}
		if ((object)planRule != null)
		{
			RtRule? rt2 = planRule.Rt;
			bool? flag3;
			if ((object)rt2 == null)
			{
				flag3 = null;
			}
			else
			{
				string[]? array2 = rt2.Select;
				flag3 = ((array2 != null) ? new bool?(array2.Contains("soulsHitGroups")) : ((bool?)null));
			}
			bool? flag2 = flag3;
			if (flag2 == true)
			{
				SoulsHitGroups(list.Count);
				return;
			}
		}
		List<RtCollections.Recorded> list2 = stateObjects.Select(RtCollections.Read).OfType<RtCollections.Recorded>().ToList();
		string text = null;
		NvSlot nvSlot = planRule?.Rt?.Nv;
		RtCollections.Nv? nv = (((object)nvSlot != null) ? new RtCollections.Nv?(new RtCollections.Nv(nvSlot.Slot, nvSlot.Space, 0u)) : ((RtCollections.Nv?)null));
		RtCollections.Rule rule;
		string value;
		if (list2.Count > 0)
		{
			(uint, uint) tuple = Mode<(uint, uint)>(list2.Select((RtCollections.Recorded c) => (Payload: c.Payload, Attributes: c.Attributes)));
			List<(uint, uint)> list3 = (from c in list2
				where recBlobs.ContainsKey(c.Library)
				select (Payload: c.Payload, Own: RtCollections.OwnPayload(recBlobs[c.Library])) into p
				where p.Own != 0
				select p).ToList();
			uint payload = ((list3.Count <= 0 || !((double)list3.Count(((uint Payload, uint Own) p) => p.Payload == p.Own) >= 0.99 * (double)list3.Count)) ? tuple.Item1 : 0u);
			rule = new RtCollections.Rule(Mode<string>(list2.Select((RtCollections.Recorded c) => c.Global)), Mode<uint>(list2.Select((RtCollections.Recorded c) => c.Flags)), Mode<uint>(list2.Select((RtCollections.Recorded c) => c.Depth)), payload, tuple.Item2, Verified: false);
			HashSet<string> hashSet = stateObjects.Select((PsoDb.Rec r) => r.Key).ToHashSet();
			int num = 0;
			int num2 = 0;
			foreach (RtCollections.Recorded item2 in list2.Where((RtCollections.Recorded c) => recBlobs.ContainsKey(c.Library) && bc.ContainsKey(c.Library)))
			{
				num++;
				string text2 = LocalRs(bc[item2.Library], rayGen: true);
				string text3 = LocalRs(bc[item2.Library], rayGen: false);
				if (text2 != null && text3 != null)
				{
					byte[] array3 = RtCollections.Collection(recBlobs[item2.Library], item2.Library, rule.GlobalRs, text2, text3, item2.Payload, item2.Attributes, rule.Depth, rule.Flags, item2.NameHash);
					if (array3 != null && hashSet.Contains(new PsoDb.Rec('R', array3).Key))
					{
						num2++;
					}
				}
			}
			rule = rule with
			{
				Verified = (num > 0 && (double)num2 >= 0.99 * (double)num)
			};
			value = $"learned from {list2.Count} recorded collections, {num2}/{num} rebuilt byte for byte";
			if (!rule.Verified)
			{
				log?.Report($"ray tracing: {list.Count} DXIL libraries; collection rule {value}: not the engine's rule, none synthesized");
				return;
			}
		}
		else
		{
			EngineInfo engineInfo = engine;
			List<ShaderInfo> list4 = new List<ShaderInfo>();
			list4.AddRange(list.Select((string l) => bc[l]));
			RtCollections.Choice choice = RtCollections.Choose(planRule, engineInfo, new _003C_003Ez__ReadOnlyList<ShaderInfo>(list4), out string why);
			if ((object)choice == null)
			{
				log?.Report($"ray tracing: {list.Count} DXIL libraries; {why}: none synthesized");
				return;
			}
			var (text4, value2) = RtCollections.Serialize(choice.Global, choice.Samplers);
			rsBlobs[text4] = value2;
			if (choice.Local != null)
			{
				byte[] value3;
				(text, value3) = RtCollections.Serialize(choice.Local, Array.Empty<byte>());
				rsBlobs[text] = value3;
			}
			rule = new RtCollections.Rule(text4, choice.Flags, choice.Depth, choice.Payload, choice.Attributes, Verified: false);
			value = choice.How;
		}
		List<PsoDb.Rec> list5 = stateObjects.Where((PsoDb.Rec r) => RtCollections.Read(r) != null).ToList();
		RtCollections.Nv? nv2 = RtCollections.LearnedNv(list5, nvRecs);
		HashSet<string> collectionKeys = list5.Select((PsoDb.Rec r) => r.Key).ToHashSet();
		bool flag4 = !nv2.HasValue && nv.HasValue && !nvRecs.Any((PsoDb.Rec r) => r.Payload.Length == 36 && collectionKeys.Contains(PsoDb.NvState.Parse(r).Target));
		RtCollections.Nv? nv3 = RtCollections.WithOptions(nv2 ?? (flag4 ? nv : ((RtCollections.Nv?)null)), RtOptions);
		object obj;
		if (!nv3.HasValue)
		{
			obj = ((list2.Count > 0) ? "no NVAPI state recorded with the recorded collections" : "NVAPI state unknown without a recording of ray tracing (a game setting NVIDIA's shader-extension slot misses them all)");
		}
		else
		{
			RtCollections.Nv valueOrDefault = nv3.GetValueOrDefault();
			obj = ((valueOrDefault.Slot == uint.MaxValue) ? "no NVAPI extension slot" : $"NVAPI extension slot u{valueOrDefault.Slot} space {valueOrDefault.Space}") + ((valueOrDefault.Options != 0) ? $", creation flags 0x{valueOrDefault.Options:x}" : "") + ", " + (flag4 ? "as the rule sets it" : "as the recorded state objects");
		}
		string value4 = (string)obj;
		HashSet<string> hashSet2 = stateObjects.SelectMany((PsoDb.Rec r) => PsoDb.ParseStateObject(r).Libraries).ToHashSet();
		RootSig.Ranges ranges = ((rsBlobs.TryGetValue(rule.GlobalRs, out byte[] value5) || recBlobs.TryGetValue(rule.GlobalRs, out value5)) ? RootSig.Parse(value5) : null);
		foreach (string item3 in list)
		{
			if (hashSet2.Contains(item3))
			{
				Count("rt_recorded");
				continue;
			}
			(string, string) tuple4 = ((text != null) ? (text, text) : (LocalRs(bc[item3], rayGen: true), LocalRs(bc[item3], rayGen: false)));
			var (text5, _) = tuple4;
			if (text5 != null)
			{
				string item = tuple4.Item2;
				if (item != null)
				{
					if (ranges != null)
					{
						(uint, uint, uint, uint, uint, bool)[] slots = ranges.Slots;
						(uint, uint, uint, uint, uint, bool)[] slots2 = RootSig.Parse(rsBlobs[item]).Slots;
						int num3 = 0;
						(uint, uint, uint, uint, uint, bool)[] array4 = new (uint, uint, uint, uint, uint, bool)[slots.Length + slots2.Length];
						ReadOnlySpan<(uint, uint, uint, uint, uint, bool)> readOnlySpan = new ReadOnlySpan<(uint, uint, uint, uint, uint, bool)>(slots);
						readOnlySpan.CopyTo(new Span<(uint, uint, uint, uint, uint, bool)>(array4).Slice(num3, readOnlySpan.Length));
						num3 += readOnlySpan.Length;
						ReadOnlySpan<(uint, uint, uint, uint, uint, bool)> readOnlySpan2 = new ReadOnlySpan<(uint, uint, uint, uint, uint, bool)>(slots2);
						readOnlySpan2.CopyTo(new Span<(uint, uint, uint, uint, uint, bool)>(array4).Slice(num3, readOnlySpan2.Length));
						num3 += readOnlySpan2.Length;
						string text6 = RootSig.Uncovered(new RootSig.Ranges(0u, array4), Stage.Library, bc[item3]);
						if (text6 != null)
						{
							Count("rt_uncovered");
							uncoveredExample.TryAdd("ray tracing " + text6, item3);
							continue;
						}
					}
					rtItems.Add(RtCollections.Item(item3, rule.GlobalRs, text5, item, rule, nv3));
					usedRs.UnionWith(new _003C_003Ez__ReadOnlyArray<string>(new string[3] { rule.GlobalRs, text5, item }));
					continue;
				}
			}
			Count("rt_unserializable");
			rtUnbuildable.Add(item3);
		}
		IProgress<string>? progress = log;
		if (progress != null)
		{
			DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(126, 9);
			defaultInterpolatedStringHandler.AppendLiteral("ray tracing: ");
			defaultInterpolatedStringHandler.AppendFormatted(list.Count);
			defaultInterpolatedStringHandler.AppendLiteral(" DXIL libraries, ");
			defaultInterpolatedStringHandler.AppendFormatted(rtItems.Count);
			defaultInterpolatedStringHandler.AppendLiteral(" collections synthesized (rule ");
			defaultInterpolatedStringHandler.AppendFormatted(value);
			defaultInterpolatedStringHandler.AppendLiteral("; payload ");
			defaultInterpolatedStringHandler.AppendFormatted((rule.Payload == 0) ? "each library's own" : ((object)rule.Payload));
			defaultInterpolatedStringHandler.AppendLiteral(", attributes ");
			defaultInterpolatedStringHandler.AppendFormatted(rule.Attributes);
			defaultInterpolatedStringHandler.AppendLiteral(", depth ");
			defaultInterpolatedStringHandler.AppendFormatted(rule.Depth);
			defaultInterpolatedStringHandler.AppendLiteral("; ");
			defaultInterpolatedStringHandler.AppendFormatted(value4);
			defaultInterpolatedStringHandler.AppendLiteral(")");
			defaultInterpolatedStringHandler.AppendLiteral(", ");
			defaultInterpolatedStringHandler.AppendFormatted(stats.GetValueOrDefault("rt_recorded"));
			defaultInterpolatedStringHandler.AppendLiteral(" already recorded, ");
			defaultInterpolatedStringHandler.AppendFormatted(stats.GetValueOrDefault("rt_uncovered"));
			defaultInterpolatedStringHandler.AppendLiteral(" uncovered");
			progress.Report(defaultInterpolatedStringHandler.ToStringAndClear());
		}
		static T Mode<T>(IEnumerable<T> v)
		{
			return (from x in v
				group x by x).MaxBy((IGrouping<T, T> g) => g.Count()).Key;
		}
	}

	private void Red3HitGroups(int libraries)
	{
		RedRayTracing.Shape shape = RedRayTracing.Learn(stateObjects);
		if ((object)shape == null || !recBlobs.TryGetValue(shape.Global, out byte[] value))
		{
			log?.Report($"ray tracing: {libraries} DXIL libraries; REDengine 3's material collections need a recording with ray tracing on: none synthesized");
			return;
		}
		RootSig.Ranges ranges = RootSig.Parse(value);
		RtCollections.Nv? nv = RtCollections.WithOptions(rasterNv, RtOptions);
		foreach (ShaderMap item in index.Maps.Where((ShaderMap m) => m.Library == "hitgroups" && m.Shaders.All(bc.ContainsKey)))
		{
			List<ShaderInfo> list = item.Shaders.Select((string h) => bc[h]).ToList();
			RootSig.Desc desc = RedRayTracing.Local(list, ranges);
			if ((object)desc == null)
			{
				Count("rt_uncovered");
				uncoveredExample.TryAdd("ray tracing: no local root signature for the hit group (RedRayTracing.Local)", item.Shaders[0]);
				continue;
			}
			if (!rsCache.TryGetValue(desc.Key, out string value2))
			{
				try
				{
					byte[] value3;
					(value2, value3) = RtCollections.Serialize(desc, Array.Empty<byte>());
					rsBlobs[value2] = value3;
				}
				catch (RootSig.SerializeException ex)
				{
					if (unserializable == null)
					{
						unserializable = ex.Message;
					}
				}
				rsCache[desc.Key] = value2;
			}
			if (value2 == null)
			{
				Count("rt_unserializable");
				rtUnbuildable.UnionWith(item.Shaders);
				continue;
			}
			(uint, uint, uint, uint, uint, bool)[] slots = ranges.Slots;
			(uint, uint, uint, uint, uint, bool)[] slots2 = RootSig.Parse(rsBlobs[value2]).Slots;
			int num = 0;
			(uint, uint, uint, uint, uint, bool)[] array = new (uint, uint, uint, uint, uint, bool)[slots.Length + slots2.Length];
			ReadOnlySpan<(uint, uint, uint, uint, uint, bool)> readOnlySpan = new ReadOnlySpan<(uint, uint, uint, uint, uint, bool)>(slots);
			readOnlySpan.CopyTo(new Span<(uint, uint, uint, uint, uint, bool)>(array).Slice(num, readOnlySpan.Length));
			num += readOnlySpan.Length;
			ReadOnlySpan<(uint, uint, uint, uint, uint, bool)> readOnlySpan2 = new ReadOnlySpan<(uint, uint, uint, uint, uint, bool)>(slots2);
			readOnlySpan2.CopyTo(new Span<(uint, uint, uint, uint, uint, bool)>(array).Slice(num, readOnlySpan2.Length));
			num += readOnlySpan2.Length;
			RootSig.Ranges ranges2 = new RootSig.Ranges(0u, array);
			string text = list.Select((ShaderInfo l) => RootSig.Uncovered(ranges2, Stage.Library, l)).FirstOrDefault((string w) => w != null);
			if (text != null)
			{
				Count("rt_uncovered");
				uncoveredExample.TryAdd("ray tracing " + text, item.Shaders[0]);
				continue;
			}
			hitGroupItems.Add(RedRayTracing.Item(item.Shaders[0], item.Shaders.ElementAtOrDefault(1), value2, shape, nv));
			usedRs.UnionWith(new _003C_003Ez__ReadOnlyArray<string>(new string[2] { shape.Global, value2 }));
		}
		log?.Report($"ray tracing: {libraries} DXIL libraries, {hitGroupItems.Count} material hit group collections synthesized (the recorded additions' global root signature {shape.Global.Substring(0, 8)}, shader config ({shape.Payload}, {shape.Attributes}), pipeline config ({shape.Depth}, 0x{shape.PipelineFlags:x})), {stats.GetValueOrDefault("rt_uncovered")} uncovered");
	}

	private void SoulsHitGroups(int libraries)
	{
		(string, byte[]) global = SoulsRayTracing.Global;
		(string, byte[]) local = SoulsRayTracing.Local;
		(string Hash, byte[] Blob) local2 = local;
		(string Hash, byte[] Blob) global2 = global;
		(uint, uint, uint, uint, uint, bool)[] slots = RootSig.Parse(global2.Blob).Slots;
		(uint, uint, uint, uint, uint, bool)[] slots2 = RootSig.Parse(local2.Blob).Slots;
		int num = 0;
		(uint, uint, uint, uint, uint, bool)[] array = new (uint, uint, uint, uint, uint, bool)[slots.Length + slots2.Length];
		ReadOnlySpan<(uint, uint, uint, uint, uint, bool)> readOnlySpan = new ReadOnlySpan<(uint, uint, uint, uint, uint, bool)>(slots);
		readOnlySpan.CopyTo(new Span<(uint, uint, uint, uint, uint, bool)>(array).Slice(num, readOnlySpan.Length));
		num += readOnlySpan.Length;
		ReadOnlySpan<(uint, uint, uint, uint, uint, bool)> readOnlySpan2 = new ReadOnlySpan<(uint, uint, uint, uint, uint, bool)>(slots2);
		readOnlySpan2.CopyTo(new Span<(uint, uint, uint, uint, uint, bool)>(array).Slice(num, readOnlySpan2.Length));
		num += readOnlySpan2.Length;
		RootSig.Ranges ranges = new RootSig.Ranges(0u, array);
		HashSet<string> hashSet = stateObjects.SelectMany((PsoDb.Rec r) => PsoDb.ParseStateObject(r).Libraries).ToHashSet();
		HashSet<string> pairs = new HashSet<string>();
		foreach (ShaderMap item in index.Maps.Where((ShaderMap m) => m.Platform == plat && SoulsRayTracing.IsPair(m) && m.Shaders.All((string h) => bc.TryGetValue(h, out ShaderInfo value2) && value2.Stage == Stage.Library)))
		{
			pairs.Add(string.Join('|', item.Shaders.Order()));
			if (item.Shaders.All(hashSet.Contains))
			{
				Count("rt_recorded");
				continue;
			}
			string text = item.Shaders.Select((string h) => RootSig.Uncovered(ranges, Stage.Library, bc[h])).FirstOrDefault((string w) => w != null);
			if (text != null)
			{
				Count("rt_uncovered");
				uncoveredExample.TryAdd("ray tracing " + text, item.Shaders[0]);
			}
			else
			{
				hitGroupItems.Add(RedRayTracing.Item(item.Shaders[0], item.Shaders[1], local2.Hash, SoulsRayTracing.Shape, null));
			}
		}
		rsBlobs[global2.Hash] = global2.Blob;
		rsBlobs[local2.Hash] = local2.Blob;
		if (hitGroupItems.Count > 0)
		{
			usedRs.UnionWith(new _003C_003Ez__ReadOnlyArray<string>(new string[2] { global2.Hash, local2.Hash }));
		}
		List<PsoDb.StateObject> list = (from s in stateObjects.Where((PsoDb.Rec r) => r.Tag == 'R').Select(PsoDb.ParseStateObject)
			where s.Type == 0
			select s).ToList();
		int value = list.Count((PsoDb.StateObject s) => pairs.Contains(string.Join('|', s.Libraries.Order())) && s.RootSignatures.Order().SequenceEqual(new string[2] { global2.Hash, local2.Hash }.Order()));
		log?.Report($"ray tracing: {libraries} DXIL libraries, {hitGroupItems.Count} material collections synthesized (Elden Ring's closest + any hit pairs, global root signature {global2.Hash.Substring(0, 8)}, local {local2.Hash.Substring(0, 8)}{((list.Count > 0) ? $"; {value}/{list.Count} recorded collections are these" : "")}), {stats.GetValueOrDefault("rt_recorded")} already recorded, {stats.GetValueOrDefault("rt_uncovered")} uncovered");
	}

	private string? LocalRs(ShaderInfo lib, bool rayGen)
	{
		RootSig.Desc desc = RtCollections.LocalRs(lib.Counts, rayGen, lib.Bindings, (engine.Family == "Unreal" && engine.Version.StartsWith("5.")) ? 6u : 4u);
		if (!rsCache.TryGetValue(desc.Key, out string value))
		{
			try
			{
				byte[] value2;
				(value, value2) = RtCollections.Serialize(desc, Array.Empty<byte>());
				rsBlobs[value] = value2;
			}
			catch (RootSig.SerializeException ex)
			{
				if (unserializable == null)
				{
					unserializable = ex.Message;
				}
			}
			rsCache[desc.Key] = value;
		}
		return value;
	}

	private (List<PsoDb.Rec> Synthesized, List<byte[]> Items) SettingsVariants()
	{
		List<byte[]> list = OtherSamplerSets();
		if (list.Count == 0)
		{
			return (Synthesized: new List<PsoDb.Rec>(), Items: new List<byte[]>());
		}
		List<PsoDb.Rec> list2 = new List<PsoDb.Rec>();
		List<byte[]> list3 = new List<byte[]>();
		foreach (byte[] item in list)
		{
			Dictionary<string, string> dictionary = new Dictionary<string, string>();
			foreach (var (key, d) in rsDescs.Where((KeyValuePair<string, RootSig.Desc> keyValuePair2) => usedRs.Contains(keyValuePair2.Key)))
			{
				try
				{
					byte[] array = RootSig.Serialize(d, item);
					dictionary[key] = PsoDb.Hex(SHA1.HashData(array));
					rsBlobs[dictionary[key]] = array;
				}
				catch (RootSig.SerializeException)
				{
					dictionary[key] = null;
				}
			}
			foreach (PsoDb.Rec item2 in synthesized)
			{
				budget.Tick();
				PsoDb.Pso pso = PsoDb.Parse(item2);
				byte[] array2 = Swap(item2.Payload, pso.Rs, dictionary, pso.Stages);
				if (array2 != null)
				{
					list2.Add(new PsoDb.Rec(item2.Tag, array2));
					usedRs.Add(dictionary[pso.Rs]);
				}
			}
			foreach (byte[] item3 in items)
			{
				budget.Tick();
				(string, string, SortedDictionary<int, string>) tuple = PsoDb.ParseItem(item3);
				byte[] array3 = Swap(item3, tuple.Item2, dictionary, tuple.Item3);
				if (array3 != null)
				{
					list3.Add(array3);
					usedRs.Add(dictionary[tuple.Item2]);
				}
			}
		}
		log?.Report($"texture filtering: {list.Count + 1} settings in the recording, {list2.Count + list3.Count} PSOs again at the other {((list.Count == 1) ? "one" : "ones")}");
		return (Synthesized: list2, Items: list3);
		byte[]? Swap(byte[] payload, string rs, Dictionary<string, string?> to, SortedDictionary<int, string> stages)
		{
			if (!to.TryGetValue(rs, out string value) || value == null || have.Contains(PsoDb.Tuple(value, stages)))
			{
				return null;
			}
			int num = ((ReadOnlySpan<byte>)payload.AsSpan()).IndexOf((ReadOnlySpan<byte>)Convert.FromHexString(rs));
			if (num < 0)
			{
				return null;
			}
			byte[] array4 = payload.ToArray();
			Convert.FromHexString(value).CopyTo(array4, num);
			return array4;
		}
	}

	private List<byte[]> OtherSamplerSets()
	{
		if (build)
		{
			byte[] array = samplers;
			if (array != null && array.Length > 0)
			{
				return (from h in SamplerVariants.OtherSettings(new HashSet<string> { Convert.ToHexStringLower(array) }, settings.Values)
					select samplerSets[h]).ToList();
			}
		}
		return new List<byte[]>();
	}

	private bool Replays(PsoDb.Rec r)
	{
		if (Rehydrate.References(new _003C_003Ez__ReadOnlySingleElementList<PsoDb.Rec>(r)).All((string h) => h == PsoDb.Zero || recBlobs.ContainsKey(h) || bc.ContainsKey(h)))
		{
			return !PsoDb.Parse(r).Stages.Values.Any(VendorOnly);
		}
		return false;
	}

	private List<PsoDb.Rec> ReplayableStateObjects()
	{
		HashSet<string> kept = new HashSet<string>();
		foreach (PsoDb.Rec stateObject in stateObjects)
		{
			if (Rehydrate.References(new _003C_003Ez__ReadOnlySingleElementList<PsoDb.Rec>(stateObject)).All((string h) => h == PsoDb.Zero || recBlobs.ContainsKey(h) || bc.ContainsKey(h)) && PsoDb.ParseStateObject(stateObject).Depends.All(kept.Contains))
			{
				kept.Add(stateObject.Key);
			}
		}
		return stateObjects.Where((PsoDb.Rec r) => kept.Contains(r.Key)).ToList();
	}

	private Plan Write()
	{
		(List<PsoDb.Rec> Synthesized, List<byte[]> Items) tuple = SettingsVariants();
		List<PsoDb.Rec> item = tuple.Synthesized;
		List<byte[]> item2 = tuple.Items;
		List<PsoDb.Rec> list = new List<PsoDb.Rec>();
		foreach (string usedTemplate in usedTemplates)
		{
			usedRs.Add(PsoDb.Parse(recByKey[usedTemplate]).Rs);
		}
		byte[] value2;
		foreach (string usedR in usedRs)
		{
			if (rsBlobs.TryGetValue(usedR, out byte[] value) || recBlobs.TryGetValue(usedR, out value))
			{
				byte[] array = Convert.FromHexString(usedR);
				byte[] array2 = value;
				int num = 0;
				value2 = new byte[array.Length + array2.Length];
				ReadOnlySpan<byte> readOnlySpan = new ReadOnlySpan<byte>(array);
				readOnlySpan.CopyTo(new Span<byte>(value2).Slice(num, readOnlySpan.Length));
				num += readOnlySpan.Length;
				ReadOnlySpan<byte> readOnlySpan2 = new ReadOnlySpan<byte>(array2);
				readOnlySpan2.CopyTo(new Span<byte>(value2).Slice(num, readOnlySpan2.Length));
				num += readOnlySpan2.Length;
				list.Add(new PsoDb.Rec('B', value2));
			}
		}
		foreach (KeyValuePair<string, byte[]> packR in packRs)
		{
			packR.Deconstruct(out var key, out value2);
			string text = key;
			byte[] array3 = value2;
			if (!usedRs.Contains(text))
			{
				value2 = Convert.FromHexString(text);
				byte[] array2 = array3;
				int num = 0;
				byte[] array = new byte[value2.Length + array2.Length];
				ReadOnlySpan<byte> readOnlySpan2 = new ReadOnlySpan<byte>(value2);
				readOnlySpan2.CopyTo(new Span<byte>(array).Slice(num, readOnlySpan2.Length));
				num += readOnlySpan2.Length;
				ReadOnlySpan<byte> readOnlySpan = new ReadOnlySpan<byte>(array2);
				readOnlySpan.CopyTo(new Span<byte>(array).Slice(num, readOnlySpan.Length));
				num += readOnlySpan.Length;
				list.Add(new PsoDb.Rec('B', array));
			}
		}
		list.AddRange(usedTemplates.Select((string t) => recByKey[t]));
		list.AddRange(synthesized.Concat(item));
		list.AddRange(from i in items.Concat(item2)
			select new PsoDb.Rec('P', i));
		RtCollections.Nv? nv = rasterNv;
		if (nv.HasValue)
		{
			RtCollections.Nv nv2 = nv.GetValueOrDefault();
			list.AddRange(from r in synthesized.Concat(item).Concat(from i in items.Concat(item2)
					select new PsoDb.Rec('P', i))
				select new PsoDb.NvState(r.Key, nv2.Slot, nv2.Space, 1u, nv2.Options).ToRec());
		}
		list.AddRange(rtItems.Select((byte[] i) => new PsoDb.Rec('Y', i)));
		list.AddRange(hitGroupItems.Select((byte[] i) => new PsoDb.Rec('H', i)));
		list.AddRange(d3d11.Select((string h) => new PsoDb.Rec('1', PsoDb.D3D11Item(bc[h].Stage, h))));
		list.AddRange(tess11.Select(((string Hs, string Ds) p) => new PsoDb.Rec('2', PsoDb.D3D11Pair(p.Hs, p.Ds))));
		list.AddRange(items11.Select((PsoDb.Draw11 d) => new PsoDb.Rec('3', PsoDb.Draw11Bytes(d))));
		list.AddRange(packEntries);
		int num2 = d3d11.Count + tess11.Count + items11.Count;
		HashSet<string> hashSet = (dx12 ? (from h in maps.Where(((string Platform, bool Pooled, IReadOnlyList<string> Shas, bool IsPipeline, string Rs) m) => m.Platform == plat).SelectMany(((string Platform, bool Pooled, IReadOnlyList<string> Shas, bool IsPipeline, string Rs) m) => m.Shas)
			where bc.TryGetValue(h, out ShaderInfo value6) && value6.Stage == Stage.Library && value6.ShaderModel != "lib_6_8"
			select h).ToHashSet() : new HashSet<string>());
		int count = hashSet.Count;
		List<PsoDb.Rec> list2 = ReplayableStateObjects();
		List<PsoDb.Rec> list3 = recs.Where(Replays).ToList();
		int num3 = list3.Select((PsoDb.Rec r) => SamplerVariants.Pipeline(r, (string h) => recBlobs.GetValueOrDefault(h)) ?? r.Key).Distinct().Count();
		int num4 = rtItems.Select((byte[] i) => RtCollections.ParseItem(i).Library).Concat(hitGroupItems.Select(RedRayTracing.ParseItem).SelectMany((RedRayTracing.ItemFields h) => new string[2] { h.ClosestHit, h.AnyHit }).OfType<string>()).Concat(list2.SelectMany((PsoDb.Rec r) => PsoDb.ParseStateObject(r).Libraries))
			.Count(hashSet.Remove);
		int num5 = hashSet.Count(rtUnbuildable.Contains);
		bool flag = rayTracing && count > 0 && stateObjects.Count == 0 && recBlobs.Values.Any((byte[] b) => Dxbc.InlineRayTracing(b));
		if (flag)
		{
			log?.Report($"ray tracing: the recording traces rays inline and builds no state object: the {count} DXIL libraries aren't used as played");
		}
		int num6 = ((count == 0 || (engine.Family == "Unreal" && engine.Version.StartsWith('5'))) ? maps.Where(((string Platform, bool Pooled, IReadOnlyList<string> Shas, bool IsPipeline, string Rs) m) => m.Platform == plat).SelectMany(((string Platform, bool Pooled, IReadOnlyList<string> Shas, bool IsPipeline, string Rs) m) => m.Shas).Distinct()
			.Count((string h) => bc.TryGetValue(h, out ShaderInfo value6) && value6.InlineRayTracing) : 0);
		Plan plan = new Plan(game.Id, index.ContentHash, string.Join(" + ", new string[2]
		{
			plat,
			(num2 > 0) ? ("D3D11 " + (plat11 ?? "DXBC")) : ""
		}.Where((string p) => p != "")), caps.Profile, new PlanStats(num3 + list2.Count, items.Count + synthesized.Count + rtItems.Count + hitGroupItems.Count, synthesized.Count, usedRs.Count, dx12 && (verified || embeddedRs > 0 || mapRoots), unitsBy[0], unitsBy[1], unitsBy[2], layoutCoverage, num2, packNew, stats.GetValueOrDefault("rs_uncovered"), count - num5, (!flag && !engine.NoRtPipelines && rayTracing) ? (count - num4 - num5) : 0, seen.Count + unpaired - stats.GetValueOrDefault("rs_unserializable"), ((IEnumerable<string>)new string[7] { "no_rs", "no_template", "no_gs_template", "rs_uncovered", "own_rs_uncovered", "rs_unknown", "stream_output" }).Sum((Func<string, int>)((IReadOnlyDictionary<string, int>)stats).GetValueOrDefault<string, int>), packShared, list2.Count, num6, RootSig.RuleFor(engine)?.Id, builtOk, builtN, items11.Count, item.Count + item2.Count + list3.Count - num3, cacheLeftOut, !pairs), Path.Combine(outDir, "plan.bin"));
		budget.Check();
		PlanFile.Write(plan, list.Select((PsoDb.Rec r) =>
		{
			budget.Tick();
			return r;
		}));
		log?.Report($"plan: {items.Count + synthesized.Count} PSOs{((rtItems.Count + hitGroupItems.Count > 0) ? $" + {rtItems.Count + hitGroupItems.Count} ray tracing collections" : "")} ({string.Join(", ", stats.Select((KeyValuePair<string, int> s) => $"{s.Key} {s.Value}"))}), {synthesized.Count} synthesized templates, {usedRs.Count} root signatures, {num2} DirectX 11 items" + ((packEntries.Count > 0) ? $", {packEntries.Count} middleware pack PSOs ({packNew} not in the recording)" : "") + ((num2 > 0) ? (" (" + string.Join(", ", from s in (from h in d3d11
				group h by bc[h].Stage into g
				select $"{g.Count()} {g.Key}").Append((tess11.Count > 0) ? $"{tess11.Count} HS+DS" : "")
			where s != ""
			select s) + ")") : "") + $", {new FileInfo(plan.FilePath).Length / 1024} KiB -> {plan.FilePath}");
		if (unserializable != null)
		{
			log?.Report($"warning: {stats.GetValueOrDefault("rs_unserializable")} stage sets and {stats.GetValueOrDefault("rt_unserializable")} DXIL libraries left out: the runtime won't serialize the root signature the rule builds for them ({unserializable})");
		}
		if (unbuilt != null)
		{
			log?.Report($"warning: {stats.GetValueOrDefault("rs_unbuilt")} stage sets left out: the rule builds no root signature for their shaders ({unbuilt})");
		}
		if (stats.TryGetValue("rs_uncovered", out var value3))
		{
			log?.Report($"warning: {value3} stage sets left out: their root signature doesn't cover a resource their shaders declare ({string.Join("; ", from e in uncoveredExample.Take(5)
				select e.Key + ", e.g. " + e.Value.Substring(0, 12))}); this engine's rule doesn't know those ranges" + ((recs.Count == 0) ? ": a recording teaches the planner the game's own root signatures" : ""));
		}
		if (stats.TryGetValue("own_rs_uncovered", out var value4))
		{
			log?.Report($"{value4} stage sets left out: the root signature the game ships with them doesn't cover their shaders ({string.Join("; ", from e in ownUncoveredExample.Take(5)
				select e.Key + ", e.g. " + e.Value.Substring(0, 12))})");
		}
		if (stats.TryGetValue("rs_unknown", out var value5))
		{
			log?.Report($"warning: {value5} stage sets left out: no readable bytes for their root signature in the rules, the recording or the game's index (e.g. {unknownRs.Substring(0, 12)})");
		}
		return plan;
	}

	private bool MeshTier(SortedDictionary<int, string> stages)
	{
		string? valueOrDefault = platOfSha.GetValueOrDefault(stages.Values.First());
		return ((valueOrDefault != null) ? valueOrDefault.Split(' ')[0] : null) == "PCD3D_SM6";
	}

	private SortedDictionary<Stage, ShaderInfo> Infos(SortedDictionary<int, string> stages)
	{
		return new SortedDictionary<Stage, ShaderInfo>(stages.ToDictionary((KeyValuePair<int, string> s) => (Stage)s.Key, (KeyValuePair<int, string> s) => bc[s.Value]));
	}

	private string PsOut(SortedDictionary<int, string> stages)
	{
		if (!stages.TryGetValue(2, out string value))
		{
			return "";
		}
		return Planner.Sig(bc[value].Outputs, dropSys: false);
	}

	private (uint[], uint) Targets(SortedDictionary<int, string> stages)
	{
		if (!stages.TryGetValue(2, out string value))
		{
			return (Array.Empty<uint>(), 0u);
		}
		return Planner.Targets(bc[value]);
	}
}
