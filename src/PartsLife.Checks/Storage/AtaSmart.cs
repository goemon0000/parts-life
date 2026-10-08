using System.Buffers.Binary;

namespace PartsLife.Checks.Storage;

/// <summary>
/// ATA の SMART 属性1件。
/// <see cref="Current"/> と <see cref="Worst"/> は「正規化値」（大きいほど健康、機種ごとの目盛り）、
/// <see cref="Raw"/> は機種がそのまま返す 48 ビットの生の値。
/// <see cref="Threshold"/> は閾値（0 は「閾値なし」）。
/// </summary>
public sealed record SmartAttribute(byte Id, ushort Flags, byte Current, byte Worst, ulong Raw, byte? Threshold)
{
    /// <summary>
    /// 正規化値が閾値以下に落ちた（＝機器自身が「故障の見込み」と言っている）。
    /// 閾値 0 は「判定しない属性」なので落ちたことにしない。
    /// </summary>
    public bool FailingNow => Threshold is { } t && t != 0 && Current <= t;
    public bool FailedInPast => Threshold is { } t && t != 0 && Worst <= t && !FailingNow;

    /// <summary>保証に関わる属性か（flags の bit0 = pre-failure）。</summary>
    public bool PreFailure => (Flags & 0x0001) != 0;

    /// <summary>
    /// 生の値を、その属性の意味で読む。
    /// **温度と通電時間は機種によって上位バイトに別の物を詰めている**ので、下位だけを見る。
    /// </summary>
    public ulong Value => Id switch
    {
        190 or 194 => Raw & 0xFF,          // 温度。上位に最低/最高温度を詰める機種が多い
        9 => Raw & 0xFFFFFFFF,             // 通電時間。上位にミリ秒等を詰める機種がある。単位は換算しない（分・秒の機種あり）
        _ => Raw,
    };

    public string Name => SmartNames.Of(Id);
}

public static class SmartNames
{
    public static string Of(byte id) => id switch
    {
        1 => L.T("読み取りエラー率", "Read error rate"),
        3 => L.T("回転開始時間", "Spin-up time"),
        4 => L.T("回転開始回数", "Start/stop count"),
        5 => L.T("代替処理済みセクタ", "Reallocated sectors"),
        7 => L.T("シークエラー率", "Seek error rate"),
        9 => L.T("通電時間", "Power-on hours"),
        10 => L.T("回転再試行", "Spin retry count"),
        12 => L.T("電源投入回数", "Power cycle count"),
        171 => L.T("書き込み失敗（SSD）", "Program fail count"),
        172 => L.T("消去失敗（SSD）", "Erase fail count"),
        173 => L.T("平均消去回数（SSD）", "Wear leveling count"),
        174 => L.T("予期しない電源断", "Unexpected power loss"),
        177 => L.T("消耗の平準化（SSD）", "Wear range delta"),
        181 => L.T("書き込み失敗合計", "Program fail total"),
        182 => L.T("消去失敗合計", "Erase fail total"),
        183 => L.T("SATA速度低下", "SATA downshift"),
        184 => L.T("経路の誤り", "End-to-end error"),
        187 => L.T("訂正不能エラー（報告）", "Reported uncorrectable"),
        188 => L.T("コマンドのタイムアウト", "Command timeout"),
        190 => L.T("温度（気流）", "Airflow temperature"),
        191 => L.T("衝撃の検知", "G-sense error rate"),
        192 => L.T("電源断による退避", "Power-off retract count"),
        193 => L.T("ヘッドの出し入れ", "Load cycle count"),
        194 => L.T("温度", "Temperature"),
        195 => L.T("ECC で訂正した数", "Hardware ECC recovered"),
        196 => L.T("代替処理の発生回数", "Reallocation events"),
        197 => L.T("代替待ちセクタ", "Current pending sectors"),
        198 => L.T("オフラインで訂正不能", "Offline uncorrectable"),
        199 => L.T("通信路の CRC エラー", "UDMA CRC errors"),
        200 => L.T("書き込みエラー率", "Write error rate"),
        231 => L.T("SSD の残り寿命", "SSD life left"),
        232 => L.T("予備領域の残り", "Available reserved space"),
        233 => L.T("メディア消耗指標", "Media wearout indicator"),
        241 => L.T("総書き込み量", "Total LBAs written"),
        242 => L.T("総読み取り量", "Total LBAs read"),
        _ => L.T($"属性 {id}", $"Attribute {id}"),
    };
}

/// <summary>
/// SMART READ DATA（512 バイト）と SMART READ THRESHOLDS（512 バイト）の表を読む。
/// どちらも先頭2バイトが版数、続いて 12 バイト × 30 枠。
///
///   データ:   [id][flags 2][current][worst][raw 6][reserved]
///   閾値:     [id][threshold][reserved 10]
/// </summary>
public static class AtaSmart
{
    public const int PageLength = 512;
    private const int Slots = 30;
    private const int Stride = 12;

    public static IReadOnlyList<SmartAttribute> Parse(ReadOnlySpan<byte> data, ReadOnlySpan<byte> thresholds = default)
    {
        if (data.Length < PageLength)
            throw new ArgumentException($"SMART data must be {PageLength} bytes, got {data.Length}.", nameof(data));

        var limits = new Dictionary<byte, byte>();
        if (thresholds.Length >= PageLength)
        {
            for (int i = 0; i < Slots; i++)
            {
                var t = thresholds.Slice(2 + i * Stride, Stride);
                if (t[0] != 0) limits[t[0]] = t[1];
            }
        }
        bool haveThresholds = thresholds.Length >= PageLength;

        var list = new List<SmartAttribute>();
        for (int i = 0; i < Slots; i++)
        {
            var a = data.Slice(2 + i * Stride, Stride);
            byte id = a[0];
            if (id == 0) continue;     // 空き枠
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(a.Slice(1, 2));
            ulong raw = 0;
            for (int b = 5; b >= 0; b--) raw = (raw << 8) | a[5 + b];
            byte? limit = haveThresholds ? limits.GetValueOrDefault(id) : null;
            list.Add(new SmartAttribute(id, flags, a[3], a[4], raw, limit));
        }
        return list;
    }

    /// <summary>
    /// 代替処理・代替待ち・訂正不能・CRC。**この4つが増えるかどうかが、HDD/SSD の寿命の読み筋。**
    /// </summary>
    public static readonly byte[] KeyIds = { 5, 196, 197, 198, 187, 199 };
}
