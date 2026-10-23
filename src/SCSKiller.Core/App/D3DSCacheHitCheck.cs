using System.Runtime.InteropServices;

namespace SCSKiller.Core.App;

/// <summary>A game's adapter db right after the fold that put a warm's dbs in: <see cref="Db"/> in D3DSCache folder
/// <see cref="Folder"/>, its highest keyid and key count, and <see cref="Sample"/> (hex keys spread over its keyids) to tell
/// later whether the runtime emptied it.</summary>
public sealed record FoldKeys(string Folder, string Db, long MaxKeyId, long Count, string[] Sample, DateTimeOffset At);

/// <summary>The first run after a warm's fold, read from the game's adapter db at its exit. <see cref="Added"/>: keys the game
/// inserted (a miss count: pipelines the compile's keys didn't hold, from coverage gaps and key mismatches, never a hit share,
/// which the db can't show). <see cref="Kept"/> of <see cref="Sampled"/>: the fold's sample keys still there, none of them
/// meaning the runtime emptied the db. <see cref="AtFold"/>: keys the db held after the fold; <see cref="Recorded"/>,
/// <see cref="BuiltOk"/>/<see cref="BuiltN"/>: the plan's recorded pipelines and root signatures its rule rebuilt exactly.</summary>
public sealed record HitCheck(long AtFold, long Added, int Kept, int Sampled, long Seconds, long Recorded, long? BuiltOk, long? BuiltN);

public static partial class D3DSCache
{
    const int SampleKeys = 8;

    /// <summary>The adapter db of <paramref name="dir"/> with the most keys, read right after a fold left it without a WAL; null
    /// when there is none, it has a WAL, or it can't be read.</summary>
    public static FoldKeys? FoldKeysOf(string dir, DateTimeOffset at)
    {
        FoldKeys? best = null;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*_VEN_*.dxcache"))
            {
                if (File.Exists(f + "-wal") && new FileInfo(f + "-wal").Length > 0) return null;
                long max = 0, count = 0;
                if (!Query(f, "select coalesce(max(keyid), 0), count(*) from keys", st => (max, count) = (sqlite3_column_int64(st, 0), sqlite3_column_int64(st, 1)))) return null;
                if (count == 0 || best != null && best.Count >= count) continue;
                // keyids rise with each insert, so evenly spaced ones are spread over the whole db
                var sample = new List<string>();
                for (var i = 1; i <= SampleKeys; i++)
                    if (!Query(f, $"select hex(key) from keys where keyid >= {max * i / SampleKeys} order by keyid limit 1",
                            st => sample.Add(Marshal.PtrToStringUTF8(sqlite3_column_text(st, 0)) ?? ""))) return null;
                best = new(Path.GetFileName(dir), Path.GetFileName(f), max, count, [.. sample.Where(s => s.Length > 0).Distinct()], at);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        return best;
    }

    /// <summary>The queries of a run's hit check on the db <see cref="FoldKeys"/> named, for <see cref="PostExit"/>: keys added
    /// since the fold (keyid past its highest: SQLite gives a new row the highest keyid plus one) and its sample keys still there.</summary>
    public sealed class HitCheckRead
    {
        readonly FoldKeys k;
        long added = -1, kept = -1;

        public HitCheckRead(FoldKeys k)
        {
            this.k = k;
            Queries = k.Sample.Length == 0 || k.Sample.Any(s => s.Length == 0 || s.Length % 2 != 0 || !s.All(char.IsAsciiHexDigit)) ? [] :
            [
                new($"select count(*) from keys where keyid > {k.MaxKeyId}", r => added = r.Long(0)),
                new($"select count(*) from keys where keyid <= {k.MaxKeyId} and key in ({string.Join(", ", k.Sample.Select(s => $"x'{s}'"))})", r => kept = r.Long(0)),
            ];
        }

        public DbQuery[] Queries { get; }

        /// <summary>The hit check of <paramref name="run"/> once its queries ran; null when one didn't.</summary>
        public HitCheck? Of(PlayWindow run, PlanStats stats) => added < 0 || kept < 0 ? null
            : new(k.Count, added, (int)kept, k.Sample.Length, (long)(run.To - run.From).TotalSeconds, stats.Recorded, stats.BuiltOk, stats.BuiltN);
    }
}
