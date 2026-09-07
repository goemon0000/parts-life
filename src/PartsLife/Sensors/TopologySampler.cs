using System.Runtime.InteropServices;

namespace PartsLife.Sensors;

/// <summary>
/// CPU が何個載っているか、どの論理コアがどの CPU のものかを見る。
///
/// **2ソケットの機械で「CPUくんが1体」だと嘘になる**ため、実際の数を数える。
/// `GetLogicalProcessorInformationEx` は権限も WMI も要らず、起動時に1回で済む。
///
/// 64論理コアを超える機械は「プロセッサグループ」に分割される。
/// そこまで持っている人向けの正確な対応付けは、実機が無いと確かめようがないので、
/// **グループが1つのときだけ厳密に、それ以外は均等割り**にしてある。
/// 均等割りでも CPU の数は正しいので、体の数が合わなくなることはない。
/// </summary>
public static class TopologySampler
{
    private const int RelationProcessorPackage = 3;

    /// <summary>CPU ごとの、担当する論理コア番号。数えられなければ全コアを1つにまとめて返す。</summary>
    public static IReadOnlyList<int[]> CpuPackages(int logicalCount)
    {
        try
        {
            var masks = ReadPackageMasks();
            if (masks.Count > 1)
            {
                var groups = masks.Where(m => m.Group == 0).ToList();
                // グループ0だけで全部の CPU を説明できるなら、ビットの立ち方をそのまま使う
                if (groups.Count == masks.Count)
                {
                    var exact = groups
                        .Select(m => Enumerable.Range(0, Math.Min(64, logicalCount))
                                               .Where(i => (m.Mask & (1UL << i)) != 0).ToArray())
                        .Where(a => a.Length > 0).ToList();
                    if (exact.Count > 0 && exact.Sum(a => a.Length) == logicalCount)
                        return exact;
                }

                // 複数グループにまたがる機械。**個数は正しいので、割り当てだけ均等にする**
                int per = Math.Max(1, logicalCount / masks.Count);
                return Enumerable.Range(0, masks.Count)
                    .Select(i => Enumerable.Range(i * per,
                        i == masks.Count - 1 ? logicalCount - i * per : per).ToArray())
                    .ToList();
            }
        }
        catch (EntryPointNotFoundException) { }
        catch (DllNotFoundException) { }

        return new[] { Enumerable.Range(0, logicalCount).ToArray() };
    }

    private readonly record struct PackageMask(ulong Mask, ushort Group);

    private static List<PackageMask> ReadPackageMasks()
    {
        var result = new List<PackageMask>();
        uint len = 0;
        GetLogicalProcessorInformationEx(RelationProcessorPackage, IntPtr.Zero, ref len);
        if (len == 0) return result;

        IntPtr buf = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorPackage, buf, ref len)) return result;

            int offset = 0;
            while (offset < len)
            {
                IntPtr p = buf + offset;
                int size = Marshal.ReadInt32(p, 4);
                if (size <= 0) break;

                // PROCESSOR_RELATIONSHIP: Flags(1) EfficiencyClass(1) Reserved(20) GroupCount(2) GroupMask[]
                int groupCount = Marshal.ReadInt16(p, 8 + 22);
                for (int g = 0; g < groupCount; g++)
                {
                    // GROUP_AFFINITY: Mask(8) Group(2) Reserved(6)
                    IntPtr ga = p + 8 + 24 + g * 16;
                    result.Add(new PackageMask(
                        (ulong)Marshal.ReadInt64(ga), (ushort)Marshal.ReadInt16(ga, 8)));
                }
                offset += size;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return result;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(
        int relationship, IntPtr buffer, ref uint returnedLength);
}
