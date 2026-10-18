using PartsLife.Checks.Gpu;
using PartsLife.Hardware;

namespace PartsLife.Checks.Recording;

/// <summary>記録の中の GPU 1枚（`gpu0` …）と、その実体（DXGI のアダプタ）。</summary>
internal sealed record GpuSlot(int Index, GpuAdapter Adapter)
{
    /// <summary>PDH の「GPU Engine」「GPU Adapter Memory」のインスタンス名に埋まっている LUID と突き合わせる鍵。</summary>
    public string LuidKey => GpuCounterName.KeyOf((long)(uint)Adapter.LuidHigh, Adapter.LuidLow);

    public string Vendor => Adapter.VendorId switch
    {
        0x10DE => "nvidia",
        0x1002 => "amd",
        0x8086 => "intel",
        _ => "other",
    };

    public GpuDescriptor Describe() => new(Index, Adapter.Name, Vendor, (long)(Adapter.DedicatedBytes / 1048576UL));
}

internal static class GpuSlots
{
    /// <summary>
    /// 記録に出る GPU の一覧。仮想ディスプレイの別名・ソフトウェアのレンダラは <see cref="GpuAdapters"/> が除いている。
    /// **番号は PCI のバス番号順**（番地の無い物は最後）。名前や NVML の番号では決めない:
    /// 同じ型番が 2 枚でも、NVML の番号がどう振られていても、`gpu0` が同じ枚数・同じ向きに並ぶ。
    /// （NVML の番号と DXGI のアダプタは PCI の番地で突き合わせてある。CUDA_DEVICE_ORDER は CUDA の並びの話で、ここには効かない。）
    /// </summary>
    public static IReadOnlyList<GpuSlot> Discover(Nvml? nvml) => Number(GpuAdapters.Enumerate(nvml));

    /// <summary>アダプタに記録の番号を振る（バス番号順）。機械に触らない純粋な関数。</summary>
    internal static IReadOnlyList<GpuSlot> Number(IEnumerable<GpuAdapter> adapters) =>
        adapters
            .OrderBy(a => a.PciBus ?? uint.MaxValue)
            .ThenBy(a => a.PciDevice ?? uint.MaxValue)
            .ThenBy(a => a.DxgiIndex)
            .Select((a, i) => new GpuSlot(i, a))
            .ToArray();
}

/// <summary>
/// NVIDIA の GPU。ドライバ同梱の nvml.dll から、温度・クロック・電力・ファン・使用率・メモリ・制限の理由を読む。
/// **管理者権限は要らない。読み出しだけ。** NVML が引き継がれる（閉じるときに nvml.dll を返す）。
/// </summary>
internal sealed class NvmlGpuSource : ISensorSource
{
    private readonly Nvml _nvml;
    private readonly List<GpuSlot> _slots;
    private readonly HashSet<string> _available = new(StringComparer.Ordinal);
    private bool _disposed;

    public NvmlGpuSource(Nvml nvml, IEnumerable<GpuSlot> slots)
    {
        _nvml = nvml;
        _slots = slots.Where(s => s.Adapter.NvmlIndex is not null).ToList();
    }

    public string Id => "nvml";

    public IReadOnlyList<MetricStatus> Detect()
    {
        var list = new List<MetricStatus>();
        foreach (var slot in _slots)
        {
            var s = _nvml.Sample(slot.Adapter.NvmlIndex!.Value);
            foreach (var (metric, unit, pick) in Fields)
            {
                string key = MetricKeys.Gpu(slot.Index, metric);
                if (s is null) { list.Add(new MetricStatus(key, unit, "nvml", Availability.Unavailable, Reasons.Failed)); continue; }
                var v = pick(s);
                if (v.Ok) { _available.Add(key); list.Add(new MetricStatus(key, unit, "nvml", Availability.Available)); }
                else list.Add(new MetricStatus(key, unit, "nvml", Availability.Unavailable, ReasonOf(v.Status)));
            }
        }
        return list;
    }

    public void Prime() { }

    public void Read(SampleRow row)
    {
        foreach (var slot in _slots)
        {
            var s = _nvml.Sample(slot.Adapter.NvmlIndex!.Value);
            if (s is null) continue;
            foreach (var (metric, _, pick) in Fields)
            {
                string key = MetricKeys.Gpu(slot.Index, metric);
                if (!_available.Contains(key)) continue;
                var v = pick(s);
                if (v.Ok) row.Set(key, metric == MetricKeys.MemUsed ? v.Value / 1048576.0 : v.Value);
            }
        }
    }

    private static readonly (string Metric, string Unit, Func<NvmlSample, NvmlValue> Pick)[] Fields =
    {
        (MetricKeys.Util, "%", s => s.UtilizationPercent),
        (MetricKeys.MemUsed, "MB", s => s.MemoryUsedBytes),
        (MetricKeys.Temp, "°C", s => s.TemperatureC),
        (MetricKeys.Clock, "MHz", s => s.GraphicsMHz),
        (MetricKeys.MemClock, "MHz", s => s.MemoryMHz),
        (MetricKeys.Power, "W", s => s.PowerW),
        (MetricKeys.PowerLimit, "W", s => s.PowerLimitW),
        (MetricKeys.FanPct, "%", s => s.FanPercent),
        (MetricKeys.Throttle, "bitmask", s => s.ThrottleMask),
    };

    /// <summary>NVML の戻り値を理由の印に。-1 = その関数が無い、3 = 非対応、4 = 権限。</summary>
    internal static string ReasonOf(int status) => status switch
    {
        -1 => Reasons.LibFunctionMissing,
        3 => Reasons.NotSupported,
        4 => Reasons.NoPermission,
        _ => Reasons.Failed,
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _nvml.Dispose();
    }
}

/// <summary>
/// ベンダーを問わない GPU の使用率と専用メモリ（PDH の「GPU Engine」「GPU Adapter Memory」。タスクマネージャーと同じ）。
/// NVML が受け持たない GPU（AMD・Intel・NVML が開けない NVIDIA）に使う。
/// 使用率は、エンジンの種類ごとに合計した値の最大（全部足すと 3D と Copy が同時に動いたとき 100% を超えるため）。
/// </summary>
internal sealed class PdhGpuSource : ISensorSource
{
    private readonly List<GpuSlot> _slots;
    private PdhQuery? _q;
    private IntPtr _util, _mem;
    private readonly Dictionary<string, (string? Key, string Type)> _names = new(StringComparer.Ordinal);
    private const int MaxCachedNames = 2048;

    public PdhGpuSource(IEnumerable<GpuSlot> slots) { _slots = slots.ToList(); }

    public string Id => "pdh-gpu";

    public IReadOnlyList<MetricStatus> Detect()
    {
        _q = PdhQuery.TryOpen();
        if (_q is not null)
        {
            _util = _q.Add(@"\GPU Engine(*)\Utilization Percentage");
            _mem = _q.Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
            _q.Collect();
            Thread.Sleep(300);
            _q.Collect();
        }
        var list = new List<MetricStatus>();
        foreach (var s in _slots)
        {
            list.Add(new MetricStatus(MetricKeys.Gpu(s.Index, MetricKeys.Util), "%", "pdh:GPU Engine",
                _util != IntPtr.Zero ? Availability.Available : Availability.Unavailable, _util != IntPtr.Zero ? "" : Reasons.NoCounter));
            list.Add(new MetricStatus(MetricKeys.Gpu(s.Index, MetricKeys.MemUsed), "MB", "pdh:GPU Adapter Memory",
                _mem != IntPtr.Zero ? Availability.Available : Availability.Unavailable, _mem != IntPtr.Zero ? "" : Reasons.NoCounter));
        }
        return list;
    }

    public void Prime() => _q?.Collect();

    public void Read(SampleRow row)
    {
        if (_q is null || !_q.Collect()) return;

        if (_util != IntPtr.Zero)
        {
            var perEngine = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
            foreach (var (instance, value) in _q.ReadArray(_util))
            {
                var (key, type) = Parse(instance);
                if (key is null) continue;
                if (!perEngine.TryGetValue(key, out var byType)) perEngine[key] = byType = new Dictionary<string, double>(StringComparer.Ordinal);
                byType[type] = byType.GetValueOrDefault(type) + value;
            }
            foreach (var s in _slots)
            {
                // インスタンスが 1 つも無い GPU は、誰も使っていない（0%）
                double u = perEngine.TryGetValue(s.LuidKey, out var t) && t.Count > 0 ? t.Values.Max() : 0;
                row.Set(MetricKeys.Gpu(s.Index, MetricKeys.Util), Math.Clamp(u, 0, 100));
            }
        }

        if (_mem != IntPtr.Zero)
        {
            var used = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (instance, value) in _q.ReadArray(_mem))
            {
                var (key, _) = Parse(instance);
                if (key is null) continue;
                used[key] = used.GetValueOrDefault(key) + value;
            }
            foreach (var s in _slots)
                row.Set(MetricKeys.Gpu(s.Index, MetricKeys.MemUsed), used.GetValueOrDefault(s.LuidKey) / 1048576.0);
        }
    }

    private (string? Key, string Type) Parse(string instance)
    {
        if (_names.TryGetValue(instance, out var hit)) return hit;
        if (_names.Count >= MaxCachedNames) _names.Clear();
        var parsed = (GpuCounterName.LuidKey(instance), GpuCounterName.EngineType(instance));
        _names[instance] = parsed;
        return parsed;
    }

    public void Dispose() => _q?.Dispose();
}

/// <summary>
/// ベンダーのドライバ経由で読めない GPU の項目を、理由つきで「読めない」と答えるだけの読み口。
/// 例: Intel（IGCL は段 3 以降）、NVML が開けなかった NVIDIA、ADLX が開けなかった AMD。**値を推測で埋めない。**
/// </summary>
internal sealed class GpuGapSource : ISensorSource
{
    private readonly List<(GpuSlot Slot, string Reason)> _gaps;

    public GpuGapSource(IEnumerable<(GpuSlot Slot, string Reason)> gaps) { _gaps = gaps.ToList(); }

    public string Id => "gpu-gap";

    public IReadOnlyList<MetricStatus> Detect()
    {
        var list = new List<MetricStatus>();
        foreach (var (slot, reason) in _gaps)
            foreach (var (metric, unit) in Missing)
                list.Add(new MetricStatus(MetricKeys.Gpu(slot.Index, metric), unit, slot.Vendor, Availability.Unavailable, reason));
        return list;
    }

    private static readonly (string, string)[] Missing =
    {
        (MetricKeys.Temp, "°C"), (MetricKeys.Clock, "MHz"), (MetricKeys.Power, "W"), (MetricKeys.FanPct, "%"),
    };

    public void Prime() { }
    public void Read(SampleRow row) { }
    public void Dispose() { }
}
