using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.RedEngine;

public sealed class RedEngineReader : IEngineReader
{
	public sealed record Loc(string Path, RedShaderCache.Entry Entry);

	public const string Family = "REDengine 3";

	public const string Version = "DX12";

	public const string HitGroups = "hitgroups";

	private const string Materials = "content\\content0\\shaderdx12_0.cache";

	private const string Static = "content\\content0\\staticshaderDx12_0.cache";

	private const int DetectTechniques = 64;

	private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>>();

	private static readonly PlanRule? Red3 = RulesFile.Embedded.RuleFor(new EngineInfo("REDengine 3", "DX12", null, "D3D12", Encrypted: false, null));

	public EngineInfo? Detect(Game game)
	{
		string path = Path.Combine(game.InstallDir, "content\\content0\\shaderdx12_0.cache");
		if (!File.Exists(path))
		{
			return null;
		}
		using FileStream f = Open(path);
		RedShaderCache.Materials m = RedShaderCache.ReadMaterials(f);
		if ((object)m == null)
		{
			return null;
		}
		Dictionary<ulong, string> shaOf = new Dictionary<ulong, string>();
		Dictionary<string, ShaderInfo> dictionary = new Dictionary<string, ShaderInfo>();
		foreach (ulong[] item in m.Techniques.Where((ulong[] t) => t.All((ulong k) => k == 0L || m.Shaders.ContainsKey(k))).Take(64))
		{
			foreach (ulong item2 in item.Where((ulong k) => k != 0L && !shaOf.ContainsKey(k)))
			{
				byte[] array = RedShaderCache.Container(f, m.Shaders[item2]);
				if (array == null || !Dxbc.Valid(array))
				{
					continue;
				}
				string text = (shaOf[item2] = Convert.ToHexStringLower(SHA1.HashData(array)));
				string text3 = text;
				try
				{
					ShaderInfo shaderInfo = ShaderContainer.Parse(array, text3, new ResourceCounts(0, 0, 0, 0));
					if ((object)shaderInfo != null)
					{
						dictionary[text3] = shaderInfo;
					}
				}
				catch (Exception ex) when ((ex is ArgumentException || ex is IndexOutOfRangeException) ? true : false)
				{
				}
			}
			List<string> list = Graphics(item, shaOf, dictionary);
			if (list != null && Listed(list, dictionary))
			{
				return new EngineInfo("REDengine 3", "DX12", null, "D3D12", Encrypted: false, null);
			}
		}
		return null;
	}

	public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		Dictionary<string, ShaderInfo> shaders = new Dictionary<string, ShaderInfo>();
		Dictionary<string, Loc> locs = new Dictionary<string, Loc>();
		Dictionary<string, string> platformOf = new Dictionary<string, string>();
		List<ShaderMap> maps = new List<ShaderMap>();
		using (IncrementalHash incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1))
		{
			string path = Path.Combine(game.InstallDir, "content\\content0\\shaderdx12_0.cache");
			RedShaderCache.Materials materials;
			using (FileStream f = Open(path))
			{
				materials = RedShaderCache.ReadMaterials(f, ct);
			}
			if (materials == null)
			{
				throw new InvalidDataException("content\\content0\\shaderdx12_0.cache no longer reads as a version 5 shader cache: scan the game again");
			}
			string staticPath = Path.Combine(game.InstallDir, "content\\content0\\staticshaderDx12_0.cache");
			int value = 0;
			if (File.Exists(staticPath))
			{
				FileStream f2 = Open(staticPath);
				try
				{
					incrementalHash.AppendData(Stamp(game, staticPath));
					List<string> list = (RedShaderCache.ReadStatic(f2) ?? new List<RedShaderCache.Entry>()).Select((RedShaderCache.Entry e2) => Add(staticPath, e2, f2)).OfType<string>().Where(shaders.ContainsKey)
						.Distinct()
						.ToList();
					value = list.Count;
					foreach (IGrouping<string, string> item in list.GroupBy(PlatformOf))
					{
						maps.Add(new ShaderMap(CarvedReader.Sha1Hex("content\\content0\\staticshaderDx12_0.cache|" + item.Key), "content\\content0\\staticshaderDx12_0.cache", item.Key, item.ToList()));
					}
				}
				finally
				{
					if (f2 != null)
					{
						((IDisposable)f2).Dispose();
					}
				}
			}
			int num = 0;
			int num2 = 0;
			int overCap = 0;
			int count;
			using (FileStream f3 = Open(path))
			{
				incrementalHash.AppendData(Stamp(game, path));
				Dictionary<ulong, string> dictionary = new Dictionary<ulong, string>(materials.Shaders.Count);
				foreach (var (key, e) in materials.Shaders.OrderBy((KeyValuePair<ulong, RedShaderCache.Entry> s) => s.Value.At))
				{
					string text = Add(path, e, f3);
					if (text != null)
					{
						dictionary[key] = text;
					}
				}
				HashSet<string> seen = new HashSet<string>();
				HashSet<string> wider = new HashSet<string>();
				HashSet<string> hashSet = new HashSet<string>();
				for (int num4 = 0; num4 < materials.Techniques.Count; num4++)
				{
					if (num4 % 4096 == 0)
					{
						ct.ThrowIfCancellationRequested();
					}
					ulong[] array = materials.Techniques[num4];
					List<string> list2 = HitGroup(array, dictionary, shaders);
					if (list2 != null && hashSet.Count < 250000 && hashSet.Add(string.Join(',', list2)))
					{
						maps.Add(new ShaderMap(CarvedReader.Sha1Hex("hitgroups|" + string.Join(',', list2)), "hitgroups", "D3D12", list2));
					}
					List<string> list3 = Graphics(array, dictionary, shaders);
					if (list3 == null)
					{
						num2++;
						continue;
					}
					bool flag = Listed(list3, shaders);
					if (list3.Count > 0)
					{
						Pipeline(list3, flag);
					}
					if ((array[5] != 0) & flag)
					{
						int num5 = 1;
						List<string> list4 = new List<string>(num5);
						CollectionsMarshal.SetCount(list4, num5);
						CollectionsMarshal.AsSpan(list4)[0] = dictionary[array[5]];
						Pipeline(list4);
					}
					num++;
				}
				List<string> list5 = (from h in dictionary.Values.Distinct()
					where shaders.TryGetValue(h, out ShaderInfo value2) && value2.Stage == Stage.Library
					select h).ToList();
				count = list5.Count;
				if (list5.Count > 0)
				{
					maps.Add(new ShaderMap(CarvedReader.Sha1Hex("content\\content0\\shaderdx12_0.cache|libraries"), "content\\content0\\shaderdx12_0.cache", "D3D12", list5));
				}
				void Pipeline(List<string> shas, bool listed = true)
				{
					string text2 = CarvedReader.Sha1Hex(string.Join(',', shas.Order(StringComparer.Ordinal)));
					HashSet<string> hashSet2 = (listed ? seen : wider);
					if (!hashSet2.Contains(text2))
					{
						if (hashSet2.Count >= 250000)
						{
							overCap++;
						}
						else
						{
							hashSet2.Add(text2);
							maps.Add(new ShaderMap(text2, "content\\content0\\shaderdx12_0.cache", shas.Select(PlatformOf).FirstOrDefault((string p) => p != "D3D12", "D3D12"), shas, IsPipeline: true));
						}
					}
				}
			}
			if (num == 0)
			{
				throw new InvalidDataException($"{"content\\content0\\shaderdx12_0.cache"}: none of its {materials.Techniques.Count} techniques names a pipeline of its shaders: scan the game again");
			}
			located[game.Id] = locs;
			log?.Report($"{shaders.Count} shaders ({value} engine, {count} ray tracing libraries; " + string.Join(", ", from s in shaders.Values
				group s by s.Stage into g
				orderby g.Count() descending
				select $"{g.Count()} {g.Key}") + $"), {materials.Techniques.Count} techniques -> {maps.Count((ShaderMap x) => x.IsPipeline)} distinct pipelines" + ((num2 > 0) ? $"; {num2} techniques left out (a shader missing or of another stage, or stages that form no pipeline)" : "") + ((overCap > 0) ? $"; warning: {overCap} pipelines over the cap of {250000} left out" : "") + $" ({stopwatch.Elapsed.TotalSeconds:F1}s)");
			string contentHash = Convert.ToHexStringLower(incrementalHash.GetHashAndReset());
			List<string> list6 = new List<string>();
			list6.Add("D3D12");
			list6.AddRange(platformOf.Values.Distinct().Order(StringComparer.Ordinal));
			return new ShaderIndex(contentHash, new _003C_003Ez__ReadOnlyList<string>(list6), shaders, maps);
		}
		string? Add(string path2, RedShaderCache.Entry entry2, Stream f4)
		{
			ct.ThrowIfCancellationRequested();
			byte[] array2 = RedShaderCache.Container(f4, entry2);
			if (array2 == null || !Dxbc.Valid(array2))
			{
				return null;
			}
			string text2 = Convert.ToHexStringLower(SHA1.HashData(array2));
			if (!locs.TryAdd(text2, new Loc(path2, entry2)))
			{
				return text2;
			}
			try
			{
				ShaderInfo shaderInfo = ShaderContainer.Parse(array2, text2, new ResourceCounts(0, 0, 0, 0));
				if ((object)shaderInfo != null)
				{
					shaders[text2] = shaderInfo with
					{
						Counts = CarvedReader.Counts(shaderInfo.Bindings),
						RootSignature = null
					};
				}
				string text3 = CarvedReader.LanePlatform(Dxbc.WaveLanes(array2));
				if (text3 != null)
				{
					platformOf[text2] = text3;
				}
			}
			catch (Exception ex) when ((ex is ArgumentException || ex is IndexOutOfRangeException) ? true : false)
			{
			}
			return text2;
		}
		string PlatformOf(string sha)
		{
			return platformOf.GetValueOrDefault(sha, "D3D12");
		}
	}

	internal static List<string>? HitGroup(ulong[] t, IReadOnlyDictionary<ulong, string> shaOf, IReadOnlyDictionary<string, ShaderInfo> shaders)
	{
		if (t.Length < 8 || t[6] == 0L || !Lib(t[6], out var sha))
		{
			return null;
		}
		int num;
		if (t[7] == 0L)
		{
			num = 1;
			List<string> list = new List<string>(num);
			CollectionsMarshal.SetCount(list, num);
			CollectionsMarshal.AsSpan(list)[0] = sha;
			return list;
		}
		if (!Lib(t[7], out var sha2))
		{
			return null;
		}
		num = 2;
		List<string> list2 = new List<string>(num);
		CollectionsMarshal.SetCount(list2, num);
		Span<string> span = CollectionsMarshal.AsSpan(list2);
		span[0] = sha;
		span[1] = sha2;
		return list2;
		bool Lib(ulong k, out string reference)
		{
			if (shaOf.TryGetValue(k, out reference) && shaders.TryGetValue(reference, out ShaderInfo value))
			{
				return value.Stage == Stage.Library;
			}
			return false;
		}
	}

	private static bool Listed(List<string> g, IReadOnlyDictionary<string, ShaderInfo> shaders)
	{
		if (g.Count != 0)
		{
			PlanRule red = Red3;
			if ((object)red != null)
			{
				return RootSig.InStageSets(red, g.Select((string h) => shaders[h].Stage));
			}
			return false;
		}
		return true;
	}

	internal static List<string>? Graphics(ulong[] t, IReadOnlyDictionary<ulong, string> shaOf, IReadOnlyDictionary<string, ShaderInfo> shaders)
	{
		Stage[] array = new Stage[6]
		{
			Stage.Vertex,
			Stage.Pixel,
			Stage.Geometry,
			Stage.Hull,
			Stage.Domain,
			Stage.Compute
		};
		string[] shas = new string[6];
		for (int i = 0; i < 6; i++)
		{
			if (t[i] != 0L)
			{
				if (!shaOf.TryGetValue(t[i], out string value) || !shaders.TryGetValue(value, out ShaderInfo value2) || value2.Stage != array[i])
				{
					return null;
				}
				shas[i] = value;
			}
		}
		List<int> list = (from num in Enumerable.Range(0, 5)
			where shas[num] != null
			select num).ToList();
		if (list.Count == 0)
		{
			if (shas[5] == null)
			{
				return null;
			}
			return new List<string>();
		}
		if (shas[0] == null || shas[3] == null != (shas[4] == null))
		{
			return null;
		}
		return list.Select((int num) => shas[num]).ToList();
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
			FileStream fileStream;
			try
			{
				fileStream = Open(item.Key);
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
				continue;
			}
			using (fileStream)
			{
				foreach (var (text, loc) in item.OrderBy(((string Sha, Loc At) x) => x.At.Entry.At))
				{
					ct.ThrowIfCancellationRequested();
					byte[] array = RedShaderCache.Container(fileStream, loc.Entry);
					if (array != null && Convert.ToHexStringLower(SHA1.HashData(array)) == text)
					{
						sink(text, array);
					}
				}
			}
		}
	}

	private static FileStream Open(string path)
	{
		return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1048576);
	}

	private static byte[] Stamp(Game game, string path)
	{
		FileInfo fileInfo = new FileInfo(path);
		return Encoding.UTF8.GetBytes($"{Path.GetRelativePath(game.InstallDir, path)}|{fileInfo.Length}|{fileInfo.LastWriteTimeUtc.Ticks}\n");
	}
}
