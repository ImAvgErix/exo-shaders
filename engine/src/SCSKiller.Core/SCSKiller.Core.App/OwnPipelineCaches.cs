using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace SCSKiller.Core.App;

public static class OwnPipelineCaches
{
	private static readonly string[] DawnFiles = new string[3] { "rawshader.store2", "rawroot.store2", "rawpso.store2" };

	private static readonly string[] ReFiles = new string[2] { "shader.cache2", "piplinelist.bin" };

	public static IReadOnlyList<string> FilesOf(Game g, EngineInfo? engine, string documents, string roamingAppData)
	{
		string bin = Path.Combine(g.InstallDir, "bin");
		List<string> list = new List<string>();
		if (DawnFiles.All((string f) => File.Exists(Path.Combine(bin, f))))
		{
			list.Add(Path.Combine(bin, "pipelineLibrary.cache"));
		}
		if (engine?.Family == "RE Engine")
		{
			list.AddRange(ReFiles.Select((string f) => Path.Combine(Path.GetDirectoryName(g.ExePath), f)));
		}
		string fileName = Path.GetFileName(g.ExePath);
		if (engine?.Family == "RAGE" && fileName.Equals("GTA5_Enhanced.exe", StringComparison.OrdinalIgnoreCase))
		{
			string path = Path.Combine(documents, "Rockstar Games", "GTAV Enhanced", "Settings");
			if (Directory.Exists(path))
			{
				list.AddRange(Directory.EnumerateFiles(path, "*.d3d12PipelineCacheWindows", new EnumerationOptions
				{
					IgnoreInaccessible = true,
					AttributesToSkip = FileAttributes.None
				}));
			}
		}
		if ((object)engine != null && engine.Family == "Nixxes" && engine.Fork == "Stingray" && fileName.Equals("helldivers2.exe", StringComparison.OrdinalIgnoreCase))
		{
			InlineArray5<string> buffer = default;
			buffer[0] = roamingAppData;
			buffer[1] = "Arrowhead";
			buffer[2] = "Helldivers2";
			buffer[3] = "shader_cache";
			buffer[4] = "pipeline_library.cache";
			list.Add(Path.Combine(buffer));
		}
		return list.Where(File.Exists).ToList();
	}
}
