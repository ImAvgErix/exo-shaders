using System;
using System.Collections.Generic;
using System.Linq;
using SCSKiller.Core.Games;

namespace SCSKiller.Core.App;

public static class Format
{
	public const string Dash = "—";

	private const string NoPlayText = "loading or compiling shaders the whole time";

	public static string Bytes(long? b)
	{
		if (b.HasValue)
		{
			if (b.GetValueOrDefault() >= 1073741824)
			{
				return $"{(double?)b / 1073741824.0:0.#} GB";
			}
			return $"{(double?)b / 1048576.0:0} MB";
		}
		return "—";
	}

	public static string Duration(TimeSpan? t)
	{
		if (t.HasValue)
		{
			TimeSpan valueOrDefault = t.GetValueOrDefault();
			return ScsKiller.Duration(valueOrDefault);
		}
		return "—";
	}

	public static string Ms(double ms)
	{
		if (ms >= 1000.0)
		{
			return $"{ms / 1000.0:0.0} s";
		}
		return $"{ms:0} ms";
	}

	public static bool NoPlay(double low1PctFps)
	{
		return low1PctFps <= 0.0;
	}

	public static string Frames(FrameReport f, IReadOnlyList<Hitch> shader)
	{
		int num = f.Hitches.Count((Hitch h) => h.Cause == HitchCause.Other);
		int num2 = f.Hitches.Count((Hitch h) => h.Cause == HitchCause.Loading);
		List<Hitch> list = f.Hitches.Where((Hitch h) => h.Cause == HitchCause.LoadingShaders).ToList();
		return (NoPlay(f.Low1PctFps) ? (Duration(f.Duration) + ", loading or compiling shaders the whole time.") : (Duration(f.Duration) + ": " + ((shader.Count == 0) ? "no stutters from shader compiles" : $"{shader.Count:N0} stutter{((shader.Count == 1) ? "" : "s")} from shader compiles (worst {Ms(shader.Max((Hitch h) => h.Ms))})") + ((num == 0) ? ", no other hitches" : $", {num:N0} other hitch{((num == 1) ? "" : "es")}") + $", 1% low {f.Low1PctFps:0} fps.")) + ((list.Count + num2 == 0) ? "" : (" Loading: " + string.Join(", ", new string[2]
		{
			(list.Count == 0) ? null : $"{list.Count:N0} slow frame{((list.Count == 1) ? "" : "s")} compiling shaders (worst {Ms(list.Max((Hitch h) => h.Ms))})",
			(num2 == 0) ? null : ((list.Count == 0) ? $"{num2:N0} slow frame{((num2 == 1) ? "" : "s")}" : $"{num2:N0} other{((num2 == 1) ? "" : "s")}")
		}.OfType<string>()) + "."));
	}

	public static string Session(FrameSession e)
	{
		if (!NoPlay(e.Low1PctFps))
		{
			return $"1% low {e.Low1PctFps:0} fps · " + ((e.Stutters == 0) ? "no stutters" : $"{e.Stutters:N0} stutter{((e.Stutters == 1) ? "" : "s")}");
		}
		return "loading or compiling shaders the whole time";
	}

	public static string Engine(EngineInfo e, string? family = null)
	{
		string version = e.Version;
		if (((version == null || version.Length != 0) && !(version == "-") && !(version == "?")) || 1 == 0)
		{
			return (family ?? e.Family) + " " + e.Version;
		}
		return family ?? e.Family;
	}

	public static bool Running(QueueItem q)
	{
		QueueStage stage = q.Stage;
		if ((uint)(stage - 1) <= 4u)
		{
			return true;
		}
		return false;
	}

	public static (string Line, double? Done) TrayStatus(QueueItem? running, string? name, bool quitting)
	{
		double? num;
		if ((object)running != null)
		{
			WarmProgress progress = running.Progress;
			if ((object)progress != null && progress.Total > 0)
			{
				num = Math.Clamp((double)progress.Done / (double)progress.Total, 0.0, 1.0);
				goto IL_004d;
			}
		}
		num = null;
		goto IL_004d;
		IL_004d:
		double? item = num;
		if (!quitting)
		{
			if (!(running == null))
			{
				if (!running.PlanCheck)
				{
					if (running.Stage != QueueStage.Paused)
					{
						if (running.Stage == QueueStage.Warming && item.HasValue)
						{
							double valueOrDefault = item.GetValueOrDefault();
							return (Line: $"Compiling {name}, {100.0 * valueOrDefault:0}%", Done: valueOrDefault);
						}
						return (Line: "Preparing " + name, Done: null);
					}
					return (Line: "Paused: " + name, Done: item);
				}
				return (Line: "Checking games for more to compile", Done: null);
			}
			return (Line: "Idle", Done: null);
		}
		return (Line: "Finishing: the driver is saving the shader cache", Done: null);
	}

	public static string Middleware(MiddlewareTag t)
	{
		string label = t.Label;
		string? text;
		if (t.Pipelines > 0)
		{
			text = $": {t.Pipelines:N0} known pipelines, compiled with the game";
		}
		else
		{
			text = ((t.Label == "DLSS") ? ": compiled by the NVIDIA driver itself" : ": detected, added after a recording sees them");
		}
		return label + text;
	}

	public static string? GpuNotice(GpuInfo gpu, string? dismissedFor, bool intel = false)
	{
		GpuVendor vendor = gpu.Vendor;
		bool flag = ((vendor == GpuVendor.Amd || vendor == GpuVendor.Nvidia) ? true : false);
		if (flag || dismissedFor == gpu.Name)
		{
			return null;
		}
		return gpu.Vendor switch
		{
			GpuVendor.Intel => (!intel) ? "Intel support is experimental and off. Turn it on in Settings." : "Intel support is experimental. DirectX 12 games need a recording first.", 
			GpuVendor.Qualcomm => "SCSKiller doesn't compile on Qualcomm GPUs yet: how Qualcomm's driver caches shaders hasn't been measured.", 
			_ => "SCSKiller doesn't compile on this GPU yet: it only knows how NVIDIA and AMD drivers cache shaders.", 
		};
	}

	public static string? ShortNote(GameState s)
	{
		string r = s.StatusReason;
		bool flag = ScsKiller.IsPartlyWarmed(s);
		GameStatus status = s.Status;
		if (!s.CompileUnreached)
		{
			switch (status)
			{
			case GameStatus.Warmed:
				if (r == "this game's shaders don't fit the driver's shader cache, raise its size")
				{
					return "Too big for the shader cache";
				}
				if (r == "the driver's shader cache is full, so it removed older games' files")
				{
					return "Shader cache full";
				}
				if (ScsKiller.IsPartlyCompiled(s))
				{
					return Sentence(ScsKiller.PartlyCompiledNote(s));
				}
				if (flag)
				{
					return $"Driver {s.WarmedDriverVersion} · {s.Careful.LaunchCompiled * 100.0:0}% still compiled";
				}
				if (ScsKiller.RtAfterRecording(s) && s.NoStutter == null)
				{
					string result = (s.RecorderInstalled ? "Recorder on: play with ray tracing" : (ScsKiller.RecordedEnough(s) ? "Ray tracing needs a recording" : "Ray tracing needs a 5-min recording"));
					return result;
				}
				if (ScsKiller.BothApis(s.Plan))
				{
					return "DirectX 11 and 12" + (ScsKiller.IsPartial(s.Plan) ? " · partly covered" : "");
				}
				return "Driver " + s.WarmedDriverVersion + (ScsKiller.IsPartial(s.Plan) ? " · partly covered" : "");
			case GameStatus.Stale:
				return StaleNote(r);
			case GameStatus.NeedsRecording:
				if (r == "EasyAntiCheat blocks the recorder, but you can record at your own risk with an offline session.")
				{
					return r;
				}
				if (s.NoStutter != null && !s.RecorderInstalled)
				{
					return "Compiling isn't needed";
				}
				if (s.RecordingPaused)
				{
					return "Recording paused: limit reached";
				}
				if (ScsKiller.NeverRecorded(s))
				{
					return null;
				}
				if (s.RecorderInstalled && !ScsKiller.RecordedEnough(s))
				{
					return Has("helps only when played on DirectX 12 (the game may run on DirectX 11)") ? "Recorder on: play on DirectX 12" : "Recorder on: play 5 minutes";
				}
				if (Starts("Ray-traced effects need one short recording"))
				{
					return "For ray-traced effects" + ((s.InCommunityDb == true) ? (" · " + InDb(r)) : "");
				}
				if (s.InCommunityDb == true)
				{
					return Sentence(InDb(r));
				}
				if (Starts("the recording has no draws"))
				{
					return "Play into the game world";
				}
				if (s.RecorderInstalled)
				{
					return Sentence(FirstClause(r));
				}
				if (s.RecorderEffective && Collections.RecordedBy(s.Game.Id) != null)
				{
					return "Recorder on: play 5 minutes";
				}
				if (s.RiskEligible)
				{
					return "Record a session on its page";
				}
				if (s.RecorderSkip != null)
				{
					return "Recorder not available";
				}
				return "Turn on Record and play";
			case GameStatus.Ready:
				if (Has("ray-traced effects aren't compiled"))
				{
					return (!s.AnyAntiCheat || s.RiskEligible) ? "Ray tracing not compiled" : ("Ray tracing blocked by " + ScsKiller.AntiCheatName(s));
				}
				if (Has("no ray tracing seen while recording: turn it on in the game and play to cover it"))
				{
					return "No ray tracing seen while recording";
				}
				if (Has("ray tracing that uses separate pipelines (such as path tracing) is compiled only from a recording"))
				{
					return "Path tracing needs a recording";
				}
				if (ScsKiller.IsPartial(s.Plan))
				{
					return "Partly covered";
				}
				if (Has("not tested on this engine version yet"))
				{
					return "Not tested on this engine version";
				}
				if (Has("for DirectX 12, "))
				{
					return (!s.AnyAntiCheat || s.RiskEligible) ? "DirectX 11, DirectX 12 needs a recording" : ("DirectX 11, DirectX 12 blocked by " + ScsKiller.AntiCheatName(s));
				}
				if (Has("also compiles every DirectX 11 shader"))
				{
					return "DirectX 11 and 12";
				}
				if (Starts("compiles every DirectX 11 shader"))
				{
					return "DirectX 11";
				}
				return null;
			default:
			{
				if (r == "builds its shaders at the main menu; in-game stutter isn't from shader compilation")
				{
					return "Builds its shaders at the main menu";
				}
				if (s.ShaderModBlocks)
				{
					return s.ShaderMod + " changes all its pipelines";
				}
				if (s.AnyAntiCheat && Starts("needs a recording, which"))
				{
					return "Blocked by " + ScsKiller.AntiCheatName(s) + ((s.InCommunityDb == true) ? (" · " + InDb(r)) : "");
				}
				if (Has("once you confirm its game folder"))
				{
					return "Needs a recording: confirm its folder";
				}
				EngineInfo? engine = s.Engine;
				if ((object)engine != null && engine.Encrypted)
				{
					return null;
				}
				string text = r;
				if ((text == "Couldn't download the Oodle codec. Check your antivirus or firewall." || text == "This CPU lacks AVX2 and BMI2, which the Oodle codec needs.") ? true : false)
				{
					return r;
				}
				string text2 = s.Engine?.Unsupported;
				if (text2 != null)
				{
					return text2.StartsWith("no D3D shaders", StringComparison.Ordinal) ? "No DirectX shaders" : "Engine not supported yet";
				}
				if (r == "checking its files")
				{
					return null;
				}
				if (r == "not checked yet: open it to check")
				{
					return "Open to check";
				}
				if (s.Engine == null)
				{
					return (r == "engine not supported yet") ? "Engine not supported yet" : "Couldn't read the game files";
				}
				if (Starts("runs on "))
				{
					return "Runs on " + FirstClause(r.Substring("runs on ".Length));
				}
				if (Starts("not supported on this GPU"))
				{
					return "Not supported on this GPU yet";
				}
				return Sentence(FirstClause(r));
			}
			}
		}
		return "This game needs a custom loader";
		bool Has(string part)
		{
			return r.Contains(part, StringComparison.Ordinal);
		}
		bool Starts(string part)
		{
			return r.StartsWith(part, StringComparison.Ordinal);
		}
	}

	public static string ModNote(GameState s)
	{
		return (((object)s != null && s.ShaderMod != null && !s.ShaderModBlocks && s.Status != GameStatus.Unsupported) ? ("\n" + ScsKiller.ShaderModNote(s)) : "") + ((s.Dxvk && s.Status != GameStatus.Unsupported) ? ("\n" + (s.DxvkCompiled ? "DXVK runs this game on Vulkan, and the compile covers it on NVIDIA" : "DXVK runs this game on Vulkan, so the compile isn't used")) : "");
	}

	public static string RecorderOnNote(GameState s)
	{
		return "recorder on: play for about 5 minutes" + (s.StatusReason.Contains("helps only when played on DirectX 12 (the game may run on DirectX 11)", StringComparison.Ordinal) ? ", helps only when played on DirectX 12 (the game may run on DirectX 11)" : "");
	}

	private static string StaleNote(string r)
	{
		if (!r.StartsWith("driver changed:", StringComparison.Ordinal))
		{
			if (!(r == "part of this game's compiled shader cache is gone, compile again"))
			{
				if (!(r == "this game's compiled shader cache is gone, compile again"))
				{
					if (!r.StartsWith("the compile didn't reach", StringComparison.Ordinal))
					{
						if (!r.StartsWith("the game runs as", StringComparison.Ordinal))
						{
							if (!r.StartsWith("the last compile filled the cache of", StringComparison.Ordinal))
							{
								if (!r.StartsWith("game updated", StringComparison.Ordinal))
								{
									if (!r.StartsWith("game shaders changed", StringComparison.Ordinal))
									{
										if (!r.StartsWith("SCSKiller can now compile", StringComparison.Ordinal))
										{
											if (!r.StartsWith("Maximum mode", StringComparison.Ordinal))
											{
												return Sentence(FirstClause(r));
											}
											return "Maximum mode: more to compile";
										}
										return "SCSKiller can compile more";
									}
									return "Game shaders changed";
								}
								return "Game updated";
							}
							return "Compiled for another exe";
						}
						return "The exe name's case changed";
					}
					return "The compile missed the game's cache";
				}
				return "Its shader cache was removed";
			}
			return "Part of its shader cache is gone";
		}
		return "Driver " + r.Substring(r.LastIndexOf(' ') + 1) + " cleared its cache";
	}

	private static string FirstClause(string t)
	{
		return t.Split(new string[3] { ": ", "; ", " (" }, 2, StringSplitOptions.None)[0];
	}

	private static string Sentence(string t)
	{
		if (t.Length <= 0)
		{
			return t;
		}
		return char.ToUpperInvariant(t[0]) + t.Substring(1);
	}

	private static string InDb(string reason)
	{
		if (!reason.Contains("in the community database for Patreon supporters", StringComparison.Ordinal))
		{
			return "in the community database";
		}
		return "in the community database for Patreon supporters";
	}
}
