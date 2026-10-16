using System;
using System.IO;
using System.Threading;

namespace SCSKiller.Core.App;

public static class CrashLog
{
	public const string FileName = "crash.log";

	private static readonly Lock _gate = new Lock();

	private static Exception? _last;

	public static void Write(string dataDir, Exception e)
	{
		using (_gate.EnterScope())
		{
			if (_last == e)
			{
				return;
			}
			_last = e;
			try
			{
				Directory.CreateDirectory(dataDir);
				string fileName = Path.Combine(dataDir, "crash.log");
				try
				{
					FileInfo fileInfo = new FileInfo(fileName);
					if (fileInfo != null && fileInfo.Exists && fileInfo.Length > 1048576)
					{
						fileInfo.MoveTo(Path.Combine(dataDir, "crash.old.log"), overwrite: true);
					}
				}
				catch (IOException)
				{
				}
				ScsKiller.AppendLog(dataDir, "crash.log", $"SCSKiller {AppVersion.Current} on Windows {Environment.OSVersion.Version}: {e}", 0L);
			}
			catch (Exception)
			{
			}
		}
	}
}
