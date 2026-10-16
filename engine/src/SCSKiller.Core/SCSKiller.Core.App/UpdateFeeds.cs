using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SCSKiller.Core.App;

public static class UpdateFeeds
{
	public const string GhRepo = "BlueHeisenberg/SCSKiller";

	public static readonly Uri Packages = new Uri("https://dl.scskiller.io/");

	public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(1);

	public const int PackagesPerDay = 8;

	private static readonly TimeSpan[] Backoff = new TimeSpan[4]
	{
		TimeSpan.FromHours(1),
		TimeSpan.FromHours(2),
		TimeSpan.FromHours(4),
		TimeSpan.FromHours(12)
	};

	private static Uri PackageHost
	{
		get
		{
			string environmentVariable = Environment.GetEnvironmentVariable("SCSKILLER_API");
			if (environmentVariable == null || environmentVariable.Length <= 0)
			{
				return Packages;
			}
			return RouteFailover.Default.Primary;
		}
	}

	public static Uri Feed(string channel, string file)
	{
		if (!(channel == "stable"))
		{
			return new Uri(RouteFailover.Default.Primary, EdgePath(channel, file));
		}
		return new Uri("https://github.com/BlueHeisenberg/SCSKiller/releases/latest/download/" + file);
	}

	public static string EdgePath(string channel, string file)
	{
		return "v1/updates/" + channel + "/" + file;
	}

	public static Uri Package(AppVersion v, string fileName)
	{
		if (!(v.Channel == "stable"))
		{
			return new Uri(PackageHost, "v1/updates/" + v.Channel + "/" + Uri.EscapeDataString(fileName));
		}
		return new Uri($"https://github.com/{"BlueHeisenberg/SCSKiller"}/releases/download/v{v}/{Uri.EscapeDataString(fileName)}");
	}

	public static DateTimeOffset? PackageHold(PackageTries t, DateTimeOffset now, bool asked, bool edge)
	{
		List<DateTimeOffset> list = t.At.Where((DateTimeOffset a) => Recent(a, now)).Order().ToList();
		DateTimeOffset? dateTimeOffset;
		if (!edge || list.Count < 8)
		{
			dateTimeOffset = null;
		}
		else
		{
			dateTimeOffset = list[list.Count - 8] + TimeSpan.FromDays(1);
		}
		DateTimeOffset? dateTimeOffset2 = dateTimeOffset;
		if (!asked && t.Failures > 0)
		{
			DateTimeOffset? failedAt = t.FailedAt;
			if (failedAt.HasValue)
			{
				DateTimeOffset valueOrDefault = failedAt.GetValueOrDefault();
				if (valueOrDefault <= now)
				{
					DateTimeOffset dateTimeOffset3 = valueOrDefault + Backoff[Math.Min(t.Failures, Backoff.Length) - 1];
					if (dateTimeOffset3 > (dateTimeOffset2 ?? DateTimeOffset.MinValue))
					{
						dateTimeOffset2 = dateTimeOffset3;
					}
				}
			}
		}
		if (!(dateTimeOffset2 > now))
		{
			return null;
		}
		return dateTimeOffset2;
	}

	internal static bool Recent(DateTimeOffset at, DateTimeOffset now)
	{
		if (at <= now)
		{
			return now - at < TimeSpan.FromDays(1);
		}
		return false;
	}

	public static async Task Download(Stream from, Stream to, long size, TimeSpan stall, CancellationToken ct)
	{
		byte[] buf = new byte[81920];
		using CancellationTokenSource idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
		long total = 0L;
		while (true)
		{
			idle.CancelAfter(stall);
			int num;
			try
			{
				num = await from.ReadAsync(buf, idle.Token);
			}
			catch (OperationCanceledException) when (!ct.IsCancellationRequested)
			{
				throw new TimeoutException($"no package bytes for {stall.TotalSeconds:0} s");
			}
			if (num == 0)
			{
				return;
			}
			long num2;
			total = (num2 = total + num);
			if (num2 > size)
			{
				break;
			}
			await to.WriteAsync(buf.AsMemory(0, num), ct);
		}
		throw new FeedRejectedException($"a package over the feed's {size} bytes");
	}

	public static Uri? Source(AppVersion v)
	{
		switch (v.Channel)
		{
		case "stable":
			return new Uri($"https://github.com/{"BlueHeisenberg/SCSKiller"}/tree/v{v}");
		case "alpha":
		case "beta":
			return new Uri(RouteFailover.Default.Primary, $"v1/updates/{v.Channel}/source-{v}.zip");
		default:
			return null;
		}
	}
}
