using System;
using System.Collections.Specialized;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SCSKiller.Core.App;

public sealed class LoopbackCallback : IDisposable
{
	private readonly HttpListener listener;

	public int Port { get; }

	public LoopbackCallback()
	{
		int num = 0;
		while (true)
		{
			Port = Random.Shared.Next(49152, 65536);
			listener = new HttpListener
			{
				Prefixes = { $"http://127.0.0.1:{Port}/" }
			};
			try
			{
				listener.Start();
				break;
			}
			catch (HttpListenerException) when (num < 20)
			{
				listener.Close();
			}
			num++;
		}
	}

	public async Task<string> WaitAsync(string state, CancellationToken ct)
	{
		HttpListenerContext httpListenerContext;
		while (true)
		{
			httpListenerContext = await listener.GetContextAsync().WaitAsync(ct);
			if (!(httpListenerContext.Request.Url?.AbsolutePath != "/cb"))
			{
				break;
			}
			Reply(httpListenerContext, 404, "Not found.");
		}
		NameValueCollection queryString = httpListenerContext.Request.QueryString;
		if (queryString["state"] != state)
		{
			Reply(httpListenerContext, 400, "This page doesn't belong to the sign-in SCSKiller is waiting for. Start again from SCSKiller.");
			throw new AccountException("The browser's answer didn't match this sign-in, so it was refused. Please try again.");
		}
		if (queryString["error"] == null)
		{
			string text = queryString["code"];
			if (text != null && text.Length > 0)
			{
				Reply(httpListenerContext, 200, "You're signed in. You can close this tab and go back to SCSKiller.");
				return text;
			}
		}
		Reply(httpListenerContext, 400, "Sign-in didn't complete. You can close this tab and try again from SCSKiller.");
		throw new AccountException((queryString["error"] == "access_denied") ? "Sign-in was cancelled on Patreon." : ("Sign-in didn't complete (" + (queryString["error"] ?? "no code") + "). Please try again."));
	}

	private static void Reply(HttpListenerContext c, int status, string text)
	{
		byte[] bytes = Encoding.UTF8.GetBytes("<!doctype html><meta charset=utf-8><title>SCSKiller</title><body style=\"font:16px 'Segoe UI',sans-serif;margin:3em\"><h2>SCSKiller</h2><p>" + WebUtility.HtmlEncode(text) + "</p>");
		c.Response.StatusCode = status;
		c.Response.ContentType = "text/html; charset=utf-8";
		c.Response.KeepAlive = false;
		c.Response.Close(bytes, willBlock: true);
	}

	public void Dispose()
	{
		listener.Close();
	}
}
