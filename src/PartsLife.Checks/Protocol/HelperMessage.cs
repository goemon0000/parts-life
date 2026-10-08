using System.Text.Json.Serialization;
using PartsLife.Checks.Scan;
using PartsLife.Checks.Storage;

namespace PartsLife.Checks.Protocol;

/// <summary>
/// 補助 exe（管理者）→ 点検の窓（利用者）へ流れる1行。JSON Lines。
///
/// 逆向きに流れるのは <c>stop</c> の1語だけ。**補助 exe に別の仕事をさせる道は無い。**
/// 何をするかは起動時の引数で決まり、それは UAC の確認画面に出る。
/// </summary>
public sealed class HelperMessage
{
    public const string Hello = "hello";
    public const string Disks = "disks";
    public const string Progress = "progress";
    public const string Scan = "scan";
    public const string Error = "error";
    public const string Done = "done";

    public string Type { get; set; } = "";
    public int Pid { get; set; }
    public string? Message { get; set; }
    public List<DiskReport>? DiskReports { get; set; }
    public ScanProgress? ScanProgress { get; set; }
    public ScanResult? ScanResult { get; set; }

    public const string StopCommand = "stop";

    /// <summary>窓から補助 exe へ送ってよいのはこの1語だけ（前後の空白・改行は無視）。</summary>
    public static bool IsStopCommand(string? line) => line is not null && line.Trim() == StopCommand;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HelperMessage))]
public partial class HelperJson : JsonSerializerContext { }
