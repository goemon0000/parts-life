using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Serialization;

namespace PartsLife.Checks.Storage;

/// <summary>Windows の STORAGE_BUS_TYPE。</summary>
public enum BusType
{
    Unknown = 0, Scsi = 1, Atapi = 2, Ata = 3, Ieee1394 = 4, Ssa = 5, Fibre = 6, Usb = 7, Raid = 8,
    iScsi = 9, Sas = 10, Sata = 11, Sd = 12, Mmc = 13, Virtual = 14, FileBackedVirtual = 15,
    Spaces = 16, Nvme = 17, Scm = 18, Ufs = 19,
}

/// <summary>STORAGE_DEVICE_DESCRIPTOR から取れる素性。</summary>
public sealed record DeviceIdentity(string Vendor, string Model, string Revision, string Serial,
                                    BusType Bus, bool Removable)
{
    /// <summary>
    /// STORAGE_DEVICE_DESCRIPTOR を読む。文字列は先頭からの位置で指されている（0 は「無し」）。
    ///
    ///   0 Version, 4 Size, 8 DeviceType, 9 Modifier, 10 RemovableMedia, 11 CommandQueueing,
    ///   12 VendorIdOffset, 16 ProductIdOffset, 20 ProductRevisionOffset, 24 SerialNumberOffset,
    ///   28 BusType, 32 RawPropertiesLength
    /// </summary>
    public static DeviceIdentity Parse(ReadOnlySpan<byte> d)
    {
        if (d.Length < 36) throw new ArgumentException("descriptor too short", nameof(d));
        int size = (int)Math.Min((uint)d.Length, BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(4, 4)));
        var span = d[..Math.Max(36, size)];
        return new DeviceIdentity(At(span, 12), At(span, 16), At(span, 20), At(span, 24),
            (BusType)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(28, 4)), span[10] != 0);
    }

    private static string At(ReadOnlySpan<byte> span, int field)
    {
        uint off = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(field, 4));
        if (off == 0 || off >= span.Length) return "";
        int end = span[(int)off..].IndexOf((byte)0);
        var bytes = end < 0 ? span[(int)off..] : span.Slice((int)off, end);
        return Encoding.ASCII.GetString(bytes).Trim();
    }
}

/// <summary>補助 exe が返す、1台ぶんの読み取り結果。**判定はしない**（判定は窓の側でする）。</summary>
public sealed class DiskReport
{
    public int Number { get; set; }
    public string Model { get; set; } = "";
    public string Serial { get; set; } = "";
    public string Firmware { get; set; } = "";
    public BusType Bus { get; set; }
    public long SizeBytes { get; set; }

    /// <summary>"nvme" / "ata" / "none"</summary>
    public string Protocol { get; set; } = "none";

    /// <summary>読めなかったときの理由（利用者に見せる文）。読めたら null。</summary>
    public string? Unavailable { get; set; }

    /// <summary>NVMe のログページ 0x02 そのもの（512 バイト）。解析は受け取った側でする。</summary>
    public byte[]? NvmeHealthPage { get; set; }
    /// <summary>NVMe のログページ 0x01（先頭の数件ぶん）。</summary>
    public byte[]? NvmeErrorPage { get; set; }

    /// <summary>ATA の SMART READ DATA / READ THRESHOLDS（各 512 バイト）。</summary>
    public byte[]? AtaSmartData { get; set; }
    public byte[]? AtaSmartThresholds { get; set; }
    /// <summary>閾値が読めず、IOCTL_STORAGE_PREDICT_FAILURE の値だけで済ませた。</summary>
    public bool AtaFromPredictFailure { get; set; }
    public bool? PredictFailure { get; set; }

    /// <summary>
    /// 機器の一意な識別子（VPD 0x83 の EUI-64 / NGUID / NAA = WWN）。"eui.0025…" / "naa.5000…"。
    /// 報告されなければ空。基準値の鍵の一部（<see cref="BaselineIdentity"/>）。
    /// </summary>
    public string UniqueId { get; set; } = "";

    /// <summary>
    /// 接続の位置（IOCTL_SCSI_GET_ADDRESS の "port:path:target:lun"）。取れなければ空。
    /// **鍵には普段使わない**（挿し替えで変わる）。同じ識別子の台が同時に複数あるときの見分けにだけ使う。
    /// </summary>
    public string Location { get; set; } = "";

    [JsonIgnore]
    public NvmeHealth? Nvme => NvmeHealthPage is { Length: >= NvmeHealth.Length } p ? NvmeHealth.Parse(p) : null;
    [JsonIgnore]
    public IReadOnlyList<NvmeErrorEntry> NvmeErrors =>
        NvmeErrorPage is { } p ? NvmeErrorEntry.ParseLog(p) : Array.Empty<NvmeErrorEntry>();
    [JsonIgnore]
    public IReadOnlyList<SmartAttribute> Ata =>
        AtaSmartData is { Length: >= AtaSmart.PageLength } d ? AtaSmart.Parse(d, AtaSmartThresholds) : Array.Empty<SmartAttribute>();
}

public static class BusNames
{
    public static string Of(BusType b) => b switch
    {
        BusType.Nvme => "NVMe",
        BusType.Sata => "SATA",
        BusType.Ata => "ATA",
        BusType.Usb => "USB",
        BusType.Sas => "SAS",
        BusType.Scsi => "SCSI",
        BusType.Raid => "RAID",
        BusType.Spaces => L.T("記憶域スペース", "Storage Spaces"),
        BusType.Virtual or BusType.FileBackedVirtual => L.T("仮想ディスク", "Virtual"),
        BusType.Sd => "SD",
        BusType.Mmc => "MMC",
        BusType.Ufs => "UFS",
        _ => b.ToString(),
    };

    /// <summary>
    /// その接続で SMART を読みに行くか。**分からない経路には推測で投げない。**
    /// USB の変換基板は ATA の命令を通す物と通さない物があり、通さない物に投げても
    /// 「異常なし」に見える空の値が返ることがある。それを健康だと表示するのは嘘になる。
    /// </summary>
    public static string ProtocolFor(BusType b) => b switch
    {
        BusType.Nvme => "nvme",
        BusType.Sata or BusType.Ata => "ata",
        _ => "none",
    };
}
