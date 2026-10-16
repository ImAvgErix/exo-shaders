using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;

namespace SCSKiller.Core.Games;

public sealed class XboxSource(IEnumerable<string>? driveRoots = null) : IGameSource
{
	private struct PACKAGE_VERSION
	{
		public ulong Version;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct PACKAGE_ID
	{
		public uint Reserved;

		public uint ProcessorArchitecture;

		public PACKAGE_VERSION Version;

		public string? Name;

		public string? Publisher;

		public string? ResourceId;

		public string? PublisherId;
	}

	private const int MaxGamingRoot = 65536;

	private static readonly string[] HelperHints = new string[13]
	{
		"launch", "bootstrap", "helper", "crash", "webview", "uploader", "gpudetection", "hashdatabase", "setup", "redist",
		"unins", "reporter", "cache_builder"
	};

	public Store Store => Store.Xbox;

	public IReadOnlyDictionary<string, Game>? Known { get; set; }

	public IReadOnlyList<Game> Discover()
	{
		List<Game> list = new List<Game>();
		foreach (string item in (driveRoots ?? (from d in DriveInfo.GetDrives().Where((DriveInfo d) =>
			{
				DriveType driveType = d.DriveType;
				return (uint)(driveType - 2) <= 1u;
			})
			select d.RootDirectory.FullName)).SelectMany(LibraryFolders).Concat((driveRoots == null) ? MutableRoots() : Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
		{
			IEnumerable<string> enumerable;
			try
			{
				enumerable = (Directory.Exists(item) ? Directory.GetDirectories(item) : Array.Empty<string>());
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
				continue;
			}
			foreach (string item2 in enumerable)
			{
				string text = Path.Combine(item2, "Content");
				string text2 = Path.Combine(text, "MicrosoftGame.config");
				if (!File.Exists(text2))
				{
					// Game Pass mutable packages (ModifiableWindowsApps) keep MicrosoftGame.config at the package root.
					text = item2;
					text2 = Path.Combine(text, "MicrosoftGame.config");
					if (!File.Exists(text2))
					{
						continue;
					}
				}
				try
				{
					Game game = ParseGame(text, text2, Known);
					if ((object)game != null)
					{
						list.Add(game);
					}
				}
				catch (Exception)
				{
				}
			}
		}
		return list;
	}

	private static IEnumerable<string> LibraryFolders(string driveRoot)
	{
		List<string> list = null;
		try
		{
			FileInfo fileInfo = new FileInfo(Path.Combine(driveRoot, ".GamingRoot"));
			if (fileInfo.Exists && fileInfo.Length <= 65536)
			{
				list = ParseGamingRoot(File.ReadAllBytes(fileInfo.FullName));
			}
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
		}
		List<string> list2 = new List<string>();
		list2.AddRange((list ?? new List<string>()).Select((string p) => Path.GetFullPath(p, driveRoot)));
		list2.Add(Path.Combine(driveRoot, "XboxGames"));
		return new _003C_003Ez__ReadOnlyList<string>(list2);
	}

	/// <summary>Program Files\ModifiableWindowsApps (both), where Game Pass puts mutable packages: Exo Launcher scans these too.</summary>
	private static IEnumerable<string> MutableRoots()
	{
		return new string[2]
		{
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ModifiableWindowsApps"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ModifiableWindowsApps")
		};
	}

	internal static List<string>? ParseGamingRoot(ReadOnlySpan<byte> bytes)
	{
		if (bytes.Length < 8 || bytes.Length > 65536 || !bytes.Slice(0, 4).SequenceEqual("RGBX"u8))
		{
			return null;
		}
		uint num = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4));
		if ((num > 64 || num == 0) ? true : false)
		{
			return null;
		}
		ReadOnlySpan<char> span = MemoryMarshal.Cast<byte, char>(bytes.Slice(8).Slice(0, (bytes.Length - 8) & -2));
		List<string> list = new List<string>();
		while (list.Count < num)
		{
			int num2 = span.IndexOf('\0');
			if (num2 <= 0)
			{
				return null;
			}
			list.Add(new string(span.Slice(0, num2)));
			span = span.Slice(num2 + 1);
		}
		return list;
	}

	private static Game? ParseGame(string content, string configPath, IReadOnlyDictionary<string, Game>? known = null)
	{
		XDocument xDocument = XDocument.Load(configPath);
		XNamespace defaultNamespace = xDocument.Root.GetDefaultNamespace();
		XElement? xElement = xDocument.Root.Element(defaultNamespace + "Identity");
		string text = xElement?.Attribute("Name")?.Value;
		string text2 = xElement?.Attribute("Publisher")?.Value;
		string text3 = xElement?.Attribute("Version")?.Value;
		if (text == null)
		{
			return null;
		}
		if (xDocument.Root.Element(defaultNamespace + "AllowedProducts") != null)
		{
			return null;
		}
		string text4 = xDocument.Root.Element(defaultNamespace + "ShellVisuals")?.Attribute("DefaultDisplayName")?.Value ?? text;
		string text5 = ((text2 != null) ? PackageFamilyName(text, text2) : null);
		string? text6 = xDocument.Root.Element(defaultNamespace + "ExecutableList")?.Elements(defaultNamespace + "Executable").Where(IsGameExecutable).Select((XElement e) => e.Attribute("Name")?.Value)
			.FirstOrDefault((string n) => n != null);
		if (text6 == null)
		{
			Game game = known?.GetValueOrDefault("xbox:" + (text5 ?? text));
			if ((object)game != null)
			{
				string version = game.Version;
				if (version != null && version == text3 && game.InstallDir.Equals(content, StringComparison.OrdinalIgnoreCase) && File.Exists(game.ExePath) && Pickable(game.ExePath))
				{
					text6 = game.ExePath;
					goto IL_0211;
				}
			}
			text6 = FindExeFallback(content);
		}
		goto IL_0211;
		IL_0211:
		string text7 = text6;
		if (text7 == null)
		{
			return null;
		}
		string fullPath = Path.GetFullPath(Path.Combine(content, text7));
		if (text4.EndsWith(" Launcher", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}
		return new Game("xbox:" + (text5 ?? text), text4, Store.Xbox, content, fullPath, text3);
	}

	internal static HashSet<string> NotTheGame(string content)
	{
		string text = Path.Combine(content, "MicrosoftGame.config");
		try
		{
			if (!File.Exists(text))
			{
				return new HashSet<string>();
			}
			XDocument xDocument = XDocument.Load(text);
			XNamespace defaultNamespace = xDocument.Root.GetDefaultNamespace();
			return (from n in (from e in xDocument.Root.Element(defaultNamespace + "ExecutableList")?.Elements(defaultNamespace + "Executable") ?? Array.Empty<XElement>()
					where !IsGameExecutable(e)
					select e.Attribute("Name")?.Value).OfType<string>()
				select Path.GetFullPath(Path.Combine(content, n))).ToHashSet(StringComparer.OrdinalIgnoreCase);
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is XmlException || ex is ArgumentException || ex is NotSupportedException) ? true : false)
		{
			return new HashSet<string>();
		}
	}

	private static bool IsGameExecutable(XElement e)
	{
		string text = e.Attribute("Name")?.Value;
		if (text == null || LauncherOrUtility(text))
		{
			return false;
		}
		string text2 = e.Attribute("TargetDeviceFamily")?.Value;
		if (text2 != null && !text2.Contains("PC", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		return !string.Equals(e.Attribute("IsDevOnly")?.Value, "true", StringComparison.OrdinalIgnoreCase);
	}

	private static string? FindExeFallback(string content)
	{
		EnumerationOptions enumerationOptions = new EnumerationOptions
		{
			RecurseSubdirectories = true,
			MaxRecursionDepth = 3,
			IgnoreInaccessible = true,
			AttributesToSkip = FileAttributes.None
		};
		return (from f in Directory.EnumerateFiles(content, "*.exe", enumerationOptions).Where(Pickable)
			select new FileInfo(f)).MaxBy((FileInfo f) => f.Length)?.FullName;
	}

	private static bool Pickable(string exe)
	{
		if (!LauncherOrUtility(Path.GetFileName(exe)))
		{
			return !GameFiles.IsHelper(exe);
		}
		return false;
	}

	internal static bool LauncherOrUtility(string name)
	{
		return HelperHints.Any((string h) => name.Contains(h, StringComparison.OrdinalIgnoreCase));
	}

	public static string? AppUserModelId(Game g)
	{
		if (g.Store != Store.Xbox || !g.Id.StartsWith("xbox:", StringComparison.Ordinal))
		{
			return null;
		}
		try
		{
			XDocument xDocument = XDocument.Load(Path.Combine(g.InstallDir, "appxmanifest.xml"));
			string text = xDocument.Root?.Element(xDocument.Root.GetDefaultNamespace() + "Applications")?.Elements().FirstOrDefault()?.Attribute("Id")?.Value;
			return (text == null) ? null : (g.Id.Substring("xbox:".Length) + "!" + text);
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is XmlException) ? true : false)
		{
			return null;
		}
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern int PackageFamilyNameFromId(ref PACKAGE_ID packageId, ref uint packageFamilyNameLength, char[]? packageFamilyName);

	public static string? PackageFamilyName(string name, string publisher)
	{
		PACKAGE_ID packageId = new PACKAGE_ID
		{
			Name = name,
			Publisher = publisher
		};
		uint packageFamilyNameLength = 0u;
		PackageFamilyNameFromId(ref packageId, ref packageFamilyNameLength, null);
		if (packageFamilyNameLength == 0)
		{
			return null;
		}
		char[] array = new char[packageFamilyNameLength];
		if (PackageFamilyNameFromId(ref packageId, ref packageFamilyNameLength, array) != 0)
		{
			return null;
		}
		return new string(array, 0, (int)(packageFamilyNameLength - 1));
	}
}
