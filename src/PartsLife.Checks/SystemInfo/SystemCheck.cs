using System.Management;
using Microsoft.Win32;

namespace PartsLife.Checks.SystemInfo;

public sealed record OsInfo(string Name, string DisplayVersion, int Build, int Ubr, TimeSpan Uptime);
public sealed record CpuInfo(string Name, int Cores, int Logical, int MaxMHz);
/// <param name="RatedMTs">Win32_PhysicalMemory.Speed（モジュールの定格）。</param>
/// <param name="ConfiguredMTs">Win32_PhysicalMemory.ConfiguredClockSpeed（いま動いている速さ）。</param>
public sealed record MemoryModule(string Slot, ulong Capacity, uint RatedMTs, uint ConfiguredMTs, string Manufacturer, string PartNumber);
public sealed record VideoInfo(string Name, string DriverVersion, DateTime? DriverDate, uint ErrorCode);
public sealed record VolumeInfo(string Letter, ulong Total, ulong Free);
public sealed record PhysicalDiskInfo(uint Number, string Model, string Bus, ulong Size, string Media,
                                      ushort? Health, ushort[] OperationalStatus, IReadOnlyList<VolumeInfo> Volumes);
public sealed record PagefileEntry(string Path, uint AllocatedMB, uint CurrentMB, uint PeakMB);
public sealed record PagefileInfo(bool? Automatic, IReadOnlyList<PagefileEntry> Files);
public sealed record EventSource(string Key, string Title, long? Errors, long? Warnings);

/// <summary>一項目ぶんの結果。<see cref="Group"/> で画面の見出しに振り分ける。</summary>
public sealed record SystemItem(string Group, Finding Finding);

/// <summary>
/// 管理者権限なしで取れる範囲の、機械の健康診断。
/// **取る処理（WMI・レジストリ・イベントログ）と、判定（純粋な関数）を分けてある。**
/// </summary>
public static class SystemCheck
{
    private const string StorageNs = @"\\.\root\microsoft\windows\storage";

    /// <summary>全部まとめて。WMI は遅いので UI スレッドで呼ばないこと。</summary>
    public static List<SystemItem> Run(CancellationToken ct = default)
    {
        var items = new List<SystemItem>();
        void Add(string group, Func<IEnumerable<Finding>> f)
        {
            ct.ThrowIfCancellationRequested();
            try { items.AddRange(f().Select(x => new SystemItem(group, x))); }
            catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                items.Add(new SystemItem(group, new Finding(group, Verdict.Unavailable, L.T("読めませんでした: ", "could not read: ") + ex.Message)));
            }
        }
        Add("OS", () => new[] { JudgeOs(ReadOs()) });
        Add("CPU", () => ReadCpus().Select(JudgeCpu));
        Add(L.T("メモリ", "Memory"), () => JudgeMemory(ReadMemory()));
        Add("GPU", () => ReadVideo().Select(v => JudgeVideo(v, DateTime.Now)));
        Add(L.T("ディスク", "Disks"), () => ReadDisks().SelectMany(JudgeDisk));
        Add(L.T("ページファイル", "Pagefile"), () => new[] { JudgePagefile(ReadPagefile()) });
        Add(L.T("イベントログ（7日）", "Event log (7 days)"), () => ReadEvents().Select(JudgeEvents));
        return items;
    }

    // =====================================================================
    // 取る
    // =====================================================================
    public static OsInfo ReadOs()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        string name = k?.GetValue("ProductName") as string ?? "Windows";
        string disp = k?.GetValue("DisplayVersion") as string ?? "";
        int build = int.TryParse(k?.GetValue("CurrentBuild") as string, out var b) ? b : Environment.OSVersion.Version.Build;
        int ubr = k?.GetValue("UBR") is int u ? u : 0;
        // Windows 11 でもレジストリの製品名は "Windows 10" のまま（Microsoft の互換のため）
        if (build >= 22000) name = name.Replace("Windows 10", "Windows 11");
        return new OsInfo(name, disp, build, ubr, TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    public static List<CpuInfo> ReadCpus()
    {
        var list = new List<CpuInfo>();
        using var q = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
        foreach (ManagementObject o in q.Get())
            list.Add(new CpuInfo((o["Name"] as string ?? "").Trim(), ToInt(o["NumberOfCores"]), ToInt(o["NumberOfLogicalProcessors"]), ToInt(o["MaxClockSpeed"])));
        return list;
    }

    public static List<MemoryModule> ReadMemory()
    {
        var list = new List<MemoryModule>();
        using var q = new ManagementObjectSearcher(
            "SELECT DeviceLocator, BankLabel, Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber FROM Win32_PhysicalMemory");
        foreach (ManagementObject o in q.Get())
        {
            string slot = (o["DeviceLocator"] as string ?? "").Trim();
            string bank = (o["BankLabel"] as string ?? "").Trim();
            if (bank.Length > 0 && !slot.Contains(bank, StringComparison.OrdinalIgnoreCase)) slot = $"{bank} / {slot}";
            list.Add(new MemoryModule(slot, ToULong(o["Capacity"]), (uint)ToInt(o["Speed"]), (uint)ToInt(o["ConfiguredClockSpeed"]),
                                      (o["Manufacturer"] as string ?? "").Trim(), (o["PartNumber"] as string ?? "").Trim()));
        }
        return list;
    }

    public static List<VideoInfo> ReadVideo()
    {
        var list = new List<VideoInfo>();
        using var q = new ManagementObjectSearcher("SELECT Name, DriverVersion, DriverDate, ConfigManagerErrorCode FROM Win32_VideoController");
        foreach (ManagementObject o in q.Get())
        {
            DateTime? date = null;
            if (o["DriverDate"] is string s && s.Length >= 14)
                try { date = ManagementDateTimeConverter.ToDateTime(s); } catch (ArgumentOutOfRangeException) { }
            list.Add(new VideoInfo((o["Name"] as string ?? "").Trim(), o["DriverVersion"] as string ?? "", date, (uint)ToInt(o["ConfigManagerErrorCode"])));
        }
        return list;
    }

    public static List<PhysicalDiskInfo> ReadDisks()
    {
        var letters = new Dictionary<uint, List<string>>();
        using (var parts = new ManagementObjectSearcher(StorageNs, "SELECT DriveLetter, DiskNumber FROM MSFT_Partition"))
        {
            foreach (ManagementObject p in parts.Get())
            {
                var raw = p["DriveLetter"];
                if (raw is null) continue;
                char letter = raw is char c ? c : (char)Convert.ToUInt16(raw);
                if (!char.IsLetter(letter)) continue;
                uint disk = (uint)ToInt(p["DiskNumber"]);
                (letters.TryGetValue(disk, out var l) ? l : letters[disk] = new()).Add($"{char.ToUpperInvariant(letter)}:");
            }
        }

        var drives = DriveInfo.GetDrives().Where(d => { try { return d.IsReady; } catch { return false; } })
                              .ToDictionary(d => d.Name.TrimEnd('\\').ToUpperInvariant(), d => d);
        var list = new List<PhysicalDiskInfo>();
        using var q = new ManagementObjectSearcher(StorageNs,
            "SELECT DeviceId, FriendlyName, Model, BusType, Size, MediaType, HealthStatus, OperationalStatus FROM MSFT_PhysicalDisk");
        foreach (ManagementObject o in q.Get())
        {
            uint n = (uint)ToInt(o["DeviceId"]);
            var vols = new List<VolumeInfo>();
            foreach (var letter in letters.GetValueOrDefault(n) ?? new())
            {
                if (!drives.TryGetValue(letter, out var d)) continue;
                try { vols.Add(new VolumeInfo(letter, (ulong)d.TotalSize, (ulong)d.TotalFreeSpace)); }
                catch (IOException) { }
            }
            string model = (o["FriendlyName"] as string ?? o["Model"] as string ?? "").Trim();
            ushort[] op = o["OperationalStatus"] is ushort[] arr ? arr : Array.Empty<ushort>();
            ushort? health = o["HealthStatus"] is null ? null : Convert.ToUInt16(o["HealthStatus"]);
            list.Add(new PhysicalDiskInfo(n, model, BusName(ToInt(o["BusType"])), ToULong(o["Size"]),
                                          MediaName(ToInt(o["MediaType"])), health, op, vols.OrderBy(v => v.Letter).ToList()));
        }
        return list.OrderBy(d => d.Number).ToList();
    }

    public static PagefileInfo ReadPagefile()
    {
        bool? auto = null;
        using (var cs = new ManagementObjectSearcher("SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem"))
            foreach (ManagementObject o in cs.Get())
                if (o["AutomaticManagedPagefile"] is bool b) auto = b;
        var files = new List<PagefileEntry>();
        using var q = new ManagementObjectSearcher("SELECT Name, AllocatedBaseSize, CurrentUsage, PeakUsage FROM Win32_PageFileUsage");
        foreach (ManagementObject o in q.Get())
            files.Add(new PagefileEntry(o["Name"] as string ?? "", (uint)ToInt(o["AllocatedBaseSize"]),
                                        (uint)ToInt(o["CurrentUsage"]), (uint)ToInt(o["PeakUsage"])));
        return new PagefileInfo(auto, files);
    }

    /// <summary>System ログの、ハードウェアと記憶装置まわりの出どころ。</summary>
    public static List<EventSource> ReadEvents()
    {
        var specs = new (string Key, string Title, string[] Providers)[]
        {
            ("whea", "WHEA-Logger", new[] { "Microsoft-Windows-WHEA-Logger" }),
            ("disk", "disk", new[] { "disk" }),
            ("ntfs", "Ntfs", new[] { "Ntfs", "Microsoft-Windows-Ntfs" }),
            ("stornvme", "stornvme", new[] { "stornvme" }),
            ("volmgr", "volmgr", new[] { "volmgr" }),
        };
        var list = specs.Select(s => new EventSource(s.Key, s.Title,
            EventCounts.Count(s.Providers, new[] { 1, 2 }), EventCounts.Count(s.Providers, new[] { 3 }))).ToList();
        list.Add(new EventSource("kp41", "Kernel-Power 41",
            EventCounts.Count(new[] { "Microsoft-Windows-Kernel-Power" }, new[] { 1, 2, 3, 4 }, eventId: 41), null));
        return list;
    }

    // =====================================================================
    // 判定（純粋な関数）
    // =====================================================================
    public static Finding JudgeOs(OsInfo os)
    {
        string up = L.T($"起動から {(int)os.Uptime.TotalDays} 日 {os.Uptime.Hours} 時間", $"up {(int)os.Uptime.TotalDays} d {os.Uptime.Hours} h");
        string ver = $"{os.Name} {os.DisplayVersion} (build {os.Build}.{os.Ubr})".Replace("  ", " ");
        return os.Uptime.TotalDays > 30
            ? new Finding("OS", Verdict.Caution, $"{ver}{L.T("、", ", ")}{up}" + L.T("。長く再起動していません（更新が溜まっている可能性）", ". Not restarted for a long time (updates may be pending)"))
            : new Finding("OS", Verdict.Ok, $"{ver}{L.T("、", ", ")}{up}");
    }

    public static Finding JudgeCpu(CpuInfo c) =>
        new(c.Name, Verdict.Ok, L.T($"{c.Cores} コア / {c.Logical} 論理プロセッサ、定格 {c.MaxMHz / 1000.0:0.0} GHz",
                                    $"{c.Cores} cores / {c.Logical} logical processors, rated {c.MaxMHz / 1000.0:0.0} GHz"));

    public static IEnumerable<Finding> JudgeMemory(IReadOnlyList<MemoryModule> mods)
    {
        if (mods.Count == 0)
        {
            yield return new Finding(L.T("メモリ", "Memory"), Verdict.Unavailable, L.T("モジュールの情報が返ってきませんでした", "no module information returned"));
            yield break;
        }
        ulong total = mods.Aggregate(0UL, (s, m) => s + m.Capacity);
        bool mixed = mods.Select(m => m.Capacity).Distinct().Count() > 1;
        yield return new Finding(L.T("合計", "Total"), mixed ? Verdict.Caution : Verdict.Ok,
            L.T($"{total / 1073741824.0:0} GB（{mods.Count} 枚）", $"{total / 1073741824.0:0} GB ({mods.Count} modules)") +
            (mixed ? L.T("。容量の違うモジュールが混ざっています（デュアルチャネルが一部効かないことがあります）",
                         ". Modules of different sizes are mixed (dual channel may only partly apply)") : ""));
        foreach (var m in mods) yield return JudgeModule(m);
    }

    public static Finding JudgeModule(MemoryModule m)
    {
        string title = m.Slot.Length > 0 ? m.Slot : L.T("モジュール", "Module");
        string size = $"{m.Capacity / 1073741824.0:0} GB";
        string part = m.PartNumber.Length > 0 ? $"  {m.PartNumber}" : "";
        if (m.ConfiguredMTs == 0 || m.RatedMTs == 0)
            return new Finding(title, Verdict.Ok, size + L.T("（速度は報告されていません）", " (speed not reported)") + part);
        if (m.ConfiguredMTs < m.RatedMTs)
            return new Finding(title, Verdict.Caution,
                L.T($"{size}。情報: 定格 {m.RatedMTs} に対し {m.ConfiguredMTs} MT/s で動いています。設定次第でよくあることで、故障ではありません",
                    $"{size}. Info: running at {m.ConfiguredMTs} MT/s, below its rated {m.RatedMTs}. Common with default settings; not a fault") + part);
        if (m.ConfiguredMTs > m.RatedMTs)
            return new Finding(title, Verdict.Ok,
                L.T($"{size}、{m.ConfiguredMTs} MT/s（標準の {m.RatedMTs} より速い設定。XMP/EXPO）",
                    $"{size}, {m.ConfiguredMTs} MT/s (above the standard {m.RatedMTs}; XMP/EXPO)") + part);
        return new Finding(title, Verdict.Ok, $"{size}、{m.ConfiguredMTs} MT/s" + part);
    }

    public static Finding JudgeVideo(VideoInfo v, DateTime now)
    {
        string drv = L.T($"ドライバ {v.DriverVersion}", $"driver {v.DriverVersion}") +
                     (v.DriverDate is { } d ? $" ({d:yyyy-MM-dd})" : "");
        if (v.ErrorCode == 22)   // 「このデバイスは無効です」= 利用者か設定が止めている。故障ではない
            return new Finding(v.Name, Verdict.Ok, L.T("無効になっています（コード 22）。使っていないなら問題ありません。", "disabled (code 22). Fine if you do not use it. ") + drv);
        if (v.ErrorCode != 0)
            return new Finding(v.Name, Verdict.Check, L.T($"デバイスがエラーを報告しています（コード {v.ErrorCode}）。", $"the device reports an error (code {v.ErrorCode}). ") + drv);
        if (v.Name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) || v.Name.Contains("基本ディスプレイ"))
            return new Finding(v.Name, Verdict.Caution, L.T("標準のドライバで動いています（GPU 本来の機能が使えません）", "running on the generic driver (GPU features unavailable)"));
        if (v.DriverDate is { } dd && (now - dd).TotalDays > 730)
            return new Finding(v.Name, Verdict.Caution, drv + L.T("。2 年以上前のドライバです", ". The driver is over two years old"));
        return new Finding(v.Name, Verdict.Ok, drv);
    }

    public static IEnumerable<Finding> JudgeDisk(PhysicalDiskInfo d)
    {
        string title = $"#{d.Number} {d.Model}";
        string basics = $"{d.Bus} {d.Media}, {d.Size / 1e9:0} GB";
        var bad = new ushort[] { 3, 5, 6, 7 };   // Degraded / Predictive Failure / Error / Non-Recoverable
        if (d.Health == 2 || d.OperationalStatus.Any(bad.Contains))
            yield return new Finding(title, Verdict.Check, basics + L.T($"。Windows の判定: 異常（{StatusText(d)}）", $". Windows reports: unhealthy ({StatusText(d)})"));
        else if (d.Health == 1)
            yield return new Finding(title, Verdict.Caution, basics + L.T($"。Windows の判定: 警告（{StatusText(d)}）", $". Windows reports: warning ({StatusText(d)})"));
        else if (d.Health == 0)
            yield return new Finding(title, Verdict.Ok, basics + L.T("。Windows の判定: 正常", ". Windows reports: healthy"));
        else
            yield return new Finding(title, Verdict.Unavailable, basics + L.T("。Windows の判定は読めませんでした", ". Windows health status not available"));

        foreach (var v in d.Volumes)
        {
            double ratio = v.Total == 0 ? 1 : (double)v.Free / v.Total;
            var verdict = ratio < 0.03 ? Verdict.Check : ratio < 0.10 ? Verdict.Caution : Verdict.Ok;
            string text = L.T($"空き {v.Free / 1e9:0} / {v.Total / 1e9:0} GB（{ratio * 100:0}%）", $"{v.Free / 1e9:0} of {v.Total / 1e9:0} GB free ({ratio * 100:0}%)");
            if (verdict != Verdict.Ok) text += L.T("。空きが少ないと遅くなり、更新に失敗することもあります", ". Low free space slows things down and can break updates");
            yield return new Finding($"  {v.Letter}", verdict, text);
        }
    }

    private static string StatusText(PhysicalDiskInfo d) =>
        $"HealthStatus={d.Health?.ToString() ?? "-"}, OperationalStatus={string.Join("/", d.OperationalStatus)}";

    public static Finding JudgePagefile(PagefileInfo p)
    {
        string title = L.T("ページファイル", "Pagefile");
        if (p.Files.Count == 0)
            return new Finding(title, Verdict.Caution, L.T("ありません。メモリが尽きたときに落ちやすく、クラッシュの記録も残りにくくなります",
                                                             "none. Apps crash more easily when memory runs out, and crash dumps may not be written"));
        var parts = p.Files.Select(f => L.T($"{f.Path} 割当 {f.AllocatedMB / 1024.0:0.0} GB・最大使用 {f.PeakMB / 1024.0:0.0} GB",
                                            $"{f.Path} {f.AllocatedMB / 1024.0:0.0} GB, peak {f.PeakMB / 1024.0:0.0} GB"));
        string text = string.Join(" / ", parts) + (p.Automatic == true ? L.T("（自動管理）", " (system managed)") : "");
        bool tight = p.Files.Any(f => f.AllocatedMB > 0 && f.PeakMB > f.AllocatedMB * 0.8);
        return tight
            ? new Finding(title, Verdict.Caution, text + L.T("。最大使用が割当の 8 割を超えたことがあります", ". Peak use exceeded 80% of the allocation"))
            : new Finding(title, Verdict.Ok, text);
    }

    public static Finding JudgeEvents(EventSource e)
    {
        if (e.Key == "kp41")
        {
            if (e.Errors is null) return new Finding(e.Title, Verdict.Unavailable, L.T("ログを読めませんでした", "could not read the log"));
            return e.Errors > 0
                ? new Finding(e.Title, Verdict.Caution, L.T($"{e.Errors} 回。正常に終了せずに再起動した記録です（停電・電源ボタンの長押し・フリーズ・電源の力不足など）",
                                                             $"{e.Errors} time(s). The PC restarted without shutting down cleanly (power loss, forced power-off, freeze, weak PSU…)"))
                : new Finding(e.Title, Verdict.Ok, L.T("0 回", "none"));
        }
        if (e.Errors is null) return new Finding(e.Title, Verdict.Unavailable, L.T("ログを読めませんでした", "could not read the log"));
        string warn = e.Warnings is > 0 ? L.T($"、警告 {e.Warnings} 件", $", {e.Warnings} warning(s)") : "";
        if (e.Key == "whea")
        {
            if (e.Errors > 0)
                return new Finding(e.Title, Verdict.Check, L.T($"エラー {e.Errors} 件{warn}。ハードウェアが訂正しきれなかった誤りを報告しています（CPU・メモリ・PCIe）",
                                                                $"{e.Errors} error(s){warn}. Hardware reported errors it could not fully correct (CPU, memory, PCIe)"));
            if (e.Warnings > 0)
                return new Finding(e.Title, Verdict.Caution, L.T($"警告 {e.Warnings} 件。訂正済みのハードウェアエラーです。増え続けるなら要注意",
                                                                  $"{e.Warnings} warning(s): corrected hardware errors. Watch if they keep growing"));
            return new Finding(e.Title, Verdict.Ok, L.T("0 件", "none"));
        }
        if (e.Errors > 0)
            return new Finding(e.Title, e.Errors >= 10 ? Verdict.Check : Verdict.Caution,
                L.T($"エラー {e.Errors} 件{warn}。記憶装置か、その接続まわりの誤りです", $"{e.Errors} error(s){warn}: storage or its connection"));
        return new Finding(e.Title, Verdict.Ok, L.T("エラー 0 件", "no errors") + warn);
    }

    // =====================================================================
    private static int ToInt(object? o) { try { return o is null ? 0 : Convert.ToInt32(o); } catch { return 0; } }
    private static ulong ToULong(object? o) { try { return o is null ? 0 : Convert.ToUInt64(o); } catch { return 0; } }

    private static string BusName(int bus) => bus switch
    {
        17 => "NVMe", 11 => "SATA", 3 => "ATA", 7 => "USB", 8 => "RAID", 10 => "SAS", 1 => "SCSI",
        14 or 15 => L.T("仮想", "Virtual"), 16 => L.T("記憶域", "Spaces"), 12 => "SD", _ => $"Bus{bus}",
    };

    private static string MediaName(int media) => media switch
    {
        3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "",
    };
}
