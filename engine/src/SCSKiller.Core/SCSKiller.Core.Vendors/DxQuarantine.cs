using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SCSKiller.Core.Vendors;

public sealed class DxQuarantine(string dxDir, string journalPath)
{
	public sealed record Move(string From, string To);

	public sealed record Journal(string GameId, List<Move> Moves, List<string> Skipped);

	public string Folder => Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dxDir)), Path.GetFileName(Path.TrimEndingDirectorySeparator(dxDir)) + ".scskiller-quarantine");

	public Journal? Pending
	{
		get
		{
			try
			{
				return File.Exists(journalPath) ? JsonSerializer.Deserialize<Journal>(File.ReadAllText(journalPath)) : null;
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is JsonException) ? true : false)
			{
				return null;
			}
		}
	}

	public Journal SetAside(string gameId, Func<string, bool> keep)
	{
		List<Move> list = new List<Move>();
		List<string> list2 = new List<string>();
		if (Directory.Exists(dxDir))
		{
			foreach (string item in Directory.EnumerateFiles(dxDir, "*.parc"))
			{
				string text = AmdAppCache.Key(Path.GetFileName(item), d3d12: false);
				if (text != null && !keep(text))
				{
					if (AppCacheFiles.ProcessesUsing(item).Count > 0)
					{
						list2.Add(item);
					}
					else
					{
						list.Add(new Move(item, Path.Combine(Folder, Path.GetFileName(item))));
					}
				}
			}
		}
		if (list.Count == 0)
		{
			Journal journal = new Journal(gameId, new List<Move>(), list2);
			Write(journal);
			return journal;
		}
		Directory.CreateDirectory(Folder);
		Write(new Journal(gameId, list, list2));
		List<Move> list3 = new List<Move>();
		foreach (Move item2 in list)
		{
			if (Rename(item2.From, item2.To))
			{
				list3.Add(item2);
			}
			else
			{
				list2.Add(item2.From);
			}
		}
		Journal journal2 = new Journal(gameId, list3, list2);
		Write(journal2);
		return journal2;
	}

	public List<string> Restore(string? old = null)
	{
		Journal pending = Pending;
		if ((object)pending == null)
		{
			return new List<string>();
		}
		List<Move> list = new List<Move>();
		List<Move> list2 = new List<Move>();
		foreach (Move move in pending.Moves)
		{
			try
			{
				if (!File.Exists(move.To))
				{
					continue;
				}
				if (old != null && string.Equals(AmdAppCache.Key(Path.GetFileName(move.To), d3d12: false), old, StringComparison.OrdinalIgnoreCase))
				{
					File.Delete(move.To);
					continue;
				}
				if (!File.Exists(move.From))
				{
					goto IL_00e5;
				}
				if (new FileInfo(move.From).Length >= new FileInfo(move.To).Length)
				{
					File.Delete(move.To);
					continue;
				}
				if (AppCacheFiles.ProcessesUsing(move.From).Count > 0)
				{
					list2.Add(move);
					continue;
				}
				File.Delete(move.From);
				goto IL_00e5;
				IL_00e5:
				if (!Rename(move.To, move.From))
				{
					list.Add(move);
				}
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
				list.Add(move);
			}
		}
		if (list.Count + list2.Count > 0)
		{
			List<Move> list3 = list2;
			int num = list.Count + list3.Count;
			List<Move> list4 = new List<Move>(num);
			CollectionsMarshal.SetCount(list4, num);
			Span<Move> span = CollectionsMarshal.AsSpan(list4);
			int num2 = 0;
			Span<Move> span2 = CollectionsMarshal.AsSpan(list);
			span2.CopyTo(span.Slice(num2, span2.Length));
			num2 += span2.Length;
			Span<Move> span3 = CollectionsMarshal.AsSpan(list3);
			span3.CopyTo(span.Slice(num2, span3.Length));
			num2 += span3.Length;
			Write(pending with
			{
				Moves = list4
			});
			return list.Select((Move m) => m.To).ToList();
		}
		File.Delete(journalPath);
		try
		{
			Directory.Delete(Folder);
		}
		catch (IOException)
		{
		}
		return new List<string>();
	}

	private void Write(Journal j)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(journalPath));
		string text = journalPath + ".tmp";
		File.WriteAllText(text, JsonSerializer.Serialize(j));
		File.Move(text, journalPath, overwrite: true);
	}

	private static bool Rename(string from, string to)
	{
		return MoveFileExW(from, to, 0u);
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool MoveFileExW(string existing, string newName, uint flags);
}
