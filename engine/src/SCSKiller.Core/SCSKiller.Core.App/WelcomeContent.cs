using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace SCSKiller.Core.App;

public sealed record WelcomeContent(string Title, IReadOnlyList<string> Paragraphs, WelcomeLink? Link, string Share, string SignIn, string Dismiss)
{
	public static WelcomeContent Default => field ?? (field = Parse(new StreamReader(typeof(WelcomeContent).Assembly.GetManifestResourceStream("SCSKiller.Core.App.welcome.json")).ReadToEnd()));

	public const string ContentPath = "v1/content/welcome.json";

	private static readonly string[] LinkDomains = new string[4] { "scskiller.com", "scskiller.io", "scskiller.xyz", "patreon.com" };

	public static WelcomeContent? Parse(string json)
	{
		WelcomeContent welcomeContent;
		try
		{
			welcomeContent = JsonSerializer.Deserialize<WelcomeContent>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
		}
		catch (JsonException)
		{
			return null;
		}
		if (welcomeContent != null && Ok(welcomeContent.Title, 100) && Ok(welcomeContent.Share, 150) && Ok(welcomeContent.SignIn, 40) && Ok(welcomeContent.Dismiss, 40))
		{
			IReadOnlyList<string> paragraphs = welcomeContent.Paragraphs;
			if (paragraphs != null)
			{
				int count = paragraphs.Count;
				if (count >= 1 && count <= 8 && welcomeContent.Paragraphs.All((string p) => Ok(p, 1000)) && (welcomeContent.Link == null || (Ok(welcomeContent.Link.Text, 100) && IsAllowedLink(welcomeContent.Link.Url))))
				{
					return welcomeContent;
				}
			}
		}
		return null;
		static bool Ok(string? s, int max)
		{
			if (!string.IsNullOrWhiteSpace(s))
			{
				return s.Length <= max;
			}
			return false;
		}
	}

	internal static bool IsAllowedLink(string? url)
	{
		if (Uri.TryCreate(url, UriKind.Absolute, out Uri u) && u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort && u.UserInfo.Length == 0)
		{
			return LinkDomains.Any((string d) => u.Host.Equals(d, StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
		}
		return false;
	}

	public static async Task<WelcomeContent?> FetchAsync(RouteFailover? routes = null, TimeSpan? budget = null)
	{
		string text = await (routes ?? RouteFailover.Default).GetContentAsync("v1/content/welcome.json", budget ?? TimeSpan.FromSeconds(3L), 65536);
		return (text != null) ? Parse(text) : null;
	}
}
