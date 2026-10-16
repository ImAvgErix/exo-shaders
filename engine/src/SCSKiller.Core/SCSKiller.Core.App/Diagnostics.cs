using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SCSKiller.Core.App;

public sealed class Diagnostics
{
	private static readonly JsonSerializerOptions Json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
	{
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
	};

	private readonly Func<bool> enabled;

	private readonly RouteFailover routes;

	private readonly HttpClient http;

	public Diagnostics(Func<bool> enabled, RouteFailover? routes = null)
	{
		this.enabled = enabled;
		this.routes = routes ?? RouteFailover.Default;
		http = new HttpClient(this.routes, disposeHandler: false)
		{
			Timeout = TimeSpan.FromSeconds(30L)
		};
	}

	public static byte[] Body(DiagnosticsReport report)
	{
		return JsonSerializer.SerializeToUtf8Bytes(report, Json);
	}

	public static bool Asks(Settings s)
	{
		if (s.WelcomeSeen && !s.DiagnosticsAsked)
		{
			return !s.ShareDiagnostics;
		}
		return false;
	}

	public static Settings Answered(Settings s, bool share)
	{
		return s with
		{
			DiagnosticsAsked = true,
			ShareDiagnostics = share
		};
	}

	public async Task<bool> SendAsync(DiagnosticsReport report, CancellationToken ct = default(CancellationToken))
	{
		if (!enabled())
		{
			return false;
		}
		try
		{
			using ByteArrayContent content = new ByteArrayContent(Body(report));
			content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
			using HttpResponseMessage httpResponseMessage = await http.PostAsync(new Uri(routes.Primary, "v1/diagnostics"), content, ct);
			return httpResponseMessage.StatusCode == HttpStatusCode.NoContent || httpResponseMessage.IsSuccessStatusCode;
		}
		catch (Exception) when (!ct.IsCancellationRequested)
		{
			return false;
		}
	}
}
