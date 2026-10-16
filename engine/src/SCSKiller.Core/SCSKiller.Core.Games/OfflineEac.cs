using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SCSKiller.Core.App;

namespace SCSKiller.Core.Games;

public static class OfflineEac
{
	private sealed record Entry(string Id, string Name, string Exe, string[] Sources, string? EacProductId = null, string? EacSandboxId = null);

	private sealed record ListFile(Entry[] Games, string? About = null);

	public static IReadOnlyList<OfflineEacGame> Current { get; internal set; } = Parse(ContentFile.Embedded("SCSKiller.Core.Games.offline-eac.json"));

	private static List<OfflineEacGame> Parse(string json)
	{
		return (ContentFile.Read<ListFile>(json) ?? throw new InvalidDataException("offline-eac.json")).Games.Select((Entry e) =>
		{
			string id = e.Id;
			string name = e.Name;
			string exe = e.Exe;
			if (!e.Id.StartsWith("steam:", StringComparison.Ordinal) || !e.Id.Substring(6).All(char.IsAsciiDigit) || e.Id.Length <= 6)
			{
				throw new InvalidDataException("offline-eac.json: " + e.Id + " isn't a Steam app");
			}
			string appId = e.Id.Substring(6);
			string eacProductId = e.EacProductId;
			string eacSandboxId = e.EacSandboxId;
			string eacProduct;
			if (eacProductId == null)
			{
				if (eacSandboxId != null)
				{
					goto IL_00d0;
				}
				eacProduct = null;
			}
			else
			{
				if (eacSandboxId == null || !IsEacId(eacProductId) || !IsEacId(eacSandboxId))
				{
					goto IL_00d0;
				}
				eacProduct = eacProductId + "/" + eacSandboxId;
			}
			return new OfflineEacGame(id, name, exe, appId, eacProduct);
			IL_00d0:
			throw new InvalidDataException("offline-eac.json: " + e.Id + " needs both EasyAntiCheat ids, 32 lowercase hex digits each");
		}).ToList();
	}

	private static bool IsEacId(string id)
	{
		if (id.Length == 32)
		{
			return id.All((char c) => char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f'));
		}
		return false;
	}

	public static OfflineEacGame? Of(Game g, AntiCheat antiCheat, EngineInfo? engine, bool unsupported = false)
	{
		if (antiCheat == AntiCheat.EasyAntiCheat && !Vac.Is(g) && engine != null && (unsupported || engine.Unsupported == null) && engine.GraphicsApi.Contains("D3D12"))
		{
			OfflineEacGame offlineEacGame = Current.FirstOrDefault((OfflineEacGame e) => Path.GetFileName(g.ExePath).Equals(e.Exe, StringComparison.OrdinalIgnoreCase) && (e.Id == g.Id || (g.Store == Store.Manual && e.EacProduct != null && GameFiles.EacProduct(g.ExePath) == e.EacProduct)));
			if ((object)offlineEacGame != null)
			{
				return offlineEacGame;
			}
		}
		return null;
	}
}
