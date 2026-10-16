using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SCSKiller.Core.App;

public sealed record CompatReport(string GameId, string StoreBuildKey, CompatChoice Choice, bool FrameGen, bool Upscaler, bool Mods, string? CpuModel, string? GpuModel, string GpuVendor, string? DriverVersion, string WindowsBuild, string AppVersion, CompatCompile Compile)
{
	public static string ThisWindows
	{
		get
		{
			Version version = Environment.OSVersion.Version;
			return $"{version.Major}.{version.Minor}.{version.Build}";
		}
	}

	public static readonly JsonSerializerOptions Json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
	{
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
		Converters = { (JsonConverter)new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
	};

	public const string NoBuild = "SCSKiller can't tell which version of this game is installed.";

	public byte[] Body()
	{
		return JsonSerializer.SerializeToUtf8Bytes(this, Json);
	}

	public static string? WhyNot(GameState s, string? vendor)
	{
		if (s.Game.Store != Store.Manual && s.Game.Version != null)
		{
			if (vendor != null)
			{
				if (!s.CompileFailed)
				{
					if (s.WarmedAt.HasValue)
					{
						if (s.PlayedAt > s.WarmedAt)
						{
							return null;
						}
						return "Play the game once since it compiled to rate how it ran.";
					}
					return "Compile the game and play it once to rate how it ran.";
				}
				return null;
			}
			return "Ratings are for NVIDIA and AMD GPUs.";
		}
		return "SCSKiller can't tell which version of this game is installed.";
	}

	public static string BuildKey(Game g)
	{
		return g.Id + "@" + g.Version;
	}

	public static CompatCompile CompileOf(GameState s, bool maximumPlans)
	{
		int status;
		if (s.Status == GameStatus.Unsupported && (s.WarmedAt.HasValue || s.CompileFailed))
		{
			status = 2;
		}
		else if (s.CompileFailed)
		{
			status = 3;
		}
		else
		{
			status = ((!s.WarmedAt.HasValue) ? 4 : (ScsKiller.IsPartlyCompiled(s) ? 1 : 0));
		}
		int? coveragePct = ScsKiller.ShownPercent(s);
		PlanStats? plan = s.Plan;
		return new CompatCompile((CompatStatus)status, coveragePct, (object)plan != null && plan.Recorded > 0, s.MaximumForGame ?? maximumPlans);
	}
}
