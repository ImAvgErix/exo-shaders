using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace SCSKiller.Core.BlackSpace;

/// <summary>BlackSpace's .paz names and encryption: names hash with Bob Jenkins' lookup3 (hashlittle, its seed offset by
/// <see cref="Seed"/>); an encrypted entry (Pamt encryption 3) is ChaCha20 with a key derived from the user's key base, the
/// index's key byte and the hash of the entry's lower-case file name, which is also the counter and each nonce word.</summary>
public static class PazCrypto
{
    public const uint Seed = 0xC5EDE;
    /// <summary>The key byte the derivation folds in besides the index's own.</summary>
    public const byte PublishedKeyByte = 0x6D;

    public static uint Hash(string s, uint seed = Seed) => Hash(Encoding.UTF8.GetBytes(s), seed);

    /// <summary>lookup3's hashlittle of <paramref name="k"/> (its initval: <paramref name="seed"/>).</summary>
    public static uint Hash(ReadOnlySpan<byte> k, uint seed = Seed)
    {
        uint a, b, c;
        a = b = c = 0xDEADBEEF + (uint)k.Length + seed;
        for (; k.Length > 12; k = k[12..])
        {
            a += BinaryPrimitives.ReadUInt32LittleEndian(k);
            b += BinaryPrimitives.ReadUInt32LittleEndian(k[4..]);
            c += BinaryPrimitives.ReadUInt32LittleEndian(k[8..]);
            a -= c; a ^= BitOperations.RotateLeft(c, 4); c += b;
            b -= a; b ^= BitOperations.RotateLeft(a, 6); a += c;
            c -= b; c ^= BitOperations.RotateLeft(b, 8); b += a;
            a -= c; a ^= BitOperations.RotateLeft(c, 16); c += b;
            b -= a; b ^= BitOperations.RotateLeft(a, 19); a += c;
            c -= b; c ^= BitOperations.RotateLeft(b, 4); b += a;
        }
        if (k.IsEmpty) return c;
        Span<byte> tail = stackalloc byte[12];
        tail.Clear();
        k.CopyTo(tail);
        a += BinaryPrimitives.ReadUInt32LittleEndian(tail);
        b += BinaryPrimitives.ReadUInt32LittleEndian(tail[4..]);
        c += BinaryPrimitives.ReadUInt32LittleEndian(tail[8..]);
        c ^= b; c -= BitOperations.RotateLeft(b, 14);
        a ^= c; a -= BitOperations.RotateLeft(c, 11);
        b ^= a; b -= BitOperations.RotateLeft(a, 25);
        c ^= b; c -= BitOperations.RotateLeft(b, 16);
        a ^= c; a -= BitOperations.RotateLeft(c, 4);
        b ^= a; b -= BitOperations.RotateLeft(a, 14);
        c ^= b; return c - BitOperations.RotateLeft(b, 24);
    }

    /// <summary>Decrypts an entry in place. <paramref name="fileName"/>: its path in the index (only the file name counts).</summary>
    public static void Decrypt(Span<byte> data, string fileName, ReadOnlySpan<byte> keyBase, byte keyByte)
    {
        if (keyBase.Length != BlackSpaceKeys.Size) throw new ArgumentException("a 32-byte base", nameof(keyBase));
        var hash = Hash(Path.GetFileName(fileName.Replace('\\', '/')).ToLowerInvariant());
        Span<byte> h = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(h, hash);
        Span<byte> bytes = stackalloc byte[32];
        for (var i = 0; i < 32; i++) bytes[i] = (byte)(keyBase[i] ^ h[i % 4] ^ keyByte ^ PublishedKeyByte);
        Span<uint> key = stackalloc uint[8];
        for (var i = 0; i < 8; i++) key[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(4 * i)..]);
        ReadOnlySpan<uint> nonce = [hash, hash, hash];
        ChaCha20(data, key, hash, nonce);
    }

    /// <summary>RFC 8439 ChaCha20 (20 rounds, a 32-bit block counter starting at <paramref name="counter"/>), XORed over <paramref name="data"/>.</summary>
    public static void ChaCha20(Span<byte> data, ReadOnlySpan<uint> key, uint counter, ReadOnlySpan<uint> nonce)
    {
        Span<uint> state = stackalloc uint[16];
        Span<uint> x = stackalloc uint[16];
        Span<byte> stream = stackalloc byte[64];
        state[0] = 0x61707865; state[1] = 0x3320646E; state[2] = 0x79622D32; state[3] = 0x6B206574;   // "expand 32-byte k"
        key.CopyTo(state[4..]);
        nonce.CopyTo(state[13..]);
        for (var at = 0; at < data.Length; at += 64, counter++)
        {
            state[12] = counter;
            state.CopyTo(x);
            for (var i = 0; i < 10; i++)
            {
                Quarter(x, 0, 4, 8, 12); Quarter(x, 1, 5, 9, 13); Quarter(x, 2, 6, 10, 14); Quarter(x, 3, 7, 11, 15);
                Quarter(x, 0, 5, 10, 15); Quarter(x, 1, 6, 11, 12); Quarter(x, 2, 7, 8, 13); Quarter(x, 3, 4, 9, 14);
            }
            for (var i = 0; i < 16; i++) BinaryPrimitives.WriteUInt32LittleEndian(stream[(4 * i)..], x[i] + state[i]);
            var block = data[at..Math.Min(data.Length, at + 64)];
            for (var i = 0; i < block.Length; i++) block[i] ^= stream[i];
        }
    }

    static void Quarter(Span<uint> x, int a, int b, int c, int d)
    {
        x[a] += x[b]; x[d] = BitOperations.RotateLeft(x[d] ^ x[a], 16);
        x[c] += x[d]; x[b] = BitOperations.RotateLeft(x[b] ^ x[c], 12);
        x[a] += x[b]; x[d] = BitOperations.RotateLeft(x[d] ^ x[a], 8);
        x[c] += x[d]; x[b] = BitOperations.RotateLeft(x[b] ^ x[c], 7);
    }
}
