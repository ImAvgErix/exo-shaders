using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SCSKiller.Core.Warming;

public static class WarmOutput
{
	private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(10L);

	public static async Task<(WarmEvent? Last, WarmEvent? Done, string? Error)> Pump(TextReader stdout, IProgress<WarmProgress>? progress, Func<long> cacheGrowth, TimeSpan every, Action? onDone = null, Func<long, long>? whole = null, Action<string>? onStage = null)
	{
		if (whole == null)
		{
			whole = (long d) => d;
		}
		WarmEvent last = null;
		WarmEvent done = null;
		WarmEvent latest = null;
		string error = null;
		Task reader = Task.Run(async () =>
		{
			while (true)
			{
				string text = await stdout.ReadLineAsync();
				if (text == null)
				{
					break;
				}
				WarmEvent warmEvent = WarmEvent.Parse(text);
				if ((object)warmEvent != null)
				{
					string text2;
					if (warmEvent.Event == "error")
					{
						if (error == null)
						{
							error = warmEvent.Message ?? "unknown error";
						}
					}
					else
					{
						text2 = warmEvent.Event;
						if ((text2 == "done" || text2 == "retry") ? true : false)
						{
							done = warmEvent;
						}
					}
					if (warmEvent.Event == "done")
					{
						onDone?.Invoke();
					}
					if ((object)warmEvent != null && warmEvent.Event == "stage")
					{
						string stage = warmEvent.Stage;
						if (stage != null && stage.Length > 0)
						{
							onStage?.Invoke(stage);
						}
					}
					bool flag;
					switch (warmEvent.Event)
					{
					case "start":
					case "progress":
					case "done":
					case "retry":
						flag = true;
						break;
					default:
						flag = false;
						break;
					}
					if (flag)
					{
						last = warmEvent;
					}
					text2 = warmEvent.Event;
					if ((text2 == "progress" || text2 == "done") ? true : false)
					{
						Volatile.Write(ref latest, warmEvent);
					}
				}
			}
		});
		Stopwatch clock = Stopwatch.StartNew();
		RecentRate rate = new RecentRate();
		WarmEvent reported = null;
		Stopwatch since = Stopwatch.StartNew();
		while (await Task.WhenAny(reader, Task.Delay(every)) != reader)
		{
			Report(final: false);
		}
		await reader;
		if (Volatile.Read(in latest) != reported)
		{
			Report(final: true);
		}
		return (Last: last, Done: done, Error: error);
		void Report(bool final)
		{
			WarmEvent warmEvent = Volatile.Read(in latest);
			if ((object)warmEvent != null)
			{
				long done2 = whole(warmEvent.Done);
				rate.Add(clock.Elapsed, done2);
				if (progress != null && (!(warmEvent == reported) || final || !(since.Elapsed < KeepAlive)))
				{
					WarmEvent warmEvent2 = warmEvent;
					Stopwatch stopwatch = Stopwatch.StartNew();
					since = stopwatch;
					reported = warmEvent2;
					progress.Report(new WarmProgress(done2, warmEvent.Total, warmEvent.Failed, rate.PerSecond ?? warmEvent.Rate, cacheGrowth(), 0L, null, warmEvent.Workers, warmEvent.Threads, warmEvent.DriverSkipped));
				}
			}
		}
	}
}
