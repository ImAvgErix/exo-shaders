using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace SCSKiller.Core.Planning;

public static class PlanFile
{
	private const int Version = 1;

	public static void Write(Plan plan, IEnumerable<PsoDb.Rec> body)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(plan.FilePath));
		string text = plan.FilePath + ".tmp";
		try
		{
			using (FileStream fileStream = File.Create(text))
			{
				WriteHeader(fileStream, "SCSKPLAN"u8, 1, plan);
				using BrotliStream stream = new BrotliStream(fileStream, CompressionLevel.Optimal);
				using BufferedStream s = new BufferedStream(stream, 1048576);
				foreach (PsoDb.Rec item in body)
				{
					PsoDb.Write(s, item.Tag, item.Payload);
				}
			}
			File.Move(text, plan.FilePath, overwrite: true);
		}
		finally
		{
			File.Delete(text);
		}
	}

	public static (Plan Plan, IEnumerable<PsoDb.Rec> Records) Read(string path)
	{
		FileStream f = File.OpenRead(path);
		Plan item;
		try
		{
			FileStream f2 = f;
			DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(14, 2);
			defaultInterpolatedStringHandler.AppendFormatted(path);
			defaultInterpolatedStringHandler.AppendLiteral(": not a v");
			defaultInterpolatedStringHandler.AppendFormatted(1);
			defaultInterpolatedStringHandler.AppendLiteral(" plan");
			item = ReadHeader<Plan>(f2, "SCSKPLAN"u8, 1, defaultInterpolatedStringHandler.ToStringAndClear())with
			{
				FilePath = path
			};
		}
		catch
		{
			f.Dispose();
			throw;
		}
		return (Plan: item, Records: Records());
		IEnumerable<PsoDb.Rec> Records()
		{
			using (f)
			{
				using BrotliStream z = new BrotliStream(f, CompressionMode.Decompress);
				foreach (PsoDb.Rec item2 in PsoDb.Read(new BufferedStream(z, 1048576)))
				{
					yield return item2;
				}
			}
		}
	}

	internal static void WriteHeader<T>(Stream f, ReadOnlySpan<byte> magic, int version, T header)
	{
		byte[] array = JsonSerializer.SerializeToUtf8Bytes(header);
		f.Write(magic);
		f.Write(BitConverter.GetBytes(version));
		f.Write(BitConverter.GetBytes(array.Length));
		f.Write(array);
	}

	internal static T ReadHeader<T>(Stream f, ReadOnlySpan<byte> magic, int version, string mismatch)
	{
		byte[] array = new byte[16];
		f.ReadExactly(array);
		if (!((ReadOnlySpan<byte>)array.AsSpan(0, 8)).SequenceEqual(magic) || BitConverter.ToInt32(array, 8) != version)
		{
			throw new InvalidDataException(mismatch);
		}
		byte[] array2 = new byte[BitConverter.ToInt32(array, 12)];
		f.ReadExactly(array2);
		return JsonSerializer.Deserialize<T>(array2);
	}
}
