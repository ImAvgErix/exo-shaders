using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace SCSKiller.Core.App;

public sealed class RouteFailover : DelegatingHandler
{
	public static readonly Uri[] DefaultRoutes = new Uri[2]
	{
		new Uri("https://api.scskiller.com/"),
		new Uri("https://api.scskiller.io/")
	};

	public static readonly TimeSpan Sticky = TimeSpan.FromMinutes(30L);

	public static readonly RouteFailover Default = new RouteFailover();

	private readonly TimeProvider clock;

	private readonly TimeSpan headersTimeout;

	private readonly Lock gate = new Lock();

	private int current;

	private DateTimeOffset stickyUntil;

	public static readonly HttpRequestOptionsKey<TimeSpan> AttemptTimeout = new HttpRequestOptionsKey<TimeSpan>("SCSKiller.AttemptTimeout");

	public static string Product { get; set; } = "SCSKiller";

	public IReadOnlyList<Uri> Routes { get; }

	public Uri Primary => Routes[0];

	public Uri Current => Routes[First()];

	public Task Probe { get; private set; } = Task.CompletedTask;

	public static string UserAgent(string product, AppVersion v, int windowsBuild)
	{
		return $"{product}/{v} ({v.Channel}; Windows {windowsBuild})";
	}

	public RouteFailover(HttpMessageHandler? inner = null, IReadOnlyList<Uri>? routes = null, TimeProvider? clock = null, TimeSpan? headersTimeout = null)
		: base(inner ?? new SocketsHttpHandler
		{
			ConnectTimeout = TimeSpan.FromSeconds(3L),
			PooledConnectionLifetime = TimeSpan.FromMinutes(5L)
		})
	{
		Routes = routes ?? FromEnvironment() ?? DefaultRoutes;
		this.clock = clock ?? TimeProvider.System;
		this.headersTimeout = headersTimeout ?? TimeSpan.FromSeconds(15L);
	}

	private static Uri[]? FromEnvironment()
	{
		string environmentVariable = Environment.GetEnvironmentVariable("SCSKILLER_API");
		if (environmentVariable == null || environmentVariable.Length <= 0)
		{
			return null;
		}
		return (from h in environmentVariable.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			select new Uri(h.TrimEnd('/') + "/")).ToArray();
	}

	private int First()
	{
		using (gate.EnterScope())
		{
			if (current != 0 && clock.GetUtcNow() >= stickyUntil && Probe.IsCompleted)
			{
				stickyUntil = DateTimeOffset.MaxValue;
				Probe = Task.Run((Func<Task?>)ProbeAsync);
			}
			return current;
		}
	}

	private async Task ProbeAsync()
	{
		bool flag;
		try
		{
			using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3L));
			using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, new Uri(Primary, "healthz"));
			using HttpResponseMessage httpResponseMessage = await SendWithAgent(request, timeout.Token).ConfigureAwait(continueOnCapturedContext: false);
			flag = httpResponseMessage.Headers.Contains("X-SCSK");
		}
		catch (Exception ex) when ((ex is HttpRequestException || ex is OperationCanceledException) ? true : false)
		{
			flag = false;
		}
		using (gate.EnterScope())
		{
			if (!flag)
			{
				int num = current;
				DateTimeOffset dateTimeOffset = clock.GetUtcNow() + Sticky;
				current = num;
				stickyUntil = dateTimeOffset;
			}
			else
			{
				current = 0;
				stickyUntil = default;
			}
		}
	}

	public async Task<string?> GetContentAsync(string path, TimeSpan budget, int maxBytes, string? bearer = null)
	{
		return (await GetContentStatusAsync(path, budget, maxBytes, bearer)).Item1;
	}

	public async Task<(string? Body, HttpStatusCode? Status)> GetContentStatusAsync(string path, TimeSpan budget, int maxBytes, string? bearer = null)
	{
		using HttpClient http = new HttpClient(this, disposeHandler: false)
		{
			MaxResponseContentBufferSize = maxBytes
		};
		using CancellationTokenSource cts = new CancellationTokenSource(budget, clock);
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, new Uri(Primary, path));
		if (bearer != null)
		{
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
		}
		request.Options.Set(AttemptTimeout, budget / 2.0);
		HttpStatusCode? status = null;
		try
		{
			using HttpResponseMessage r = await http.SendAsync(request, cts.Token);
			status = r.StatusCode;
			string item = ((!r.IsSuccessStatusCode) ? null : (await r.Content.ReadAsStringAsync(cts.Token)));
			return (Body: item, Status: status);
		}
		catch (Exception)
		{
			return (Body: null, Status: status);
		}
	}

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
	{
		Uri requestUri = request.RequestUri;
		if (Uri.Compare(requestUri, Primary, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) != 0)
		{
			return await SendWithAgent(request, ct).ConfigureAwait(continueOnCapturedContext: false);
		}
		string path = requestUri.PathAndQuery.TrimStart('/');
		bool idempotent = request.Method == HttpMethod.Get || request.Method == HttpMethod.Head;
		int first = First();
		Exception inner = null;
		for (int i = 0; i < Routes.Count; i++)
		{
			int at = (first + i) % Routes.Count;
			request.RequestUri = new Uri(Routes[at], path);
			TimeSpan value;
			using CancellationTokenSource timeout = new CancellationTokenSource(request.Options.TryGetValue(AttemptTimeout, out value) ? value : headersTimeout, clock);
			using (CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token))
			{
				_ = 1;
				bool flag2;
				try
				{
					HttpResponseMessage httpResponseMessage = await SendWithAgent(request, attempt.Token).ConfigureAwait(continueOnCapturedContext: false);
					if (httpResponseMessage.StatusCode >= HttpStatusCode.InternalServerError && !httpResponseMessage.Headers.Contains("X-SCSK"))
					{
						if (!idempotent)
						{
							return httpResponseMessage;
						}
						inner = new HttpRequestException($"{Routes[at].Host}: {(int)httpResponseMessage.StatusCode} from the route", null, httpResponseMessage.StatusCode);
						httpResponseMessage.Dispose();
						continue;
					}
					using (gate.EnterScope())
					{
						if (at != first && current == first)
						{
							int num = at;
							DateTimeOffset dateTimeOffset = clock.GetUtcNow() + Sticky;
							current = num;
							stickyUntil = dateTimeOffset;
						}
					}
					return httpResponseMessage;
				}
				catch (HttpRequestException ex) when (!ct.IsCancellationRequested)
				{
					inner = ex;
					HttpRequestError httpRequestError = ex.HttpRequestError;
					bool flag = (((uint)(httpRequestError - 1) <= 2u || httpRequestError == HttpRequestError.ProxyTunnelError) ? true : false);
					flag2 = !flag;
					goto IL_03cd;
				}
				catch (OperationCanceledException ex2) when (!ct.IsCancellationRequested)
				{
					inner = ex2;
					flag2 = attempt.IsCancellationRequested;
					goto IL_03cd;
				}
				IL_03cd:
				if (flag2 && !idempotent)
				{
					break;
				}
				goto end_IL_0192;
			}
			end_IL_0192:;
		}
		throw new ApiUnreachableException(inner);
	}

	private Task<HttpResponseMessage> SendWithAgent(HttpRequestMessage request, CancellationToken ct)
	{
		if (request.Headers.UserAgent.Count == 0)
		{
			request.Headers.UserAgent.ParseAdd(UserAgent(Product, AppVersion.Current, Environment.OSVersion.Version.Build));
		}
		return base.SendAsync(request, ct);
	}
}
