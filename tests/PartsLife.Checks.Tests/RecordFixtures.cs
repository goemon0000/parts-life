using PartsLife.Checks.Recording;

namespace PartsLife.Checks.Tests;

/// <summary>
/// テストの記録の試験の下ごしらえ。**数字は仕様から直接書く**（要約の中の定数を使わない）。
/// 見本の記録は 1 秒刻み（t = 1, 2, 3 …）で作る。
/// </summary>
internal static class RecordFixtures
{
    public static MetricStatus Avail(string key, string source = "test") =>
        new(key, MetricKeys.UnitOf(key), source, Availability.Available);

    public static MetricStatus Missing(string key, Availability state, string reason) =>
        new(key, MetricKeys.UnitOf(key), "test", state, reason);

    /// <summary>n 秒ぶん（t = 1..n）。列ごとに「秒 → 値」の関数を渡す。NaN を返せば読めなかった秒。</summary>
    public static SeriesSet Series(int n, params (string Key, Func<int, double> F)[] cols)
    {
        var t = Enumerable.Range(1, n).Select(i => (double)i).ToArray();
        var d = new Dictionary<string, double[]>();
        foreach (var (key, f) in cols) d[key] = t.Select(x => f((int)x)).ToArray();
        return new SeriesSet(t, d);
    }

    public static RunSession Session(IEnumerable<MetricStatus>? caps = null, string status = "completed")
    {
        var s = new RunSession
        {
            AppVersion = "0.7.4",
            RunId = "abcd2345efgh6789",
            Status = status,
            StopReason = "user",
            StartUtc = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc),
            EndUtc = new DateTime(2026, 10, 18, 12, 5, 0, DateTimeKind.Utc),
            IntervalMs = 1000,
            System = new SystemSnapshot("Windows 11 Pro", 26300, "balanced", 65536, 16),
            CsvFile = "sensors-abcd2345efgh6789.csv",
        };
        if (caps is not null) s.Capabilities.AddRange(caps);
        return s;
    }

    public const string BuildJson =
        "{\"v\":1,\"src\":\"parts-life\",\"p\":{\"cpu\":[{\"n\":\"Test CPU 9000X 16-Core Processor\"}],"
        + "\"ram\":[{\"n\":\"CMK64GX5M2B5600Z40\",\"cap\":32,\"spd\":5600,\"t\":\"DDR5\",\"mfr\":\"Corsair\",\"q\":2}],"
        + "\"ssd\":[{\"n\":\"Some SSD 2TB\",\"cap\":2000,\"t\":\"nvme\"}]}}";

    /// <summary>GPU が 1 枚（gpu0）の、300 秒の見本。負荷は終始 100%、温度は 120 秒かけて 40→80 ℃、クロックは同時に 2800→2000 MHz。</summary>
    public static SeriesSet ThermalRamp(Func<int, double>? util = null, Func<int, double>? clock = null)
    {
        double Ramp(int t) => Math.Min(t, 120) / 120.0;
        return Series(300,
            ("cpu.load_pct", _ => 5),
            ("gpu0.util_pct", util ?? (_ => 100)),
            ("gpu0.temp_c", t => 40 + 40 * Ramp(t)),
            ("gpu0.clock_mhz", clock ?? (t => 2800 - 800 * Ramp(t))),
            ("gpu0.power_w", _ => 250));
    }

    public static IEnumerable<MetricStatus> GpuCaps() => new[]
    {
        Avail("cpu.load_pct"), Avail("gpu0.util_pct"), Avail("gpu0.temp_c"), Avail("gpu0.clock_mhz"), Avail("gpu0.power_w"),
    };

    public static string TempFolder()
    {
        string p = Path.Combine(Path.GetTempPath(), "pl-record-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        return p;
    }
}
