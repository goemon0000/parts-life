using System.Diagnostics;
using PartsLife.Checks.Gpu;

namespace PartsLife.Checks.Recording;

/// <summary>
/// この機械で使う読み口の組み立て。**ドライバは足さない・管理者は要らない**範囲だけ:
/// PDH（CPU・ディスク・GPU の使用率）・GlobalMemoryStatusEx・GetSystemPowerStatus・NVIDIA のドライバ同梱の nvml.dll。
///
/// GPU は 1 枚ずつ、持ち主を 1 つに決める（同じ項目を 2 つの読み口が名乗らない）:
/// - NVIDIA で NVML が開ける → NVML が全項目
/// - それ以外 → 使用率とメモリは PDH（ベンダーを問わない）。温度・クロック・電力・ファンは、AMD は ADLX、
///   Intel は IGCL を読む読み口が受け持つ。**この版は ADLX のみ。Intel の IGCL は段 3 以降**で、理由つきで「読めない」と答える
/// </summary>
public static class DefaultSources
{
    /// <summary>WMI を使わない（速い）。DXGI と NVML の初期化に数百ミリ秒かかるので、画面の糸では呼ばないこと。</summary>
    public static SourceSet Create()
    {
        var sources = new List<ISensorSource>
        {
            new CpuPdhSource(),
            new MemorySource(),
            new DiskPdhSource(),
            new PowerStateSource(),
            new ExtensionSource(),
        };

        Nvml? nvml = null;
        try { nvml = Nvml.TryOpen(); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Trace.TraceWarning("PartsLife: record: nvml could not be opened: " + ex.GetType().Name);
        }

        IReadOnlyList<GpuSlot> slots = Array.Empty<GpuSlot>();
        try { slots = GpuSlots.Discover(nvml); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or System.Runtime.InteropServices.COMException)
        {
            Trace.TraceWarning("PartsLife: record: could not list GPUs: " + ex.GetType().Name);
        }

        var viaNvml = slots.Where(s => s.Adapter.NvmlIndex is not null).ToList();
        var others = slots.Where(s => s.Adapter.NvmlIndex is null).ToList();

        if (nvml is not null && viaNvml.Count > 0) sources.Add(new NvmlGpuSource(nvml, viaNvml));
        else nvml?.Dispose();

        if (others.Count > 0)
        {
            sources.Add(new PdhGpuSource(others));
            var gaps = new List<(GpuSlot, string)>();
            var amd = others.Where(s => s.Vendor == "amd").ToList();
            if (amd.Count > 0)
            {
                var adlx = AdlxGpuSource.TryCreate(amd);
                if (adlx is not null) sources.Add(adlx);
                else gaps.AddRange(amd.Select(s => (s, Reasons.NoAdlx)));
            }
            foreach (var s in others.Where(s => s.Vendor == "nvidia")) gaps.Add((s, Reasons.NoNvml));
            foreach (var s in others.Where(s => s.Vendor is not ("amd" or "nvidia"))) gaps.Add((s, Reasons.VendorNotImplemented));
            if (gaps.Count > 0) sources.Add(new GpuGapSource(gaps));
        }

        return new SourceSet(sources, slots.Select(s => s.Describe()).ToArray(), Environment.ProcessorCount);
    }
}
