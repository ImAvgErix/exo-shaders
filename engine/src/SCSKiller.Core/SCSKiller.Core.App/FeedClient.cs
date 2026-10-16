using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace SCSKiller.Core.App;

public sealed class FeedClient(HttpMessageHandler handler, FeedTrust trust, Func<Task<string?>> token, Action<string>? log = null)
{
	private readonly HttpClient http = new HttpClient(handler, disposeHandler: false)
	{
		Timeout = Timeout.InfiniteTimeSpan
	};

	private readonly ConcurrentDictionary<string, long> retryAt = new ConcurrentDictionary<string, long>();

	public async Task<(List<(string Channel, SignedFeed Feed)> Feeds, bool Partial)> ReadAsync(string channel)
	{
		List<(string, SignedFeed)> feeds = new List<(string, SignedFeed)>();
		bool partial = false;
		try
		{
			List<(string, SignedFeed)> list = feeds;
			string item = channel;
			list.Add((item, await SignedAsync(channel)));
		}
		catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound && channel != "stable")
		{
		}
		foreach (string item2 in UpdateChannels.All.TakeWhile((string c) => c != channel))
		{
			try
			{
				List<(string, SignedFeed)> list = feeds;
				string item = item2;
				list.Add((item, await SignedAsync(item2)));
			}
			catch (HttpRequestException ex2) when (ex2.StatusCode == HttpStatusCode.NotFound)
			{
			}
			catch (Exception ex3) when ((ex3 is HttpRequestException || ex3 is TaskCanceledException || ex3 is IOException || ex3 is InvalidOperationException || ex3 is AccountException) ? true : false)
			{
				partial = true;
			}
		}
		return (Feeds: feeds, Partial: partial);
	}

	private async Task<SignedFeed> SignedAsync(string channel)
	{
		string name = "releases." + channel + ".json";
		byte[] feed = await GetAsync(UpdateFeeds.Feed(channel, name), channel);
		byte[] sig = await GetAsync(UpdateFeeds.Feed(channel, name + ".sig"), channel);
		trust.Accept(channel, feed, sig);
		return new SignedFeed(feed, sig);
	}

	private async Task<byte[]> GetAsync(Uri url, string channel)
	{
		using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromMinutes(1L));
		using HttpResponseMessage r = await SendAsync(url, channel, HttpCompletionOption.ResponseContentRead, cts.Token);
		return await r.Content.ReadAsByteArrayAsync(cts.Token);
	}

	public Task<HttpResponseMessage> PackageAsync(AppVersion v, string fileName, CancellationToken ct)
	{
		return SendAsync(UpdateFeeds.Package(v, fileName), v.Channel, HttpCompletionOption.ResponseHeadersRead, ct);
	}

	private async Task<HttpResponseMessage> SendAsync(Uri url, string channel, HttpCompletionOption completion, CancellationToken ct)
	{
		if (retryAt.TryGetValue(url.Host, out var value) && Environment.TickCount64 < value)
		{
			throw new HttpRequestException(url.Host + " asked to wait", null, HttpStatusCode.TooManyRequests);
		}
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
		if (channel != "stable")
		{
			HttpRequestHeaders headers = request.Headers;
			headers.Authorization = new AuthenticationHeaderValue("Bearer", (await token()) ?? throw new InvalidOperationException("sign in for the " + channel + " channel"));
		}
		HttpResponseMessage httpResponseMessage = await http.SendAsync(request, completion, ct);
		if (httpResponseMessage.IsSuccessStatusCode)
		{
			return httpResponseMessage;
		}
		if (httpResponseMessage.StatusCode == HttpStatusCode.TooManyRequests)
		{
			Limited(url, httpResponseMessage);
		}
		using (httpResponseMessage)
		{
			httpResponseMessage.EnsureSuccessStatusCode();
		}
		return httpResponseMessage;
	}

	private void Limited(Uri url, HttpResponseMessage r)
	{
		RetryConditionHeaderValue retryAfter = r.Headers.RetryAfter;
		TimeSpan timeSpan;
		if (retryAfter != null)
		{
			TimeSpan? delta = retryAfter.Delta;
			if (delta.HasValue)
			{
				TimeSpan valueOrDefault = delta.GetValueOrDefault();
				timeSpan = valueOrDefault;
				goto IL_0096;
			}
		}
		timeSpan = (r.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromHours(1);
		goto IL_0096;
		IL_0096:
		TimeSpan timeSpan2 = timeSpan;
		if (timeSpan2 < TimeSpan.Zero)
		{
			timeSpan2 = TimeSpan.Zero;
		}
		else
		{
			timeSpan2 = ((timeSpan2 > TimeSpan.FromDays(1)) ? TimeSpan.FromDays(1) : timeSpan2);
		}
		retryAt[url.Host] = Environment.TickCount64 + (long)timeSpan2.TotalMilliseconds;
		log?.Invoke($"429 from {url.Host} for {url.AbsolutePath}: no request to it for {timeSpan2:c}");
	}
}
