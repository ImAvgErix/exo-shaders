using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace SCSKiller.Core.Vendors;

public sealed class NvidiaBackend : IGpuVendorBackend, IRefreshableGpu
{
	private sealed class Drs : IDisposable
	{
		public readonly nint Session;

		public readonly nint Profile;

		public unsafe Drs()
		{
			nint num = default;
			Check(((delegate* unmanaged<nint*, int>)Fn(110417198u))(&num), "NvAPI_DRS_CreateSession");
			Session = num;
			nint profile = default;
			try
			{
				Check(((delegate* unmanaged<nint, int>)Fn(928890219u))(num), "NvAPI_DRS_LoadSettings");
				Check(((delegate* unmanaged<nint, nint*, int>)Fn(3666110112u))(num, &profile), "NvAPI_DRS_GetBaseProfile");
			}
			catch
			{
				Dispose();
				throw;
			}
			Profile = profile;
		}

		public unsafe void Dispose()
		{
			((delegate* unmanaged<nint, int>)Fn(3671707640u))(Session);
		}
	}

	public const string CacheSizeSettingName = "Shader disk cache maximum size";

	private const uint KnownCacheSizeId = 11306135u;

	private const uint Unlimited = uint.MaxValue;

	private const int SettingNotFound = -160;

	private const int InvalidUserPrivilege = -137;

	private const int EndEnumeration = -7;

	private const long MiB = 1048576L;

	private bool _confirmed;

	public const uint AutoShaderCompilationId = 15389065u;

	public const string AutoShaderTaskName = "\\NVIDIA Shader Compiler";

	public const uint GetSettingExId = 3935914381u;

	public const uint SetSettingExId = 2318202357u;

	public const uint GetSettingId = 1941930808u;

	private static readonly string[] NvOscExits = new string[12]
	{
		"Success", "General abnormal termination", "Major error", "Library/DLL loading failure", "Initialization failure", "Task scheduler operation failure", "Memory allocation/resource failure", "Configuration/command line argument error", "Compilation/processing error", "File/disk cache error",
		"Memory leak detected", "Thread pool initialization failure"
	};

	private static readonly int[] TaskPriorities = new int[3] { 10, 4, 2 };

	private const int SettingSize = 12320;

	private const int OffSettingId = 4100;

	private const int OffSettingType = 4104;

	private const int OffIsCurrentPredefined = 4112;

	private const int OffCurrentValue = 8220;

	public const uint SettingVersion1 = 77856u;

	private static nint _queryInterface;

	public GpuVendor Vendor => GpuVendor.Nvidia;

	public GpuInfo Gpu { get; private set; }

	public VendorCaps Caps { get; } = new VendorCaps("nvidia-1", CacheKeyedByExeName: true, StateIndependentCache: true, CacheSizeConfigurable: true, PerStageCache: true, RtCacheGranularity.Collection, PackageKeyed: true);

	public static string CacheDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NVIDIA", "DXCache");

	public NvidiaAppCache AppCache { get; } = new NvidiaAppCache(CacheDir);

	IAppCache? IGpuVendorBackend.AppCache => AppCache;

	public unsafe NvidiaBackend(GpuInfo dxgi)
	{
		Check(((delegate* unmanaged<int>)Fn(22079528u))(), "NvAPI_Initialize");
		string text = DriverVersionOf(dxgi.DriverVersion, Wine.Running);
		GpuInfo gpu = dxgi with
		{
			DriverVersion = (text ?? ReadDriverVersion())
		};
		bool confirmed = text != null;
		Gpu = gpu;
		_confirmed = confirmed;
	}

	internal unsafe static string ReadDriverVersion()
	{
		byte* ptr = stackalloc byte[64];
		uint num = default;
		Check(((delegate* unmanaged<uint*, byte*, int>)Fn(690399917u))(&num, ptr), "NvAPI_SYS_GetDriverAndBranchVersion");
		return $"{num / 100}.{num % 100:00}";
	}

	public bool Refresh(GpuInfo adapter)
	{
		string text = DriverVersionOf(adapter.DriverVersion, Wine.Running);
		if (text != null)
		{
			GpuInfo gpu = adapter with
			{
				DriverVersion = text
			};
			Gpu = gpu;
			_confirmed = true;
			return true;
		}
		string text2 = null;
		if (!_confirmed)
		{
			try
			{
				text2 = ReadDriverVersion();
			}
			catch (InvalidOperationException)
			{
			}
		}
		Gpu = adapter with
		{
			DriverVersion = (text2 ?? Gpu.DriverVersion)
		};
		return false;
	}

	public string FallbackVersion(string umd)
	{
		return DriverVersionOf(umd, Wine.Running) ?? umd;
	}

	public static string? DriverVersionOf(string adapterVersion, bool wine)
	{
		if (wine)
		{
			string[] array = adapterVersion.Split('.');
			if (array != null && array.Length == 3)
			{
				return adapterVersion;
			}
		}
		return FromUserModeVersion(adapterVersion);
	}

	public static string? FromUserModeVersion(string umd)
	{
		string[] array = umd.Split('.');
		if (array != null && array.Length == 4)
		{
			string s = array[2];
			string s2 = array[3];
			if (int.TryParse(s, out var result) && int.TryParse(s2, out var result2) && result2 < 10000)
			{
				string text = $"{result}{result2:0000}";
				if (text != null && text.Length >= 5)
				{
					int length = text.Length;
					int num = length - 5;
					return text.Substring(num, length - 2 - num) + "." + text.Substring(text.Length - 2);
				}
			}
		}
		return null;
	}

	public CacheUsage GetCacheUsage()
	{
		return new CacheUsage(CacheDir, AppCacheFiles.Readable(CacheDir) ? new DirectoryInfo(CacheDir).EnumerateFiles("*", new EnumerationOptions
		{
			RecurseSubdirectories = true,
			IgnoreInaccessible = true
		}).Sum((FileInfo f) => f.Length) : 0, UpperBound: true);
	}

	public (uint Id, uint? Raw, uint DriverDefault) ReadCacheSetting()
	{
		uint num = CacheSizeSettingId();
		using Drs drs = new Drs();
		return (Id: num, Raw: ReadBase(drs, num), DriverDefault: DriverDefault(num));
	}

	private unsafe static uint? ReadBase(Drs drs, uint id)
	{
		byte* ptr = stackalloc byte[12320];
		InitSetting(ptr);
		int num = ((delegate* unmanaged<nint, nint, uint, byte*, int>)Fn(1941930808u))(drs.Session, drs.Profile, id, ptr);
		if (num != -160)
		{
			Check(num, "NvAPI_DRS_GetSetting");
		}
		if (num != 0 || ((uint*)ptr)[1028] != 0)
		{
			return null;
		}
		return ((uint*)ptr)[2055];
	}

	public AutoShaderState? GetAutoShaderCompilation()
	{
		try
		{
			AutoShaderCompilation? autoShaderCompilation;
			using (Drs drs = new Drs())
			{
				autoShaderCompilation = ReadAutoShaderLevel(drs.Session, drs.Profile, TryFn(3935914381u), Fn(1941930808u));
			}
			object result;
			if (autoShaderCompilation.HasValue)
			{
				AutoShaderCompilation valueOrDefault = autoShaderCompilation.GetValueOrDefault();
				result = TaskState(valueOrDefault, QueryTaskXml(), NvOscPath());
			}
			else
			{
				result = null;
			}
			return (AutoShaderState?)result;
		}
		catch (Exception ex) when ((ex is InvalidOperationException || ex is DllNotFoundException || ex is Win32Exception) ? true : false)
		{
			return null;
		}
	}

	public unsafe static AutoShaderCompilation? ReadAutoShaderLevel(nint session, nint profile, nint getEx, nint get)
	{
		byte* ptr = stackalloc byte[12320];
		if (getEx != 0)
		{
			InitSetting(ptr);
			uint num = 0u;
			switch (((delegate* unmanaged<nint, nint, uint, byte*, uint*, int>)getEx)(session, profile, 15389065u, ptr, &num))
			{
			case -160:
				return AutoShaderCompilation.Off;
			case 0:
				return DecodeAutoShaderCompilation(((uint*)ptr)[2055]);
			}
		}
		InitSetting(ptr);
		if (((delegate* unmanaged<nint, nint, uint, byte*, int>)get)(session, profile, 15389065u, ptr) != 0)
		{
			return null;
		}
		return DecodeAutoShaderCompilation(((uint*)ptr)[2055]);
	}

	public unsafe static int WriteAutoShaderLevel(nint session, nint profile, nint setEx, AutoShaderCompilation level)
	{
		byte* ptr = stackalloc byte[12320];
		InitSetting(ptr);
		((int*)ptr)[1025] = 15389065;
		((int*)ptr)[1026] = 0;
		((int*)ptr)[2055] = (int)level;
		return ((delegate* unmanaged<nint, nint, byte*, uint, uint, int>)setEx)(session, profile, ptr, 0u, 0u);
	}

	private unsafe static void InitSetting(byte* s)
	{
		new Span<byte>(s, 12320).Clear();
		*(int*)s = 77856;
	}

	public static AutoShaderCompilation? DecodeAutoShaderCompilation(uint? raw)
	{
		if (raw.HasValue)
		{
			if (raw.GetValueOrDefault() <= 3)
			{
				return (AutoShaderCompilation)raw.Value;
			}
			return null;
		}
		return AutoShaderCompilation.Off;
	}

	public static string? RefuseReason(AutoShaderCompilation level, CacheLimit? cache)
	{
		if (level != AutoShaderCompilation.Off && (object)cache != null)
		{
			long? bytes = cache.Bytes;
			if (bytes.HasValue && bytes.GetValueOrDefault() == 0L)
			{
				return "The driver's shader cache is disabled, so there is nothing to compile into. Set a cache size first.";
			}
		}
		return null;
	}

	public unsafe string SetAutoShaderCompilation(AutoShaderCompilation level)
	{
		var (flag, text) = AutoShaderSteps(GetAutoShaderCompilation(), level);
		if (text == null)
		{
			return AutoShaderOutcome(level, changed: false);
		}
		string text2 = RefuseReason(level, GetCacheLimit());
		if (text2 != null)
		{
			throw new InvalidOperationException(text2);
		}
		string text3 = NvOscPath() ?? throw new InvalidOperationException("NvOSC.exe was not found in the NVIDIA driver's folder (it needs driver r610 or newer)");
		if (flag)
		{
			nint num = TryFn(2318202357u);
			if (num == 0)
			{
				throw new InvalidOperationException("this NVIDIA driver can't switch Auto Shader Compilation from outside the NVIDIA App (no NvAPI_DRS_SetSettingEx)");
			}
			nint setEx = num;
			using Drs drs = new Drs();
			int num2 = WriteAutoShaderLevel(drs.Session, drs.Profile, setEx, level);
			string call = "NvAPI_DRS_SetSettingEx";
			int num3 = num2;
			if (num3 == 0)
			{
				int num4 = ((delegate* unmanaged<nint, int>)Fn(4240211476u))(drs.Session);
				call = "NvAPI_DRS_SaveSettings";
				num3 = num4;
			}
			if (num3 != 0)
			{
				throw AutoShaderDrsFailure(call, num3, ErrorName(num3));
			}
		}
		ConcurrentQueue<string> output = new ConcurrentQueue<string>();
		Process process;
		try
		{
			process = new Process
			{
				StartInfo = new ProcessStartInfo(text3, text)
				{
					UseShellExecute = false,
					CreateNoWindow = true,
					WorkingDirectory = Path.GetDirectoryName(text3),
					RedirectStandardOutput = true,
					RedirectStandardError = true
				}
			};
			process.OutputDataReceived += (object _, DataReceivedEventArgs e) =>
			{
				if (e.Data != null)
				{
					output.Enqueue(e.Data);
				}
			};
			process.ErrorDataReceived += (object _, DataReceivedEventArgs e) =>
			{
				if (e.Data != null)
				{
					output.Enqueue(e.Data);
				}
			};
			process.Start();
			process.BeginOutputReadLine();
			process.BeginErrorReadLine();
		}
		catch (Win32Exception ex)
		{
			throw new InvalidOperationException(NvOscFailure(text, null, flag, ex.Message));
		}
		using (process)
		{
			if (!process.WaitForExit(60000))
			{
				throw new InvalidOperationException(NvOscFailure(text, null, flag, null, string.Join('\n', output)));
			}
			process.WaitForExit();
			if (process.ExitCode != 0)
			{
				throw new InvalidOperationException(NvOscFailure(text, process.ExitCode, flag, null, string.Join('\n', output)));
			}
		}
		AutoShaderState autoShaderCompilation = GetAutoShaderCompilation();
		if ((object)autoShaderCompilation != null && !autoShaderCompilation.Is(level))
		{
			throw new InvalidOperationException((autoShaderCompilation.Level != level) ? $"the driver still reads {autoShaderCompilation.Level} after NvOSC.exe {text}" : ((flag ? "the setting is saved, but " : "") + "NvOSC.exe " + text + " finished and the NVIDIA task " + ((level == AutoShaderCompilation.Off) ? "is still enabled" : "still isn't set up")));
		}
		return AutoShaderOutcome(level, changed: true);
	}

	public static (bool WriteSetting, string? NvOscArg) AutoShaderSteps(AutoShaderState? now, AutoShaderCompilation level)
	{
		if ((object)now != null)
		{
			if (now.Is(level))
			{
				return (WriteSetting: false, NvOscArg: null);
			}
		}
		return (WriteSetting: (object)now == null || now.Level != level, NvOscArg: (level == AutoShaderCompilation.Off) ? "-deregister" : "-register");
	}

	public static string AutoShaderOutcome(AutoShaderCompilation level, bool changed)
	{
		if (level == AutoShaderCompilation.Off)
		{
			if (!changed)
			{
				return "Already off. Nothing to change.";
			}
			return "Turned off.";
		}
		if (!changed)
		{
			return $"Already on ({level}). Nothing to change.";
		}
		return $"Turned on ({level}).";
	}

	public static Exception AutoShaderDrsFailure(string call, int rc, string? name)
	{
		return rc switch
		{
			-137 => (Exception)new UnauthorizedAccessException("switching NVIDIA Auto Shader Compilation needs administrator rights"), 
			-160 => new InvalidOperationException("this NVIDIA driver doesn't have the Auto Shader Compilation setting"), 
			_ => new InvalidOperationException($"the NVIDIA driver didn't take the setting, nothing was changed ({call} answered {rc}{(string.IsNullOrEmpty(name) ? "" : (" " + name))})"), 
		};
	}

	public static string NvOscFailure(string arg, int? exitCode, bool settingSaved, string? startError = null, string? output = null)
	{
		string value = ((arg == "-deregister") ? "removed" : "set up");
		string text = (settingSaved ? "the setting is saved, but " : "");
		List<string> list = (from l in (output ?? "").Split('\n')
			select l.Trim() into l
			where l.Length > 0
			select l).ToList();
		string text2 = list.LastOrDefault((string l) => l.Contains("0x", StringComparison.OrdinalIgnoreCase)) ?? list.LastOrDefault((string l) => !l.StartsWith("Exiting", StringComparison.Ordinal));
		string value2 = ((text2 != null) ? (" NvOSC.exe said: " + text2) : "");
		string text3 = ((list.Count > 0) ? ("\nNvOSC.exe output:\n" + string.Join('\n', list)) : "");
		if (!exitCode.HasValue && startError == null)
		{
			return text + $"NvOSC.exe {arg} is still running after a minute, so the NVIDIA task may not be {value} yet.{value2} Wait a minute before applying again." + text3;
		}
		string text4;
		if (exitCode.HasValue)
		{
			int valueOrDefault = exitCode.GetValueOrDefault();
			text4 = $"failed with {((valueOrDefault >= 0 && valueOrDefault < NvOscExits.Length) ? NvOscExits[valueOrDefault] : "an unknown error")} (exit code {valueOrDefault})";
		}
		else
		{
			text4 = "didn't start (" + startError + ")";
		}
		string value3 = text4;
		return text + $"the NVIDIA task couldn't be {value}: NvOSC.exe {arg} {value3}.{value2} Apply again to retry." + text3;
	}

	public static string? NvOscPath()
	{
		return (from d in DriverFolders()
			select Path.Combine(d, "NvOSC.exe")).FirstOrDefault(File.Exists);
	}

	public static DateTime? InstalledAt()
	{
		return DriverFolders().Select(Directory.GetCreationTimeUtc).Cast<DateTime?>().FirstOrDefault();
	}

	private static List<string> DriverFolders()
	{
		string value = Path.Combine(Environment.SystemDirectory, "DriverStore", "FileRepository") + Path.DirectorySeparatorChar;
		List<string> list = new List<string>();
		try
		{
			using RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Control\\Class\\{4d36e968-e325-11ce-bfc1-08002be10318}");
			string[] array = registryKey?.GetSubKeyNames() ?? Array.Empty<string>();
			foreach (string text in array)
			{
				if (text.Length != 4 || !text.All(char.IsAsciiDigit))
				{
					continue;
				}
				try
				{
					using RegistryKey registryKey2 = registryKey.OpenSubKey(text);
					if (registryKey2 == null)
					{
						continue;
					}
					string obj = registryKey2.GetValue("MatchingDeviceId") as string;
					if (obj == null || !obj.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase) || !(registryKey2.GetValue("UserModeDriverName") is string[] array2) || array2.Length <= 0)
					{
						continue;
					}
					string directoryName = Path.GetDirectoryName(array2[0]);
					if (directoryName != null)
					{
						string fullPath = Path.GetFullPath(directoryName);
						if (fullPath.StartsWith(value, StringComparison.OrdinalIgnoreCase) && Directory.Exists(fullPath))
						{
							list.Add(fullPath);
						}
					}
				}
				catch (SecurityException)
				{
				}
			}
		}
		catch (Exception ex2) when ((ex2 is SecurityException || ex2 is UnauthorizedAccessException || ex2 is IOException) ? true : false)
		{
		}
		return list;
	}

	private static string? QueryTaskXml()
	{
		return QueryTaskXml("\\Users\\" + Environment.UserName + "\\NVIDIA Shader Compiler") ?? QueryTaskXml("\\NVIDIA Shader Compiler");
	}

	private static string? QueryTaskXml(string name)
	{
		using Process process = Process.Start(new ProcessStartInfo("schtasks.exe", new _003C_003Ez__ReadOnlyArray<string>(new string[4] { "/Query", "/TN", name, "/XML" }))
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		});
		Task<string> task = process.StandardError.ReadToEndAsync();
		string text = process.StandardOutput.ReadToEnd();
		process.WaitForExit();
		_ = task.Result;
		return (process.ExitCode == 0) ? text : null;
	}

	public static AutoShaderState TaskState(AutoShaderCompilation level, string? taskXml, string? nvosc)
	{
		if (string.IsNullOrWhiteSpace(taskXml))
		{
			return new AutoShaderState(level, TaskReady: false, TaskEnabled: false);
		}
		try
		{
			XNamespace xNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";
			XElement? root = XDocument.Parse(taskXml.Trim()).Root;
			XElement xElement = root?.Element(xNamespace + "Settings");
			string text = xElement?.Element(xNamespace + "Enabled")?.Value;
			bool flag = text == null || bool.Parse(text.Trim());
			string a = root?.Element(xNamespace + "Actions")?.Element(xNamespace + "Exec")?.Element(xNamespace + "Command")?.Value.Trim().Trim('"');
			int num = (int.TryParse(xElement?.Element(xNamespace + "Priority")?.Value, out var result) ? result : (-1));
			bool taskReady = flag && nvosc != null && string.Equals(a, nvosc, StringComparison.OrdinalIgnoreCase) && (level == AutoShaderCompilation.Off || !TaskPriorities.Contains(num) || num == TaskPriorities[(int)(level - 1)]);
			return new AutoShaderState(level, taskReady, flag);
		}
		catch (Exception ex) when ((ex is XmlException || ex is FormatException) ? true : false)
		{
			return new AutoShaderState(level, TaskReady: false, TaskEnabled: true);
		}
	}

	public unsafe static List<(uint Id, string? Name, uint Type, uint Value)> BaseProfileSettings()
	{
		using Drs drs = new Drs();
		byte* ptr = (byte*)NativeMemory.AllocZeroed(12615680u);
		try
		{
			for (int i = 0; i < 1024; i++)
			{
				*(int*)(ptr + 12320 * i) = 77856;
			}
			uint num = 1024u;
			int num2 = ((delegate* unmanaged<nint, nint, uint, uint*, byte*, int>)Fn(2922396122u))(drs.Session, drs.Profile, 0u, &num, ptr);
			if (num2 == -7)
			{
				num = 0u;
			}
			else
			{
				Check(num2, "NvAPI_DRS_EnumSettings");
			}
			List<(uint, string, uint, uint)> list = new List<(uint, string, uint, uint)>();
			for (int j = 0; j < num; j++)
			{
				byte* ptr2 = ptr + 12320 * j;
				uint num3 = ((uint*)ptr2)[1025];
				list.Add((num3, SettingName(num3), ((uint*)ptr2)[1026], ((uint*)ptr2)[2055]));
			}
			return list;
		}
		finally
		{
			NativeMemory.Free(ptr);
		}
	}

	public CacheLimit? GetCacheLimit()
	{
		try
		{
			(uint Id, uint? Raw, uint DriverDefault) tuple = ReadCacheSetting();
			uint? item = tuple.Raw;
			uint item2 = tuple.DriverDefault;
			CacheLimit result;
			if (item.HasValue)
			{
				uint valueOrDefault = item.GetValueOrDefault();
				result = Decode(valueOrDefault, isDefault: false);
			}
			else
			{
				result = Decode(item2, isDefault: true);
			}
			return result;
		}
		catch (Exception ex) when ((ex is InvalidOperationException || ex is DllNotFoundException) ? true : false)
		{
			return null;
		}
	}

	public static CacheLimit Decode(uint raw, bool isDefault)
	{
		return new CacheLimit((raw == uint.MaxValue) ? ((long?)null) : new long?((long)raw * 1048576L), isDefault);
	}

	public static uint Encode(CacheLimit limit)
	{
		long? bytes = limit.Bytes;
		if (bytes.HasValue)
		{
			long valueOrDefault = bytes.GetValueOrDefault();
			if (valueOrDefault < 0 || valueOrDefault / 1048576 >= uint.MaxValue)
			{
				throw new ArgumentOutOfRangeException("limit", "cache size out of range");
			}
			return (uint)((valueOrDefault + 1048576 - 1) / 1048576);
		}
		return uint.MaxValue;
	}

	public unsafe void SetCacheLimit(CacheLimit limit)
	{
		uint num = CacheSizeSettingId();
		using Drs drs = new Drs();
		if (limit.IsDriverDefault)
		{
			int num2 = ((delegate* unmanaged<nint, nint, uint, int>)Fn(3835847522u))(drs.Session, drs.Profile, num);
			if (num2 != -160)
			{
				Check(num2, "NvAPI_DRS_DeleteProfileSetting");
			}
		}
		else
		{
			WriteBase(drs, num, Encode(limit));
		}
		Save(drs, "changing the NVIDIA shader cache size needs administrator rights");
	}

	private unsafe static void WriteBase(Drs drs, uint id, uint value)
	{
		byte* ptr = stackalloc byte[12320];
		InitSetting(ptr);
		((int*)ptr)[1025] = (int)id;
		((int*)ptr)[1026] = 0;
		((int*)ptr)[2055] = (int)value;
		Check(((delegate* unmanaged<nint, nint, byte*, int>)Fn(1467863554u))(drs.Session, drs.Profile, ptr), "NvAPI_DRS_SetSetting");
	}

	private unsafe static void Save(Drs drs, string needsAdmin)
	{
		int num = ((delegate* unmanaged<nint, int>)Fn(4240211476u))(drs.Session);
		if (num == -137)
		{
			throw new UnauthorizedAccessException(needsAdmin);
		}
		Check(num, "NvAPI_DRS_SaveSettings");
	}

	public unsafe static uint CacheSizeSettingId()
	{
		if (SettingName(11306135u) == "Shader disk cache maximum size")
		{
			return 11306135u;
		}
		delegate* unmanaged<uint*, uint*, int> obj = (delegate* unmanaged<uint*, uint*, int>)Fn(4028653898u);
		uint[] array = new uint[8192];
		uint length = (uint)array.Length;
		fixed (uint* ptr = array)
		{
			Check(obj(ptr, &length), "NvAPI_DRS_EnumAvailableSettingIds");
		}
		Span<uint> span = array.AsSpan(0, (int)length);
		for (int i = 0; i < span.Length; i++)
		{
			uint num = span[i];
			if (string.Equals(SettingName(num), "Shader disk cache maximum size", StringComparison.OrdinalIgnoreCase))
			{
				return num;
			}
		}
		throw new InvalidOperationException("NVIDIA driver has no 'Shader disk cache maximum size' setting");
	}

	private unsafe static string? SettingName(uint id)
	{
		char* ptr = stackalloc char[2048];
		*ptr = '\0';
		if (((delegate* unmanaged<uint, char*, int>)Fn(3592207982u))(id, ptr) != 0)
		{
			return null;
		}
		return new string(ptr);
	}

	private unsafe static uint DriverDefault(uint id)
	{
		byte* ptr = (byte*)NativeMemory.AllocZeroed(414112u);
		try
		{
			*(int*)ptr = 479648;
			uint num = 100u;
			Check(((delegate* unmanaged<uint, uint*, byte*, int>)Fn(784572304u))(id, &num, ptr), "NvAPI_DRS_EnumAvailableSettingValues");
			return ((uint*)ptr)[3];
		}
		finally
		{
			NativeMemory.Free(ptr);
		}
	}

	private static nint Fn(uint id)
	{
		nint num = TryFn(id);
		if (num == 0)
		{
			throw new InvalidOperationException($"NvAPI function 0x{id:X8} not available");
		}
		return num;
	}

	private unsafe static nint TryFn(uint id)
	{
		if (_queryInterface == 0)
		{
			_queryInterface = NativeLibrary.GetExport(NativeLibrary.Load("nvapi64.dll"), "nvapi_QueryInterface");
		}
		return ((delegate* unmanaged<uint, nint>)_queryInterface)(id);
	}

	private static void Check(int rc, string what)
	{
		if (rc != 0)
		{
			throw new InvalidOperationException($"{what} failed: {rc} {ErrorName(rc)}");
		}
	}

	private unsafe static string? ErrorName(int rc)
	{
		byte* ptr = stackalloc byte[64];
		*ptr = 0;
		((delegate* unmanaged<int, byte*, int>)Fn(1814889612u))(rc, ptr);
		return Marshal.PtrToStringAnsi((nint)ptr);
	}
}
