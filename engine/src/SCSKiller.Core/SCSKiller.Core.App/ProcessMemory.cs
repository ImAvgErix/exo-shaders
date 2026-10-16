using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SCSKiller.Core.App;

public static class ProcessMemory
{
	public const string LogFile = "memory.log";

	public static void Trim()
	{
		SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
	}

	public static string Line()
	{
		using Process process = Process.GetCurrentProcess();
		GCMemoryInfo gc = GC.GetGCMemoryInfo();
		IFormatProvider invariantCulture = CultureInfo.InvariantCulture;
		DefaultInterpolatedStringHandler handler = new DefaultInterpolatedStringHandler(199, 14, invariantCulture);
		handler.AppendLiteral("working set ");
		handler.AppendFormatted(process.WorkingSet64 >> 20);
		handler.AppendLiteral(" MB, private ");
		handler.AppendFormatted(process.PrivateMemorySize64 >> 20);
		handler.AppendLiteral(" MB, managed heap ");
		handler.AppendFormatted(GC.GetTotalMemory(forceFullCollection: false) >> 20);
		handler.AppendLiteral(" MB");
		handler.AppendLiteral(" (after the last collection: gen0 ");
		handler.AppendFormatted(Gen(0));
		handler.AppendLiteral(", gen1 ");
		handler.AppendFormatted(Gen(1));
		handler.AppendLiteral(", gen2 ");
		handler.AppendFormatted(Gen(2));
		handler.AppendLiteral(", large ");
		handler.AppendFormatted(Gen(3));
		handler.AppendLiteral(", pinned ");
		handler.AppendFormatted(Gen(4));
		handler.AppendLiteral(" MB),");
		handler.AppendLiteral(" GC committed ");
		handler.AppendFormatted(gc.TotalCommittedBytes >> 20);
		handler.AppendLiteral(" MB of which ");
		handler.AppendFormatted(gc.FragmentedBytes >> 20);
		handler.AppendLiteral(" MB free, cached keys ");
		handler.AppendFormatted(KeyFiles.CachedKeys, "N0");
		handler.AppendLiteral(",");
		handler.AppendLiteral(" threads ");
		handler.AppendFormatted(process.Threads.Count);
		handler.AppendLiteral(", handles ");
		handler.AppendFormatted(process.HandleCount);
		handler.AppendLiteral(", JIT ");
		handler.AppendFormatted(JitInfo.GetCompiledMethodCount(), "N0");
		handler.AppendLiteral(" methods");
		return string.Create(invariantCulture, ref handler);
		string Gen(int i)
		{
			if (i < gc.GenerationInfo.Length)
			{
				return $"{gc.GenerationInfo[i].SizeAfterBytes >> 20}";
			}
			return "-";
		}
	}

	[DllImport("kernel32.dll")]
	private static extern nint GetCurrentProcess();

	[DllImport("kernel32.dll")]
	private static extern bool SetProcessWorkingSetSize(nint process, nint min, nint max);
}
