using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SCSKiller.Core.Planning;

namespace SCSKiller.Core.App;

public sealed class Community
{
	public static readonly TimeSpan ManifestMaxAge = TimeSpan.FromHours(4);

	public const int MaxRaw = 335544320;

	public const int MaxFullRaw = 805306368;

	private readonly string dir;

	private readonly RouteFailover routes;

	private readonly HttpClient http;

	private readonly TimeProvider clock;

	private readonly Func<bool, CancellationToken, Task<string?>> dbToken;

	private readonly SemaphoreSlim manifestGate = new SemaphoreSlim(1, 1);

	private CommunityManifest? manifest;

	private DateTimeOffset backoffUntil;

	private DateTimeOffset packBackoffUntil;

	private volatile bool expired;

	private long manifestFailedAt;

	private long freeRefusedAt = -1L;

	private TimeSpan manifestWait;

	public Func<string, bool, CancellationToken, Task<string?>>? TokenWith { get; init; }

	public string? Problem { get; private set; }

	public TimeSpan BodyIdle { get; init; } = TimeSpan.FromSeconds(30L);

	public Community(string dataDir, Func<bool, CancellationToken, Task<string?>> dbToken, RouteFailover? routes = null, TimeProvider? clock = null)
	{
		dir = Path.Combine(dataDir, "community");
		this.routes = routes ?? RouteFailover.Default;
		http = new HttpClient(this.routes, disposeHandler: false)
		{
			Timeout = TimeSpan.FromSeconds(60L)
		};
		this.clock = clock ?? TimeProvider.System;
		this.dbToken = dbToken;
	}

	public void Expire()
	{
		expired = true;
	}

	public async Task<CommunityManifest?> ManifestAsync(CancellationToken ct = default(CancellationToken))
	{
		await manifestGate.WaitAsync(ct);
		try
		{
			string file = Path.Combine(dir, "manifest.bin");
			if (manifest == null && File.Exists(file))
			{
				try
				{
					manifest = CommunityManifest.Parse(File.ReadAllBytes(file));
				}
				catch (InvalidDataException)
				{
				}
			}
			if (manifest != null && !expired && clock.GetUtcNow() - File.GetLastWriteTimeUtc(file) < ManifestMaxAge)
			{
				return manifest;
			}
			if (clock.GetElapsedTime(manifestFailedAt) < manifestWait)
			{
				return manifest;
			}
			expired = false;
			byte[] have = ((manifest != null && File.Exists(file)) ? File.ReadAllBytes(file) : null);
			try
			{
				using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, new Uri(routes.Primary, $"v1/manifest/{manifest?.Epoch ?? 0}"));
				request.Options.Set(RouteFailover.AttemptTimeout, TimeSpan.FromSeconds(3L));
				if (have != null)
				{
					request.Headers.Range = new RangeHeaderValue(have.Length, null);
				}
				using HttpResponseMessage r = await http.SendAsync(request, ct);
				HttpStatusCode statusCode = r.StatusCode;
				byte[] array;
				if (statusCode != HttpStatusCode.OK)
				{
					if (statusCode != HttpStatusCode.PartialContent)
					{
						if (statusCode != HttpStatusCode.RequestedRangeNotSatisfiable || have == null)
						{
							goto IL_04df;
						}
						array = have;
					}
					else
					{
						if (have == null || r.Content.Headers.ContentRange?.From != have.Length)
						{
							goto IL_04df;
						}
						byte[] array2 = have;
						byte[] array3 = await r.Content.ReadAsByteArrayAsync(ct);
						int num = 0;
						byte[] array4 = new byte[array2.Length + array3.Length];
						ReadOnlySpan<byte> readOnlySpan = new ReadOnlySpan<byte>(array2);
						readOnlySpan.CopyTo(new Span<byte>(array4).Slice(num, readOnlySpan.Length));
						num += readOnlySpan.Length;
						ReadOnlySpan<byte> readOnlySpan2 = new ReadOnlySpan<byte>(array3);
						readOnlySpan2.CopyTo(new Span<byte>(array4).Slice(num, readOnlySpan2.Length));
						_ = num + readOnlySpan2.Length;
						array = array4;
					}
				}
				else
				{
					array = await r.Content.ReadAsByteArrayAsync(ct);
				}
				goto IL_04e2;
				IL_04e2:
				byte[] array5 = array;
				if (array5 == null)
				{
					Problem = Refused(r);
					statusCode = r.StatusCode;
					if ((statusCode == HttpStatusCode.TooManyRequests || statusCode == HttpStatusCode.ServiceUnavailable) ? true : false)
					{
						ManifestBackoff(RetryAt(r) - clock.GetUtcNow());
					}
				}
				else
				{
					CommunityManifest communityManifest = CommunityManifest.Parse(array5);
					if (array5 != have)
					{
						AppStore.WriteAtomic(file, array5);
					}
					File.SetLastWriteTimeUtc(file, clock.GetUtcNow().UtcDateTime);
					CommunityManifest communityManifest2 = communityManifest;
					manifest = communityManifest2;
					Problem = null;
				}
				goto end_IL_02bf;
				IL_04df:
				array = null;
				goto IL_04e2;
				end_IL_02bf:;
			}
			catch (Exception ex2) when (!ct.IsCancellationRequested)
			{
				Problem = Plain(ex2);
				if ((ex2 is HttpRequestException || ex2 is OperationCanceledException) ? true : false)
				{
					ManifestBackoff(TimeSpan.FromMinutes(5L));
				}
			}
			return manifest;
		}
		finally
		{
			manifestGate.Release();
		}
	}

	private void ManifestBackoff(TimeSpan wait)
	{
		long timestamp = clock.GetTimestamp();
		TimeSpan timeSpan;
		if (wait < TimeSpan.Zero)
		{
			timeSpan = TimeSpan.Zero;
		}
		else
		{
			timeSpan = ((wait > TimeSpan.FromDays(1)) ? TimeSpan.FromDays(1) : wait);
		}
		manifestFailedAt = timestamp;
		manifestWait = timeSpan;
	}

	public Task<CommunityDownload?> DownloadAsync(CommunityEntry e, string gameDir, CancellationToken ct = default(CancellationToken))
	{
		return Download(e.Object, e.Size, e.ContentHash, dbToken, gameDir, "community", null, ct, e.Full);
	}

	public Task<CommunityDownload?> DownloadDrawsAsync(CommunityEntry e, string gameDir, CancellationToken ct = default(CancellationToken))
	{
		return Download(e.Object, e.Size, e.ContentHash, dbToken, gameDir, "draws", null, ct);
	}

	public Task<CommunityDownload?> DownloadOfficialAsync(OfficialEntry e, string gameDir, CancellationToken ct = default(CancellationToken))
	{
		switch (e.Visibility)
		{
		case OfficialVisibility.Everyone:
			if (freeRefusedAt == (manifest?.Length ?? 0))
			{
				return Task.FromResult<CommunityDownload>(null);
			}
			return Download(e.Object, e.Size, e.ContentHash, null, gameDir, "official", e.Visibility, ct);
		case OfficialVisibility.Patreon:
			return Download(e.Object, e.Size, e.ContentHash, dbToken, gameDir, "official", e.Visibility, ct);
		case OfficialVisibility.Prio:
		{
			Func<string, bool, CancellationToken, Task<string?>> tokenWith = TokenWith;
			if (tokenWith != null)
			{
				return Download(e.Object, e.Size, e.ContentHash, (bool fresh, CancellationToken c) => tokenWith("prio", fresh, c), gameDir, "official", e.Visibility, ct);
			}
			break;
		}
		}
		return Task.FromResult<CommunityDownload>(null);
	}

	private async Task<CommunityDownload?> Download(string obj, long size, string contentHash, Func<bool, CancellationToken, Task<string?>>? token, string gameDir, string name, OfficialVisibility? visibility, CancellationToken ct, bool full = false)
	{
		if (clock.GetUtcNow() < backoffUntil)
		{
			return null;
		}
		try
		{
			if (size > (full ? 805306368 : 335544320))
			{
				return null;
			}
			string token2 = null;
			bool flag = token != null;
			if (flag)
			{
				flag = (token2 = await token(arg1: false, ct)) == null;
			}
			if (flag)
			{
				return null;
			}
			HttpResponseMessage r = await GetObject(obj, token2, ct);
			flag = r.StatusCode == HttpStatusCode.Unauthorized && token != null;
			string fresh = default;
			if (flag)
			{
				fresh = await token(arg1: true, ct);
				flag = fresh != null;
			}
			if (flag)
			{
				r.Dispose();
				r = await GetObject(obj, fresh, ct);
			}
			using (r)
			{
				flag = token == null;
				if (flag)
				{
					HttpStatusCode statusCode = r.StatusCode;
					bool flag2 = ((statusCode == HttpStatusCode.Unauthorized || statusCode == HttpStatusCode.Forbidden) ? true : false);
					flag = flag2;
				}
				if (flag)
				{
					freeRefusedAt = manifest?.Length ?? 0;
					return null;
				}
				if (!r.IsSuccessStatusCode)
				{
					HttpStatusCode statusCode = r.StatusCode;
					if ((statusCode == HttpStatusCode.TooManyRequests || statusCode == HttpStatusCode.ServiceUnavailable) ? true : false)
					{
						backoffUntil = RetryAt(r);
					}
					Problem = Refused(r);
					return null;
				}
				byte[] array = await Body(r, (int)size, ct);
				if (array == null || Convert.ToHexStringLower(SHA256.HashData(array)) != obj)
				{
					Problem = "A community recording didn't match its checksum and was ignored. It's downloaded again at the next scan.";
					return null;
				}
				byte[] compact;
				int? num;
				if (name == "draws")
				{
					compact = await Bounded(new BrotliStream(new MemoryStream(array), CompressionMode.Decompress), 335544320, ct);
					num = ((compact == null) ? ((int?)null) : Check(compact, draws: true));
				}
				else
				{
					num = CheckPacked(array, out compact, full);
				}
				if (num.HasValue)
				{
					int valueOrDefault = num.GetValueOrDefault();
					CommunityDownload communityDownload = new CommunityDownload(obj, contentHash, valueOrDefault, clock.GetUtcNow(), visibility, full);
					string store = Path.Combine(gameDir, "recording.db");
					CancellationToken ct2 = ct;
					using (Recordings.Lock(store, null, ct2))
					{
						if (name == "community")
						{
							File.Delete(Path.Combine(gameDir, "community.json"));
						}
						AppStore.WriteAtomic(Path.Combine(gameDir, name + ".db"), compact);
						Write(Path.Combine(gameDir, name + ".json"), communityDownload);
					}
					Problem = null;
					return communityDownload;
				}
				Problem = "A community recording this version can't read was ignored. An update of SCSKiller may fix it.";
				return null;
			}
		}
		catch (Exception ex) when (!ct.IsCancellationRequested)
		{
			if ((ex is HttpRequestException || ex is OperationCanceledException) ? true : false)
			{
				backoffUntil = clock.GetUtcNow() + TimeSpan.FromMinutes(5L);
			}
			Problem = Plain(ex);
			return null;
		}
	}

	public async Task<int?> DownloadPackAsync(CommunityEntry e, MiddlewareDll dll, MiddlewareImage image, MiddlewarePacks shared, bool amd, CancellationToken ct = default(CancellationToken))
	{
		if (clock.GetUtcNow() < packBackoffUntil || e.Size > 335544320)
		{
			return null;
		}
		try
		{
			using HttpResponseMessage r = await http.GetAsync(new Uri(routes.Primary, "v1/p/" + e.Object), HttpCompletionOption.ResponseHeadersRead, ct);
			if (!r.IsSuccessStatusCode)
			{
				HttpStatusCode statusCode = r.StatusCode;
				if ((statusCode == HttpStatusCode.TooManyRequests || statusCode == HttpStatusCode.ServiceUnavailable) ? true : false)
				{
					packBackoffUntil = RetryAt(r);
				}
				Problem = Refused(r);
				return null;
			}
			byte[] array = await Body(r, (int)e.Size, ct);
			if (array == null || Convert.ToHexStringLower(SHA256.HashData(array)) != e.Object)
			{
				Problem = "An upscaler pack didn't match its checksum and was ignored. It's downloaded again at the next scan.";
				return null;
			}
			MiddlewarePack middlewarePack;
			try
			{
				middlewarePack = MiddlewarePacks.FromShared(HashOnly.CheckPack(HashOnly.Canonical(HashOnly.Decompress(array, 335544320L), local: false, out HashOnly.Dropped _, 1048576, null, 250000)), dll, image, amd, e.Object, out var _);
			}
			catch (InvalidDataException)
			{
				Problem = "An upscaler pack this version can't read was ignored. An update of SCSKiller may fix it.";
				return null;
			}
			middlewarePack.Write(shared.PathOf(dll.Vendor, dll.Name, image.ContentHash));
			Problem = null;
			return middlewarePack.Entries.Count;
		}
		catch (Exception ex2) when (!ct.IsCancellationRequested)
		{
			if ((ex2 is HttpRequestException || ex2 is OperationCanceledException) ? true : false)
			{
				packBackoffUntil = clock.GetUtcNow() + TimeSpan.FromMinutes(5L);
			}
			Problem = Plain(ex2);
			return null;
		}
	}

	private Task<HttpResponseMessage> GetObject(string obj, string? token, CancellationToken ct)
	{
		HttpRequestMessage httpRequestMessage = new HttpRequestMessage(HttpMethod.Get, new Uri(routes.Primary, "v1/o/" + obj));
		if (token != null)
		{
			httpRequestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		}
		return http.SendAsync(httpRequestMessage, HttpCompletionOption.ResponseHeadersRead, ct);
	}

	public static CommunityDownload? Downloaded(string gameDir)
	{
		return Downloaded(gameDir, "community");
	}

	public static bool Restate(string gameDir, OfficialEntry e, CancellationToken ct = default(CancellationToken))
	{
		if (Differs() == null)
		{
			return false;
		}
		using (Recordings.Lock(Path.Combine(gameDir, "recording.db"), null, ct))
		{
			CommunityDownload communityDownload = Differs();
			if ((object)communityDownload == null)
			{
				return false;
			}
			Write(Path.Combine(gameDir, "official.json"), communityDownload with
			{
				Visibility = e.Visibility
			});
			return true;
		}
		CommunityDownload? Differs()
		{
			CommunityDownload communityDownload2 = DownloadedOfficial(gameDir);
			if ((object)communityDownload2 == null || !(communityDownload2.Object == e.Object) || communityDownload2.Visibility == e.Visibility)
			{
				return null;
			}
			return communityDownload2;
		}
	}

	public static CommunityDownload? DownloadedDraws(string gameDir)
	{
		return Downloaded(gameDir, "draws");
	}

	public static CommunityDownload? DownloadedOfficial(string gameDir)
	{
		return Downloaded(gameDir, "official");
	}

	private static CommunityDownload? Downloaded(string gameDir, string name)
	{
		if (!File.Exists(Path.Combine(gameDir, name + ".db")))
		{
			return null;
		}
		return Read<CommunityDownload>(Path.Combine(gameDir, name + ".json"));
	}

	public static int? Check(byte[] raw, bool draws = false)
	{
		try
		{
			List<PsoDb.Rec> canonical = HashOnly.Canonical(HashOnly.Records(raw, 250001), local: false, out HashOnly.Dropped _, 1048576, null, 250000);
			return HashOnly.Count(draws ? HashOnly.CheckDraws(canonical) : HashOnly.CheckNoDraws(canonical)).Psos;
		}
		catch (InvalidDataException)
		{
			return null;
		}
	}

	internal static int? CheckPacked(byte[] packed, out byte[]? compact, bool full = false)
	{
		compact = null;
		try
		{
			using BrotliStream stream = new BrotliStream(new MemoryStream(packed), CompressionMode.Decompress);
			int value;
			long raw;
			if (full)
			{
				(value, raw) = HashOnly.Verify(HashOnly.Records(new BufferedStream(stream, 65536), 805306368L), 1048576, 500000, flagsApart: true);
			}
			else
			{
				(value, raw) = HashOnly.Verify(HashOnly.Records(new BufferedStream(stream, 65536), 335544320L), 1048576, 250000);
			}
			compact = PsoDb.Compact(raw, packed);
			return value;
		}
		catch (InvalidDataException)
		{
			return null;
		}
	}

	public static void Union(string first, IReadOnlyList<string> more, string output, IReadOnlySet<string>? leaveOut = null)
	{
		HashSet<string> have = (File.Exists(first) ? (from r in PsoDb.Read(first)
			select r.Key).ToHashSet() : new HashSet<string>());
		if (leaveOut != null)
		{
			have.UnionWith(leaveOut);
		}
		string text = output + ".tmp";
		using (BufferedStream s = new BufferedStream(File.Create(text), 1048576))
		{
			foreach (PsoDb.Rec item in Pass(blobs: true).Concat(Pass(blobs: false)))
			{
				PsoDb.Write(s, item.Tag, item.Payload);
			}
		}
		File.Move(text, output, overwrite: true);
		IEnumerable<PsoDb.Rec> Pass(bool blobs)
		{
			IEnumerable<PsoDb.Rec> source;
			if (!File.Exists(first))
			{
				IEnumerable<PsoDb.Rec> enumerable = Array.Empty<PsoDb.Rec>();
				source = enumerable;
			}
			else
			{
				source = PsoDb.Read(first);
			}
			return source.Where((PsoDb.Rec r) => r.Tag == 'B' == blobs).Concat(more.SelectMany((string m) => from r in PsoDb.Read(m)
				where r.Tag == 'B' == blobs && have.Add(r.Key)
				select r));
		}
	}

	private async Task<byte[]?> Body(HttpResponseMessage r, int max, CancellationToken ct)
	{
		CancellationTokenSource idle = new CancellationTokenSource(BodyIdle, clock);
		try
		{
			using CancellationTokenSource either = CancellationTokenSource.CreateLinkedTokenSource(ct, idle.Token);
			return await Bounded(await r.Content.ReadAsStreamAsync(either.Token), max, either.Token, () =>
			{
				idle.CancelAfter(BodyIdle);
			});
		}
		finally
		{
			if (idle != null)
			{
				((IDisposable)idle).Dispose();
			}
		}
	}

	private static async Task<byte[]?> Bounded(Stream s, int max, CancellationToken ct, Action? progress = null)
	{
		byte[] result;
		await using (s)
		{
			MemoryStream buf = new MemoryStream();
			byte[] chunk = new byte[81920];
			while (true)
			{
				int num;
				if ((num = await s.ReadAsync(chunk, ct)) > 0)
				{
					if (buf.Length + num > max)
					{
						result = null;
						break;
					}
					if (buf.Length + num > buf.Capacity)
					{
						buf.Capacity = (int)Math.Min(max, Math.Max(2L * (long)buf.Capacity, buf.Length + num));
					}
					buf.Write(chunk, 0, num);
					progress?.Invoke();
					continue;
				}
				result = buf.ToArray();
				break;
			}
		}
		return result;
	}

	private DateTimeOffset RetryAt(HttpResponseMessage r)
	{
		RetryConditionHeaderValue retryAfter = r.Headers.RetryAfter;
		if (retryAfter != null)
		{
			TimeSpan? delta = retryAfter.Delta;
			if (delta.HasValue)
			{
				TimeSpan valueOrDefault = delta.GetValueOrDefault();
				return clock.GetUtcNow() + valueOrDefault;
			}
			DateTimeOffset? date = retryAfter.Date;
			if (date.HasValue)
			{
				return date.GetValueOrDefault();
			}
		}
		return clock.GetUtcNow() + TimeSpan.FromHours(1);
	}

	private static string Refused(HttpResponseMessage r)
	{
		HttpStatusCode statusCode = r.StatusCode;
		if (statusCode >= HttpStatusCode.InternalServerError)
		{
			if (statusCode != HttpStatusCode.ServiceUnavailable)
			{
				return $"The community database had a problem (error {(int)r.StatusCode}). It retries at the next scan.";
			}
			return "The community database is busy right now. Downloads resume later by themselves.";
		}
		return statusCode switch
		{
			HttpStatusCode.Unauthorized => "The community database didn't accept this PC's sign-in. Refresh status or sign in again in Settings.", 
			HttpStatusCode.Forbidden => "Your Patreon membership doesn't include the community database right now.", 
			HttpStatusCode.TooManyRequests => "The community database's daily limit is reached. Downloads resume later by themselves.", 
			_ => $"The community database refused the request (error {(int)r.StatusCode}).", 
		};
	}

	private static string Plain(Exception e)
	{
		if (!(e is HttpRequestException))
		{
			if (!(e is OperationCanceledException))
			{
				if (!(e is AccountException))
				{
					if (e is InvalidDataException)
					{
						return "The community database sent a list this version can't read. An update of SCSKiller may fix it.";
					}
					return "The community database gave an answer this version doesn't understand. An update of SCSKiller may fix it.";
				}
				return e.Message;
			}
			return "The community database didn't answer in time. It retries at the next scan.";
		}
		return "Can't reach the community database right now. Compiling works as usual from your own files.";
	}

	internal static T? Read<T>(string path) where T : class
	{
		try
		{
			return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllBytes(path)) : null;
		}
		catch (Exception ex) when ((ex is JsonException || ex is IOException) ? true : false)
		{
			return null;
		}
	}

	internal static void Write<T>(string path, T value)
	{
		AppStore.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(value));
	}
}
