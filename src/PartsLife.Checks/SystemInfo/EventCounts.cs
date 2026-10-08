using System.Runtime.InteropServices;

namespace PartsLife.Checks.SystemInfo;

/// <summary>
/// System イベントログの件数を数える（wevtapi.dll を直接呼ぶ。新しい依存を増やさない）。
/// System ログは管理者でなくても読める（対話ログオンの利用者に読み取り権がある）。
/// **中身は読まない。数えるだけ。**
/// </summary>
public static class EventCounts
{
    public const long Week = 7L * 24 * 60 * 60 * 1000;

    /// <summary>
    /// 指定した出どころ・水準の件数。読めなければ null（「0 件」とは言わない）。
    /// </summary>
    /// <param name="providers">Provider の Name（例: "disk", "Microsoft-Windows-WHEA-Logger"）。</param>
    /// <param name="levels">1=重大, 2=エラー, 3=警告。</param>
    public static long? Count(IEnumerable<string> providers, IEnumerable<int> levels, long withinMs = Week, int? eventId = null)
    {
        string query = BuildQuery(providers, levels, withinMs, eventId);
        IntPtr h = EvtQuery(IntPtr.Zero, "System", query, 0x1 /*EvtQueryChannelPath*/ | 0x100 /*TolerateQueryErrors*/);
        if (h == IntPtr.Zero) return null;
        try
        {
            long count = 0;
            var events = new IntPtr[64];
            while (EvtNext(h, events.Length, events, 5000, 0, out int got))
            {
                for (int i = 0; i < got; i++) EvtClose(events[i]);
                count += got;
                if (count > 1_000_000) break;
            }
            int err = Marshal.GetLastPInvokeError();
            return err is 259 /*ERROR_NO_MORE_ITEMS*/ or 0 ? count : null;
        }
        finally { EvtClose(h); }
    }

    /// <summary>XPath の組み立て。**純粋な関数**（テスト用に公開）。</summary>
    public static string BuildQuery(IEnumerable<string> providers, IEnumerable<int> levels, long withinMs, int? eventId)
    {
        string prov = string.Join(" or ", providers.Select(p => $"@Name='{p.Replace("'", "")}'"));
        string lv = string.Join(" or ", levels.Select(l => $"Level={l}"));
        string id = eventId is { } e ? $" and EventID={e}" : "";
        return $"*[System[Provider[{prov}] and ({lv}){id} and TimeCreated[timediff(@SystemTime) <= {withinMs}]]]";
    }

    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr EvtQuery(IntPtr session, string path, string query, int flags);

    [DllImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtNext(IntPtr resultSet, int size, [Out] IntPtr[] events, int timeout, int flags, out int returned);

    [DllImport("wevtapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtClose(IntPtr handle);
}
