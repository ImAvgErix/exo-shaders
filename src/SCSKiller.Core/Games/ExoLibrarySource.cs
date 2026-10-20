// Exo Shaders - unofficial fork of SCSKiller, based on SCSKiller (https://github.com/BlueHeisenberg/SCSKiller).
// Copyright (C) 2026 Exo Shaders contributors. GPL-3.0-or-later (see LICENSE, LICENSE-EXCEPTION.txt).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SCSKiller.Core.Games;

/// <summary>One installed game as Exo Launcher lists it (the service's exo.library method).</summary>
public sealed record ExoLibraryEntry(string Id, string Title, string Store, string InstallDir, string? Exe = null);

/// <summary>
/// The safety net from Exo Launcher: its installed games, as it last sent them, kept in &lt;data&gt;\exo-library.json.
/// A game whose folder no other source found (or that lies inside or around one) becomes a discovered, non-manual game
/// with Exo's store label, id exo:&lt;Exo's id&gt;. It goes through the same anti-cheat and engine detection as every game.
/// </summary>
public sealed class ExoLibrarySource : IGameSource
{
	public const string FileName = "exo-library.json";

	private static readonly Regex SafeId = new Regex("^[A-Za-z0-9._:-]{1,120}$");

	private static volatile Dictionary<string, string> _labels = new Dictionary<string, string>(StringComparer.Ordinal);

	private readonly string _path;

	private readonly object _lock = new object();

	private volatile IReadOnlyList<ExoLibraryEntry> _entries;

	public ExoLibrarySource(string dataDir)
	{
		_path = Path.Combine(dataDir, FileName);
		_entries = Load(_path);
		_labels = Labels(_entries);
	}

	public Store Store => Store.Other;

	/// <summary>The games the other sources found so far in this discovery: an Exo game in or around one of their folders is left out.</summary>
	public IReadOnlyList<Game>? Covered { get; set; }

	public IReadOnlyList<ExoLibraryEntry> Entries => _entries;

	/// <summary>The store label Exo gave a game this source lists (exo:… ids), else null.</summary>
	public static string? LabelOf(string gameId)
	{
		return _labels.GetValueOrDefault(gameId);
	}

	public static string IdOf(ExoLibraryEntry e)
	{
		string text = e.Id.Trim();
		if (SafeId.IsMatch(text))
		{
			return "exo:" + text;
		}
		return "exo:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Key(e.InstallDir).ToUpperInvariant()))).Substring(0, 16);
	}

	/// <summary>Why Exo Shaders won't take this folder as a game (ManualSource.RootProblem's reasons), or null.</summary>
	public static string? Problem(ExoLibraryEntry e)
	{
		if (string.IsNullOrWhiteSpace(e.InstallDir) || e.InstallDir.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathFullyQualified(e.InstallDir))
		{
			return "the game folder isn't a full path";
		}
		string text = Key(e.InstallDir);
		string? text2 = ResolveExe(text, e.Exe);
		if (text2 == null)
		{
			return Directory.Exists(text) ? ("no game program found in " + text) : (text + " doesn't exist.");
		}
		return ManualSource.RootProblem(text, text2, Array.Empty<Game>());
	}

	/// <summary>Replaces the list (games no longer in it disappear at the next scan) and saves it. True when it changed.</summary>
	public bool Replace(IEnumerable<ExoLibraryEntry> entries)
	{
		List<ExoLibraryEntry> list = Normalize(entries);
		lock (_lock)
		{
			if (list.SequenceEqual(_entries))
			{
				return false;
			}
			Save(_path, list);
			_entries = list;
			_labels = Labels(list);
			return true;
		}
	}

	public IReadOnlyList<Game> Discover()
	{
		List<string> covered = (from g in Covered ?? Array.Empty<Game>()
			where g.InstallDir.Length > 0
			select GameFiles.DirKey(g.InstallDir)).ToList();
		List<Game> list = new List<Game>();
		HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (ExoLibraryEntry entry in _entries)
		{
			try
			{
				string dir = Key(entry.InstallDir);
				if (!Directory.Exists(dir) || covered.Any((string c) => Overlaps(c, dir)) || list.Any((Game g) => Overlaps(g.InstallDir, dir)))
				{
					continue;
				}
				string? text = ResolveExe(dir, entry.Exe);
				string text2 = IdOf(entry);
				if (text != null && !ids.Contains(text2) && ManualSource.RootProblem(dir, text, Array.Empty<Game>()) == null)
				{
					ids.Add(text2);
					list.Add(new Game(text2, (entry.Title.Trim().Length > 0) ? entry.Title.Trim() : GameFiles.FolderName(dir), Store.Other, dir, text));
				}
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException) ? true : false)
			{
			}
		}
		return list;
		static bool Overlaps(string a, string b)
		{
			if (!a.Equals(b, StringComparison.OrdinalIgnoreCase) && !GameFiles.Inside(a, b))
			{
				return GameFiles.Inside(b, a);
			}
			return true;
		}
	}

	/// <summary>
	/// Exo's exe when it is an existing .exe inside the folder; otherwise the folder's game the way an added folder is
	/// read (ManualSource.Found): GameFiles.FindExe (the Unreal Shipping exe first), then GameFiles.GameExe.
	/// </summary>
	internal static string? ResolveExe(string dir, string? exe)
	{
		if (!Directory.Exists(dir))
		{
			return null;
		}
		if (exe != null && exe.Trim().Length > 0 && exe.IndexOfAny(Path.GetInvalidPathChars()) < 0)
		{
			try
			{
				string fullPath = Path.GetFullPath(Path.IsPathFullyQualified(exe) ? exe : Path.Combine(dir, exe.Trim()));
				if (fullPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && GameFiles.Inside(dir, fullPath) && File.Exists(fullPath))
				{
					return fullPath;
				}
			}
			catch (Exception ex) when ((ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) ? true : false)
			{
			}
		}
		string? text = GameFiles.FindExe(dir);
		if (text == null)
		{
			return null;
		}
		return GameFiles.GameExe(dir, text);
	}

	private static string Key(string dir)
	{
		return GameFiles.DirKey(dir.Trim());
	}

	private static List<ExoLibraryEntry> Normalize(IEnumerable<ExoLibraryEntry> entries)
	{
		List<ExoLibraryEntry> list = new List<ExoLibraryEntry>();
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (ExoLibraryEntry entry in entries)
		{
			string text = entry.Id?.Trim() ?? "";
			string text2 = entry.InstallDir?.Trim() ?? "";
			if (text.Length == 0 || text2.Length == 0 || text2.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathFullyQualified(text2))
			{
				continue;
			}
			string text3;
			try
			{
				text3 = GameFiles.DirKey(text2);
			}
			catch (Exception ex) when ((ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) ? true : false)
			{
				continue;
			}
			ExoLibraryEntry exoLibraryEntry = new ExoLibraryEntry(text, entry.Title?.Trim() ?? "", (entry.Store?.Trim() is { Length: >0 } s) ? s : "Exo Launcher", text3, (entry.Exe?.Trim() is { Length: >0 } x) ? x : null);
			if (hashSet.Add(IdOf(exoLibraryEntry)))
			{
				list.Add(exoLibraryEntry);
			}
		}
		return list.OrderBy((ExoLibraryEntry e) => e.Id, StringComparer.Ordinal).ToList();
	}

	private static Dictionary<string, string> Labels(IEnumerable<ExoLibraryEntry> entries)
	{
		Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (ExoLibraryEntry entry in entries)
		{
			dictionary.TryAdd(IdOf(entry), entry.Store);
		}
		return dictionary;
	}

	private static IReadOnlyList<ExoLibraryEntry> Load(string path)
	{
		try
		{
			if (!File.Exists(path))
			{
				return Array.Empty<ExoLibraryEntry>();
			}
			using JsonDocument jsonDocument = JsonDocument.Parse(File.ReadAllText(path));
			if (!jsonDocument.RootElement.TryGetProperty("games", out var value) || value.ValueKind != JsonValueKind.Array)
			{
				return Array.Empty<ExoLibraryEntry>();
			}
			List<ExoLibraryEntry> list = new List<ExoLibraryEntry>();
			foreach (JsonElement item in value.EnumerateArray())
			{
				if (item.ValueKind == JsonValueKind.Object)
				{
					list.Add(new ExoLibraryEntry(EpicInstalls.Text(item, "id") ?? "", EpicInstalls.Text(item, "title") ?? "", EpicInstalls.Text(item, "store") ?? "", EpicInstalls.Text(item, "installDir") ?? "", EpicInstalls.Text(item, "exe")));
				}
			}
			return Normalize(list);
		}
		catch (Exception ex) when ((ex is JsonException || ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return Array.Empty<ExoLibraryEntry>();
		}
	}

	private static void Save(string path, IReadOnlyList<ExoLibraryEntry> entries)
	{
		using MemoryStream memoryStream = new MemoryStream();
		using (Utf8JsonWriter utf8JsonWriter = new Utf8JsonWriter(memoryStream, new JsonWriterOptions
		{
			Indented = true
		}))
		{
			utf8JsonWriter.WriteStartObject();
			utf8JsonWriter.WriteNumber("version", 1);
			utf8JsonWriter.WriteStartArray("games");
			foreach (ExoLibraryEntry entry in entries)
			{
				utf8JsonWriter.WriteStartObject();
				utf8JsonWriter.WriteString("id", entry.Id);
				utf8JsonWriter.WriteString("title", entry.Title);
				utf8JsonWriter.WriteString("store", entry.Store);
				utf8JsonWriter.WriteString("installDir", entry.InstallDir);
				if (entry.Exe != null)
				{
					utf8JsonWriter.WriteString("exe", entry.Exe);
				}
				utf8JsonWriter.WriteEndObject();
			}
			utf8JsonWriter.WriteEndArray();
			utf8JsonWriter.WriteEndObject();
		}
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		string text = path + ".tmp";
		File.WriteAllBytes(text, memoryStream.ToArray());
		File.Move(text, path, overwrite: true);
	}
}
