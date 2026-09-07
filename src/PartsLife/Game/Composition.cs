using PartsLife.Rendering;
using PartsLife.Sensors;

namespace PartsLife.Game;

/// <summary>
/// 盤の上に立つ1体ぶん。**実機の構成から毎回組み立てる。**
/// </summary>
/// <param name="Key">経験値の鍵。GPU が2枚なら gpu#0 と gpu#1 で別々に育つ</param>
/// <param name="CharacterId">絵</param>
/// <param name="Title">展開したときに出す名前</param>
/// <param name="Detail">その右に出す実測値</param>
/// <param name="Load">0..1。姿・熱・経験値のすべてがこの値から決まる</param>
public sealed record PartySlot(
    string Key, string CharacterId, SlotKind Kind,
    string Title, string Detail, double Load);

/// <summary>
/// 実機に載っている物から、並べる顔ぶれを決める。
///
/// **固定の7体では、実機と食い違う。**
/// 開発機には GPU が2枚挿さっているのに GPU くんは1体だけ、という状態だった。
/// 台数が違えば体の数も違う、が守れないと、監視アプリとしての信用が最初に崩れる。
///
/// 逆に「有るものは必ず出す」も守る。SSD しか無い機械に HDD くんは立たない。
/// </summary>
public static class Composition
{
    /// <summary>これ以上は並べない。サーバでも窓が画面をはみ出さない上限。</summary>
    public const int MaxGpu = 6;

    public static List<PartySlot> Build(
        Snapshot cpu,
        IReadOnlyList<int[]> packages,
        IReadOnlyList<GpuInfo> gpus,
        DiskThroughput io,
        IReadOnlyList<DriveInfoLite> drives,
        double diskLoad,
        string lang)
    {
        var slots = new List<PartySlot>();
        bool many = packages.Count > 1;

        for (int i = 0; i < packages.Count; i++)
        {
            double load = PackageLoad(cpu, packages[i]);
            string n = many ? $"CPU{i}" : "CPU";
            slots.Add(new PartySlot($"cpu#{i}", "cpu", SlotKind.Socket, n,
                $"{load * 100:0}%  {Cores(packages[i].Length, lang)}", load));
            // クーラーが冷やす相手は、その CPU ただ一人。**必ず同じ数だけ立つ。**
            slots.Add(new PartySlot($"cpu-cooler#{i}", "cpu-cooler", SlotKind.Socket,
                many ? $"FAN{i}" : "FAN", "", load));
        }

        slots.Add(new PartySlot("memory", "memory", SlotKind.Dimm, "MEM",
            $"{Gb(cpu.MemoryUsedBytes):0.0} / {Gb(cpu.MemoryTotalBytes):0.0} GB", cpu.MemoryRatio));

        for (int i = 0; i < Math.Min(gpus.Count, MaxGpu); i++)
        {
            var g = gpus[i];
            slots.Add(new PartySlot($"gpu#{i}", "gpu", SlotKind.Pcie,
                gpus.Count > 1 ? $"GPU{i}" : "GPU",
                $"{g.Utilization * 100:0}%  {g.Name}", g.Utilization));
        }

        // 有る種類だけ立てる。SSD しか無い機械に HDD くんは居ない
        bool hasSsd = drives.Any(d => d.IsSsd);
        bool hasHdd = drives.Any(d => !d.IsSsd);
        if (!hasSsd && !hasHdd) hasSsd = true;      // 判別できなかったときは1体だけ出す

        if (hasSsd)
            slots.Add(new PartySlot("ssd", "ssd", SlotKind.M2, "SSD",
                Capacity(drives.Where(d => d.IsSsd)), diskLoad));
        if (hasHdd)
            slots.Add(new PartySlot("hdd", "hdd", SlotKind.Sata, "HDD",
                Capacity(drives.Where(d => !d.IsSsd)), diskLoad * 0.7));

        // 電源は全員に配る側。誰かが働けば、その分だけ働いている
        double gpuMax = gpus.Count == 0 ? 0 : gpus.Max(g => g.Utilization);
        slots.Add(new PartySlot("psu", "psu", SlotKind.Atx, "PSU",
            $"{Mb(io.ReadBytesPerSec + io.WriteBytesPerSec):0.0} MB/s",
            Math.Clamp((cpu.CpuTotal + gpuMax) * 0.5 + diskLoad * 0.2, 0, 1)));

        return slots;
    }

    /// <summary>その CPU が担当する論理コアだけを平均する。2ソケットで別々の値が出る。</summary>
    private static double PackageLoad(Snapshot s, int[] cores)
    {
        if (s.CpuCores.Count == 0) return s.CpuTotal;
        var mine = cores.Where(i => i < s.CpuCores.Count).Select(i => s.CpuCores[i]).ToList();
        return mine.Count == 0 ? s.CpuTotal : mine.Average();
    }

    private static string Capacity(IEnumerable<DriveInfoLite> drives)
    {
        var list = drives.ToList();
        if (list.Count == 0) return "";
        ulong used = 0, total = 0;
        foreach (var d in list) { used += d.UsedBytes; total += d.TotalBytes; }
        string count = list.Count > 1 ? $" ({list.Count})" : "";
        return $"{Gb(used):0} / {Gb(total):0} GB{count}";
    }

    private static string Cores(int n, string lang) => lang == "en" ? $"{n} cores" : $"{n} コア";
    private static double Gb(ulong b) => b / 1024.0 / 1024.0 / 1024.0;
    private static double Mb(double b) => b / 1024.0 / 1024.0;
}
