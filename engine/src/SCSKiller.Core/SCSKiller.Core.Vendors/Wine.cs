using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace SCSKiller.Core.Vendors;

public static class Wine
{
	public const int WarmFiles = 8;

	public static bool Running { get; } = NativeLibrary.TryLoad("ntdll.dll", out var handle) && NativeLibrary.TryGetExport(handle, "wine_get_version", out var _);

	public unsafe static string? UnixPath(string windowsPath)
	{
		if (!Running || !NativeLibrary.TryLoad("kernel32.dll", out var handle) || !NativeLibrary.TryGetExport(handle, "wine_get_unix_file_name", out var address))
		{
			return null;
		}
		nint num;
		fixed (char* ptr = windowsPath)
		{
			num = ((delegate* unmanaged<char*, nint>)address)(ptr);
		}
		if (num == 0)
		{
			return null;
		}
		try
		{
			return Marshal.PtrToStringUTF8(num);
		}
		finally
		{
			HeapFree(GetProcessHeap(), 0u, num);
		}
	}

	public unsafe static string? DosPath(string unixPath)
	{
		if (unixPath.Length == 0 || !Running || !NativeLibrary.TryLoad("kernel32.dll", out var handle) || !NativeLibrary.TryGetExport(handle, "wine_get_dos_file_name", out var address))
		{
			return null;
		}
		nint num;
		fixed (byte* bytes = Encoding.UTF8.GetBytes(unixPath + "\0"))
		{
			num = ((delegate* unmanaged<byte*, nint>)address)(bytes);
		}
		if (num == 0)
		{
			return null;
		}
		try
		{
			return Marshal.PtrToStringUni(num);
		}
		finally
		{
			HeapFree(GetProcessHeap(), 0u, num);
		}
	}

	[DllImport("kernel32.dll")]
	private static extern nint GetProcessHeap();

	[DllImport("kernel32.dll")]
	private static extern bool HeapFree(nint heap, uint flags, nint p);

	public static (List<(string Name, string Value)> Warm, List<(string Name, string Value)> Game) CacheEnvironment(string unixRoot, string exe)
	{
		string text = new string(Path.GetFileNameWithoutExtension(exe).Select((char c) =>
		{
			bool flag = char.IsAsciiLetterOrDigit(c);
			if (!flag)
			{
				bool flag2 = ((c == '-' || c == '_') ? true : false);
				flag = flag2;
			}
			return (!flag) ? '_' : c;
		}).ToArray());
		int num = 3;
		List<(string, string)> list = new List<(string, string)>(num);
		CollectionsMarshal.SetCount(list, num);
		Span<(string, string)> span = CollectionsMarshal.AsSpan(list);
		span[0] = ("__GL_SHADER_DISK_CACHE_PATH", unixRoot + "/nvidia");
		span[1] = ("__GL_SHADER_DISK_CACHE_SIZE", "10737418240");
		span[2] = ("VKD3D_SHADER_CACHE_PATH", unixRoot + "/vkd3d");
		List<(string, string)> list2 = list;
		string warm = "scskiller_" + text;
		List<(string, string)> list3 = list2;
		num = 1 + list3.Count;
		List<(string, string)> list4 = new List<(string, string)>(num);
		CollectionsMarshal.SetCount(list4, num);
		Span<(string, string)> span2 = CollectionsMarshal.AsSpan(list4);
		int num2 = 0;
		Span<(string, string)> span3 = CollectionsMarshal.AsSpan(list3);
		span3.CopyTo(span2.Slice(num2, span3.Length));
		num2 += span3.Length;
		span2[num2] = ("__GL_SHADER_DISK_CACHE_APP_NAME", warm);
		list3 = list2;
		num2 = 2 + list3.Count;
		List<(string, string)> list5 = new List<(string, string)>(num2);
		CollectionsMarshal.SetCount(list5, num2);
		Span<(string, string)> span4 = CollectionsMarshal.AsSpan(list5);
		num = 0;
		span3 = CollectionsMarshal.AsSpan(list3);
		span3.CopyTo(span4.Slice(num, span3.Length));
		num += span3.Length;
		span4[num] = ("__GL_SHADER_DISK_CACHE_APP_NAME", text);
		num++;
		span4[num] = ("__GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME", string.Join(';', from i in Enumerable.Range(0, 8)
			select warm + i));
		return (Warm: list4, Game: list5);
	}

	public static string FormatDriverVersion(uint vendorId, uint v)
	{
		if (vendorId == 4318)
		{
			return $"{v >> 22}.{(v >> 14) & 0xFF:00}.{(v >> 6) & 0xFF:00}";
		}
		return $"{v >> 22}.{(v >> 12) & 0x3FF}.{v & 0xFFF}";
	}

	public unsafe static Dictionary<(uint Vendor, uint Device), string> HostDriverVersions()
	{
		Dictionary<(uint, uint), string> dictionary = new Dictionary<(uint, uint), string>();
		if (!Running || !NativeLibrary.TryLoad("vulkan-1.dll", out var handle))
		{
			return dictionary;
		}
		try
		{
			if (!NativeLibrary.TryGetExport(handle, "vkCreateInstance", out var address) || !NativeLibrary.TryGetExport(handle, "vkDestroyInstance", out var address2) || !NativeLibrary.TryGetExport(handle, "vkEnumeratePhysicalDevices", out var address3) || !NativeLibrary.TryGetExport(handle, "vkGetPhysicalDeviceProperties", out var address4))
			{
				return dictionary;
			}
			nint* ptr = stackalloc nint[8];
			new Span<nint>(ptr, 8).Clear();
			*(int*)ptr = 1;
			nint num = default;
			if (((delegate* unmanaged<nint*, void*, nint*, int>)address)(ptr, null, &num) != 0)
			{
				return dictionary;
			}
			try
			{
				uint num2 = 16u;
				nint* ptr2 = stackalloc nint[16];
				if (((delegate* unmanaged<nint, uint*, nint*, int>)address3)(num, &num2, ptr2) < 0)
				{
					return dictionary;
				}
				uint* ptr3 = stackalloc uint[512];
				for (int i = 0; i < num2; i++)
				{
					((delegate* unmanaged<nint, uint*, void>)address4)(ptr2[i], ptr3);
					dictionary[(ptr3[2], ptr3[3])] = FormatDriverVersion(ptr3[2], ptr3[1]);
				}
				return dictionary;
			}
			finally
			{
				((delegate* unmanaged<nint, void*, void>)address2)(num, null);
			}
		}
		finally
		{
			NativeLibrary.Free(handle);
		}
	}
}
