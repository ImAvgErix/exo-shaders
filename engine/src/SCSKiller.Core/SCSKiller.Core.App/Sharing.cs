using System;
using System.Buffers.Text;
using System.Collections.Generic;
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
using SCSKiller.Core.Planning;

namespace SCSKiller.Core.App;

public sealed class Sharing
{
	private sealed record Device(string Token, string Id);

	public const int MaxBody = 4194304;

	public const int ChunkRecords = 20000;

	public const long ChunkRaw = 25165824L;

	private static readonly JsonSerializerOptions Json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
	{
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
	};

	private readonly string file;

	private readonly Func<bool> enabled;

	private readonly RouteFailover routes;

	private readonly HttpClient http;

	private readonly TimeProvider clock;

	private DateTimeOffset backoffUntil;

	private DateTimeOffset reportBackoffUntil;

	public const string DrawsRefusedFile = "draws-refused.txt";

	public const string ReportedFile = "reported.txt";

	private const string CompatClosed = "Reports aren't open yet.";

	private bool compatClosed;

	public const string ShippedFile = "index.shaders";

	public string? Problem { get; private set; }

	public string? Waiting { get; set; }

	public string? DeviceId => Load()?.Id;

	private string DrawsRefusedPath => Path.Combine(Path.GetDirectoryName(file), "draws-refused.txt");

	public Sharing(string dataDir, Func<bool> enabled, RouteFailover? routes = null, TimeProvider? clock = null)
	{
		file = Path.Combine(dataDir, "upload.dat");
		this.enabled = enabled;
		this.routes = routes ?? RouteFailover.Default;
		http = new HttpClient(this.routes, disposeHandler: false)
		{
			Timeout = TimeSpan.FromSeconds(60L)
		};
		this.clock = clock ?? TimeProvider.System;
	}

	public void Reset()
	{
		File.Delete(file);
	}

	public static SharedRecording? Shared(string gameDir)
	{
		return Community.Read<SharedRecording>(Path.Combine(gameDir, "shared.json"));
	}

	public async Task<SharedRecording?> ShareAsync(string gameDir, string contentHash, Func<UploadMeta> meta, Func<IEnumerable<string>>? middlewareShaders = null, Func<IReadOnlySet<string>>? layerMade = null, string? planCheck = null, CancellationToken ct = default(CancellationToken))
	{
		FileInfo fileInfo = new FileInfo(Path.Combine(gameDir, "recording.db"));
		if (!enabled() || !fileInfo.Exists || clock.GetUtcNow() < backoffUntil)
		{
			return null;
		}
		Problem = null;
		FileInfo fileInfo2 = new FileInfo(Path.Combine(gameDir, "index.shaders"));
		bool drawsOff = DrawsRefused();
		string stamp = $"{fileInfo.Length}:{fileInfo.LastWriteTimeUtc.Ticks}|{contentHash}|3" + (fileInfo2.Exists ? $"|{fileInfo2.Length}:{fileInfo2.LastWriteTimeUtc.Ticks}" : "") + ((planCheck != null) ? ("|" + planCheck) : "") + (drawsOff ? "|nodraws" : "");
		SharedRecording last = Shared(gameDir) ?? new SharedRecording("");
		if (last.Stamp == stamp)
		{
			return null;
		}
		string shared = Path.Combine(gameDir, "shared.json");
		IReadOnlySet<string> layerMade2;
		try
		{
			layerMade2 = layerMade?.Invoke();
		}
		catch (Recordings.IncompleteLayerList incompleteLayerList)
		{
			Waiting = incompleteLayerList.Message;
			return null;
		}
		List<PsoDb.Rec> list = PsoDb.Read(fileInfo.FullName).ToList();
		List<PsoDb.Rec> list2 = list.Where((PsoDb.Rec r) => r.Tag == 'D' && Draw11Valid(r)).ToList();
		list.RemoveAll((PsoDb.Rec r) => r.Tag == 'D');
		List<PsoDb.Rec> list3 = new List<PsoDb.Rec>();
		HashOnly.Dropped dropped;
		if (list2.Count == 0 || list.Any((PsoDb.Rec r) => r.Tag != 'B'))
		{
			try
			{
				list3 = HashOnly.Canonical(list, local: true, out dropped, 1048576, layerMade2);
			}
			catch (InvalidDataException ex) when (list2.Count == 0)
			{
				Community.Write(shared, last with
				{
					Stamp = stamp
				});
				Problem = "A recording has nothing to share (" + ex.Message + ").";
				return null;
			}
			catch (InvalidDataException)
			{
			}
		}
		HashSet<string> hashSet = Shipped(gameDir, contentHash);
		if (hashSet != null)
		{
			hashSet.UnionWith(middlewareShaders?.Invoke() ?? Array.Empty<string>());
			if (list3.Count > 0)
			{
				List<PsoDb.Rec> list4 = new List<PsoDb.Rec>();
				list4.AddRange(list3);
				list4.AddRange(HashOnly.LocalOnly(list3, hashSet.Contains));
				list3 = HashOnly.Canonical(new _003C_003Ez__ReadOnlyList<PsoDb.Rec>(list4), local: false, out dropped, 1048576);
			}
		}
		string drawsKey = HashOnly.DrawsKey(contentHash);
		string drawsHash = HashOnly.PackHash(drawsKey);
		List<PsoDb.Rec> canonical = new List<PsoDb.Rec>();
		if (list2.Count > 0 && !drawsOff)
		{
			try
			{
				List<PsoDb.Rec> list5 = new List<PsoDb.Rec>();
				list5.AddRange(list2);
				IEnumerable<PsoDb.Rec> collection;
				if (hashSet != null)
				{
					collection = HashOnly.LocalOnly(list2, hashSet.Contains);
				}
				else
				{
					IEnumerable<PsoDb.Rec> enumerable = Array.Empty<PsoDb.Rec>();
					collection = enumerable;
				}
				list5.AddRange(collection);
				canonical = HashOnly.Canonical(new _003C_003Ez__ReadOnlyList<PsoDb.Rec>(list5), local: false, out dropped);
			}
			catch (InvalidDataException)
			{
			}
		}
		HashSet<string> have = ((Community.Downloaded(gameDir)?.ContentHash == contentHash) ? (from r in PsoDb.Read(Path.Combine(gameDir, "community.db"))
			where r.Tag != 'B'
			select r.Key).ToHashSet() : new HashSet<string>());
		HashSet<string> haveDraws = ((Community.DownloadedDraws(gameDir)?.ContentHash == drawsHash) ? (from r in PsoDb.Read(Path.Combine(gameDir, "draws.db"))
			select r.Key).ToHashSet() : new HashSet<string>());
		List<string> sent = new List<string>();
		string uploadId = null;
		int psos = 0;
		int fresh = 0;
		List<List<PsoDb.Rec>> source = HashOnly.Chunks(list3, 20000, 25165824L, out var skipped);
		IReadOnlySet<string> made;
		if (planCheck != null && planCheck != last.Meta)
		{
			made = null;
			try
			{
				made = layerMade?.Invoke();
			}
			catch (Exception)
			{
			}
			if (source.All((List<PsoDb.Rec> c) =>
			{
				List<PsoDb.Rec> list10 = Kept(c);
				if (list10 != null)
				{
					string[]? sent3 = last.Sent;
					if (sent3 == null || !sent3.Contains(Id(list10, contentHash)))
					{
						return list10.All((PsoDb.Rec r) => r.Tag == 'B' || have.Contains(r.Key));
					}
				}
				return true;
			}))
			{
				List<PsoDb.Rec> list6 = Kept(list3);
				if (list6 != null)
				{
					List<PsoDb.Rec> list7 = Smallest(list6);
					if (list7 != null)
					{
						source = new List<List<PsoDb.Rec>>(1) { list7 };
					}
				}
			}
		}
		string sentWith = last.Meta;
		if (skipped > 0)
		{
			Problem = $"{skipped} ray tracing records are more than an upload may carry with what they build on ({20000} records, {24L} MB, {262144} references): shared without them.";
		}
		foreach (var item in source.Select((List<PsoDb.Rec> c) => (c: c, false)).Concat(from c in HashOnly.Chunks(canonical, 20000, 25165824L)
			select (c: c, true)))
		{
			var (list8, isDraws) = item;
			if (isDraws & drawsOff)
			{
				break;
			}
			IReadOnlySet<string> readOnlySet;
			try
			{
				readOnlySet = layerMade?.Invoke();
			}
			catch (Recordings.IncompleteLayerList incompleteLayerList2)
			{
				Waiting = incompleteLayerList2.Message;
				return Stop(null);
			}
			catch (Exception ex5)
			{
				return Stop("What a layer made can't be read (" + ex5.Message + "): sharing waits.");
			}
			List<PsoDb.Rec> list9;
			try
			{
				list9 = (((readOnlySet == null) | isDraws) ? list8 : HashOnly.Canonical(list8, local: true, out dropped, 262144, readOnlySet));
			}
			catch (InvalidDataException)
			{
				continue;
			}
			string id = Id(list9, isDraws ? drawsHash : contentHash);
			string[]? sent2 = last.Sent;
			if ((sent2 != null && sent2.Contains(id)) || list9.All((PsoDb.Rec r) => r.Tag == 'B' || (isDraws ? haveDraws : have).Contains(r.Key)))
			{
				sent.Add(id);
				continue;
			}
			byte[] body = HashOnly.Compress(list9);
			if (body.Length > 4194304)
			{
				return Stop($"A recording is too large to share ({body.Length >> 20} MB compressed, the limit is {4} MB).");
			}
			try
			{
				HttpResponseMessage httpResponseMessage = await PostAsync(body, Meta(), ct);
				if (httpResponseMessage != null && httpResponseMessage.StatusCode == HttpStatusCode.Unauthorized)
				{
					httpResponseMessage.Dispose();
					Reset();
					httpResponseMessage = await PostAsync(body, Meta(), ct);
				}
				if (httpResponseMessage == null)
				{
					return Stop(Problem);
				}
				using (httpResponseMessage)
				{
					if (httpResponseMessage.StatusCode == HttpStatusCode.Accepted)
					{
						JsonElement jsonElement = await httpResponseMessage.Content.ReadFromJsonAsync<JsonElement>(ct);
						uploadId = jsonElement.GetProperty("upload_id").GetString();
						psos += jsonElement.GetProperty("records").GetInt32();
						fresh += jsonElement.GetProperty("new_records").GetInt32();
						sent.Add(id);
						if (!isDraws)
						{
							sentWith = planCheck;
						}
						continue;
					}
					bool flag = isDraws;
					HttpStatusCode statusCode;
					if (flag)
					{
						statusCode = httpResponseMessage.StatusCode;
						bool flag2 = ((statusCode == HttpStatusCode.BadRequest || (uint)(statusCode - 404) <= 1u) ? true : false);
						flag = flag2;
					}
					if (flag)
					{
						File.WriteAllText(DrawsRefusedPath, AppVersion.Current.ToString());
						drawsOff = true;
						stamp += "|nodraws";
						continue;
					}
					string text = $"Sharing a recording was refused (error {(int)httpResponseMessage.StatusCode}).";
					statusCode = httpResponseMessage.StatusCode;
					if ((statusCode == HttpStatusCode.BadRequest || statusCode == HttpStatusCode.Forbidden || statusCode == HttpStatusCode.RequestEntityTooLarge) ? true : false)
					{
						Problem = text;
						if (httpResponseMessage.StatusCode == HttpStatusCode.Forbidden)
						{
							break;
						}
						sent.Add(id);
						continue;
					}
					BackOff(httpResponseMessage);
					return Stop(text);
				}
			}
			catch (Exception ex7) when (!ct.IsCancellationRequested)
			{
				backoffUntil = clock.GetUtcNow() + TimeSpan.FromMinutes(5L);
				bool flag = ((ex7 is HttpRequestException || ex7 is OperationCanceledException) ? true : false);
				return Stop(flag ? "Can't reach the community database to share a recording." : ex7.Message);
			}
			UploadMeta Meta()
			{
				if (!isDraws)
				{
					return meta();
				}
				return meta()with
				{
					StoreBuildKey = drawsKey,
					ContentHash = drawsHash
				};
			}
		}
		SharedRecording sharedRecording = ((uploadId == null) ? last with
		{
			Stamp = stamp,
			Sent = sent.ToArray()
		} : new SharedRecording(stamp, clock.GetUtcNow(), uploadId, psos, fresh, sent.ToArray(), sentWith));
		Community.Write(shared, sharedRecording);
		return (uploadId == null) ? null : sharedRecording;
		List<PsoDb.Rec>? Kept(List<PsoDb.Rec> c)
		{
			try
			{
				HashOnly.Dropped dropped2;
				return (made == null) ? c : HashOnly.Canonical(c, local: true, out dropped2, 262144, made);
			}
			catch (InvalidDataException)
			{
				return null;
			}
		}
		SharedRecording? Stop(string? why)
		{
			if (sent.Count > 0)
			{
				Community.Write(shared, last with
				{
					Sent = (last.Sent ?? Array.Empty<string>()).Union(sent).ToArray()
				});
			}
			if (why != null)
			{
				Problem = why;
			}
			return null;
		}
	}

	private bool DrawsRefused()
	{
		try
		{
			return File.Exists(DrawsRefusedPath) && File.ReadAllText(DrawsRefusedPath) == AppVersion.Current.ToString();
		}
		catch (IOException)
		{
			return false;
		}
	}

	private static bool Draw11Valid(PsoDb.Rec r)
	{
		try
		{
			HashOnly.CheckDraw11(r);
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	internal static List<PsoDb.Rec>? Smallest(List<PsoDb.Rec> canonical)
	{
		PsoDb.Rec pso = canonical.FirstOrDefault((PsoDb.Rec r) =>
		{
			char tag = r.Tag;
			return (tag == 'C' || tag == 'G' || tag == 'S') ? true : false;
		});
		List<PsoDb.Rec> list = new List<PsoDb.Rec>();
		list.AddRange(canonical.Where((PsoDb.Rec r) =>
		{
			bool flag = r.Tag == 'B';
			if (!flag)
			{
				char tag = r.Tag;
				bool flag2 = ((tag == 'L' || tag == 'N') ? true : false);
				flag = flag2 && HashOnly.Target(r) == pso.Key;
			}
			return flag;
		}));
		list.Add(pso);
		HashOnly.Dropped dropped;
		return HashOnly.Canonical(new _003C_003Ez__ReadOnlyList<PsoDb.Rec>(list), local: false, out dropped);
	}

	public async Task<List<(string Dll, int Psos)>> SharePacksAsync(string packsDir, string gpu, string appVersion, Func<IReadOnlySet<string>>? layered = null, CancellationToken ct = default(CancellationToken))
	{
		List<(string, int)> done = new List<(string, int)>();
		if (!enabled() || clock.GetUtcNow() < backoffUntil)
		{
			return done;
		}
		string stampFile = Path.Combine(Path.GetDirectoryName(file), "packs-shared.json");
		Dictionary<string, string> stamps = Community.Read<Dictionary<string, string>>(stampFile) ?? new Dictionary<string, string>();
		List<string> list = Middleware.SharedVendors.Select((string v) => Path.Combine(packsDir, v)).Where(Directory.Exists).SelectMany((string d) => Directory.EnumerateFiles(d, "*.pack"))
			.Order(StringComparer.OrdinalIgnoreCase)
			.ToList();
		try
		{
			foreach (string item in list)
			{
				FileInfo fileInfo = new FileInfo(item);
				string stamp = $"{fileInfo.Length}:{fileInfo.LastWriteTimeUtc.Ticks}|{gpu}";
				string name = Path.GetFileName(item);
				if (stamps.GetValueOrDefault(name) == stamp)
				{
					continue;
				}
				IReadOnlySet<string> layered2;
				try
				{
					layered2 = layered?.Invoke();
				}
				catch (Recordings.IncompleteLayerList incompleteLayerList)
				{
					Waiting = incompleteLayerList.Message;
					break;
				}
				MiddlewarePack pack;
				List<PsoDb.Rec> records;
				try
				{
					MiddlewarePack pack2;
					pack = (pack2 = MiddlewarePack.Read(item));
					records = MiddlewarePacks.Records(pack2, layered2);
				}
				catch (Exception ex) when ((ex is InvalidDataException || ex is IOException || ex is JsonException) ? true : false)
				{
					stamps[name] = stamp;
					continue;
				}
				if (pack.Header.Gpu != gpu)
				{
					stamps[name] = stamp;
					continue;
				}
				string text = HashOnly.PackKey(gpu, pack.Header.Vendor, pack.Header.Dll, pack.Header.ContentHash);
				byte[] body = HashOnly.Compress(records);
				if (body.Length > 4194304)
				{
					Problem = $"An upscaler pack is too large to share ({body.Length >> 20} MB compressed, the limit is {4} MB).";
					stamps[name] = stamp;
					continue;
				}
				UploadMeta meta = new UploadMeta(text, HashOnly.PackHash(text), null, gpu, appVersion);
				HttpResponseMessage httpResponseMessage = await PostAsync(body, meta, ct);
				if (httpResponseMessage != null && httpResponseMessage.StatusCode == HttpStatusCode.Unauthorized)
				{
					httpResponseMessage.Dispose();
					Reset();
					httpResponseMessage = await PostAsync(body, meta, ct);
				}
				if (httpResponseMessage == null)
				{
					break;
				}
				using (httpResponseMessage)
				{
					if (httpResponseMessage.StatusCode == HttpStatusCode.Accepted)
					{
						List<(string, int)> list2 = done;
						string dll = pack.Header.Dll;
						list2.Add((dll, (await httpResponseMessage.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("records").GetInt32()));
						stamps[name] = stamp;
						continue;
					}
					Problem = $"Sharing an upscaler pack was refused (error {(int)httpResponseMessage.StatusCode}).";
					HttpStatusCode statusCode = httpResponseMessage.StatusCode;
					if ((statusCode == HttpStatusCode.BadRequest || statusCode == HttpStatusCode.RequestEntityTooLarge) ? true : false)
					{
						stamps[name] = stamp;
						continue;
					}
					if (httpResponseMessage.StatusCode == HttpStatusCode.Forbidden)
					{
						stamps[name] = stamp;
					}
					else
					{
						BackOff(httpResponseMessage);
					}
				}
				break;
			}
		}
		catch (Exception ex2) when (!ct.IsCancellationRequested)
		{
			backoffUntil = clock.GetUtcNow() + TimeSpan.FromMinutes(5L);
			bool flag = ((ex2 is HttpRequestException || ex2 is OperationCanceledException) ? true : false);
			Problem = (flag ? "Can't reach the community database to share an upscaler pack." : ex2.Message);
		}
		finally
		{
			Community.Write(stampFile, stamps);
		}
		return done;
	}

	public async Task<bool> ReportAsync(string gameDir, string warm, Func<OutcomeReport?> report, CancellationToken ct = default(CancellationToken))
	{
		string stamp = Path.Combine(gameDir, "reported.txt");
		if (enabled() && !(clock.GetUtcNow() < reportBackoffUntil) && (!File.Exists(stamp) || !(File.ReadAllText(stamp) == warm)))
		{
			OutcomeReport r = report();
			if ((object)r != null)
			{
				try
				{
					HttpResponseMessage httpResponseMessage = await Post(r);
					HttpStatusCode statusCode = httpResponseMessage.StatusCode;
					if ((statusCode == HttpStatusCode.BadRequest || statusCode == HttpStatusCode.RequestEntityTooLarge) ? true : false)
					{
						string[]? keys = r.Keys;
						if (keys != null && keys.Sum((string k) => Base64Url.DecodeFromChars(k).Length) > 40000)
						{
							httpResponseMessage.Dispose();
							httpResponseMessage = await Post(r.Slowest(2000));
						}
					}
					using (httpResponseMessage)
					{
						bool flag = httpResponseMessage.IsSuccessStatusCode;
						if (!flag)
						{
							statusCode = httpResponseMessage.StatusCode;
							bool flag2 = ((statusCode == HttpStatusCode.BadRequest || statusCode == HttpStatusCode.RequestEntityTooLarge) ? true : false);
							flag = flag2;
						}
						if (flag)
						{
							Directory.CreateDirectory(gameDir);
							File.WriteAllText(stamp, warm);
							return httpResponseMessage.IsSuccessStatusCode;
						}
						reportBackoffUntil = clock.GetUtcNow() + ((httpResponseMessage.StatusCode == HttpStatusCode.NotFound) ? TimeSpan.FromDays(1) : Delay(httpResponseMessage));
						return false;
					}
				}
				catch (Exception) when (!ct.IsCancellationRequested)
				{
					reportBackoffUntil = clock.GetUtcNow() + TimeSpan.FromMinutes(5L);
					return false;
				}
			}
		}
		return false;
		async Task<HttpResponseMessage> Post(OutcomeReport sent)
		{
			HttpRequestMessage httpRequestMessage = new HttpRequestMessage(HttpMethod.Post, new Uri(routes.Primary, "v1/report"))
			{
				Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(sent, Json))
			};
			httpRequestMessage.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
			return await http.SendAsync(httpRequestMessage, ct);
		}
	}

	public async Task<string?> SendCompatAsync(CompatReport report, CancellationToken ct = default(CancellationToken))
	{
		if (compatClosed)
		{
			return "Reports aren't open yet.";
		}
		try
		{
			int attempt = 0;
			while (true)
			{
				string text = await DeviceTokenAsync(ct);
				if (text == null)
				{
					break;
				}
				using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, new Uri(routes.Primary, "v1/compat"))
				{
					Content = new ByteArrayContent(report.Body())
				})
				{
					request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
					request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", text);
					using HttpResponseMessage httpResponseMessage = await http.SendAsync(request, ct);
					if (httpResponseMessage.StatusCode != HttpStatusCode.Unauthorized || attempt != 0)
					{
						HttpStatusCode statusCode = httpResponseMessage.StatusCode;
						bool flag = ((statusCode == HttpStatusCode.NotFound || statusCode == HttpStatusCode.NotImplemented) ? true : false);
						compatClosed = flag;
						return httpResponseMessage.StatusCode switch
						{
							HttpStatusCode.OK => null, 
							HttpStatusCode.BadRequest => "The server didn't take this report.", 
							_ => (!compatClosed) ? "Not sent. Try again later." : "Reports aren't open yet.", 
						};
					}
					Reset();
				}
				attempt++;
			}
			return "Couldn't register this PC for reports. Try again later.";
		}
		catch (Exception) when (!ct.IsCancellationRequested)
		{
			return "Not sent. Try again later.";
		}
	}

	public static void SaveShipped(string gameDir, ShaderIndex index)
	{
		if (index.ContentHash.Length != 40)
		{
			return;
		}
		Directory.CreateDirectory(gameDir);
		string text = Path.Combine(gameDir, "index.shaders.tmp");
		using (BufferedStream bufferedStream = new BufferedStream(File.Create(text), 1048576))
		{
			foreach (string item in index.Shaders.Keys.Prepend(index.ContentHash))
			{
				bufferedStream.Write(Convert.FromHexString(item));
			}
		}
		File.Move(text, Path.Combine(gameDir, "index.shaders"), overwrite: true);
	}

	internal static HashSet<string>? Shipped(string gameDir, string contentHash)
	{
		string path = Path.Combine(gameDir, "index.shaders");
		byte[] array = (File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>());
		if (array.Length < 20 || array.Length % 20 != 0 || Convert.ToHexStringLower(array.AsSpan(0, 20)) != contentHash)
		{
			return null;
		}
		HashSet<string> hashSet = new HashSet<string>();
		foreach (string item in array.Chunk(20).Skip(1).Select(Convert.ToHexStringLower))
		{
			hashSet.Add(item);
		}
		return hashSet;
	}

	private static string Id(List<PsoDb.Rec> chunk, string contentHash)
	{
		using IncrementalHash incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		foreach (PsoDb.Rec item in chunk)
		{
			byte b = (byte)item.Tag;
			byte[] bytes = BitConverter.GetBytes(item.Payload.Length);
			int num = 0;
			byte[] array = new byte[1 + bytes.Length];
			array[num] = b;
			num++;
			ReadOnlySpan<byte> readOnlySpan = new ReadOnlySpan<byte>(bytes);
			readOnlySpan.CopyTo(new Span<byte>(array).Slice(num, readOnlySpan.Length));
			num += readOnlySpan.Length;
			incrementalHash.AppendData(new ReadOnlySpan<byte>(array));
			incrementalHash.AppendData(item.Payload);
		}
		return Convert.ToHexStringLower(incrementalHash.GetHashAndReset()) + "|" + contentHash;
	}

	private async Task<HttpResponseMessage?> PostAsync(byte[] body, UploadMeta meta, CancellationToken ct)
	{
		bool flag = !enabled();
		string text = default;
		if (!flag)
		{
			text = await DeviceTokenAsync(ct);
			flag = text == null;
		}
		if (flag || !enabled())
		{
			return null;
		}
		HttpRequestMessage httpRequestMessage = new HttpRequestMessage(HttpMethod.Post, new Uri(routes.Primary, "v1/upload"))
		{
			Content = new ByteArrayContent(body)
		};
		httpRequestMessage.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
		httpRequestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", text);
		httpRequestMessage.Headers.Add("X-SCSK-Upload", Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(meta, Json)));
		return await http.SendAsync(httpRequestMessage, ct);
	}

	private async Task<string?> DeviceTokenAsync(CancellationToken ct)
	{
		string text = Load()?.Token;
		if (text != null)
		{
			return text;
		}
		using HttpResponseMessage r = await http.PostAsync(new Uri(routes.Primary, "v1/devices"), null, ct);
		if (!r.IsSuccessStatusCode)
		{
			BackOff(r);
			Problem = $"Couldn't register for sharing (error {(int)r.StatusCode}).";
			return null;
		}
		JsonElement jsonElement = await r.Content.ReadFromJsonAsync<JsonElement>(ct);
		Device device = new Device(jsonElement.GetProperty("device_token").GetString(), jsonElement.GetProperty("device_id").GetString());
		Dpapi.Save(file, device);
		return device.Token;
	}

	private void BackOff(HttpResponseMessage r)
	{
		backoffUntil = clock.GetUtcNow() + Delay(r);
	}

	private TimeSpan Delay(HttpResponseMessage r)
	{
		TimeSpan result;
		switch (r.StatusCode)
		{
		case HttpStatusCode.TooManyRequests:
		case HttpStatusCode.ServiceUnavailable:
		{
			RetryConditionHeaderValue retryAfter = r.Headers.RetryAfter;
			if (retryAfter == null)
			{
				goto IL_0082;
			}
			TimeSpan? delta = retryAfter.Delta;
			if (delta.HasValue)
			{
				TimeSpan valueOrDefault = delta.GetValueOrDefault();
				result = valueOrDefault;
			}
			else
			{
				DateTimeOffset? date = retryAfter.Date;
				if (!date.HasValue)
				{
					goto IL_0082;
				}
				DateTimeOffset valueOrDefault2 = date.GetValueOrDefault();
				result = valueOrDefault2 - clock.GetUtcNow();
			}
			goto IL_008a;
		}
		case HttpStatusCode.NotImplemented:
			return TimeSpan.FromDays(1);
		default:
			{
				return TimeSpan.FromMinutes(5L);
			}
			IL_0082:
			result = TimeSpan.FromHours(1);
			goto IL_008a;
			IL_008a:
			return result;
		}
	}

	private Device? Load()
	{
		return Dpapi.Load<Device>(file);
	}
}
