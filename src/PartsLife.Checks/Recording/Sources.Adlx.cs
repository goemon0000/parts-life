namespace PartsLife.Checks.Recording;

/// <summary>AMD の GPU を ADLX（ドライバ同梱の amdadlx64.dll）から読む。作業中の仮置き: まだ読まない。</summary>
internal sealed class AdlxGpuSource : ISensorSource
{
    private AdlxGpuSource() { }

    public static AdlxGpuSource? TryCreate(IReadOnlyList<GpuSlot> slots) => null;

    public string Id => "adlx";
    public IReadOnlyList<MetricStatus> Detect() => Array.Empty<MetricStatus>();
    public void Prime() { }
    public void Read(SampleRow row) { }
    public void Dispose() { }
}
