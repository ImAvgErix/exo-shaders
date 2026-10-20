using System.Buffers.Binary;
using System.Runtime;
using System.Security.Cryptography;
using SCSKiller.Core.Planning;
using Xunit.Abstractions;

namespace SCSKiller.Tests.Planning;

/// <summary>The managed heap is the process's: measured with nothing else running.</summary>
[Collection(TimingCollection.Name)]
public class ReplayMemoryTests(ITestOutputHelper output)
{
    static long Live()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        return GC.GetTotalMemory(true);
    }

    [Fact]
    public void Reading_a_warm_s_inputs_keeps_no_shader_bytes()
    {
        var db = Path.Combine(Ff7.TempDir("replay-memory"), "big.db");
        var blob = new byte[1 << 20];
        using (var f = File.Create(db))
            for (var i = 0; i < 64; i++)   // 64 MB of shaders built at run time, each after the PSO that names it
            {
                BinaryPrimitives.WriteInt32LittleEndian(blob, i);
                var sha = PsoDb.Hex(SHA1.HashData(blob));
                PsoDb.Write(f, 'C', PsoDb.Compute(PsoDb.Zero, sha));
                PsoDb.WriteBlob(f, sha, blob);
            }
        // what is live, not what the GC hasn't collected yet (1 MB blobs are large objects, collected late): measured at each
        // PSO, which asks for its shader before reading it, with the blobs read so far either kept or garbage
        var before = Live();
        long peak = 0;
        var keys = SCSKiller.Core.App.WarmInputs.Of([db], null, [], _ =>
        {
            peak = Math.Max(peak, Live() - before);
            return false;
        });
        output.WriteLine($"peak live growth {peak >> 20} MB for 64 MB of blobs");
        Assert.Equal(64, keys.Count);
        Assert.All(keys, k => Assert.Equal(40, k.Length));   // each shader found later in the recording
        Assert.True(peak < 16 << 20);
    }
}
