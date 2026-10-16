using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core.Planning;
using SCSKiller.Core.RedEngine;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.FromSoft;

public static class SoulsRayTracing
{
	public const string Bundle = "_[RT].shaderbdle";

	public static readonly (string Hash, byte[] Blob) Global;

	public static readonly (string Hash, byte[] Blob) Local;

	public static readonly RedRayTracing.Shape Shape;

	public static bool IsPair(ShaderMap m)
	{
		if (m.Library.Contains("_[RT].shaderbdle|p", StringComparison.OrdinalIgnoreCase))
		{
			return m.Shaders.Count == 2;
		}
		return false;
	}

	private static (string, byte[]) Version10(RootSig.Desc d)
	{
		byte[] array = RootSig.AsVersion10(RootSig.Serialize(d, Array.Empty<byte>()));
		return (Convert.ToHexStringLower(SHA1.HashData(array)), array);
	}

	public static byte[]? Collection(ReadOnlySpan<byte> first, RedRayTracing.ItemFields f, ReadOnlySpan<byte> second, string? material = null)
	{
		if (f.AnyHit == null)
		{
			return null;
		}
		List<ShaderContainer.LibraryFunction> item = ShaderContainer.Rdat(first).Functions;
		List<ShaderContainer.LibraryFunction> item2 = ShaderContainer.Rdat(second).Functions;
		List<ShaderContainer.LibraryFunction> list = item;
		if (list.Count != 1 || item2.Count != 1)
		{
			return null;
		}
		string text;
		ShaderContainer.LibraryFunction libraryFunction;
		string h;
		ShaderContainer.LibraryFunction libraryFunction2;
		if (list[0].Kind != 10 || item2[0].Kind != 9)
		{
			((text, libraryFunction), (h, libraryFunction2)) = ((list[0].Kind == 9 && item2[0].Kind == 10) ? ((f.AnyHit, item2[0]), (f.ClosestHit, list[0])) : default(((string, ShaderContainer.LibraryFunction), (string, ShaderContainer.LibraryFunction))));
		}
		else
		{
			string closestHit = f.ClosestHit;
			ShaderContainer.LibraryFunction libraryFunction3 = list[0];
			string anyHit = f.AnyHit;
			ShaderContainer.LibraryFunction libraryFunction4 = item2[0];
			libraryFunction2 = libraryFunction4;
			h = anyHit;
			libraryFunction = libraryFunction3;
			text = closestHit;
		}
		if (libraryFunction == null)
		{
			return null;
		}
		string text2 = (material ?? text.Substring(0, 16)) + "_RayTracing_";
		string text3 = ((libraryFunction.Payload == 4) ? "[AO]" : "[Rad]");
		string s = text2 + "[ClosestHit]_" + text3;
		string s2 = text2 + "[AnyHit]_" + text3;
		string text4 = "HitGroup_" + (material ?? text.Substring(0, 16)) + "_" + text3;
		MemoryStream w = new MemoryStream();
		U(0u);
		U(9u);
		U(9u);
		U(f.Shape.Payload);
		U(f.Shape.Attributes);
		U(10u);
		U(f.Shape.Depth);
		U(1u);
		Hash(f.Shape.Global);
		U(5u);
		Hash(text);
		U(1u);
		Str(s);
		Str(libraryFunction.Name);
		U(0u);
		U(5u);
		Hash(h);
		U(1u);
		Str(s2);
		Str(libraryFunction2.Name);
		U(0u);
		U(11u);
		Str(text4);
		U(0u);
		Str(null);
		Str(s);
		Str(null);
		U(11u);
		Str(text4 + "_[A]");
		U(0u);
		Str(s2);
		Str(s);
		Str(null);
		U(2u);
		Hash(f.Local);
		U(7u);
		U(7u);
		U(2u);
		Str(text4);
		Str(text4 + "_[A]");
		return w.ToArray();
		void Hash(string s3)
		{
			w.Write(Convert.FromHexString(s3));
		}
		void Str(string? text5)
		{
			if (text5 == null)
			{
				U(uint.MaxValue);
			}
			else
			{
				U((uint)text5.Length);
				w.Write(Encoding.Unicode.GetBytes(text5));
			}
		}
		void U(uint v)
		{
			Span<byte> span = stackalloc byte[4];
			BinaryPrimitives.WriteUInt32LittleEndian(span, v);
			w.Write(span);
		}
	}

	static SoulsRayTracing()
	{
		int num = 4;
		List<uint[]> list = new List<uint[]>(num);
		CollectionsMarshal.SetCount(list, num);
		Span<uint[]> span = CollectionsMarshal.AsSpan(list);
		span[0] = new uint[12]
		{
			0u, 0u, 2u, 1u, 0u, 0u, 0u, 2u, 2u, 8u,
			0u, 0u
		};
		span[1] = new uint[7] { 0u, 0u, 0u, 3u, 64u, 0u, 0u };
		span[2] = new uint[7] { 0u, 0u, 1u, 4u, 0u, 0u, 0u };
		span[3] = new uint[7] { 0u, 0u, 3u, 16u, 0u, 0u, 0u };
		Global = Version10(new RootSig.Desc(0u, list, AppendRanges: true));
		num = 3;
		list = new List<uint[]>(num);
		CollectionsMarshal.SetCount(list, num);
		Span<uint[]> span2 = CollectionsMarshal.AsSpan(list);
		span2[0] = new uint[12]
		{
			0u, 0u, 2u, 1u, 16u, 0u, 0u, 2u, 3u, 1u,
			0u, 0u
		};
		span2[1] = new uint[7] { 0u, 0u, 0u, 4u, 67u, 0u, 0u };
		span2[2] = new uint[7] { 0u, 0u, 0u, 25u, 0u, 0u, 0u };
		Local = Version10(new RootSig.Desc(128u, list, AppendRanges: true));
		Shape = new RedRayTracing.Shape(Global.Hash, 12u, 8u, 1u, 0u);
	}
}
