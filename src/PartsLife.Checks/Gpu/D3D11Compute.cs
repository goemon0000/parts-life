using System.Runtime.InteropServices;
using System.Text;

namespace PartsLife.Checks.Gpu;

/// <summary>
/// D3D11 の計算シェーダを1本だけ回すための、最小限の COM 呼び出し。
///
/// ■ なぜ包みのライブラリ（Vortice など）を使わないか
/// 使う関数は 15 個ほどで、包みを入れると依存が 6 パッケージ増え、そのうち一部はベータ版に
/// 依存している。配布物の信用を考えると、OS に入っている d3d11.dll / dxgi.dll /
/// d3dcompiler_47.dll を直接呼ぶ方が軽くて素直。
/// ■ 番号の取り違えをどう防ぐか
/// vtable の番号は d3d11.h の宣言順。**テストで WARP（CPU で動く D3D11）に対して実際に回し、
/// 答え合わせまで通している。** WARP は GPU を一切使わない。
/// </summary>
public sealed unsafe class D3D11Compute : IDisposable
{
    public const int ThreadsPerGroup = 256;

    private IntPtr _device, _context, _shader, _buffer, _uav, _staging, _cbuffer, _query;
    public int Elements { get; }

    /// <summary>計算シェーダ（HLSL）。整数だけで回すので、CPU で同じ答えを作り直せる。</summary>
    public const string Hlsl = @"
cbuffer P : register(b0) { uint seed; uint iters; uint pad0; uint pad1; };
RWStructuredBuffer<uint> o : register(u0);
[numthreads(256, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    uint x = id.x * 0x9E3779B9u + seed;
    uint y = x ^ 0xA5A5A5A5u;
    [loop] for (uint i = 0; i < iters; i++)
    {
        x ^= x << 13; x ^= x >> 17; x ^= x << 5;
        y = y * 1664525u + 1013904223u;
        x += y ^ (x >> 3);
    }
    o[id.x] = x;
}";

    /// <summary>上のシェーダを CPU で計算した物。答え合わせ用。</summary>
    public static uint Reference(uint index, uint seed, uint iters)
    {
        unchecked
        {
            uint x = index * 0x9E3779B9u + seed;
            uint y = x ^ 0xA5A5A5A5u;
            for (uint i = 0; i < iters; i++)
            {
                x ^= x << 13; x ^= x >> 17; x ^= x << 5;
                y = y * 1664525u + 1013904223u;
                x += y ^ (x >> 3);
            }
            return x;
        }
    }

    /// <param name="adapterIndex">DXGI のアダプタ番号。null なら WARP（CPU。テスト用）。</param>
    public D3D11Compute(int? adapterIndex, int groups)
    {
        Elements = groups * ThreadsPerGroup;
        try { Create(adapterIndex); }
        catch { Dispose(); throw; }
    }

    private void Create(int? adapterIndex)
    {
        IntPtr adapter = IntPtr.Zero, factory = IntPtr.Zero;
        try
        {
            int driverType = 5;   // D3D_DRIVER_TYPE_WARP
            if (adapterIndex is { } ai)
            {
                var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
                Check(CreateDXGIFactory1(&iid, &factory), "CreateDXGIFactory1");
                Check(((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Com.Slot(factory, 12))(factory, (uint)ai, &adapter), "EnumAdapters1");
                driverType = 0;   // アダプタを指定するときは UNKNOWN
            }
            int level = 0xb000;   // D3D_FEATURE_LEVEL_11_0（cs_5_0 が要る）
            IntPtr dev, ctx;
            int got;
            Check(D3D11CreateDevice(adapter, driverType, IntPtr.Zero, 0, &level, 1, 7, &dev, &got, &ctx), "D3D11CreateDevice");
            _device = dev;
            _context = ctx;
        }
        finally
        {
            Com.Release(adapter);
            Com.Release(factory);
        }

        // シェーダ
        var bytecode = Compile();
        IntPtr cs;
        fixed (byte* p = bytecode)
            Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Com.Slot(_device, 18))(
                _device, p, (nuint)bytecode.Length, IntPtr.Zero, &cs), "CreateComputeShader");
        _shader = cs;

        // 書き込み先（RWStructuredBuffer<uint>）
        var desc = new BufferDesc { ByteWidth = (uint)(Elements * 4), Usage = 0, BindFlags = 0x80, MiscFlags = 0x40, StructureByteStride = 4 };
        IntPtr buf;
        Check(CreateBuffer(&desc, &buf), "CreateBuffer(uav)");
        _buffer = buf;

        var uavDesc = stackalloc uint[5] { 0 /*UNKNOWN*/, 1 /*BUFFER*/, 0, (uint)Elements, 0 };
        IntPtr uav;
        Check(((delegate* unmanaged<IntPtr, IntPtr, uint*, IntPtr*, int>)Com.Slot(_device, 8))(_device, _buffer, uavDesc, &uav),
              "CreateUnorderedAccessView");
        _uav = uav;

        // 読み戻し用
        var sdesc = desc with { Usage = 3 /*STAGING*/, BindFlags = 0, CpuAccessFlags = 0x20000 /*READ*/, MiscFlags = 0, StructureByteStride = 0 };
        IntPtr st;
        Check(CreateBuffer(&sdesc, &st), "CreateBuffer(staging)");
        _staging = st;

        // 定数（seed, iters）
        var cdesc = new BufferDesc { ByteWidth = 16, Usage = 0, BindFlags = 0x4 /*CONSTANT*/ };
        IntPtr cb;
        Check(CreateBuffer(&cdesc, &cb), "CreateBuffer(cb)");
        _cbuffer = cb;

        // 終わりを知るための印
        var qdesc = stackalloc uint[2] { 0 /*D3D11_QUERY_EVENT*/, 0 };
        IntPtr q;
        Check(((delegate* unmanaged<IntPtr, uint*, IntPtr*, int>)Com.Slot(_device, 24))(_device, qdesc, &q), "CreateQuery");
        _query = q;

        // 束ねておく（毎回やり直す必要は無い）
        IntPtr shader = _shader, uavp = _uav, cbp = _cbuffer;
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, uint, void>)Com.Slot(_context, 69))(_context, shader, IntPtr.Zero, 0);   // CSSetShader
        ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, uint*, void>)Com.Slot(_context, 68))(_context, 0, 1, &uavp, null);  // CSSetUnorderedAccessViews
        ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)Com.Slot(_context, 71))(_context, 0, 1, &cbp);                // CSSetConstantBuffers
    }

    /// <summary>
    /// 1回ぶん流して、終わるまで待つ。かかった時間を返す。
    /// <paramref name="timeout"/> を過ぎても終わらなければ例外（GPU が止まっている）。
    /// </summary>
    public TimeSpan Dispatch(uint seed, uint iters, TimeSpan timeout)
    {
        var cb = stackalloc uint[4] { seed, iters, 0, 0 };
        ((delegate* unmanaged<IntPtr, IntPtr, uint, IntPtr, void*, uint, uint, void>)Com.Slot(_context, 48))(
            _context, _cbuffer, 0, IntPtr.Zero, cb, 0, 0);                                                          // UpdateSubresource
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ((delegate* unmanaged<IntPtr, uint, uint, uint, void>)Com.Slot(_context, 41))(
            _context, (uint)(Elements / ThreadsPerGroup), 1, 1);                                                    // Dispatch
        ((delegate* unmanaged<IntPtr, IntPtr, void>)Com.Slot(_context, 28))(_context, _query);                      // End
        ((delegate* unmanaged<IntPtr, void>)Com.Slot(_context, 111))(_context);                                     // Flush
        uint done = 0;
        while (true)
        {
            int hr = ((delegate* unmanaged<IntPtr, IntPtr, void*, uint, uint, int>)Com.Slot(_context, 29))(
                _context, _query, &done, 4, 0);                                                                     // GetData
            if (hr == 0) break;
            if (hr < 0) Check(hr, "GetData");
            if (sw.Elapsed > timeout) throw new TimeoutException("GPU did not finish in time");
            Thread.Sleep(0);
        }
        return sw.Elapsed;
    }

    /// <summary>結果の先頭 <paramref name="count"/> 個を読み戻す。</summary>
    public uint[] ReadBack(int count)
    {
        count = Math.Min(count, Elements);
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)Com.Slot(_context, 47))(_context, _staging, _buffer);   // CopyResource
        var mapped = stackalloc byte[16];
        Check(((delegate* unmanaged<IntPtr, IntPtr, uint, int, uint, void*, int>)Com.Slot(_context, 14))(
            _context, _staging, 0, 1 /*READ*/, 0, mapped), "Map");
        try
        {
            var src = new ReadOnlySpan<uint>(*(void**)mapped, count);
            return src.ToArray();
        }
        finally { ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)Com.Slot(_context, 15))(_context, _staging, 0); }   // Unmap
    }

    private static byte[] Compile()
    {
        var src = Encoding.ASCII.GetBytes(Hlsl);
        IntPtr code = IntPtr.Zero, errors = IntPtr.Zero;
        try
        {
            fixed (byte* s = src)
            {
                var entry = "main\0"u8;
                var target = "cs_5_0\0"u8;
                fixed (byte* e = entry)
                fixed (byte* t = target)
                {
                    int hr = D3DCompile(s, (nuint)src.Length, null, IntPtr.Zero, IntPtr.Zero, e, t,
                                        1 << 15 /*OPTIMIZATION_LEVEL3*/, 0, &code, &errors);
                    if (hr < 0)
                    {
                        string msg = errors != IntPtr.Zero
                            ? Marshal.PtrToStringAnsi((IntPtr)((delegate* unmanaged<IntPtr, void*>)Com.Slot(errors, 3))(errors)) ?? ""
                            : "";
                        throw new InvalidOperationException($"D3DCompile failed 0x{hr:X8}: {msg}");
                    }
                }
            }
            void* ptr = ((delegate* unmanaged<IntPtr, void*>)Com.Slot(code, 3))(code);       // GetBufferPointer
            nuint size = ((delegate* unmanaged<IntPtr, nuint>)Com.Slot(code, 4))(code);      // GetBufferSize
            return new ReadOnlySpan<byte>(ptr, (int)size).ToArray();
        }
        finally
        {
            Com.Release(code);
            Com.Release(errors);
        }
    }

    /// <summary>HLSL が通るかだけ確かめる（CPU だけで済む。GPU には触らない）。</summary>
    public static int CompileForTest() => Compile().Length;

    public void Dispose()
    {
        if (_context != IntPtr.Zero)
        {
            ((delegate* unmanaged<IntPtr, void>)Com.Slot(_context, 110))(_context);    // ClearState: 束ねた物を外す
        }
        foreach (var p in new[] { _query, _cbuffer, _staging, _uav, _buffer, _shader, _context, _device })
            Com.Release(p);
        _query = _cbuffer = _staging = _uav = _buffer = _shader = _context = _device = IntPtr.Zero;
    }

    private int CreateBuffer(BufferDesc* desc, IntPtr* buffer) =>
        ((delegate* unmanaged<IntPtr, BufferDesc*, IntPtr, IntPtr*, int>)Com.Slot(_device, 3))(_device, desc, IntPtr.Zero, buffer);

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what} failed: 0x{hr:X8}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BufferDesc
    {
        public uint ByteWidth, Usage, BindFlags, CpuAccessFlags, MiscFlags, StructureByteStride;
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
        int* featureLevels, uint levels, uint sdkVersion, IntPtr* device, int* level, IntPtr* context);

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(Guid* riid, IntPtr* factory);

    [DllImport("d3dcompiler_47.dll", ExactSpelling = true)]
    private static extern int D3DCompile(void* src, nuint size, byte* name, IntPtr defines, IntPtr include,
        byte* entry, byte* target, uint flags1, uint flags2, IntPtr* code, IntPtr* errors);
}
