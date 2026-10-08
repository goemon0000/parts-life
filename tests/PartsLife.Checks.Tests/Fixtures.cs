using System.Buffers.Binary;

namespace PartsLife.Checks.Tests;

/// <summary>
/// 実機の値を模したバイト列。**位置は仕様書から直接書く**（解析側の定数を使わない）。
/// 解析の取り違えを、解析と同じ取り違えで作った見本が隠してしまわないように。
/// </summary>
internal static class Fixtures
{
    private static void U16(byte[] b, int off, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(off), v);
    private static void U32(byte[] b, int off, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(off), v);
    private static void U64(byte[] b, int off, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(off), v);

    /// <summary>
    /// 健康な NVMe（2TB 級・通電 3 年ほど）。NVMe 2.0 Figure 207 の位置で書く。
    /// </summary>
    public static byte[] HealthyNvme()
    {
        var b = new byte[512];
        b[0] = 0x00;                 // Critical Warning
        U16(b, 1, 273 + 41);         // Composite Temperature 41 ℃
        b[3] = 100;                  // Available Spare
        b[4] = 10;                   // Available Spare Threshold
        b[5] = 3;                    // Percentage Used
        U64(b, 32, 61_234_567);      // Data Units Read
        U64(b, 48, 48_765_432);      // Data Units Written
        U64(b, 64, 1_000_000_000);   // Host Read Commands
        U64(b, 80, 900_000_000);     // Host Write Commands
        U64(b, 96, 4_321);           // Controller Busy Time
        U64(b, 112, 1_502);          // Power Cycles
        U64(b, 128, 21_345);         // Power On Hours
        U64(b, 144, 87);             // Unsafe Shutdowns
        U64(b, 160, 0);              // Media and Data Integrity Errors
        U64(b, 176, 2_017);          // Number of Error Information Log Entries
        U32(b, 192, 0);              // Warning Composite Temperature Time
        U32(b, 196, 0);              // Critical Composite Temperature Time
        U16(b, 200, 273 + 41);       // Temperature Sensor 1
        U16(b, 202, 273 + 52);       // Temperature Sensor 2
        return b;
    }

    /// <summary>
    /// 弱っている NVMe。メディアエラー 363,278（オーナーの C: と同じ累計）、
    /// Critical Warning の bit2（信頼性低下）が立っている。予備領域は下限割れ。
    /// </summary>
    public static byte[] FailingNvme(ulong mediaErrors = 363_278)
    {
        var b = HealthyNvme();
        b[0] = 0x04 | 0x01;          // reliability degraded + spare below threshold
        b[3] = 5;                    // Available Spare 5%
        b[4] = 10;                   // threshold 10%
        b[5] = 104;                  // Percentage Used 104%
        U16(b, 1, 273 + 74);         // 74 ℃
        U64(b, 160, mediaErrors);
        U64(b, 176, 400_123);
        U32(b, 196, 12);             // Critical Composite Temperature Time 12 分
        return b;
    }

    /// <summary>NVMe の Error Information ログ（64 バイト × 4 枠、うち 2 件が記録済み）。</summary>
    public static byte[] NvmeErrorLog()
    {
        var b = new byte[64 * 4];
        // 1件目: 「未対応の命令」（SCT=0, SC=0x02 Invalid Field）。Status Field = (SC<<1)|(SCT<<9)
        U64(b, 0, 2016);
        U16(b, 8, 0); U16(b, 10, 0x1234);
        U16(b, 12, (ushort)((0x02 << 1) | (0 << 9)));
        // 2件目: 媒体の誤り（SCT=2, SC=0x81 Unrecovered Read Error）
        U64(b, 64, 2017);
        U16(b, 72, 1); U16(b, 74, 0x0042);
        U16(b, 76, (ushort)((0x81 << 1) | (2 << 9)));
        U64(b, 80, 123_456_789);     // LBA
        U32(b, 88, 1);               // NSID
        return b;
    }

    /// <summary>SMART 属性1件（12 バイト）を、2 バイトの版数の後ろ、slot 番目に書く。</summary>
    private static void Attr(byte[] page, int slot, byte id, ushort flags, byte current, byte worst, ulong raw)
    {
        int off = 2 + slot * 12;
        page[off] = id;
        U16(page, off + 1, flags);
        page[off + 3] = current;
        page[off + 4] = worst;
        for (int i = 0; i < 6; i++) page[off + 5 + i] = (byte)(raw >> (8 * i));
    }

    private static void Threshold(byte[] page, int slot, byte id, byte threshold)
    {
        int off = 2 + slot * 12;
        page[off] = id;
        page[off + 1] = threshold;
    }

    /// <summary>
    /// 代替待ちセクタ（197）が 8 個ある SATA の HDD。代替済み（5）は 0、CRC（199）は 3。
    /// 温度（194）の生値は上位に最低/最高温度を詰めた形（下位バイトが 38 ℃）。
    /// </summary>
    public static (byte[] Data, byte[] Thresholds) SataWithPendingSectors()
    {
        var d = new byte[512];
        var t = new byte[512];
        U16(d, 0, 0x0010);
        U16(t, 0, 0x0010);
        var rows = new (byte Id, ushort Flags, byte Cur, byte Worst, ulong Raw, byte Thr)[]
        {
            (1,   0x000F, 200, 200, 0, 51),
            (5,   0x0033, 200, 200, 0, 140),
            (9,   0x0032,  71,  71, 0x0000_0000_5208, 0),      // 21,000 時間
            (12,  0x0032, 100, 100, 1_234, 0),
            (194, 0x0022, 112,  96, 0x0000_2E14_0026, 0),      // 38 ℃（上位に 20/46 ℃）
            (196, 0x0032, 200, 200, 0, 0),
            (197, 0x0032, 200, 200, 8, 0),
            (198, 0x0030, 100, 253, 0, 0),
            (199, 0x0032, 200, 200, 3, 0),
        };
        for (int i = 0; i < rows.Length; i++)
        {
            var r = rows[i];
            Attr(d, i, r.Id, r.Flags, r.Cur, r.Worst, r.Raw);
            Threshold(t, i, r.Id, r.Thr);
        }
        return (d, t);
    }

    /// <summary>機器自身の閾値を割っている SATA（代替済みセクタの正規化値が 5、閾値 10）。</summary>
    public static (byte[] Data, byte[] Thresholds) SataFailingThreshold()
    {
        var d = new byte[512];
        var t = new byte[512];
        Attr(d, 0, 5, 0x0033, 5, 5, 3_000);
        Threshold(t, 0, 5, 10);
        Attr(d, 1, 194, 0x0022, 60, 50, 40);
        return (d, t);
    }

    /// <summary>
    /// STORAGE_DEVICE_DESCRIPTOR の見本。文字列は構造体の後ろに置き、位置で指す。
    /// </summary>
    public static byte[] DeviceDescriptor(string vendor, string product, string rev, string serial, uint busType)
    {
        var b = new byte[256];
        int pos = 40;
        uint Put(string s)
        {
            if (s.Length == 0) return 0;
            uint at = (uint)pos;
            foreach (char c in s) b[pos++] = (byte)c;
            b[pos++] = 0;
            return at;
        }
        U32(b, 0, 40);               // Version
        U32(b, 12, Put(vendor));
        U32(b, 16, Put(product));
        U32(b, 20, Put(rev));
        U32(b, 24, Put(serial));
        U32(b, 28, busType);
        U32(b, 4, (uint)pos);        // Size
        return b;
    }
}
