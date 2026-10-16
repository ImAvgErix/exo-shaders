using System.Collections.Generic;
using System.Text.Json;

namespace SCSKiller.Core.Warming;

public sealed record WarmEvent(string Event, long Done, long Total, long Failed, double Rate, double Seconds, bool Stopped, string? Message, long From = 0L, int RtThreads = 0, long FailedItem = -1L, string? Reason = null, IReadOnlyList<string>? Crashed = null, IReadOnlyList<long>? Isolate = null, string? Stage = null, int Workers = 0, int Threads = 0, long DriverSkipped = 0L, IReadOnlyList<long>? Skip = null)
{
	private static readonly JsonSerializerOptions Json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

	public static WarmEvent? Parse(string line)
	{
		if (!line.StartsWith('{'))
		{
			return null;
		}
		try
		{
			WarmEvent warmEvent = JsonSerializer.Deserialize<WarmEvent>(line, Json);
			if ((object)warmEvent == null || warmEvent.Event == null)
			{
				return null;
			}
			IReadOnlyList<string> crashed = warmEvent.Crashed;
			IReadOnlyList<string> crashed2 = ((crashed != null && crashed.Count > 0) ? warmEvent.Crashed : null);
			IReadOnlyList<long> isolate = warmEvent.Isolate;
			IReadOnlyList<long> isolate2 = ((isolate != null && isolate.Count > 0) ? warmEvent.Isolate : null);
			isolate = warmEvent.Skip;
			return warmEvent with
			{
				Crashed = crashed2,
				Isolate = isolate2,
				Skip = ((isolate != null && isolate.Count > 0) ? warmEvent.Skip : null)
			};
		}
		catch (JsonException)
		{
			return null;
		}
	}
}
