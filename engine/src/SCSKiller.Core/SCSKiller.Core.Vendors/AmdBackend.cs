using System;
using System.IO;
using System.Linq;
using System.Security;
using Microsoft.Win32;

namespace SCSKiller.Core.Vendors;

public sealed class AmdBackend : IGpuVendorBackend, IRefreshableGpu
{
	private const string DisplayClass = "SYSTEM\\CurrentControlSet\\Control\\Class\\{4d36e968-e325-11ce-bfc1-08002be10318}";

	private readonly Func<string, (string? Adrenalin, string? DriverStore)> _registry;

	private string? _umd;

	private string? _read;

	private bool _hadAdrenalin;

	private bool _hadStore;

	private string? _adrenalin;

	public GpuVendor Vendor => GpuVendor.Amd;

	public GpuInfo Gpu { get; private set; }

	public VendorCaps Caps { get; } = new VendorCaps("amd-1", CacheKeyedByExeName: true, StateIndependentCache: false, CacheSizeConfigurable: false, PerStageCache: true);

	private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

	public static string CacheDir => Path.Combine(LocalAppData, "AMD", "DxcCache");

	public static string D3D11CacheDir => Path.Combine(LocalAppData, "AMD", "DxCache");

	public AmdAppCache AppCache { get; } = new AmdAppCache(CacheDir, D3D11CacheDir);

	IAppCache? IGpuVendorBackend.AppCache => AppCache;

	public string FallbackVersion(string umd)
	{
		return FormatVersion(_adrenalin, umd);
	}

	public AmdBackend(GpuInfo dxgi)
		: this(dxgi, DriverVersions)
	{
	}

	internal AmdBackend(GpuInfo dxgi, Func<string, (string? Adrenalin, string? DriverStore)> registry)
	{
		_registry = registry;
		Gpu = dxgi;
		Refresh(dxgi);
	}

	public bool Refresh(GpuInfo adapter)
	{
		(string, string) tuple = _registry(adapter.Name);
		string item = tuple.Item1;
		string item2 = tuple.Item2;
		_adrenalin = item ?? _adrenalin;
		if (_read != null && ((_hadAdrenalin && item == null) || (_hadStore && item2 == null)))
		{
			Gpu = adapter with
			{
				DriverVersion = _read
			};
			return false;
		}
		string text = FormatVersion(item, item2 ?? adapter.DriverVersion);
		Gpu = adapter with
		{
			DriverVersion = text
		};
		if ((item == null && item2 == null) || (_umd != null && adapter.DriverVersion != _umd && text == _read))
		{
			return false;
		}
		string driverVersion = adapter.DriverVersion;
		string read = text;
		bool hadAdrenalin = item != null;
		bool hadStore = item2 != null;
		_umd = driverVersion;
		_read = read;
		_hadAdrenalin = hadAdrenalin;
		_hadStore = hadStore;
		return true;
	}

	public static string FormatVersion(string? adrenalin, string? driverStore)
	{
		bool num = adrenalin != null && adrenalin.Length > 0;
		bool flag = driverStore != null && driverStore.Length > 0;
		if (num)
		{
			if (flag)
			{
				return adrenalin + " (" + driverStore + ")";
			}
			return adrenalin;
		}
		if (flag)
		{
			return driverStore;
		}
		return "";
	}

	public CacheUsage GetCacheUsage()
	{
		return new CacheUsage(CacheDir, Bytes(CacheDir) + Bytes(D3D11CacheDir), UpperBound: true);
	}

	public static long Bytes(string dir)
	{
		if (!AppCacheFiles.Readable(dir))
		{
			return 0L;
		}
		return new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions
		{
			RecurseSubdirectories = true,
			IgnoreInaccessible = true
		}).Sum((FileInfo f) => f.Length);
	}

	public CacheLimit? GetCacheLimit()
	{
		return new CacheLimit(17179869184L, IsDriverDefault: false);
	}

	public void SetCacheLimit(CacheLimit limit)
	{
		throw new NotSupportedException("the AMD shader cache size is not configurable (" + Gpu.Name + ")");
	}

	public static (string? Adrenalin, string? DriverStore) DriverVersions(string adapterName)
	{
		string text = null;
		string item = null;
		try
		{
			using RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Control\\Class\\{4d36e968-e325-11ce-bfc1-08002be10318}");
			RegistryKey registryKey2 = null;
			string[] array = registryKey?.GetSubKeyNames() ?? Array.Empty<string>();
			foreach (string text2 in array)
			{
				if (text2.Length != 4 || !text2.All(char.IsAsciiDigit))
				{
					continue;
				}
				RegistryKey registryKey3;
				try
				{
					registryKey3 = registryKey.OpenSubKey(text2);
				}
				catch (SecurityException)
				{
					continue;
				}
				if (registryKey3 == null)
				{
					continue;
				}
				bool num = (registryKey3.GetValue("MatchingDeviceId") as string)?.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase) ?? false;
				bool flag = string.Equals((registryKey3.GetValue("DriverDesc") as string)?.Trim(), adapterName.Trim(), StringComparison.OrdinalIgnoreCase);
				if (num && (flag || registryKey2 == null))
				{
					registryKey2?.Dispose();
					registryKey2 = registryKey3;
					if (flag)
					{
						break;
					}
				}
				else
				{
					registryKey3.Dispose();
				}
			}
			using (registryKey2)
			{
				string text3 = (registryKey2?.GetValue("DriverVersion") as string)?.Trim();
				item = ((text3 != null && text3.Length > 0) ? text3 : null);
				if (registryKey2?.GetValue("RadeonSoftwareVersion") is string { Length: >0 } text4)
				{
					return (Adrenalin: text4.Trim(), DriverStore: item);
				}
				text = registryKey2?.GetValue("ReleaseVersion") as string;
			}
		}
		catch (Exception ex2) when ((ex2 is SecurityException || ex2 is UnauthorizedAccessException || ex2 is IOException) ? true : false)
		{
		}
		try
		{
			using RegistryKey registryKey4 = Registry.LocalMachine.OpenSubKey("SOFTWARE\\AMD\\CN");
			if (registryKey4?.GetValue("RadeonSoftwareVersion") is string { Length: >0 } text5)
			{
				return (Adrenalin: text5.Trim(), DriverStore: item);
			}
		}
		catch (Exception ex3) when ((ex3 is SecurityException || ex3 is UnauthorizedAccessException || ex3 is IOException) ? true : false)
		{
		}
		string text6 = ((text != null) ? text.Split('-')[0] : null);
		return (Adrenalin: (text6 != null && text6.Length > 0) ? text6.Trim() : null, DriverStore: item);
	}
}
