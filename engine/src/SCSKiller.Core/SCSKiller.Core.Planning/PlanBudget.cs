using System;
using System.Diagnostics;

namespace SCSKiller.Core.Planning;

public sealed class PlanBudget(long memoryBytes, TimeSpan time)
{
	public static readonly TimeSpan DefaultTime = TimeSpan.FromMinutes(30L);

	private readonly Stopwatch clock = Stopwatch.StartNew();

	private long ticks;

	private long collectAfterMs;

	internal Func<string>? Progress { get; set; }

	internal string What { get; init; } = "the plan";

	internal string Why { get; init; } = "this game's shaders combine into more pipelines than a compile can hold";

	public long Peak { get; private set; }

	public static long DefaultMemory(long ram)
	{
		return Math.Clamp(ram / 2, 4294967296L, 34359738368L);
	}

	public void Tick()
	{
		if ((++ticks & 0xFF) == 0L)
		{
			Check();
		}
	}

	public void Check()
	{
		if (clock.Elapsed > time)
		{
			throw new PlanTooBigException($"{What} took over {time.TotalMinutes:N0} minutes{At()}");
		}
		long totalMemory = GC.GetTotalMemory(forceFullCollection: false);
		Peak = Math.Max(Peak, totalMemory);
		if (totalMemory > memoryBytes && (clock.ElapsedMilliseconds >= collectAfterMs || totalMemory - memoryBytes > memoryBytes / 4))
		{
			GC.Collect();
			collectAfterMs = clock.ElapsedMilliseconds + 5000;
			if (GC.GetTotalMemory(forceFullCollection: false) > memoryBytes)
			{
				throw new PlanTooBigException($"{What} outgrew its memory limit of {Gb(memoryBytes)} on this PC{At()}");
			}
		}
	}

	public override string ToString()
	{
		return $"plan: built in {clock.Elapsed.TotalSeconds:F1}s, managed memory up to {Gb(Peak)} of its {Gb(memoryBytes)} limit";
	}

	private static string Gb(long bytes)
	{
		return $"{(double)bytes / 1073741824.0:N1} GB";
	}

	private string At()
	{
		string text = Progress?.Invoke();
		return ((text != null) ? (" at " + text) : "") + ": " + Why;
	}
}
