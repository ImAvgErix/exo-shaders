using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SCSKiller.Core.Vendors;

/// <summary>
/// A state object database (SODB, DirectX-Specs StateObjectDatabase.md) of a game's pipelines, written from a compile's
/// work folder (<c>scskiller.db</c> + <c>scskiller_gen.db</c>, the proxy's db format: <see cref="Planning.PsoDb"/>): every
/// PSO record ('G' graphics desc, 'C' compute desc, 'S' pipeline stream) and plan item ('P', its template with the item's
/// root signature, stages and input layout), each as a pipeline stream with its root signature serialized, which is what
/// an SODB keeps instead of an ID3D12RootSignature. A game's create of the same desc then hits the compiled database
/// whether it passes a desc or a stream (measured on driver 621.01: 6 of 6 lookups from the database).
/// Left out: state objects ('R' / 'A'), whose collections an SODB names by key; PSOs created with an NVAPI state ('N'),
/// which a database can't carry; D3D11 items. The payloads are read as proxy/proxy.cpp's Reader reads them.
/// </summary>
public static unsafe class Sodb
{
    /// <summary>PSOs stored, and PSOs left out (a shader or root signature not in the work folder, a desc it couldn't read,
    /// or one the database refused).</summary>
    public readonly record struct Result(int Stored, int Skipped);

    /// <summary>Writes <paramref name="path"/> (replaced) from <paramref name="dbs"/> with <paramref name="compilerDll"/>
    /// (<see cref="AsdTools.Dll"/>), naming the application <paramref name="exeName"/> / <paramref name="name"/>, as the
    /// registration does.</summary>
    public static Result Write(string compilerDll, string path, string exeName, string name, IReadOnlyList<string> dbs, CancellationToken ct = default)
    {
        var index = Index(dbs);
        var views = new List<(MemoryMappedFile File, MemoryMappedViewAccessor View, nint Base)>();
        nint lib = 0, factory = 0, db = 0, db1 = 0;
        try
        {
            foreach (var f in dbs)
            {
                if (new FileInfo(f).Length == 0) { views.Add((null!, null!, 0)); continue; }
                var mm = MemoryMappedFile.CreateFromFile(new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete), null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, false);
                var view = mm.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                byte* b = null;
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref b);
                views.Add((mm, view, (nint)(b + view.PointerOffset)));
            }
            lib = NativeLibrary.Load(compilerDll);
            var get = (delegate* unmanaged<Guid*, Guid*, nint*, int>)NativeLibrary.GetExport(lib, "D3D12CompilerGetInterface");
            var clsid = ClsidStateObjectFactory; var iid = IidFactory;
            Check(get(&clsid, &iid, &factory), "D3D12CompilerGetInterface");
            File.Delete(path);
            foreach (var side in new[] { "-wal", "-shm" }) File.Delete(path + side);
            var dbIid = IidDatabase;
            fixed (char* p = path) Check(((delegate* unmanaged<nint, char*, uint, Guid*, nint*, int>)Vt(factory)[3])(factory, p, 0, &dbIid, &db), "CreateStateObjectDatabaseFromFile");
            fixed (char* exe = exeName) fixed (char* n = name)
            {
                var desc = new AppDesc { Exe = exe, Name = n, Version = 0 };
                Check(((delegate* unmanaged<nint, AppDesc*, int>)Vt(db)[3])(db, &desc), "SetApplicationDesc");
            }
            var db1Iid = IidDatabase1;
            if (((delegate* unmanaged<nint, Guid*, nint*, int>)Vt(db)[0])(db, &db1Iid, &db1) < 0) db1 = 0;   // transactions: one per batch, not per PSO

            int stored = 0, skipped = 0, batch = 0;
            var done = new HashSet<string>();
            using var stream = new StreamWriter(index, views);
            foreach (var (key, rec, item) in index.Psos)
            {
                ct.ThrowIfCancellationRequested();
                if (!done.Add(key)) continue;
                if (db1 != 0 && batch == 0) ((delegate* unmanaged<nint, int, int>)Vt(db1)[16])(db1, 1);   // D3D12_TRANSACTION_TYPE_IMMEDIATE
                try
                {
                    var k = Convert.FromHexString(key);
                    var sd = new StreamDesc { Size = (nuint)stream.Length, Stream = stream.Stream };
                    bool ok;
                    fixed (byte* kp = k)
                        ok = stream.Build(rec, item) && (sd.Size = (nuint)stream.Length) > 0
                            && ((delegate* unmanaged<nint, byte*, uint, uint, StreamDesc*, int>)Vt(db)[5])(db, kp, (uint)k.Length, 1, &sd) >= 0;
                    if (ok) stored++; else skipped++;
                }
                finally { stream.Free(); }
                if (db1 != 0 && ++batch == 1000) { ((delegate* unmanaged<nint, int>)Vt(db1)[17])(db1); batch = 0; }
            }
            if (db1 != 0)
            {
                if (batch > 0) ((delegate* unmanaged<nint, int>)Vt(db1)[17])(db1);
                // TRUNCATE, then the rollback journal: one file, no -wal beside it for the compiler and the runtime to find
                ((delegate* unmanaged<nint, int, int, int*, int>)Vt(db1)[20])(db1, 3, 1, null);
            }
            return new(stored, skipped + index.Unreadable);
        }
        finally
        {
            Release(db1); Release(db); Release(factory);
            foreach (var (mm, view, b) in views)
            {
                if (b == 0) continue;
                view.SafeMemoryMappedViewHandle.ReleasePointer();
                view.Dispose(); mm.Dispose();
            }
            // the DLL stays loaded: a database it handed out may still be closing on its threads
        }
    }

    /// <summary>The blobs (file, offset, length of the bytes after the SHA-1) and PSO records of the work folder, and the
    /// keys created with an NVAPI state, which are left out.</summary>
    sealed class WorkIndex
    {
        public readonly Dictionary<string, (int File, long At, int Length)> Blobs = new();
        public readonly Dictionary<string, Planning.PsoDb.Rec> Templates = new();
        public readonly List<(string Key, Planning.PsoDb.Rec Rec, Planning.PsoDb.Rec? Item)> Psos = [];
        public int Unreadable;
    }

    static WorkIndex Index(IReadOnlyList<string> dbs)
    {
        var x = new WorkIndex();
        var nv = new HashSet<string>();
        var records = new List<Planning.PsoDb.Rec>();
        for (var i = 0; i < dbs.Count; i++)
        {
            using var f = new BufferedStream(new FileStream(dbs[i], FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete), 1 << 20);
            var head = new byte[5];
            long at = 0, length = f.Length;
            while (f.ReadAtLeast(head, 5, false) == 5)
            {
                var len = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(1));
                at += 5;
                if (len > length - at) break;   // a torn tail, as the proxy stops
                if (head[0] == 'B')
                {
                    if (len < 20) break;
                    var sha = new byte[20];
                    f.ReadExactly(sha);
                    x.Blobs.TryAdd(Convert.ToHexStringLower(sha), (i, at + 20, (int)(len - 20)));
                    f.Seek(len - 20, SeekOrigin.Current);
                }
                else if (head[0] is (byte)'G' or (byte)'C' or (byte)'S' or (byte)'P' or (byte)'N')
                {
                    var body = new byte[len];
                    f.ReadExactly(body);
                    var r = new Planning.PsoDb.Rec((char)head[0], body);
                    if (r.Tag == 'N') { if (len >= 20) nv.Add(Convert.ToHexStringLower(body.AsSpan(0, 20))); }
                    else records.Add(r);
                }
                else f.Seek(len, SeekOrigin.Current);
                at += len;
            }
        }
        foreach (var r in records) if (r.Tag != 'P') x.Templates.TryAdd(r.Key, r);
        foreach (var r in records)
        {
            var key = r.Key;
            if (nv.Contains(key)) continue;
            if (r.Tag != 'P') { x.Psos.Add((key, r, null)); continue; }
            if (r.Payload.Length < 44 || !x.Templates.TryGetValue(Convert.ToHexStringLower(r.Payload.AsSpan(0, 20)), out var t)) { x.Unreadable++; continue; }
            x.Psos.Add((key, t, r));
        }
        return x;
    }

    /// <summary>One PSO's pipeline stream at a time: subobjects laid out as D3D12 reads them (CD3DX12's alignas(void*)
    /// { type; inner } pairs), pointing into the mapped work files and into native memory it frees after each.</summary>
    sealed class StreamWriter(WorkIndex index, List<(MemoryMappedFile File, MemoryMappedViewAccessor View, nint Base)> views) : IDisposable
    {
        // a stream is at most ~30 subobjects of up to 336 bytes each
        const int Capacity = 16 << 10;
        public readonly byte* Stream = (byte*)NativeMemory.AllocZeroed(Capacity);
        readonly byte* Spare = (byte*)NativeMemory.AllocZeroed(512);   // where a subobject past Capacity is written and dropped
        public int Length;
        readonly List<nint> _native = [];
        byte[] _p = [];
        int _pos;
        bool _ok;
        // a plan item's override ('P': template key, root signature, stages, input layout)
        string? _rs;
        readonly Dictionary<uint, string> _sh = [];
        byte[]? _il;

        public void Dispose()
        {
            Free();
            NativeMemory.Free(Stream);
            NativeMemory.Free(Spare);
        }

        public void Free()
        {
            foreach (var p in _native) NativeMemory.Free((void*)p);
            _native.Clear();
        }

        public bool Build(Planning.PsoDb.Rec rec, Planning.PsoDb.Rec? item)
        {
            (Length, _ok, _rs, _il) = (0, true, null, null);
            _sh.Clear();
            if (item is { } it) Override(it.Payload);
            (_p, _pos) = (rec.Payload, 0);
            if (!_ok) return false;
            switch (rec.Tag)
            {
                case 'G': Graphics(); break;
                case 'C':
                    RootSig();
                    Shader(Cs);
                    Pod(NodeMask, 4);
                    Pod(Flags, 4);
                    break;
                default:
                    for (uint i = 0, n = U32(); i < n && _ok; i++)
                    {
                        var t = U32();
                        Sub(t == SoDecl ? StreamOutput : t, t == SoDecl);
                    }
                    break;
            }
            return _ok;
        }

        void Override(byte[] p)
        {
            (_p, _pos) = (p, 20);
            _rs = Hash();
            for (uint i = 0, n = U32(); i < n && _ok; i++)
            {
                var st = U32();
                var h = Hash();
                if (st < 32 && h != Zero) _sh[st] = h;
            }
            if (_pos + 4 <= p.Length && BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(_pos)) != 0xFFFFFFFF) _il = p[_pos..];
            if (_rs == Zero) _rs = null;
        }

        // io_gfx's order; each field becomes the subobject CD3DX12_PIPELINE_STATE_STREAM makes of it
        void Graphics()
        {
            RootSig();
            foreach (var s in new uint[] { 1, 2, 3, 4, 5 }) Shader(s);   // VS, PS, DS, HS, GS
            Pod(Blend, 328); Pod(SampleMask, 4); Pod(Rasterizer, 44); Pod(DepthStencil, 52);
            InputLayout();
            Pod(IbStripCut, 4); Pod(Topology, 4);
            RtFormats();
            Pod(DsvFormat, 4); Pod(SampleDesc, 8); Pod(NodeMask, 4); Pod(Flags, 4);
            if (_pos < _p.Length) StreamOutputBody();   // older 'G' records end before it
        }

        void Sub(uint t, bool soBody)
        {
            switch (t)
            {
                case RootSignature: RootSig(); break;
                case 1 or 2 or 3 or 4 or 5 or Cs or 24 or 25: Shader(t, keepEmpty: true); break;   // as the game passed it
                case StreamOutput: if (soBody) StreamOutputBody(); else Pod(StreamOutput, 0, 32, 8); break;
                case InputLayoutType: InputLayout(); break;
                case ViewInstancing: ViewInstances(); break;
                case RtFormatsType: RtFormats(); break;
                case CachedPso: _ok = false; break;   // never recorded
                default:
                    if (PodSize(t) is var n and > 0) Pod(t, n);
                    else _ok = false;
                    break;
            }
        }

        static int PodSize(uint t) => t switch
        {
            Blend => 328, SampleMask or IbStripCut or Topology or DsvFormat or NodeMask or Flags => 4, Rasterizer or 27 => 44, 28 => 40,
            DepthStencil => 52, 21 => 56, 26 => 60, SampleDesc => 8, _ => 0,
        };

        void RootSig()
        {
            var h = Hash();
            if (_rs != null) h = _rs;
            if (h == Zero) return;
            if (!Blob(h, out var b, out var n)) { _ok = false; return; }
            var d = Begin(SerializedRootSignature, 16, 8);
            *(byte**)d = b;
            *(nuint*)(d + 8) = (nuint)n;
        }

        void Shader(uint stage, bool keepEmpty = false)
        {
            var h = Hash();
            if (_sh.TryGetValue(stage, out var o)) h = o;
            if (!_ok || h == Zero && !keepEmpty) return;
            byte* b = null;
            var n = 0;
            if (h != Zero && !Blob(h, out b, out n)) { _ok = false; return; }
            var d = Begin(stage, 16, 8);
            *(byte**)d = b;
            *(nuint*)(d + 8) = (nuint)n;
        }

        void Pod(uint t, int n) => Pod(t, n, n, 4);

        void Pod(uint t, int read, int size, int align)
        {
            if (_pos + read > _p.Length) { _ok = false; return; }
            var d = Begin(t, size, align);
            new ReadOnlySpan<byte>(_p, _pos, read).CopyTo(new Span<byte>(d, size));
            _pos += read;
        }

        void RtFormats()   // u32 count + 8 formats; D3D12_RT_FORMAT_ARRAY is the formats, then the count
        {
            var n = U32();
            if (_pos + 32 > _p.Length) { _ok = false; return; }
            var d = Begin(RtFormatsType, 36, 4);
            new ReadOnlySpan<byte>(_p, _pos, 32).CopyTo(new Span<byte>(d, 32));
            *(uint*)(d + 32) = n;
            _pos += 32;
        }

        void InputLayout()
        {
            var (p, pos) = (_p, _pos);
            var elems = Layout();
            if (_il != null)   // the item's own: the template's was read past
            {
                var back = _pos;
                (_p, _pos) = (_il, 0);
                elems = Layout();
                (_p, _pos) = (p, back);
            }
            if (!_ok) return;
            var d = Begin(InputLayoutType, 16, 8);
            *(nint*)d = elems.Ptr;
            *(uint*)(d + 8) = elems.Count;
        }

        (nint Ptr, uint Count) Layout()
        {
            var n = Count(32);
            var e = (byte*)Alloc(32 * (int)Math.Max(n, 1));
            for (var i = 0; i < n && _ok; i++)
            {
                var name = Str(U32());
                var at = e + 32 * i;
                *(nint*)at = name;
                for (var f = 0; f < 6; f++) *(uint*)(at + 8 + 4 * f) = U32();   // index, format, slot, offset, class, step rate
            }
            return (n == 0 ? 0 : (nint)e, n);
        }

        void StreamOutputBody()
        {
            var n = Count(24);
            var e = (byte*)Alloc(24 * (int)Math.Max(n, 1));
            for (var i = 0; i < n && _ok; i++)
            {
                var at = e + 24 * i;
                *(uint*)at = U32();
                var len = U32();
                *(nint*)(at + 8) = len == 0xFFFFFFFF ? 0 : Str(len);
                *(uint*)(at + 16) = U32();
                at[20] = (byte)U32(); at[21] = (byte)U32(); at[22] = (byte)U32();
            }
            var ns = Count(4);
            var s = (uint*)Alloc(4 * (int)Math.Max(ns, 1));
            for (var i = 0; i < ns && _ok; i++) s[i] = U32();
            var rasterized = U32();
            if (!_ok) return;
            var d = Begin(StreamOutput, 32, 8);
            *(nint*)d = n == 0 ? 0 : (nint)e;
            *(uint*)(d + 8) = n;
            *(nint*)(d + 16) = ns == 0 ? 0 : (nint)s;
            *(uint*)(d + 24) = ns;
            *(uint*)(d + 28) = rasterized;
        }

        void ViewInstances()
        {
            var n = Count(8);
            var l = (byte*)Alloc(8 * (int)Math.Max(n, 1));
            for (var i = 0; i < n && _ok; i++) { *(uint*)(l + 8 * i) = U32(); *(uint*)(l + 8 * i + 4) = U32(); }
            var flags = U32();
            if (!_ok) return;
            var d = Begin(ViewInstancing, 24, 8);
            *(uint*)d = n;
            *(nint*)(d + 8) = n == 0 ? 0 : (nint)l;
            *(uint*)(d + 16) = flags;
        }

        /// <summary>The next subobject's inner struct (zeroed), after its type.</summary>
        byte* Begin(uint type, int size, int align)
        {
            var inner = Align(Length + 4, align);
            var end = Align(inner + size, 8);
            if (end > Capacity) { _ok = false; return Spare; }
            new Span<byte>(Stream + Length, end - Length).Clear();
            *(uint*)(Stream + Length) = type;
            Length = end;
            return Stream + inner;
        }

        static int Align(int v, int a) => (v + a - 1) & ~(a - 1);

        nint Alloc(int n)
        {
            var p = (nint)NativeMemory.AllocZeroed((nuint)Math.Max(n, 1));
            _native.Add(p);
            return p;
        }

        nint Str(uint n)
        {
            if (_pos + n > _p.Length) { _ok = false; return 0; }
            var s = (byte*)Alloc((int)n + 1);
            new ReadOnlySpan<byte>(_p, _pos, (int)n).CopyTo(new Span<byte>(s, (int)n));
            _pos += (int)n;
            return (nint)s;
        }

        uint U32()
        {
            if (_pos + 4 > _p.Length) { _ok = false; return 0; }
            var v = BinaryPrimitives.ReadUInt32LittleEndian(_p.AsSpan(_pos));
            _pos += 4;
            return v;
        }

        uint Count(int elementSize)
        {
            var n = U32();
            if ((long)n * Math.Min(elementSize, 4) > _p.Length) { _ok = false; return 0; }
            return n;
        }

        string Hash()
        {
            if (_pos + 20 > _p.Length) { _ok = false; return Zero; }
            var h = Convert.ToHexStringLower(_p, _pos, 20);
            _pos += 20;
            return h;
        }

        bool Blob(string h, out byte* b, out int n)
        {
            b = null;
            n = 0;
            if (!index.Blobs.TryGetValue(h, out var at) || views[at.File].Base == 0) return false;
            b = (byte*)views[at.File].Base + at.At;
            n = at.Length;
            return true;
        }
    }

    const uint RootSignature = 0, Cs = 6, StreamOutput = 7, Blend = 8, SampleMask = 9, Rasterizer = 10, DepthStencil = 11, InputLayoutType = 12,
        IbStripCut = 13, Topology = 14, RtFormatsType = 15, DsvFormat = 16, SampleDesc = 17, NodeMask = 18, CachedPso = 19, Flags = 20,
        ViewInstancing = 22, SerializedRootSignature = 29;
    const uint SoDecl = 0x10007;   // proxy.cpp kSoDecl: a stream output subobject with entries (its body follows)
    static readonly string Zero = new('0', 40);

    [StructLayout(LayoutKind.Sequential)]
    struct AppDesc { public char* Exe, Name; public ulong Version; public char* Engine; public ulong EngineVersion; }

    [StructLayout(LayoutKind.Sequential)]
    struct StreamDesc { public nuint Size; public byte* Stream; }

    static void Check(int hr, string what) { if (hr < 0) throw new IOException($"{what} failed (0x{hr:X8})"); }
    static nint* Vt(nint com) => *(nint**)com;
    static void Release(nint com) { if (com != 0) ((delegate* unmanaged<nint, uint>)Vt(com)[2])(com); }

    static readonly Guid ClsidStateObjectFactory = new("54e1c9f3-1303-4112-bf8e-7bf2bb606a73");
    static readonly Guid IidFactory = new("f5b066f0-648a-4611-bd41-27fd0948b9eb");
    static readonly Guid IidDatabase = new("c56060b7-b5fc-4135-98e0-a1e9997eace0");
    static readonly Guid IidDatabase1 = new("c066f807-403d-40a3-b1a3-5f50f912b6e0");
}
