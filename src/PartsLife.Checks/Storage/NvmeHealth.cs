using System.Buffers.Binary;

namespace PartsLife.Checks.Storage;

/// <summary>
/// NVMe の SMART / Health Information（ログページ 0x02、512 バイト）。
/// 位置は NVMe Base Specification 2.0 の Figure 207 のとおり。
///
/// 128 ビットの数え上げは ulong に丸める（飽和させる）。
/// 2^64 を超えるのは現実の機器では起きないが、起きたら <see cref="Saturated"/> で分かる。
/// </summary>
public sealed record NvmeHealth
{
    public const int Length = 512;

    public byte CriticalWarning { get; init; }
    /// <summary>複合温度（ケルビン）。0 は「報告なし」。</summary>
    public ushort CompositeTemperatureKelvin { get; init; }
    public byte AvailableSpare { get; init; }
    public byte AvailableSpareThreshold { get; init; }
    /// <summary>寿命の消費の目安（%）。100 を超えてもよい（仕様上 255 で頭打ち）。</summary>
    public byte PercentageUsed { get; init; }
    /// <summary>1 単位 = 512,000 バイト（1000 × 512）。</summary>
    public ulong DataUnitsRead { get; init; }
    public ulong DataUnitsWritten { get; init; }
    public ulong HostReadCommands { get; init; }
    public ulong HostWriteCommands { get; init; }
    public ulong ControllerBusyMinutes { get; init; }
    public ulong PowerCycles { get; init; }
    public ulong PowerOnHours { get; init; }
    public ulong UnsafeShutdowns { get; init; }
    /// <summary>**訂正できなかったデータ誤り。** 一番大事な数字。</summary>
    public ulong MediaErrors { get; init; }
    public ulong ErrorLogEntries { get; init; }
    public uint WarningTempMinutes { get; init; }
    public uint CriticalTempMinutes { get; init; }
    /// <summary>温度センサー 1〜8（ケルビン、0 は無し）。</summary>
    public ushort[] TemperatureSensorsKelvin { get; init; } = Array.Empty<ushort>();
    /// <summary>128 ビットの値のどれかが ulong に収まらず、頭打ちにした。</summary>
    public bool Saturated { get; init; }

    public int? CompositeTemperatureCelsius =>
        CompositeTemperatureKelvin == 0 ? null : CompositeTemperatureKelvin - 273;

    /// <summary>読み書きの総量（バイト）。1 単位は 512,000 バイト。</summary>
    public double BytesRead => DataUnitsRead * 512_000.0;
    public double BytesWritten => DataUnitsWritten * 512_000.0;

    /// <summary>
    /// 512 バイトのログページを読む。**短ければ例外にする。**
    /// 足りない所を 0 で埋めて読むと、「エラー 0 件」という嘘の健康診断ができてしまう。
    /// </summary>
    public static NvmeHealth Parse(ReadOnlySpan<byte> page)
    {
        if (page.Length < Length)
            throw new ArgumentException($"NVMe health log must be {Length} bytes, got {page.Length}.", nameof(page));

        bool sat = false;

        var sensors = new ushort[8];
        for (int i = 0; i < 8; i++)
            sensors[i] = BinaryPrimitives.ReadUInt16LittleEndian(page.Slice(200 + i * 2, 2));

        var h = new NvmeHealth
        {
            CriticalWarning = page[0],
            CompositeTemperatureKelvin = BinaryPrimitives.ReadUInt16LittleEndian(page.Slice(1, 2)),
            AvailableSpare = page[3],
            AvailableSpareThreshold = page[4],
            PercentageUsed = page[5],
            DataUnitsRead = U128(page, 32, ref sat),
            DataUnitsWritten = U128(page, 48, ref sat),
            HostReadCommands = U128(page, 64, ref sat),
            HostWriteCommands = U128(page, 80, ref sat),
            ControllerBusyMinutes = U128(page, 96, ref sat),
            PowerCycles = U128(page, 112, ref sat),
            PowerOnHours = U128(page, 128, ref sat),
            UnsafeShutdowns = U128(page, 144, ref sat),
            MediaErrors = U128(page, 160, ref sat),
            ErrorLogEntries = U128(page, 176, ref sat),
            WarningTempMinutes = BinaryPrimitives.ReadUInt32LittleEndian(page.Slice(192, 4)),
            CriticalTempMinutes = BinaryPrimitives.ReadUInt32LittleEndian(page.Slice(196, 4)),
            TemperatureSensorsKelvin = sensors,
        };
        return h with { Saturated = sat };
    }

    private static ulong U128(ReadOnlySpan<byte> page, int offset, ref bool saturated)
    {
        ulong lo = BinaryPrimitives.ReadUInt64LittleEndian(page.Slice(offset, 8));
        ulong hi = BinaryPrimitives.ReadUInt64LittleEndian(page.Slice(offset + 8, 8));
        if (hi != 0) { saturated = true; return ulong.MaxValue; }
        return lo;
    }

    /// <summary>Critical Warning の各ビットの意味。立っている物だけ返す。</summary>
    public static IReadOnlyList<string> CriticalWarningNames(byte bits)
    {
        var list = new List<string>();
        if ((bits & 0x01) != 0) list.Add(L.T("予備領域が下限を下回った", "available spare below threshold"));
        if ((bits & 0x02) != 0) list.Add(L.T("温度が範囲外", "temperature out of range"));
        if ((bits & 0x04) != 0) list.Add(L.T("内部エラーで信頼性が低下", "reliability degraded by media/internal errors"));
        if ((bits & 0x08) != 0) list.Add(L.T("読み取り専用になっている", "media placed in read-only mode"));
        if ((bits & 0x10) != 0) list.Add(L.T("揮発メモリの退避装置が故障", "volatile memory backup device failed"));
        if ((bits & 0x20) != 0) list.Add(L.T("永続メモリ領域が読み取り専用", "persistent memory region read-only"));
        if ((bits & 0xC0) != 0) list.Add(L.T($"仕様外のビット 0x{bits & 0xC0:X2}", $"reserved bits 0x{bits & 0xC0:X2}"));
        return list;
    }
}

/// <summary>
/// NVMe の Error Information ログ（ページ 0x01）の1件、64 バイト。
/// 中身はコマンドごとの失敗の記録で、**ここにあるからといってデータが失われたとは限らない**
/// （対応していないコマンドを OS が投げただけ、という記録が大半の機種で溜まる）。
/// </summary>
public sealed record NvmeErrorEntry(ulong ErrorCount, ushort SubmissionQueueId, ushort CommandId,
                                    ushort StatusField, ulong Lba, uint Namespace)
{
    public const int Length = 64;

    /// <summary>状態コード種別（SCT）と状態コード（SC）。フェーズタグのビットは除く。</summary>
    public int StatusCodeType => (StatusField >> 9) & 0x7;
    public int StatusCode => (StatusField >> 1) & 0xFF;

    /// <summary>
    /// 媒体の誤り（SCT=2: Media and Data Integrity Errors）か。
    /// それ以外（SCT=0 の「対応していない」等）は、壊れている兆候ではない。
    /// </summary>
    public bool IsMediaError => StatusCodeType == 2;

    public static IReadOnlyList<NvmeErrorEntry> ParseLog(ReadOnlySpan<byte> page)
    {
        var list = new List<NvmeErrorEntry>();
        for (int off = 0; off + Length <= page.Length; off += Length)
        {
            var e = page.Slice(off, Length);
            ulong count = BinaryPrimitives.ReadUInt64LittleEndian(e);
            if (count == 0) continue;     // 空の枠
            list.Add(new NvmeErrorEntry(
                count,
                BinaryPrimitives.ReadUInt16LittleEndian(e.Slice(8, 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(e.Slice(10, 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(e.Slice(12, 2)),
                BinaryPrimitives.ReadUInt64LittleEndian(e.Slice(16, 8)),
                BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(24, 4))));
        }
        list.Sort((a, b) => b.ErrorCount.CompareTo(a.ErrorCount));   // 新しい順
        return list;
    }
}
