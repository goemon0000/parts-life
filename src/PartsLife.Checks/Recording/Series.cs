using System.Globalization;
using System.Text;

namespace PartsLife.Checks.Recording;

/// <summary>
/// 記録を時間順に並べた列。<see cref="T"/> は記録の開始からの秒（単調な時計）。読めなかった値は NaN。
/// 要約に要る列だけを持つ（論理プロセッサごとの列は、平均だけを <see cref="LogicalProcessorAvgLoad"/> に畳む）。
/// </summary>
public sealed class SeriesSet
{
    public double[] T { get; }
    public IReadOnlyDictionary<string, double[]> Columns { get; }
    /// <summary>論理プロセッサごとの平均使用率（%）。列が無ければ空。</summary>
    public double[] LogicalProcessorAvgLoad { get; }

    public SeriesSet(double[] t, IReadOnlyDictionary<string, double[]> columns, double[]? lpAvgLoad = null)
    {
        T = t;
        Columns = columns;
        LogicalProcessorAvgLoad = lpAvgLoad ?? Array.Empty<double>();
    }

    public int Count => T.Length;
    public double DurationSec => T.Length == 0 ? 0 : T[^1] - T[0];
    public double[]? Get(string key) => Columns.TryGetValue(key, out var v) ? v : null;
}

/// <summary>
/// センサーの記録（CSV）。1 行目が列の名前で、先頭の 2 列は `t_s`（単調な時計の秒）と `utc`（ISO 8601）。
/// 数字は区切りがカンマ・小数点がピリオド（地域に依らない）。読めなかった値は空欄。UTF-8（BOM なし）。
/// </summary>
public static class CsvStream
{
    public const string TimeColumn = "t_s";
    public const string UtcColumn = "utc";

    public sealed class Writer : IDisposable
    {
        private readonly StreamWriter _out;
        private readonly int _count;
        private readonly StringBuilder _line = new(512);

        public Writer(string path, IReadOnlyList<string> columns)
        {
            // 他のプログラム（表計算など）が開いていても書き続けられるように、読み取りの共有を許す
            var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            _out = new StreamWriter(fs, new UTF8Encoding(false)) { NewLine = "\n" };
            _count = columns.Count;
            _line.Append(TimeColumn).Append(',').Append(UtcColumn);
            foreach (var c in columns) _line.Append(',').Append(c);
            _out.WriteLine(_line.ToString());
            _out.Flush();
        }

        /// <summary>1 行書いて、すぐ OS に渡す（途中で落ちても、そこまでが残る）。</summary>
        public void WriteRow(double tSec, DateTime utc, SampleRow row)
        {
            _line.Clear();
            _line.Append(tSec.ToString("0.000", CultureInfo.InvariantCulture)).Append(',').Append(SessionJson.Utc(utc));
            for (int i = 0; i < _count; i++)
            {
                _line.Append(',');
                double v = row.ValueAt(i);
                if (double.IsFinite(v)) _line.Append(v.ToString("0.###", CultureInfo.InvariantCulture));
            }
            _out.WriteLine(_line.ToString());
            _out.Flush();
        }

        public void Dispose() => _out.Dispose();
    }

    /// <summary>
    /// 読む。<paramref name="keep"/> が false を返した列は読み捨てる（既定は論理プロセッサごとの列以外）。
    /// 形が壊れた行（最後の行が途中で切れている等）・時間が戻る行は飛ばす。ヘッダが無ければ <see cref="FormatException"/>。
    /// 1 行ずつ読むので、長い記録でも読み込みの途中で大きくならない。
    /// </summary>
    public static SeriesSet Read(string path, Func<string, bool>? keep = null)
    {
        keep ??= k => !MetricKeys.IsPerLogicalProcessor(k);
        using var reader = new StreamReader(path, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        string? header = reader.ReadLine();
        if (header is null) throw new FormatException("空のファイルです");
        var names = header.Split(',');
        if (names.Length < 2 || names[0] != TimeColumn || names[1] != UtcColumn)
            throw new FormatException("先頭の列が t_s,utc ではありません");

        var wanted = new List<(int Col, string Key, List<double> Values)>();
        var lpLoad = new List<(int Col, double[] Acc)>();   // Acc[0] = 合計, Acc[1] = 個数
        for (int i = 2; i < names.Length; i++)
        {
            string key = names[i];
            if (MetricKeys.IsPerLogicalProcessor(key))
            {
                if (key.EndsWith(".load_pct", StringComparison.Ordinal)) lpLoad.Add((i, new double[2]));
                continue;
            }
            if (keep(key)) wanted.Add((i, key, new List<double>()));
        }

        var t = new List<double>();
        double last = double.NegativeInfinity;
        var starts = new int[names.Length];
        var ends = new int[names.Length];
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            // 区切りの位置だけ拾う（1 行ごとに文字列を作らない。長い記録でも読み込みが重くならないように）
            int count = 0, from = 0;
            for (int i = 0; i <= line.Length; i++)
            {
                if (i < line.Length && line[i] != ',') continue;
                if (count == names.Length) { count++; break; }
                starts[count] = from; ends[count] = i; count++; from = i + 1;
            }
            if (count != names.Length) continue;                  // 途中で切れた行・列の数が違う行
            if (!double.TryParse(line.AsSpan(starts[0], ends[0] - starts[0]), NumberStyles.Float, CultureInfo.InvariantCulture, out double ts)
                || !double.IsFinite(ts)) continue;
            if (ts <= last) continue;                             // 時間が戻る・重なる行は信用しない
            last = ts;
            t.Add(ts);
            foreach (var (col, _, values) in wanted) values.Add(Parse(line.AsSpan(starts[col], ends[col] - starts[col])));
            foreach (var (col, acc) in lpLoad)
            {
                double v = Parse(line.AsSpan(starts[col], ends[col] - starts[col]));
                if (!double.IsNaN(v)) { acc[0] += v; acc[1]++; }
            }
        }

        var cols = new Dictionary<string, double[]>(StringComparer.Ordinal);
        foreach (var (_, key, values) in wanted) cols[key] = values.ToArray();
        var lp = lpLoad.Where(x => x.Acc[1] > 0).Select(x => x.Acc[0] / x.Acc[1]).ToArray();
        return new SeriesSet(t.ToArray(), cols, lp);
    }

    private static double Parse(ReadOnlySpan<char> cell) =>
        cell.Length > 0 && double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v)
            ? v : double.NaN;
}
