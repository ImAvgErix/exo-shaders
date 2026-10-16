using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace SCSKiller.Core.App;

public static class Download
{
	private static readonly HttpClient Http = new HttpClient
	{
		Timeout = TimeSpan.FromSeconds(10L)
	};

	public static string? Text(string url)
	{
		byte[] array = Bytes(url);
		if (array == null)
		{
			return null;
		}
		return new StreamReader(new MemoryStream(array)).ReadToEnd();
	}

	public static byte[]? Bytes(string url)
	{
		try
		{
			using HttpResponseMessage httpResponseMessage = Http.Send(new HttpRequestMessage(HttpMethod.Get, url));
			if (!httpResponseMessage.IsSuccessStatusCode)
			{
				return null;
			}
			using MemoryStream memoryStream = new MemoryStream();
			httpResponseMessage.Content.ReadAsStream().CopyTo(memoryStream);
			return memoryStream.ToArray();
		}
		catch (Exception ex) when ((ex is HttpRequestException || ex is TaskCanceledException || ex is IOException || ex is NotSupportedException) ? true : false)
		{
			return null;
		}
	}
}
