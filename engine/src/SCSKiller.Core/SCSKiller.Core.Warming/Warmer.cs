using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.PortableExecutable;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Games;
using SCSKiller.Core.Vendors;

namespace SCSKiller.Core.Warming;

public sealed class Warmer(IGpuVendorBackend vendor, string? warmExe = null) : IWarmer
{
	public const string StuckError = "the compile didn't stop cleanly (the driver may be stuck), so it was ended";

	public const string NoDoneError = "scskiller_warm exited without a done event";

	public const string HungError = "the GPU driver kept hanging, so the compile stopped. Compiling again continues where it stopped";

	private const int MaxPath = 260;

	private const string LongestStagedName = "scskiller_warm_times.csv";

	private const string LongestStageDir = "stage-4294967295-99";

	public TimeSpan StuckAfter { get; init; } = TimeSpan.FromSeconds(30L);

	public int MaxRecoveries { get; init; } = 20;

	public IReadOnlyDictionary<string, string>? Environment { get; init; }

	public IProgress<string>? Log { get; set; }

	public Func<Game, AgsRegistration?>? Ags { get; set; }

	public Func<Game, string, string?>? Layer { get; set; }

	public static string CrashedError(int skipped)
	{
		return skipped switch
		{
			1 => "1 pipeline that crashes the GPU driver was skipped. Compiling again continues where it stopped", 
			0 => "the GPU driver kept crashing with no single pipeline to blame, so the compile stopped", 
			_ => $"{skipped} pipelines that crash the GPU driver were skipped. Compiling again continues where it stopped", 
		};
	}

	public static string ExeFor(GpuVendor vendor)
	{
		if (vendor != GpuVendor.Nvidia)
		{
			return "scskiller_warm.exe";
		}
		return "segheap\\scskiller_warm.exe";
	}

	public IWarmRun Start(Game game, string workDir, WarmOptions options, IProgress<WarmProgress>? progress)
	{
		return Start(game, workDir, options, progress, vendor.Gpu);
	}

	public IWarmRun Start(Game game, string workDir, WarmOptions options, IProgress<WarmProgress>? progress, GpuInfo gpu)
	{
		string exe = warmExe ?? NativeTools.Find(ExeFor(vendor.Vendor)) ?? throw new FileNotFoundException(ExeFor(vendor.Vendor) + " not found next to the app or in proxy\\build\\Release");
		string stagePath = null;
		if (XboxSource.AppUserModelId(game) == null && (stagePath = StagePath(game, workDir, out string why)) == null)
		{
			Log?.Report(game.Name + ": the compile runs without the install's folder layout: " + why);
		}
		AgsRegistration agsRegistration = ((vendor.Vendor != GpuVendor.Amd) ? null : Ags?.Invoke(game));
		string[] array = AgsArgs(vendor.Vendor, agsRegistration, (agsRegistration == null) ? null : AmdAgs.DllFor(game, NativeTools.Find("amd_ags_x64.dll")), out string why2);
		if (why2 != null)
		{
			Log?.Report(game.Name + ": " + why2);
		}
		string text = Layer?.Invoke(game, workDir);
		if (text != null)
		{
			string[] array2 = array;
			int num = 0;
			string[] array3 = new string[2 + array2.Length];
			ReadOnlySpan<string> readOnlySpan = new ReadOnlySpan<string>(array2);
			readOnlySpan.CopyTo(new Span<string>(array3).Slice(num, readOnlySpan.Length));
			num += readOnlySpan.Length;
			array3[num] = "--layer";
			num++;
			array3[num] = text;
			array = array3;
		}
		string text2 = AgilityDir(game.ExePath);
		if (text2 != null)
		{
			string[] array3 = array;
			int num = 0;
			string[] array2 = new string[2 + array3.Length];
			ReadOnlySpan<string> readOnlySpan = new ReadOnlySpan<string>(array3);
			readOnlySpan.CopyTo(new Span<string>(array2).Slice(num, readOnlySpan.Length));
			num += readOnlySpan.Length;
			array2[num] = "--d3d12";
			num++;
			array2[num] = text2;
			array = array2;
		}
		string text3 = DxvkDll(vendor.Vendor, game.ExePath);
		if (text3 != null)
		{
			string[] array2 = array;
			int num = 0;
			string[] array3 = new string[2 + array2.Length];
			ReadOnlySpan<string> readOnlySpan = new ReadOnlySpan<string>(array2);
			readOnlySpan.CopyTo(new Span<string>(array3).Slice(num, readOnlySpan.Length));
			num += readOnlySpan.Length;
			array3[num] = "--d3d11";
			num++;
			array3[num] = text3;
			array = array3;
		}
		return new WarmRun(vendor, gpu, exe, game, workDir, options, progress, StuckAfter, stagePath, MaxRecoveries, Environment, array, Log);
	}

	public static string[] AgsArgs(GpuVendor vendor, AgsRegistration? reg, string? agsDll, out string? why)
	{
		why = null;
		if (vendor != GpuVendor.Amd || reg == null)
		{
			return Array.Empty<string>();
		}
		if (agsDll == null)
		{
			why = "amd_ags_x64.dll not found next to the app: the compile fills the exe name's cache, not the one of the game's AGS app name " + reg.App;
			return Array.Empty<string>();
		}
		return new string[6] { "--ags", agsDll, "--ags-app", reg.App, "--ags-engine", reg.Engine };
	}

	public static string? DxvkDll(GpuVendor vendor, string exePath)
	{
		try
		{
			string text = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exePath)), "d3d11.dll");
			return (vendor == GpuVendor.Nvidia && ReShade.IsDxvk(text)) ? text : null;
		}
		catch (Exception ex) when ((ex is ArgumentException || ex is NotSupportedException || ex is IOException) ? true : false)
		{
			return null;
		}
	}

	public static string? AgilityDir(string exePath)
	{
		try
		{
			string directoryName = Path.GetDirectoryName(Path.GetFullPath(exePath));
			string text = null;
			try
			{
				using PEReader pe = PeFile.Open(exePath);
				text = PeFile.ExportedString(pe, "D3D12SDKPath");
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is BadImageFormatException) ? true : false)
			{
			}
			string text2 = Path.Combine(directoryName, "D3D12");
			string text3;
			if (string.IsNullOrEmpty(text))
			{
				text3 = (File.Exists(Path.Combine(text2, "D3D12Core.dll")) ? text2 : Path.Combine(text2, "x64"));
			}
			else
			{
				text3 = AgilityFolder(directoryName, text);
			}
			return (text3 != null && File.Exists(Path.Combine(text3, "D3D12Core.dll"))) ? text3 : null;
		}
		catch (Exception ex2) when ((ex2 is ArgumentException || ex2 is NotSupportedException || ex2 is IOException) ? true : false)
		{
			return null;
		}
	}

	public static string? AgilityFolder(string exeDir, string? sdkPath)
	{
		if (string.IsNullOrEmpty(sdkPath) || Path.IsPathRooted(sdkPath) || sdkPath.Contains(':'))
		{
			return null;
		}
		string text = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(exeDir, sdkPath)));
		string relativePath = Path.GetRelativePath(exeDir, text);
		if (!(relativePath == "..") && !relativePath.StartsWith("..\\", StringComparison.Ordinal) && !Path.IsPathRooted(relativePath))
		{
			return text;
		}
		return null;
	}

	public static string? StagePath(Game game, string workDir, out string? why)
	{
		string text = Path.TrimEndingDirectorySeparator(Path.GetFullPath(game.InstallDir));
		string relativePath = Path.GetRelativePath(text, Path.GetFullPath(game.ExePath));
		object obj;
		if (Path.GetFileName(text).Length != 0)
		{
			obj = ((Path.IsPathRooted(relativePath) || relativePath == "." || relativePath == ".." || relativePath.StartsWith("..\\", StringComparison.Ordinal)) ? (game.ExePath + " is outside " + game.InstallDir) : null);
		}
		else
		{
			obj = "the install folder " + game.InstallDir + " is a drive root";
		}
		why = (string?)obj;
		if (why != null)
		{
			return null;
		}
		string text2 = Path.Combine(Path.GetFileName(text), relativePath);
		if (Path.GetFullPath(Path.Combine(workDir, "stage-4294967295-99", Path.GetDirectoryName(text2))).Length + 1 + Math.Max(Path.GetFileName(text2).Length, "scskiller_warm_times.csv".Length) < 260)
		{
			return text2;
		}
		why = $"the staged path under {workDir} would be longer than {259} characters";
		return null;
	}
}
