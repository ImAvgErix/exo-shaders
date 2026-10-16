using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SCSKiller.Core.App;

namespace SCSKiller.Core.Planning;

public sealed class RulesFile
{
	private sealed record Doc(JsonElement[] Rules, string? About = null);

	public const string Name = "plan-rules";

	public const string ContentPath = "v1/content/plan-rules.json";

	public static readonly IReadOnlySet<string> Capabilities = new HashSet<string>
	{
		"rs.desc.v1", "rs.desc.v2", "rs.stock.v1", "rs.select.v1", "stagesets.v1", "rt.collection.v1", "rt.noconfig.v1", "rs.re.v1", "rs.re.v2", "rs.samplers.v1",
		"rs.stock.v2", "rt.nv.v1", "amd.ags.v1"
	};

	public static readonly IReadOnlySet<string> RsShapes = new HashSet<string> { "ff7", "bindless", "spaceBindless", "avalancheSrvs", "dagor", "dagorCbvRanges" };

	public static readonly IReadOnlySet<string> RtShapes = new HashSet<string> { "avalancheGlobal", "ue5Shape", "red3HitGroups", "soulsHitGroups" };

	private static readonly Dictionary<string, Stage> StageNames = new Dictionary<string, Stage>
	{
		["VS"] = Stage.Vertex,
		["PS"] = Stage.Pixel,
		["DS"] = Stage.Domain,
		["HS"] = Stage.Hull,
		["GS"] = Stage.Geometry,
		["CS"] = Stage.Compute,
		["AS"] = Stage.Amplification,
		["MS"] = Stage.Mesh
	};

	private static readonly JsonSerializerOptions Canonical = new JsonSerializerOptions(JsonSerializerDefaults.Web);

	public IReadOnlyList<PlanRule> Rules { get; }

	public IReadOnlyList<string> Dropped { get; }

	public static RulesFile Embedded { get; } = TryParse(ContentFile.Embedded("SCSKiller.Core.Planning.plan-rules.json"));

	public static RulesFile Current { get; set; } = Embedded;

	internal static Stage StageOf(string name)
	{
		return StageNames[name];
	}

	private RulesFile(IReadOnlyList<PlanRule> rules, IReadOnlyList<string> dropped)
	{
		Rules = rules;
		Dropped = dropped;
	}

	public static RulesFile? TryParse(string json)
	{
		Doc doc = ContentFile.Read<Doc>(json);
		if ((object)doc == null || (doc.About != null && !ContentFile.Text(doc.About, 2000)))
		{
			return null;
		}
		List<PlanRule> list = new List<PlanRule>();
		List<string> list2 = new List<string>();
		List<PlanRule> list3 = list;
		JsonElement[] rules = doc.Rules;
		for (int i = 0; i < rules.Length; i++)
		{
			JsonElement e = rules[i];
			string text = ((e.ValueKind == JsonValueKind.Object && e.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.String) ? value.GetString() : "?");
			string text2 = Unusable(e);
			PlanRule r = ((text2 == null) ? ContentFile.Read<PlanRule>(e) : null);
			if (text2 == null)
			{
				text2 = ((r == null) ? "malformed" : (Invalid(r) ?? (list3.Any((PlanRule x) => x.Id == r.Id) ? "a second rule with this id" : null)));
			}
			if (text2 == null)
			{
				list3.Add(r);
			}
			else
			{
				list2.Add(text + ": " + text2);
			}
		}
		return new RulesFile(list3, list2);
	}

	private static string? Unusable(JsonElement e)
	{
		if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("requires", out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
		{
			return "no requires";
		}
		foreach (JsonElement item in value.EnumerateArray())
		{
			if (item.ValueKind != JsonValueKind.String || !Capabilities.Contains(item.GetString()))
			{
				return $"needs {item}";
			}
		}
		return null;
	}

	private static string? Invalid(PlanRule r)
	{
		string id = r.Id;
		RuleMatch match;
		int num;
		if (id != null)
		{
			int length = id.Length;
			if (length > 0 && length <= 64 && r.Id.All((char c) =>
			{
				bool flag6 = char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c);
				if (!flag6)
				{
					bool flag7 = ((c == '-' || c == '.') ? true : false);
					flag6 = flag7;
				}
				return flag6;
			}))
			{
				match = r.Match;
				if (ContentFile.Text(match.Family, 64) && match.From == null == (match.To == null))
				{
					if (match.From == null)
					{
						goto IL_00cd;
					}
					Version version = Ver(match.From);
					if ((object)version != null)
					{
						Version version2 = Ver(match.To);
						if ((object)version2 != null && !(version > version2))
						{
							goto IL_00cd;
						}
					}
				}
				num = 1;
				goto IL_012b;
			}
		}
		return "bad id";
		IL_00cd:
		if (match.Fork != null)
		{
			id = match.Fork;
			if (id != null)
			{
				int length = id.Length;
				if (length > 0 && length <= 64)
				{
					num = ((!match.Fork.All((char c) =>
					{
						bool flag6 = char.IsAsciiLetterOrDigit(c);
						if (!flag6)
						{
							bool flag7 = ((c == ' ' || c == '-' || c == '_') ? true : false);
							flag6 = flag7;
						}
						return flag6;
					})) ? 1 : 0);
					goto IL_012b;
				}
			}
			num = 1;
		}
		else
		{
			num = 0;
		}
		goto IL_012b;
		IL_02b8:
		if (r.StageSets != null && (!Needs("stagesets.v1") || r.StageSets.Length == 0 || !r.StageSets.All((string[] s) => Stages(s))))
		{
			return "bad stageSets";
		}
		RootSigRule rs = r.RootSignature;
		bool flag;
		if ((object)rs != null)
		{
			flag = rs.Samplers != null;
			if (flag)
			{
				bool flag2 = !Needs("rs.samplers.v1") || rs.StaticSamplers != null;
				if (!flag2)
				{
					int length = rs.Samplers.Length;
					bool flag3 = ((length > 2032 || length == 0) ? true : false);
					flag2 = flag3;
				}
				flag = flag2 || rs.Samplers.Any((uint[] x) => x == null || x.Length != 13);
			}
			if (flag)
			{
				return "bad samplers";
			}
			byte[] samplers = RootSig.StaticSamplers(r);
			if (rs.Select != null && (!Needs("rs.select.v1") || !rs.Select.All((string s) => s != null && RsShapes.Contains(s))))
			{
				return "bad select";
			}
			int num2 = rs.Select?.Count((string s) =>
			{
				switch (s)
				{
				case "ff7":
				case "dagor":
				case "dagorCbvRanges":
					return true;
				default:
					return false;
				}
			}) ?? 0;
			if (rs.ByStages != null)
			{
				if (!Needs("rs.desc.v1") || rs.Stock != null || rs.Select != null || rs.Re != null || rs.ByStages.Length == 0)
				{
					return "bad byStages";
				}
				if (rs.ByStages.All((StagesDesc b) => b?.Desc == null))
				{
					return "bad byStages";
				}
				StagesDesc[] byStages = rs.ByStages;
				foreach (StagesDesc stagesDesc in byStages)
				{
					if (stagesDesc == null || (stagesDesc.Any != null && !Stages(stagesDesc.Any)))
					{
						return "bad byStages";
					}
					if (stagesDesc.Predicated && (!Needs("rs.desc.v2") || stagesDesc.Desc == null))
					{
						return "bad byStages";
					}
					if (stagesDesc.Desc != null)
					{
						string text = Unserializable(stagesDesc.Desc, samplers);
						if (text != null)
						{
							return "a description doesn't serialize (" + text + ")";
						}
					}
				}
				string[][] stageSets = r.StageSets;
				if ((stageSets != null) ? (!stageSets.All(Described)) : (!rs.ByStages.Any((StagesDesc b) => !b.Predicated && b.Any == null)))
				{
					return "bad byStages: a stage set with no description";
				}
			}
			else
			{
				ReRule re = rs.Re;
				if ((object)re != null)
				{
					flag = !Needs("rs.re.v1") || rs.Stock != null || rs.Select != null || re.CbvBase > 14;
					if (!flag)
					{
						uint computeUavs = re.ComputeUavs;
						bool flag2 = ((computeUavs > 1000000 || computeUavs == 0) ? true : false);
						flag = flag2;
					}
					if (flag || ((!re.DenyMesh || re.CbvUavAbove || re.BindlessSpaces != 0) && !Needs("rs.re.v2")) || re.BindlessSpaces > 16 || !Sizes(re.CbvSizes) || !Sizes(re.SrvSizes) || !Sizes(re.UavSizes))
					{
						return "bad re";
					}
					Stage[][] array = new Stage[2][]
					{
						new Stage[1] { Stage.Compute },
						new Stage[2]
						{
							Stage.Vertex,
							Stage.Pixel
						}
					};
					foreach (Stage[] stages in array)
					{
						try
						{
							RootSig.Serialize(RootSig.BuildRe(re, stages, re.CbvSizes[^1], re.SrvSizes[^1], re.UavSizes[^1]), samplers);
						}
						catch (RootSig.SerializeException ex)
						{
							return "a description doesn't serialize (" + ex.Message + ")";
						}
					}
				}
				else if (num2 > 1 || num2 == 1 == (rs.Stock != null))
				{
					return "no single construction";
				}
			}
			StockRule stock = rs.Stock;
			flag = (object)stock != null;
			if (flag)
			{
				bool flag2 = !Needs("rs.stock.v1") || !Stages(stock.Stages) || stock.Stages.Contains("CS") || !Stages(stock.UavStages, empty: true);
				if (!flag2)
				{
					uint computeUavs = stock.MaxSrvs;
					bool flag4 = ((computeUavs > 1000000 || computeUavs == 0) ? true : false);
					flag2 = flag4;
				}
				bool flag3 = flag2;
				if (!flag3)
				{
					uint computeUavs = stock.MaxSamplers;
					bool flag4 = ((computeUavs > 2048 || computeUavs == 0) ? true : false);
					flag3 = flag4;
				}
				flag = flag3 || (stock.DenyMesh && !Needs("rs.stock.v2"));
			}
			if (flag)
			{
				return "bad stock";
			}
		}
		RtRule rt = r.Rt;
		if ((object)rt != null)
		{
			if (!Needs("rt.collection.v1") || !ContentFile.Text(rt.Note, 300) || (rt.Select != null && !rt.Select.All((string s) => s != null && RtShapes.Contains(s))))
			{
				return "bad rt";
			}
			if (!rt.Config && (!Needs("rt.noconfig.v1") || rt.Flags != 0))
			{
				return "bad rt: config";
			}
			if (rt.Nv != null && !Needs("rt.nv.v1"))
			{
				return "bad rt: nv";
			}
			bool flag5 = rt.Select?.Any((string s) => (s == "red3HitGroups" || s == "soulsHitGroups") ? true : false) ?? false;
			if (rt.Local != null)
			{
				string text2 = Unserializable(rt.Local, Array.Empty<byte>());
				if (text2 != null)
				{
					return "a description doesn't serialize (" + text2 + ")";
				}
			}
			if (rt.Global == null && !flag5)
			{
				return "bad rt: no global root signature";
			}
			if (rt.Global != null)
			{
				string text3 = Unserializable(rt.Global, RtSamplers(rt));
				if (text3 != null)
				{
					return "a description doesn't serialize (" + text3 + ")";
				}
			}
		}
		return null;
		IL_012b:
		flag = (byte)num != 0;
		if (!flag)
		{
			bool flag2 = match.Games != null;
			if (flag2)
			{
				bool flag3 = !Needs("rs.desc.v2");
				if (!flag3)
				{
					int length = match.Games.Length;
					bool flag4 = ((length > 16 || length == 0) ? true : false);
					flag3 = flag4;
				}
				flag2 = flag3 || !match.Games.All((string g) => ContentFile.Text(g, 64) && g.Contains(':'));
			}
			flag = flag2;
		}
		if (flag)
		{
			return "bad match";
		}
		if (r.RootSignature == null && r.Rt == null && r.Ags == null)
		{
			return "no rule";
		}
		AgsRule ags = r.Ags;
		if ((object)ags != null)
		{
			if (Needs("amd.ags.v1") && !(r.RootSignature != null) && !(r.Rt != null))
			{
				Dictionary<string, string>? profiles = ags.Profiles;
				if (profiles == null || profiles.All((KeyValuePair<string, string> p) =>
				{
					if (ContentFile.Text(p.Key, 64))
					{
						string value = p.Value;
						if (value != null && value.Length == 8)
						{
							return p.Value.All(char.IsAsciiHexDigit);
						}
					}
					return false;
				}))
				{
					string[]? proven = ags.Proven;
					if (proven == null || proven.All((string a) => ContentFile.Text(a, 64)))
					{
						goto IL_02b8;
					}
				}
			}
			return "bad ags";
		}
		goto IL_02b8;
		bool Described(IEnumerable<string> set)
		{
			return rs.ByStages.Any((StagesDesc b) => !b.Predicated && (b.Any == null || b.Any.Intersect(set).Any()));
		}
		bool Needs(string cap)
		{
			return r.Requires.Contains(cap);
		}
	}

	private static bool Sizes(uint[]? s)
	{
		if (s != null && s.Length > 0 && s[0] != 0 && s[^1] <= 1000000)
		{
			return s.Zip(s.Skip(1)).All(((uint First, uint Second) p) => p.First < p.Second);
		}
		return false;
	}

	private static bool Stages(string[]? names, bool empty = false)
	{
		if (names != null && (empty || names.Length != 0) && names.All((string n) => n != null && StageNames.ContainsKey(n)))
		{
			return names.Distinct().Count() == names.Length;
		}
		return false;
	}

	private static Version? Ver(string s)
	{
		if (s.Count((char c) => c == '.') != 1 || !Version.TryParse(s, out Version result))
		{
			return null;
		}
		return result;
	}

	private static bool Row(uint[]? r)
	{
		bool flag = r != null && r.Length >= 2 && r[1] <= 7;
		if (flag)
		{
			bool flag2;
			switch (r[0])
			{
			case 0u:
				flag2 = r.Length >= 7 && ((r.Length - 2) % 5 == 0 || r.Length == 8);
				break;
			case 1u:
			case 2u:
			case 3u:
			case 4u:
				flag2 = r.Length == 5;
				break;
			default:
				flag2 = false;
				break;
			}
			flag = flag2;
		}
		return flag;
	}

	private static string? Unserializable(DescData d, byte[] samplers)
	{
		if (d.Rows == null || !d.Rows.All(Row))
		{
			return "a malformed row";
		}
		try
		{
			RootSig.Serialize(d.ToDesc(), samplers);
			return null;
		}
		catch (RootSig.SerializeException ex)
		{
			return ex.Message;
		}
	}

	internal static byte[] RtSamplers(RtRule rt)
	{
		SamplerSet globalSamplers = rt.GlobalSamplers;
		if ((object)globalSamplers == null)
		{
			return Array.Empty<byte>();
		}
		return RootSig.UeSamplers(globalSamplers.Register, globalSamplers.Space);
	}

	public static RulesFile WithServer(RulesFile server)
	{
		List<PlanRule> list = new List<PlanRule>();
		list.AddRange(server.Rules);
		list.AddRange(Embedded.Rules.Where((PlanRule e) => server.Rules.All((PlanRule s) => s.Id != e.Id)));
		return new RulesFile(new _003C_003Ez__ReadOnlyList<PlanRule>(list), server.Dropped);
	}

	public PlanRule? RuleFor(EngineInfo e)
	{
		return First(e, (PlanRule r) => r.RootSignature != null);
	}

	public PlanRule? RtFor(EngineInfo e)
	{
		return First(e, (PlanRule r) => r.Rt != null);
	}

	private PlanRule? First(EngineInfo e, Func<PlanRule, bool> kind)
	{
		return (from r in Rules
			where kind(r) && Matches(r.Match, e)
			orderby r.Match.Fork == null
			select r).FirstOrDefault();
	}

	private static bool Matches(RuleMatch m, EngineInfo e)
	{
		if (m.Family != e.Family || (m.Fork != null && m.Fork != e.Fork) || (m.Games != null && !m.Games.Any((string g) =>
		{
			object obj;
			if (!g.Contains('@'))
			{
				string? game = e.Game;
				obj = ((game != null) ? game.Split('@')[0] : null);
			}
			else
			{
				obj = e.Game;
			}
			return g == (string?)obj;
		})))
		{
			return false;
		}
		if (m.From == null)
		{
			return true;
		}
		if (!Version.TryParse(e.Version, out Version result))
		{
			return false;
		}
		Version version = new Version(result.Major, result.Minor);
		if (Ver(m.From) <= version)
		{
			return version <= Ver(m.To);
		}
		return false;
	}

	public string Hash(EngineInfo e)
	{
		return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new PlanRule[2]
		{
			RuleFor(e),
			RtFor(e)
		}.OfType<PlanRule>().DistinctBy((PlanRule r) => r.Id).OrderBy((PlanRule r) => r.Id, StringComparer.Ordinal), Canonical)))).Substring(0, 32);
	}

	public static RulesFile? Cached(string file, ContentTrust trust, IReadOnlyCollection<string>? channels)
	{
		return trust.Cached("plan-rules", TryParse, file, channels);
	}

	public static string[] Channels(IReadOnlyCollection<string>? ent)
	{
		if (ent != null)
		{
			if (ent.Contains("alpha") || ent.Contains("internal"))
			{
				return new string[2] { "alpha", "beta" };
			}
			if (!ent.Contains("beta"))
			{
				return Array.Empty<string>();
			}
			return new string[1] { "beta" };
		}
		return Array.Empty<string>();
	}

	public static Task<RulesFile?> FetchAsync(string cacheFile, ContentTrust trust, RouteFailover? routes = null, string? token = null, IReadOnlyCollection<string>? channels = null, TimeSpan? budget = null)
	{
		return trust.FetchAsync("plan-rules", TryParse, cacheFile, channels, token, routes, budget);
	}
}
