using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace PartsLife.Checks.Load;

/// <summary>論理プロセッサ1つ。Group と Number で SetThreadGroupAffinity に渡せる。</summary>
public sealed record LogicalCpu(int Index, ushort Group, byte Number, int Core, byte EfficiencyClass);

/// <summary>
/// 論理プロセッサの並び。**コアの種類（P コア / E コア）**も拾う。
///
/// 負荷テストでコアごとの速さを比べるとき、種類の違うコアを同じ物差しで並べると
/// 「E コアが遅い」という当たり前のことを異常として出してしまう。種類ごとに比べる。
/// </summary>
public static class CpuTopology
{
    private const int RelationProcessorCore = 0;

    public static IReadOnlyList<LogicalCpu> Enumerate()
    {
        try
        {
            uint len = 0;
            GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref len);
            if (len == 0) return Fallback();
            var buf = new byte[len];
            unsafe
            {
                fixed (byte* p = buf)
                    if (!GetLogicalProcessorInformationEx(RelationProcessorCore, (IntPtr)p, ref len)) return Fallback();
            }
            var list = Parse(buf.AsSpan(0, (int)len));
            return list.Count > 0 ? list : Fallback();
        }
        catch (EntryPointNotFoundException) { return Fallback(); }
    }

    /// <summary>
    /// SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX（RelationProcessorCore）の並びを読む。
    ///   0 Relationship, 4 Size, 8 Flags, 9 EfficiencyClass, 30 GroupCount, 32 GroupMask[]（各 16 バイト: Mask 8, Group 2）
    /// </summary>
    internal static List<LogicalCpu> Parse(ReadOnlySpan<byte> buf)
    {
        var cpus = new List<(ushort Group, byte Number, int Core, byte Eff)>();
        int core = 0;
        for (int off = 0; off + 8 <= buf.Length;)
        {
            int rel = BinaryPrimitives.ReadInt32LittleEndian(buf[off..]);
            int size = BinaryPrimitives.ReadInt32LittleEndian(buf[(off + 4)..]);
            if (size <= 0 || off + size > buf.Length) break;
            if (rel == RelationProcessorCore && size >= 48)
            {
                byte eff = buf[off + 9];
                int groups = BinaryPrimitives.ReadUInt16LittleEndian(buf[(off + 30)..]);
                for (int g = 0; g < groups; g++)
                {
                    int ga = off + 32 + g * 16;
                    if (ga + 16 > off + size) break;
                    ulong mask = BinaryPrimitives.ReadUInt64LittleEndian(buf[ga..]);
                    ushort group = BinaryPrimitives.ReadUInt16LittleEndian(buf[(ga + 8)..]);
                    for (int bit = 0; bit < 64; bit++)
                        if ((mask & (1UL << bit)) != 0) cpus.Add((group, (byte)bit, core, eff));
                }
                core++;
            }
            off += size;
        }
        return cpus.OrderBy(c => c.Group).ThenBy(c => c.Number)
                   .Select((c, i) => new LogicalCpu(i, c.Group, c.Number, c.Core, c.Eff)).ToList();
    }

    private static IReadOnlyList<LogicalCpu> Fallback() =>
        Enumerable.Range(0, Environment.ProcessorCount)
                  .Select(i => new LogicalCpu(i, (ushort)(i / 64), (byte)(i % 64), i, 0)).ToList();

    /// <summary>いま動いているスレッドを、その論理プロセッサに縛る。失敗しても続ける（縛れないだけ）。</summary>
    public static bool PinCurrentThread(LogicalCpu cpu)
    {
        var aff = new GROUP_AFFINITY { Mask = (UIntPtr)(1UL << cpu.Number), Group = cpu.Group };
        return SetThreadGroupAffinity(GetCurrentThread(), ref aff, out _);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GROUP_AFFINITY
    {
        public UIntPtr Mask;
        public ushort Group;
        private ushort _r0, _r1, _r2;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadGroupAffinity(IntPtr thread, ref GROUP_AFFINITY affinity, out GROUP_AFFINITY previous);
}
