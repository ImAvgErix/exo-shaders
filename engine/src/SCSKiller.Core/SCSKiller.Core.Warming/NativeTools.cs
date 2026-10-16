using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace SCSKiller.Core.Warming;

public static class NativeTools
{
	public static string? Find(string fileName)
	{
		string baseDirectory = AppContext.BaseDirectory;
		string[] array = new string[3]
		{
			Path.Combine(baseDirectory, "native", fileName),
			Path.Combine(baseDirectory, fileName),
			Path.GetFullPath(Path.Combine(baseDirectory, "..", "native", fileName))
		};
		foreach (string text in array)
		{
			if (File.Exists(text))
			{
				return text;
			}
		}
		for (DirectoryInfo directoryInfo = new DirectoryInfo(baseDirectory); directoryInfo != null; directoryInfo = directoryInfo.Parent)
		{
			InlineArray5<string> buffer = default;
			buffer[0] = directoryInfo.FullName;
			buffer[1] = "proxy";
			buffer[2] = "build";
			buffer[3] = "Release";
			buffer[4] = fileName;
			string text2 = Path.Combine(buffer);
			if (File.Exists(text2))
			{
				return text2;
			}
		}
		return null;
	}
}
