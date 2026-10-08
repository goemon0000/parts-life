using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PartsLife.Checks.Storage;

/// <summary>
/// 物理ディスクから健康情報を**読むだけ**。管理者で動く PartsLife.Check.exe からだけ呼ぶ。
///
/// ■ 開き方
/// - 素性（型番・シリアル・接続）: アクセス権 0 で開く（問い合わせだけ。管理者でなくても通る）
/// - NVMe のログページ: まず GENERIC_READ だけで開く。通らない機種に限り READ|WRITE で開き直す
/// - ATA の SMART: Windows が SMART_RCV_DRIVE_DATA を「読み書き権のある handle でのみ受け付ける」
///   と定義しているため READ|WRITE で開く。**投げる命令は SMART READ DATA / READ THRESHOLDS
///   の2つに固定**してあり（<see cref="StorageProtocol.BuildSmartRead"/>）、媒体には一切書かない。
///   それも通らなければ IOCTL_STORAGE_PREDICT_FAILURE（読み取り権だけで通る）で値だけ読む
/// - USB・RAID・仮想ディスクなど、分からない経路には**何も投げない**
/// </summary>
public static class StorageReader
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;

    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private const uint IOCTL_STORAGE_PREDICT_FAILURE = 0x002D1100;
    private const uint SMART_RCV_DRIVE_DATA = 0x0007C088;
    private const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;

    private const int ERROR_FILE_NOT_FOUND = 2;
    private const int ERROR_ACCESS_DENIED = 5;

    public const int MaxDisks = 64;

    /// <summary>全物理ディスクを読む。1台の失敗で全体を止めない。</summary>
    public static List<DiskReport> ReadAll(CancellationToken ct = default)
    {
        var list = new List<DiskReport>();
        for (int n = 0; n < MaxDisks; n++)
        {
            ct.ThrowIfCancellationRequested();
            var r = ReadOne(n);
            if (r is not null) list.Add(r);
        }
        return list;
    }

    /// <summary>1台ぶん。そのディスクが無ければ null。</summary>
    public static DiskReport? ReadOne(int number)
    {
        string path = $@"\\.\PhysicalDrive{number}";
        using var info = Open(path, 0);
        if (info.IsInvalid)
        {
            int err = Marshal.GetLastPInvokeError();
            if (err == ERROR_FILE_NOT_FOUND) return null;
            return new DiskReport { Number = number, Unavailable = L.T($"開けませんでした（{err}）", $"could not open (error {err})") };
        }

        var r = new DiskReport { Number = number };
        var desc = Query(info, IOCTL_STORAGE_QUERY_PROPERTY, StorageProtocol.BuildDeviceQuery(), 1024);
        if (desc is null)
        {
            r.Unavailable = L.T("素性を問い合わせられませんでした", "could not query the device descriptor");
            return r;
        }
        var id = DeviceIdentity.Parse(desc);
        r.Model = (id.Vendor.Length > 0 && !id.Model.StartsWith(id.Vendor, StringComparison.OrdinalIgnoreCase)
                   ? id.Vendor + " " : "") + id.Model;
        r.Serial = id.Serial.Trim().TrimEnd('.');
        r.Firmware = id.Revision;
        r.Bus = id.Bus;
        var geo = Query(info, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, Array.Empty<byte>(), 256);
        if (geo is not null) r.SizeBytes = StorageProtocol.ParseGeometryEx(geo).Size;

        r.Protocol = BusNames.ProtocolFor(id.Bus);
        switch (r.Protocol)
        {
            case "nvme": ReadNvme(path, r); break;
            case "ata": ReadAta(path, r, (byte)number); break;
            default:
                r.Unavailable = L.T($"この接続（{BusNames.Of(id.Bus)}）では健康情報を読めません。推測では出しません",
                                    $"health data is not available through this connection ({BusNames.Of(id.Bus)}); not guessing");
                break;
        }
        return r;
    }

    private static void ReadNvme(string path, DiskReport r)
    {
        byte[]? health = null, errors = null;
        foreach (uint access in new[] { GENERIC_READ, GENERIC_READ | GENERIC_WRITE })
        {
            using var h = Open(path, access);
            if (h.IsInvalid) continue;
            health = NvmeLog(h, StorageProtocol.NvmeLogHealthInfo, NvmeHealth.Length);
            if (health is null) continue;
            errors = NvmeLog(h, StorageProtocol.NvmeLogErrorInfo, NvmeErrorEntry.Length * 16);
            break;
        }
        if (health is null)
        {
            r.Unavailable = L.T("NVMe の健康情報を返してもらえませんでした（ドライバが対応していない可能性）",
                                "the NVMe driver did not return the health log (driver may not support it)");
            return;
        }
        r.NvmeHealthPage = health;
        r.NvmeErrorPage = errors;
    }

    private static byte[]? NvmeLog(SafeFileHandle h, byte page, int length)
    {
        var buf = StorageProtocol.BuildNvmeLogQuery(page, length);
        var outp = Query(h, IOCTL_STORAGE_QUERY_PROPERTY, buf, buf.Length);
        return outp is null ? null : StorageProtocol.ExtractProtocolData(outp, length);
    }

    private static void ReadAta(string path, DiskReport r, byte number)
    {
        using (var h = Open(path, GENERIC_READ | GENERIC_WRITE))
        {
            if (!h.IsInvalid)
            {
                var data = Smart(h, StorageProtocol.SmartReadData, number);
                if (data is not null)
                {
                    r.AtaSmartData = data;
                    r.AtaSmartThresholds = Smart(h, StorageProtocol.SmartReadThresholds, number);
                }
            }
        }
        if (r.AtaSmartData is null)
        {
            using var h = Open(path, GENERIC_READ);
            if (!h.IsInvalid)
            {
                var outp = Query(h, IOCTL_STORAGE_PREDICT_FAILURE, Array.Empty<byte>(), 516 + 16);
                if (outp is not null)
                {
                    var (predict, data) = StorageProtocol.ExtractPredictFailure(outp);
                    r.PredictFailure = predict;
                    if (data is not null) { r.AtaSmartData = data; r.AtaFromPredictFailure = true; }
                }
            }
        }
        if (r.AtaSmartData is null)
            r.Unavailable = L.T("SMART を返してもらえませんでした（SMART が無効か、間に変換基板がある可能性）",
                                "the drive did not return SMART data (disabled, or behind a bridge)");
    }

    private static byte[]? Smart(SafeFileHandle h, byte feature, byte number)
    {
        var input = StorageProtocol.BuildSmartRead(feature, number);
        var outp = Query(h, SMART_RCV_DRIVE_DATA, input, StorageProtocol.SendCmdOutSize, inputLength: StorageProtocol.SendCmdInSize);
        return outp is null ? null : StorageProtocol.ExtractSmart(outp);
    }

    // ---------------------------------------------------------------------
    private static SafeFileHandle Open(string path, uint access) =>
        CreateFileW(path, access, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

    /// <summary>DeviceIoControl。失敗したら null（理由は問わない。読めなかった、で足りる）。</summary>
    private static unsafe byte[]? Query(SafeFileHandle h, uint code, byte[] input, int outputLength, int? inputLength = null)
    {
        var output = new byte[outputLength];
        int inLen = inputLength ?? input.Length;
        fixed (byte* pi = input)
        fixed (byte* po = output)
        {
            if (!DeviceIoControl(h, code, inLen == 0 ? null : pi, inLen, po, output.Length, out int got, IntPtr.Zero))
                return null;
            return got <= 0 ? null : got == output.Length ? output : output.AsSpan(0, got).ToArray();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
                                                      uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern unsafe bool DeviceIoControl(SafeFileHandle h, uint code, void* inBuf, int inSize,
                                                       void* outBuf, int outSize, out int returned, IntPtr overlapped);

    internal static string Win32Message(int err) => new Win32Exception(err).Message;
}
