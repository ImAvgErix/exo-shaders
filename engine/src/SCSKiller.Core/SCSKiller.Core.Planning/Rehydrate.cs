using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using SCSKiller.Core.RedEngine;

namespace SCSKiller.Core.Planning;

public static class Rehydrate
{
	public static HashSet<string> References(IEnumerable<PsoDb.Rec> records)
	{
		HashSet<string> hashSet = new HashSet<string>();
		foreach (PsoDb.Rec record in records)
		{
			switch (record.Tag)
			{
			case 'C':
			case 'G':
			case 'S':
			{
				PsoDb.Pso pso = PsoDb.Parse(record);
				hashSet.UnionWith(pso.Stages.Values);
				hashSet.Add(pso.Rs);
				break;
			}
			case 'P':
			{
				(string Template, string Rs, SortedDictionary<int, string> Stages) tuple = PsoDb.ParseItem(record.Payload);
				string item = tuple.Rs;
				SortedDictionary<int, string> item2 = tuple.Stages;
				hashSet.UnionWith(item2.Values);
				hashSet.Add(item);
				break;
			}
			case '1':
				hashSet.Add(PsoDb.Hex(record.Payload.AsSpan(4, 20)));
				break;
			case '2':
				hashSet.Add(PsoDb.Hex(record.Payload.AsSpan(0, 20)));
				hashSet.Add(PsoDb.Hex(record.Payload.AsSpan(20, 20)));
				break;
			case '3':
			case 'D':
			{
				PsoDb.Draw11 draw = PsoDb.ParseDraw11(record.Payload);
				hashSet.UnionWith(new _003C_003Ez__ReadOnlyArray<string>(new string[4] { draw.Vs, draw.Gs, draw.Hs, draw.Ds }));
				break;
			}
			case 'A':
			case 'R':
			{
				PsoDb.StateObject stateObject = PsoDb.ParseStateObject(record);
				hashSet.UnionWith(stateObject.Libraries);
				hashSet.UnionWith(stateObject.RootSignatures);
				break;
			}
			case 'Y':
			{
				RtCollections.ItemFields itemFields2 = RtCollections.ParseItem(record.Payload);
				hashSet.UnionWith(new _003C_003Ez__ReadOnlyArray<string>(new string[4] { itemFields2.Library, itemFields2.Global, itemFields2.LocalRayGen, itemFields2.LocalOther }));
				break;
			}
			case 'H':
			{
				RedRayTracing.ItemFields itemFields = RedRayTracing.ParseItem(record.Payload);
				hashSet.UnionWith(new _003C_003Ez__ReadOnlyArray<string>(new string[4]
				{
					itemFields.ClosestHit,
					itemFields.AnyHit ?? PsoDb.Zero,
					itemFields.Shape.Global,
					itemFields.Local
				}));
				break;
			}
			}
		}
		hashSet.Remove(PsoDb.Zero);
		return hashSet;
	}

	public static RehydrateResult Run(string hashOnlyDb, string outDb, Game game, EngineInfo engine, IEngineReader reader, ShaderIndex? index = null, string? expectedContentHash = null, CancellationToken ct = default(CancellationToken), Func<IReadOnlySet<string>, IEnumerable<(string Sha1, byte[] Bytes)>>? moreBlobs = null)
	{
		HashSet<string> have = (from r in PsoDb.Read(hashOnlyDb)
			where r.Tag == 'B'
			select PsoDb.Hex(r.Payload.AsSpan(0, 20))).ToHashSet();
		HashSet<string> hashSet = References(PsoDb.Read(hashOnlyDb));
		HashSet<string> want = hashSet.Where((string h) => !have.Contains(h)).ToHashSet();
		string? directoryName = Path.GetDirectoryName(Path.GetFullPath(outDb));
		Directory.CreateDirectory(directoryName);
		string text = Path.Combine(directoryName, Path.GetFileName(outDb) + ".tmp");
		HashSet<string> found = new HashSet<string>();
		int num = 0;
		try
		{
			BufferedStream f = new BufferedStream(File.Create(text), 1048576);
			try
			{
				foreach (PsoDb.Rec item in from r in PsoDb.Read(hashOnlyDb)
					where r.Tag == 'B'
					select r)
				{
					PsoDb.Write(f, 'B', item.Payload);
				}
				if (want.Count > 0)
				{
					reader.ReadShaders(game, engine, want, (string h, byte[] b) =>
					{
						lock (found)
						{
							if (want.Contains(h) && found.Add(h))
							{
								PsoDb.WriteBlob(f, h, b);
							}
						}
					}, ct);
				}
				if (moreBlobs != null)
				{
					HashSet<string> hashSet2 = want.Where((string h) => !found.Contains(h)).ToHashSet();
					if (hashSet2 != null && hashSet2.Count > 0)
					{
						foreach (var (text2, array) in moreBlobs(hashSet2))
						{
							ct.ThrowIfCancellationRequested();
							if (hashSet2.Contains(text2) && !found.Contains(text2) && !(PsoDb.Hex(SHA1.HashData(array)) != text2))
							{
								found.Add(text2);
								PsoDb.WriteBlob(f, text2, array);
								num++;
							}
						}
					}
				}
				foreach (PsoDb.Rec item2 in from r in PsoDb.Read(hashOnlyDb)
					where r.Tag != 'B'
					select r)
				{
					PsoDb.Write(f, item2.Tag, item2.Payload);
				}
			}
			finally
			{
				if (f != null)
				{
					((IDisposable)f).Dispose();
				}
			}
			File.Move(text, outDb, overwrite: true);
		}
		finally
		{
			File.Delete(text);
		}
		List<string> missing = want.Where((string h) => !found.Contains(h)).Order(StringComparer.Ordinal).ToList();
		bool? contentHashMatches = ((index != null && expectedContentHash != null) ? new bool?(string.Equals(index.ContentHash, expectedContentHash, StringComparison.OrdinalIgnoreCase)) : ((bool?)null));
		return new RehydrateResult(hashSet.Count, hashSet.Count - want.Count, found.Count, missing, contentHashMatches, new FileInfo(outDb).Length, num);
	}
}
