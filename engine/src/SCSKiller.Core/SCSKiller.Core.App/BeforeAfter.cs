using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.RegularExpressions.Generated;

namespace SCSKiller.Core.App;

public static class BeforeAfter
{
	public static readonly TimeSpan MinPlay = TimeSpan.FromMinutes(3L);

	public const int SmoothStutters = 3;

	private const string NoFrames = "The {0} run has no frame times";

	private const string NoCreates = "The {0} run has no pipeline log";

	private const string TooShort = "The {0} run had {1} of play, play at least {2}.";

	private const string LibraryLoads = "The after run loaded {0} more pipelines from the game's own cache, not counted for SCSKiller";

	private const string OtherSettings = "The runs had different resolutions or display formats: {0} before, {1} after";

	private const string OverlayOnce = "The in-game countdown was on in one run only";

	private const string Crashed = "The game didn't close normally in the before run";

	private const string MaybeWarm = "AMD's DirectX 11 driver cache can't be checked, so the before run may not have been fully cold";

	private const string MaybeHelped = "The game's DirectX 11 driver cache couldn't be told from another app's, so the after run may have used the before run's";

	private const string FrameGenBoth = "Frame generation was {0} in both runs.";

	private const string FrameGenOne = "Frame generation was on in the {0} run only.";

	private const string Share = "Shader compilation stutter in {0}: {1} → {2} in {3} of play after SCSKiller precompiled its shaders.";

	private const string ShareReady = " {0}% of pipelines were ready instead of {1}%.";

	private const string ShareSmooth = "{0} was already smooth here: SCSKiller took it from {1}% to {2}% of pipelines ready.";

	private const string ShareSmoothNoShare = "{0} was already smooth here: {1} → {2} shader compilation stutters in {3} of play with SCSKiller.";

	private const string ShareDx11 = "{0} on DirectX 11: {1} → {2} stutters of 50 ms or more in {3} of play, before and after SCSKiller precompiled its shaders.";

	public const string Dx11Label = "DirectX 11: SCSKiller can't tell shader compilation stutters from other stutters";

	private const string ShareLink = " scskiller.com";

	private const int ShareMax = 280;

	private const int LinkWeight = 23;

	public const string Link = "https://scskiller.com";

	public static (bool Shown, bool Enabled, string Note) StartButton(bool on, string? blocked)
	{
		return (Shown: on, Enabled: on && blocked == null, Note: blocked ?? "");
	}

	public static string? RunProblem(SessionSnapshot? run, bool before)
	{
		string text = (before ? "before" : "after");
		if (run == null || run.Frames == 0L)
		{
			return F("The {0} run has no frame times", text);
		}
		if (run.Creates == null)
		{
			return F("The {0} run has no pipeline log", text);
		}
		if (run.PlaySeconds < MinPlay.TotalSeconds)
		{
			return F("The {0} run had {1} of play, play at least {2}.", text, (run.PlaySeconds < 60.0) ? "under a minute" : $"{(int)(run.PlaySeconds / 60.0)} min", $"{(int)MinPlay.TotalMinutes} min");
		}
		return null;
	}

	public static RunStats Stats(SessionSnapshot s)
	{
		List<double> list = (from h in s.Hitches
			where h.Cause == HitchCause.Shader
			select h.Ms).ToList();
		List<double> list2 = (from h in s.Hitches.Where((Hitch h) =>
			{
				HitchCause cause = h.Cause;
				return (uint)cause <= 1u;
			})
			select h.Ms).ToList();
		return new RunStats(s.PlaySeconds, list.Count, list.Sum() / 1000.0, list.DefaultIfEmpty(0.0).Max(), s.Creates?.ReadyShare, s.Creates?.PlayCompiles ?? 0, s.Low1PctFps, s.AvgFps, s.Hitches.Count((Hitch h) => h.Cause == HitchCause.Other), list2.Count, list2.Sum() / 1000.0, list2.DefaultIfEmpty(0.0).Max());
	}

	public static Comparison Compare(SessionSnapshot before, SessionSnapshot after, bool notCold = false, bool afterHelped = false)
	{
		RunStats runStats = Stats(before);
		RunStats runStats2 = Stats(after);
		RunStats runStats3 = runStats;
		bool flag = before.D3D11 || after.D3D11;
		List<string> list = new List<string>();
		long num = (after.Creates?.Library ?? 0) - (before.Creates?.Library ?? 0);
		if (num > 0)
		{
			list.Add(F("The after run loaded {0} more pipelines from the game's own cache, not counted for SCSKiller", num));
		}
		if (before.SwapChain != null && after.SwapChain != null && before.SwapChain != after.SwapChain)
		{
			list.Add(F("The runs had different resolutions or display formats: {0} before, {1} after", before.SwapChain, after.SwapChain));
		}
		if (before.Overlay != after.Overlay)
		{
			list.Add("The in-game countdown was on in one run only");
		}
		if (!before.CleanExit)
		{
			list.Add("The game didn't close normally in the before run");
		}
		if (notCold)
		{
			list.Add("AMD's DirectX 11 driver cache can't be checked, so the before run may not have been fully cold");
		}
		else if (afterHelped)
		{
			list.Add("The game's DirectX 11 driver cache couldn't be told from another app's, so the after run may have used the before run's");
		}
		bool? frameGen = before.FrameGen;
		if (frameGen.HasValue)
		{
			bool valueOrDefault = frameGen == true;
			frameGen = after.FrameGen;
			if (frameGen.HasValue)
			{
				bool valueOrDefault2 = frameGen == true;
				list.Add((valueOrDefault == valueOrDefault2) ? F("Frame generation was {0} in both runs.", valueOrDefault ? "on" : "off") : F("Frame generation was on in the {0} run only.", valueOrDefault ? "before" : "after"));
			}
		}
		return new Comparison(runStats3, runStats2, Math.Abs(runStats3.PlaySeconds - runStats2.PlaySeconds) > 0.1 * Math.Min(runStats3.PlaySeconds, runStats2.PlaySeconds), !flag && runStats3.ShaderStutters < 3, list, flag);
	}

	public static (int Before, int After) PlayMinutes(Comparison c)
	{
		return (Before: (int)Math.Round(c.Before.PlaySeconds / 60.0, MidpointRounding.AwayFromZero), After: (int)Math.Round(c.After.PlaySeconds / 60.0, MidpointRounding.AwayFromZero));
	}

	public static string ShareText(string game, Comparison c)
	{
		(int Before, int After) tuple = PlayMinutes(c);
		int item = tuple.Before;
		int item2 = tuple.After;
		string span = ((item == item2) ? $"{item} minutes" : $"{item} and {item2} minutes");
		double? readyShare = c.Before.ReadyShare;
		double? readyShare2 = c.After.ReadyShare;
		double? after = readyShare2;
		double? before = readyShare;
		string text = Text(game);
		int num = game.Length - 1;
		while (num >= 1 && PostLength(text) > 280)
		{
			text = Text(game.Substring(0, num).TrimEnd() + "…");
			num--;
		}
		return text;
		string Text(string name)
		{
			string text2;
			if (c.Dx11)
			{
				text2 = F("{0} on DirectX 11: {1} → {2} stutters of 50 ms or more in {3} of play, before and after SCSKiller precompiled its shaders.", name, c.Before.Hitches, c.After.Hitches, span);
			}
			else if (c.AlreadySmooth)
			{
				text2 = ((before.HasValue && after.HasValue) ? F("{0} was already smooth here: SCSKiller took it from {1}% to {2}% of pipelines ready.", name, Percent(before.Value), Percent(after.Value)) : F("{0} was already smooth here: {1} → {2} shader compilation stutters in {3} of play with SCSKiller.", name, c.Before.ShaderStutters, c.After.ShaderStutters, span));
			}
			else
			{
				text2 = F("Shader compilation stutter in {0}: {1} → {2} in {3} of play after SCSKiller precompiled its shaders.", name, c.Before.ShaderStutters, c.After.ShaderStutters, span) + ((before.HasValue && after.HasValue) ? F(" {0}% of pipelines were ready instead of {1}%.", Percent(after.Value), Percent(before.Value)) : "");
			}
			return text2 + " scskiller.com";
		}
	}

	public static int PostLength(string text)
	{
		return Domain().Replace(text, new string('x', 23)).EnumerateRunes().Sum((Rune r) =>
		{
			int value = r.Value;
			if (value >= 8208)
			{
				if (value >= 8242)
				{
					if (value <= 8247)
					{
						goto IL_0046;
					}
				}
				else if (value <= 8223)
				{
					goto IL_0046;
				}
			}
			else if (value >= 8192)
			{
				if (value <= 8205)
				{
					goto IL_0046;
				}
			}
			else if (value <= 4351)
			{
				goto IL_0046;
			}
			bool flag = false;
			goto IL_004c;
			IL_0046:
			flag = true;
			goto IL_004c;
			IL_004c:
			if (!flag)
			{
				return 2;
			}
			return 1;
		});
	}

	[GeneratedCode("System.Text.RegularExpressions.Generator", "10.0.14.42308")]
	private static Regex Domain()
	{
		return _003CRegexGenerator_g_003EF6B40337B96C8FAE9C8F3EA55FFFB441396170F2BF16CC354AFD984B612499B82__Domain_0.Instance;
	}

	private static int Percent(double share)
	{
		if (!(share >= 1.0))
		{
			return Math.Min(99, (int)Math.Round(share * 100.0));
		}
		return 100;
	}

	private static string F(string format, params object[] args)
	{
		return string.Format(CultureInfo.InvariantCulture, format, args);
	}
}
