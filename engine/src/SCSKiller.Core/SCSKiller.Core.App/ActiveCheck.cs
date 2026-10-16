using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SCSKiller.Core.App;

public sealed class ActiveCheck
{
	private readonly string file;

	private readonly Func<bool> enabled;

	private readonly AppVersion version;

	private readonly string gpu;

	private readonly RouteFailover routes;

	private readonly HttpClient http;

	private readonly TimeProvider clock;

	private readonly SemaphoreSlim sending = new SemaphoreSlim(1, 1);

	private DateTimeOffset retryAt;

	public ActiveCheck(string dataDir, Func<bool> enabled, GpuVendor vendor, AppVersion? version = null, RouteFailover? routes = null, TimeProvider? clock = null)
	{
		file = Path.Combine(dataDir, "active-check.txt");
		this.enabled = enabled;
		this.version = version ?? AppVersion.Current;
		gpu = Gpu(vendor);
		this.routes = routes ?? RouteFailover.Default;
		this.clock = clock ?? TimeProvider.System;
		http = new HttpClient(this.routes, disposeHandler: false)
		{
			Timeout = TimeSpan.FromSeconds(10L)
		};
	}

	public static bool Allowed(Settings s)
	{
		if ((object)s != null && s.ActiveCheck && s.ActiveCheckAsked)
		{
			return s.WelcomeSeen;
		}
		return false;
	}

	public static bool Asks(Settings s)
	{
		if (s.WelcomeSeen)
		{
			return !s.ActiveCheckAsked;
		}
		return false;
	}

	public static Settings Answered(Settings s, bool on)
	{
		return s with
		{
			ActiveCheck = on,
			ActiveCheckAsked = true
		};
	}

	public static string Gpu(GpuVendor vendor)
	{
		return vendor switch
		{
			GpuVendor.Nvidia => "nvidia", 
			GpuVendor.Amd => "amd", 
			_ => "other", 
		};
	}

	public static string Body(DateOnly? last, DateOnly today, AppVersion version, string gpu)
	{
		DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(43, 5);
		defaultInterpolatedStringHandler.AppendLiteral("{\"first\":");
		defaultInterpolatedStringHandler.AppendFormatted(B(!last.HasValue));
		defaultInterpolatedStringHandler.AppendLiteral(",\"week\":");
		int b;
		if (last.HasValue)
		{
			DateOnly valueOrDefault = last.GetValueOrDefault();
			(int, int) tuple = Week(valueOrDefault);
			(int, int) tuple2 = Week(today);
			b = ((tuple.Item1 != tuple2.Item1 || tuple.Item2 != tuple2.Item2) ? 1 : 0);
		}
		else
		{
			b = 1;
		}
		defaultInterpolatedStringHandler.AppendFormatted(B((byte)b != 0));
		defaultInterpolatedStringHandler.AppendLiteral(",\"month\":");
		int b2;
		if (last.HasValue)
		{
			DateOnly valueOrDefault2 = last.GetValueOrDefault();
			int year = valueOrDefault2.Year;
			int month = valueOrDefault2.Month;
			int year2 = today.Year;
			int month2 = today.Month;
			b2 = ((year != year2 || month != month2) ? 1 : 0);
		}
		else
		{
			b2 = 1;
		}
		defaultInterpolatedStringHandler.AppendFormatted(B((byte)b2 != 0));
		defaultInterpolatedStringHandler.AppendLiteral(",\"v\":\"");
		defaultInterpolatedStringHandler.AppendFormatted(version);
		defaultInterpolatedStringHandler.AppendLiteral("\",\"gpu\":\"");
		defaultInterpolatedStringHandler.AppendFormatted(gpu);
		defaultInterpolatedStringHandler.AppendLiteral("\"}");
		return defaultInterpolatedStringHandler.ToStringAndClear();
		static string B(bool flag)
		{
			if (!flag)
			{
				return "false";
			}
			return "true";
		}
		static (int, int) Week(DateOnly d)
		{
			return (ISOWeek.GetYear(d.ToDateTime(default)), ISOWeek.GetWeekOfYear(d.ToDateTime(default)));
		}
	}

	public async Task SendAsync(CancellationToken ct = default(CancellationToken))
	{
		if (!enabled() || version.Channel == "internal" || clock.GetUtcNow() < retryAt)
		{
			return;
		}
		await sending.WaitAsync(ct);
		try
		{
			DateOnly today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
			DateOnly? dateOnly = Last();
			if (!enabled() || dateOnly >= today || clock.GetUtcNow() < retryAt)
			{
				return;
			}
			retryAt = clock.GetUtcNow() + TimeSpan.FromHours(1);
			using StringContent body = new StringContent(Body(dateOnly, today, version, gpu), Encoding.UTF8, "application/json");
			using HttpResponseMessage httpResponseMessage = await http.PostAsync(new Uri(routes.Primary, "v1/active"), body, ct);
			if (!httpResponseMessage.IsSuccessStatusCode)
			{
				return;
			}
			Directory.CreateDirectory(Path.GetDirectoryName(file));
			File.WriteAllText(file, today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
		}
		catch (Exception) when (!ct.IsCancellationRequested)
		{
		}
		finally
		{
			sending.Release();
		}
	}

	private DateOnly? Last()
	{
		if (!File.Exists(file) || !DateOnly.TryParseExact(File.ReadAllText(file).Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
		{
			return null;
		}
		return result;
	}
}
