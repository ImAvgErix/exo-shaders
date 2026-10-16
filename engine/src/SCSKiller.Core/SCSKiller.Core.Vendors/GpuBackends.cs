using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace SCSKiller.Core.Vendors;

public static class GpuBackends
{
	private struct AdapterDesc1
	{
		public unsafe fixed char Description[128];

		public uint VendorId;

		public uint DeviceId;

		public uint SubSysId;

		public uint Revision;

		public nuint DedicatedVideoMemory;

		public nuint DedicatedSystemMemory;

		public nuint SharedSystemMemory;

		public uint LuidLow;

		public int LuidHigh;

		public uint Flags;
	}

	public static IGpuVendorBackend Detect(GpuChoice? choice = null, bool intel = false)
	{
		return For(Pick(Adapters(), choice)?.Gpu ?? new GpuInfo(GpuVendor.Unknown, "no D3D adapter", "", 0L, 0uL), intel);
	}

	public static IGpuVendorBackend For(GpuInfo gpu, bool intel = false)
	{
		try
		{
			GpuVendor vendor = gpu.Vendor;
			IGpuVendorBackend result;
			if (vendor == GpuVendor.Amd)
			{
				result = new AmdBackend(gpu);
			}
			else if (vendor == GpuVendor.Nvidia)
			{
				result = new NvidiaBackend(gpu);
			}
			else
			{
				result = ((vendor != GpuVendor.Intel || !intel) ? ((IGpuVendorBackend)new UnsupportedVendor(gpu)) : ((IGpuVendorBackend)new IntelBackend(gpu)));
			}
			return result;
		}
		catch (Exception ex) when ((ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is InvalidOperationException) ? true : false)
		{
			return new UnsupportedVendor(gpu);
		}
	}

	public static GpuInfo? PrimaryAdapter()
	{
		return Primary(Adapters());
	}

	public static GpuInfo? Primary(IReadOnlyList<DxgiAdapter> adapters)
	{
		return adapters.MaxBy((DxgiAdapter a) => a.Gpu.DedicatedVideoMemory)?.Gpu;
	}

	public static DxgiAdapter? Pick(IReadOnlyList<DxgiAdapter> adapters, GpuChoice? choice)
	{
		return Chosen(adapters, choice) ?? adapters.MaxBy((DxgiAdapter a) => a.Gpu.DedicatedVideoMemory);
	}

	public static DxgiAdapter? Chosen(IReadOnlyList<DxgiAdapter> adapters, GpuChoice? choice)
	{
		object obj;
		if ((object)choice != null)
		{
			GpuChoice c = choice;
			obj = adapters.FirstOrDefault((DxgiAdapter a) =>
			{
				long adapterLuid = a.Gpu.AdapterLuid;
				GpuVendor vendor = a.Gpu.Vendor;
				uint deviceId = a.DeviceId;
				long adapterLuid2 = c.AdapterLuid;
				GpuVendor vendor2 = c.Vendor;
				uint deviceId2 = c.DeviceId;
				return adapterLuid == adapterLuid2 && vendor == vendor2 && deviceId == deviceId2;
			});
			if (obj == null)
			{
				return adapters.FirstOrDefault((DxgiAdapter a) =>
				{
					GpuVendor vendor = a.Gpu.Vendor;
					uint deviceId = a.DeviceId;
					string name = a.Gpu.Name;
					GpuVendor vendor2 = c.Vendor;
					uint deviceId2 = c.DeviceId;
					string name2 = c.Name;
					return vendor == vendor2 && deviceId == deviceId2 && name == name2;
				});
			}
		}
		else
		{
			obj = null;
		}
		return (DxgiAdapter?)obj;
	}

	public static IEnumerable<DxgiAdapter> Usable(IReadOnlyList<DxgiAdapter> adapters, bool intel = false)
	{
		return adapters.Where((DxgiAdapter a) =>
		{
			GpuVendor vendor = a.Gpu.Vendor;
			return ((vendor == GpuVendor.Amd || vendor == GpuVendor.Nvidia) ? true : false) || (intel && a.Gpu.Vendor == GpuVendor.Intel);
		});
	}

	public static GpuChoice ChoiceOf(DxgiAdapter a)
	{
		return new GpuChoice(a.Gpu.AdapterLuid, a.Gpu.Vendor, a.DeviceId, a.Gpu.Name);
	}

	public unsafe static IReadOnlyList<DxgiAdapter> Adapters()
	{
		List<DxgiAdapter> list = new List<DxgiAdapter>();
		Dictionary<(uint, uint), string> dictionary = Wine.HostDriverVersions();
		Guid guid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
		if (CreateDXGIFactory1(&guid, out var factory) < 0)
		{
			return list;
		}
		try
		{
			delegate* unmanaged<nint, uint, nint*, int> obj = (delegate* unmanaged<nint, uint, nint*, int>)(*(IntPtr*)((nint)(*(IntPtr*)factory) + (nint)12 * (nint)sizeof(nint)));
			nint num2 = default;
			AdapterDesc1 adapterDesc = default;
			long num4 = default;
			for (uint num = 0u; obj(factory, num, &num2) >= 0; num++)
			{
				nint num3 = *(nint*)num2;
				((delegate* unmanaged<nint, AdapterDesc1*, int>)(*(IntPtr*)(num3 + (nint)10 * (nint)sizeof(nint))))(num2, &adapterDesc);
				Guid guid2 = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
				string driverVersion = ((((delegate* unmanaged<nint, Guid*, long*, int>)(*(IntPtr*)(num3 + (nint)9 * (nint)sizeof(nint))))(num2, &guid2, &num4) >= 0) ? $"{(num4 >> 48) & 0xFFFF}.{(num4 >> 32) & 0xFFFF}.{(num4 >> 16) & 0xFFFF}.{num4 & 0xFFFF}" : "");
				((delegate* unmanaged<nint, uint>)(*(IntPtr*)(num3 + (nint)2 * (nint)sizeof(nint))))(num2);
				if ((adapterDesc.Flags & 2) == 0)
				{
					if (dictionary.TryGetValue((adapterDesc.VendorId, adapterDesc.DeviceId), out var value))
					{
						driverVersion = value;
					}
					GpuVendor vendor = (Enum.IsDefined(typeof(GpuVendor), (int)adapterDesc.VendorId) ? ((GpuVendor)adapterDesc.VendorId) : GpuVendor.Unknown);
					list.Add(new DxgiAdapter(new GpuInfo(vendor, new string(adapterDesc.Description), driverVersion, ((long)adapterDesc.LuidHigh << 32) | adapterDesc.LuidLow, adapterDesc.DedicatedVideoMemory), adapterDesc.DeviceId, adapterDesc.SubSysId));
				}
			}
			return list;
		}
		finally
		{
			((delegate* unmanaged<nint, uint>)(*(IntPtr*)((nint)(*(IntPtr*)factory) + (nint)2 * (nint)sizeof(nint))))(factory);
		}
	}

	[DllImport("dxgi.dll")]
	private unsafe static extern int CreateDXGIFactory1(Guid* riid, out nint factory);
}
