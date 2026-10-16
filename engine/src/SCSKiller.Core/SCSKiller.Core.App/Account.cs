using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SCSKiller.Core.App;

public sealed class Account
{
	private sealed record Stored(string Member);

	public static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5L);

	private const string Unreachable = "Can't reach the SCSKiller server right now. Check your internet connection, or try again later.";

	private const string NotRevoked = "Signed out on this PC. The server couldn't remove it from your account's devices. It drops off there after 90 days unused.";

	private readonly string file;

	private readonly RouteFailover routes;

	private readonly HttpClient http;

	private readonly Action<Uri> openBrowser;

	private readonly TimeProvider clock;

	private string? deviceToken;

	private string? accessToken;

	private long accessAt;

	private TimeSpan accessLife;

	private CancellationTokenSource? signIn;

	public bool SignedIn => deviceToken != null;

	public bool SigningIn => signIn != null;

	public bool Busy { get; private set; }

	public AccountStatus? Status { get; private set; }

	public bool HasDb => Status?.Ent.Contains("db") ?? false;

	public string? Problem { get; private set; }

	public bool JustSignedIn { get; private set; }

	private TimeSpan AccessAge => clock.GetElapsedTime(accessAt);

	public bool NeedsRefresh
	{
		get
		{
			if (accessToken != null)
			{
				return AccessAge >= accessLife / 2.0;
			}
			return true;
		}
	}

	public event Action? Changed;

	public Account(string dataDir, RouteFailover? routes = null, Action<Uri>? openBrowser = null, TimeProvider? clock = null)
	{
		file = Path.Combine(dataDir, "auth.dat");
		this.routes = routes ?? RouteFailover.Default;
		http = new HttpClient(this.routes, disposeHandler: false)
		{
			Timeout = TimeSpan.FromSeconds(60L)
		};
		this.openBrowser = openBrowser ?? ((Action<Uri>)((Uri u) =>
		{
			Process.Start(new ProcessStartInfo(u.AbsoluteUri)
			{
				UseShellExecute = true
			})?.Dispose();
		}));
		this.clock = clock ?? TimeProvider.System;
		deviceToken = Load();
	}

	public bool OffersSharing(Settings s)
	{
		if (JustSignedIn && !s.ShareRecordings)
		{
			return !s.SharePromptDismissed;
		}
		return false;
	}

	public async Task<bool> SignInAsync(CancellationToken ct = default(CancellationToken))
	{
		CancelSignIn();
		CancellationTokenSource mine = CancellationTokenSource.CreateLinkedTokenSource(ct);
		using CancellationTokenSource timeout = new CancellationTokenSource(SignInTimeout);
		using CancellationTokenSource any = CancellationTokenSource.CreateLinkedTokenSource(mine.Token, timeout.Token);
		signIn = mine;
		Busy = true;
		Problem = null;
		Raise();
		try
		{
			using LoopbackCallback loopback = new LoopbackCallback();
			string verifier = Pkce.Random(32);
			string state = Pkce.Random(16);
			using (HttpResponseMessage health = await http.GetAsync(new Uri(routes.Primary, "healthz"), any.Token))
			{
				if (health.StatusCode >= HttpStatusCode.InternalServerError)
				{
					await AnswerAsync(health, any.Token);
				}
			}
			openBrowser(new Uri(routes.Current, $"v1/auth/start?provider=patreon&port={loopback.Port}&challenge={Pkce.Challenge(verifier)}&state={state}"));
			string code = await loopback.WaitAsync(state, any.Token);
			using HttpResponseMessage r = await SendAsync(HttpMethod.Post, "v1/auth/exchange", null, any.Token, new
			{
				code = code,
				verifier = verifier,
				label = DeviceLabel()
			});
			if (r.StatusCode == HttpStatusCode.BadRequest)
			{
				throw new AccountException("The sign-in expired or was already used. Please try again.");
			}
			JsonElement j = await AnswerAsync(r, any.Token);
			Save(j.GetProperty("device_token").GetString());
			SetAccess(r, j);
			JustSignedIn = true;
			return true;
		}
		catch (OperationCanceledException) when (timeout.IsCancellationRequested && !mine.IsCancellationRequested)
		{
			Problem = $"Sign-in timed out after {SignInTimeout.TotalMinutes:0} minutes. If the Patreon page didn't load, Patreon may be " + "unreachable from your network right now. If a football match is on, try again afterwards.";
		}
		catch (OperationCanceledException) when (mine.IsCancellationRequested)
		{
		}
		catch (Exception e)
		{
			Problem = Plain(e);
		}
		finally
		{
			if (signIn == mine)
			{
				signIn = null;
				Busy = false;
			}
			Raise();
		}
		return false;
	}

	public void CancelSignIn()
	{
		try
		{
			signIn?.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
	}

	public async Task RefreshAsync(CancellationToken ct = default(CancellationToken))
	{
		if (deviceToken == null)
		{
			return;
		}
		Busy = true;
		Problem = null;
		Raise();
		try
		{
			await NewAccessTokenAsync(ct);
		}
		catch (Exception e) when (!ct.IsCancellationRequested)
		{
			Problem = Plain(e);
		}
		finally
		{
			Busy = false;
			Raise();
		}
	}

	public async Task<string?> GetAccessTokenAsync(CancellationToken ct = default(CancellationToken))
	{
		if (deviceToken == null)
		{
			return null;
		}
		if (!NeedsRefresh)
		{
			return accessToken;
		}
		try
		{
			await NewAccessTokenAsync(ct);
		}
		catch (Exception ex) when (((ex is HttpRequestException || ex is AccountException) ? true : false) && accessToken != null && AccessAge < accessLife)
		{
		}
		return accessToken;
	}

	public Task<string?> GetDbTokenAsync(bool fresh = false, CancellationToken ct = default(CancellationToken))
	{
		return GetTokenAsync("db", fresh, ct);
	}

	public async Task<string?> GetTokenAsync(string flag, bool fresh = false, CancellationToken ct = default(CancellationToken))
	{
		if (fresh && deviceToken != null)
		{
			await NewAccessTokenAsync(ct);
		}
		string text = await GetAccessTokenAsync(ct);
		return (Status?.Ent.Contains(flag) ?? false) ? text : null;
	}

	public async Task SignOutAsync(CancellationToken ct = default(CancellationToken))
	{
		string text = deviceToken;
		if (text == null)
		{
			return;
		}
		Busy = true;
		Problem = null;
		Raise();
		try
		{
			Forget();
			using HttpResponseMessage httpResponseMessage = await SendAsync(HttpMethod.Post, "v1/auth/logout", text, ct);
			if (httpResponseMessage.StatusCode >= HttpStatusCode.InternalServerError)
			{
				Problem = "Signed out on this PC. The server couldn't remove it from your account's devices. It drops off there after 90 days unused.";
			}
		}
		catch (Exception ex) when ((ex is HttpRequestException || ex is OperationCanceledException) ? true : false)
		{
			Problem = "Signed out on this PC. The server couldn't remove it from your account's devices. It drops off there after 90 days unused.";
		}
		catch (Exception e)
		{
			Problem = Plain(e);
		}
		finally
		{
			Busy = false;
			Raise();
		}
	}

	private async Task NewAccessTokenAsync(CancellationToken ct)
	{
		string token = deviceToken;
		using HttpResponseMessage r = await SendAsync(HttpMethod.Post, "v1/token", token, ct);
		if (!(deviceToken != token))
		{
			if (r.StatusCode == HttpStatusCode.Unauthorized)
			{
				Forget();
				throw new AccountException("This PC was signed out of your Patreon account (removed from its devices, or unused for 90 days). Sign in again.");
			}
			HttpResponseMessage r2 = r;
			SetAccess(r2, await AnswerAsync(r, ct));
		}
	}

	private void SetAccess(HttpResponseMessage r, JsonElement j)
	{
		accessToken = j.GetProperty("access_token").GetString();
		accessAt = clock.GetTimestamp();
		accessLife = j.GetProperty("exp").GetDateTimeOffset() - (r.Headers.Date ?? clock.GetUtcNow());
		string?[] ent = (from e in j.GetProperty("ent").EnumerateArray()
			select e.GetString()).ToArray();
		JsonElement property = j.GetProperty("until");
		Status = new AccountStatus(ent, (property.ValueKind == JsonValueKind.String) ? new DateTimeOffset?(property.GetDateTimeOffset()) : ((DateTimeOffset?)null));
	}

	private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? bearer, CancellationToken ct, object? json = null)
	{
		HttpRequestMessage httpRequestMessage = new HttpRequestMessage(method, new Uri(routes.Primary, path))
		{
			Content = ((json == null) ? null : JsonContent.Create(json))
		};
		if (bearer != null)
		{
			httpRequestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
		}
		return http.SendAsync(httpRequestMessage, ct);
	}

	private static async Task<JsonElement> AnswerAsync(HttpResponseMessage r, CancellationToken ct)
	{
		if (!r.IsSuccessStatusCode)
		{
			HttpStatusCode statusCode = r.StatusCode;
			string message;
			if (statusCode >= HttpStatusCode.InternalServerError)
			{
				message = $"The SCSKiller server had a problem (error {(int)r.StatusCode}). Try again in a few minutes.";
			}
			else
			{
				message = ((statusCode != HttpStatusCode.TooManyRequests) ? $"The SCSKiller server refused the request (error {(int)r.StatusCode}). If it keeps happening, sign out and in again." : "Too many attempts from your network. Wait a while and try again.");
			}
			throw new AccountException(message);
		}
		return await r.Content.ReadFromJsonAsync<JsonElement>(ct);
	}

	private static string Plain(Exception e)
	{
		if (!(e is AccountException))
		{
			if (!(e is HttpRequestException))
			{
				if (!(e is OperationCanceledException))
				{
					if (!(e is JsonException) && !(e is KeyNotFoundException) && !(e is InvalidOperationException) && !(e is FormatException))
					{
						if (e is Win32Exception)
						{
							return "Couldn't open your web browser: " + e.Message;
						}
						return e.Message;
					}
					return "The SCSKiller server gave an answer this version doesn't understand. An update of SCSKiller may fix it.";
				}
				return "The SCSKiller server didn't answer in time. Try again later.";
			}
			return "Can't reach the SCSKiller server right now. Check your internet connection, or try again later.";
		}
		return e.Message;
	}

	private static string DeviceLabel()
	{
		return "Windows PC · " + RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZ23456789", 4);
	}

	private void Raise()
	{
		Changed?.Invoke();
	}

	private string? Load()
	{
		return Dpapi.Load<Stored>(file)?.Member;
	}

	private void Save(string token)
	{
		Dpapi.Save(file, new Stored(token));
		deviceToken = token;
	}

	private void Forget()
	{
		deviceToken = null;
		accessToken = null;
		Status = null;
		JustSignedIn = false;
		File.Delete(file);
	}
}
