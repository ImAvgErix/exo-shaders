using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SCSKiller.Core.App;

public static class UnrealUserCache
{
	public static string? Project(string exePath)
	{
		string directoryName = Path.GetDirectoryName(Path.GetDirectoryName(exePath));
		if (directoryName != null && Path.GetFileName(directoryName).Equals("Binaries", StringComparison.OrdinalIgnoreCase))
		{
			string fileName = Path.GetFileName(Path.GetDirectoryName(directoryName));
			if (fileName != null && fileName.Length > 0)
			{
				return fileName;
			}
		}
		return null;
	}

	public static IReadOnlyList<string> FilesOf(Game g, IEnumerable<string> roots)
	{
		string text = Project(g.ExePath);
		if (text == null)
		{
			return Array.Empty<string>();
		}
		Regex name = new Regex("^" + Regex.Escape(text) + "_(PCD3D|SF_VULKAN|VULKAN)_[A-Z0-9_]+(\\.stable)?\\.upipelinecache$", RegexOptions.IgnoreCase);
		string install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(g.InstallDir)) + "\\";
		EnumerationOptions enumerationOptions = new EnumerationOptions
		{
			IgnoreInaccessible = true,
			AttributesToSkip = FileAttributes.None
		};
		List<string> list = new List<string>();
		foreach (string item in roots.Where(Directory.Exists))
		{
			foreach (string item2 in Directory.EnumerateDirectories(item, "*", enumerationOptions))
			{
				try
				{
					foreach (string item3 in Directory.EnumerateDirectories(item2, "Saved*", enumerationOptions))
					{
						string fileName = Path.GetFileName(item3);
						if (fileName.Equals("Saved", StringComparison.OrdinalIgnoreCase) || fileName.StartsWith("Saved_", StringComparison.OrdinalIgnoreCase))
						{
							list.AddRange(from f in Directory.EnumerateFiles(item3, "*.upipelinecache", enumerationOptions)
								where name.IsMatch(Path.GetFileName(f)) && !Path.GetFullPath(f).StartsWith(install, StringComparison.OrdinalIgnoreCase)
								select f);
						}
					}
				}
				catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
				{
				}
			}
		}
		return list;
	}

	public static IEnumerable<string> Folders(Game g)
	{
		return (from n in new string[3]
			{
				g.Name,
				Path.GetFileName(Path.TrimEndingDirectorySeparator(g.InstallDir)),
				Project(g.ExePath)
			}.OfType<string>()
			select n.Trim() into n
			where n.Length > 0 && n.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
			select n).Distinct(StringComparer.OrdinalIgnoreCase);
	}

	public static IReadOnlyList<string> PrecacheFilesOf(Game g, IEnumerable<string> roots, IEnumerable<string> folders)
	{
		string install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(g.InstallDir)) + "\\";
		EnumerationOptions options = new EnumerationOptions
		{
			IgnoreInaccessible = true,
			AttributesToSkip = FileAttributes.None,
			RecurseSubdirectories = true,
			MaxRecursionDepth = 2
		};
		List<string> names = folders.ToList();
		return (from f in roots.Where(Directory.Exists).SelectMany((string r) => names.Select((string n) => Path.Combine(r, n))).Where(Directory.Exists)
				.SelectMany((string d) => Directory.EnumerateFiles(d, "*.ushaderprecache", options))
			where !Path.GetFullPath(f).StartsWith(install, StringComparison.OrdinalIgnoreCase)
			select f).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
	}
}
