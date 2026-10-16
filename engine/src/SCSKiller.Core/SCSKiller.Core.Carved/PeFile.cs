using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace SCSKiller.Core.Carved;

public static class PeFile
{
	public static PEReader Open(string path)
	{
		return new PEReader(new BufferedStream(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)));
	}

	public static string? ExportName(PEReader pe)
	{
		BlobReader? blobReader = ExportDirectory(pe);
		if (blobReader.HasValue)
		{
			BlobReader valueOrDefault = blobReader.GetValueOrDefault();
			valueOrDefault.Offset = 12;
			string text = Str(pe, valueOrDefault.ReadInt32());
			if (text == null || text.Length <= 0)
			{
				return null;
			}
			return text;
		}
		return null;
	}

	public static IEnumerable<string> ExportNames(PEReader pe)
	{
		BlobReader? blobReader = ExportDirectory(pe);
		if (!blobReader.HasValue)
		{
			yield break;
		}
		BlobReader valueOrDefault = blobReader.GetValueOrDefault();
		valueOrDefault.Offset = 24;
		int count = valueOrDefault.ReadInt32();
		valueOrDefault.Offset = 32;
		BlobReader names = Data(pe, valueOrDefault.ReadInt32());
		for (int i = 0; i < count; i++)
		{
			if (names.RemainingBytes < 4)
			{
				break;
			}
			yield return Str(pe, names.ReadInt32());
		}
	}

	public static IEnumerable<string> Forwarders(PEReader pe)
	{
		BlobReader? blobReader = ExportDirectory(pe);
		if (!blobReader.HasValue)
		{
			yield break;
		}
		BlobReader valueOrDefault = blobReader.GetValueOrDefault();
		DirectoryEntry dir = pe.PEHeaders.PEHeader.ExportTableDirectory;
		valueOrDefault.Offset = 20;
		int count = valueOrDefault.ReadInt32();
		valueOrDefault.Offset = 28;
		BlobReader functions = Data(pe, valueOrDefault.ReadInt32());
		for (int i = 0; i < count; i++)
		{
			if (functions.RemainingBytes < 4)
			{
				break;
			}
			int num = functions.ReadInt32();
			if (num >= dir.RelativeVirtualAddress && num < dir.RelativeVirtualAddress + dir.Size)
			{
				yield return Str(pe, num);
			}
		}
	}

	public static IEnumerable<string> ImportedFunctions(PEReader pe, string dll)
	{
		PEHeader pEHeader = pe.PEHeaders.PEHeader;
		if (pEHeader == null || pEHeader.Magic != PEMagic.PE32Plus)
		{
			yield break;
		}
		BlobReader table = Data(pe, pEHeader.ImportTableDirectory.RelativeVirtualAddress);
		while (table.RemainingBytes >= 20)
		{
			int num = table.ReadInt32();
			table.ReadInt32();
			table.ReadInt32();
			int num2 = table.ReadInt32();
			int num3 = table.ReadInt32();
			int num4 = num2;
			int num5 = num;
			if (num4 == 0)
			{
				break;
			}
			if (!Str(pe, num4).Equals(dll, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			BlobReader thunks = Data(pe, (num5 != 0) ? num5 : num3);
			ulong num6;
			while (thunks.RemainingBytes >= 8 && (num6 = thunks.ReadUInt64()) != 0L)
			{
				if (num6 >> 63 == 0L)
				{
					yield return Str(pe, (int)(num6 & 0x7FFFFFFF) + 2);
				}
			}
		}
	}

	public static BlobReader? ExportData(PEReader pe, string name)
	{
		BlobReader? blobReader = ExportDirectory(pe);
		if (blobReader.HasValue)
		{
			BlobReader valueOrDefault = blobReader.GetValueOrDefault();
			valueOrDefault.Offset = 24;
			int num = valueOrDefault.ReadInt32();
			int num2 = valueOrDefault.ReadInt32();
			int num3 = valueOrDefault.ReadInt32();
			int num4 = valueOrDefault.ReadInt32();
			int rva = num3;
			int num5 = num2;
			BlobReader blobReader2 = Data(pe, rva);
			for (int i = 0; i < num; i++)
			{
				if (blobReader2.RemainingBytes < 4)
				{
					break;
				}
				if (Str(pe, blobReader2.ReadInt32()) == name)
				{
					return Data(pe, Data(pe, num5 + 4 * Data(pe, num4 + 2 * i).ReadUInt16()).ReadInt32());
				}
			}
			return null;
		}
		return null;
	}

	public static string? ExportedString(PEReader pe, string name)
	{
		BlobReader? blobReader = ExportData(pe, name);
		if (blobReader.HasValue)
		{
			BlobReader valueOrDefault = blobReader.GetValueOrDefault();
			ulong num = ((pe.PEHeaders.PEHeader.Magic == PEMagic.PE32Plus) ? valueOrDefault.ReadUInt64() : valueOrDefault.ReadUInt32());
			return Str(pe, (int)(num - pe.PEHeaders.PEHeader.ImageBase));
		}
		return null;
	}

	internal static string Str(PEReader pe, int rva)
	{
		BlobReader blobReader = Data(pe, rva);
		StringBuilder stringBuilder = new StringBuilder();
		byte value;
		while (blobReader.RemainingBytes > 0 && stringBuilder.Length < 256 && (value = blobReader.ReadByte()) != 0)
		{
			stringBuilder.Append((char)value);
		}
		return stringBuilder.ToString();
	}

	private static BlobReader? ExportDirectory(PEReader pe)
	{
		BlobReader value = Data(pe, pe.PEHeaders.PEHeader?.ExportTableDirectory.RelativeVirtualAddress ?? 0);
		if (value.RemainingBytes < 40)
		{
			return null;
		}
		return value;
	}

	private static BlobReader Data(PEReader pe, int rva)
	{
		if (rva > 0)
		{
			return pe.GetSectionData(rva).GetReader();
		}
		return default;
	}
}
