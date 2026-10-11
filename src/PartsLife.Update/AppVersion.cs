using System.Globalization;

namespace PartsLife.Update;

/// <summary>
/// 版の数（X.Y.Z）。タグは <c>vX.Y.Z</c>。
///
/// **読めるのは数字 3 つだけ。** <c>v1.2.3-beta</c> や <c>v1.2</c> は読めない（＝更新の対象にしない）。
/// 試験版のタグを間違って最新にしてしまっても、自動で配られないようにするため。
/// </summary>
public readonly record struct AppVersion(int Major, int Minor, int Patch) : IComparable<AppVersion>
{
    public static bool TryParse(string? text, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(text)) return false;
        var s = text.AsSpan();
        if (s[0] is 'v' or 'V') s = s[1..];

        Span<int> parts = stackalloc int[3];
        for (int i = 0; i < 3; i++)
        {
            int dot = i < 2 ? s.IndexOf('.') : s.Length;
            if (dot <= 0 || dot > 6) return false;
            var digits = s[..dot];
            foreach (char c in digits) if (c is < '0' or > '9') return false;
            parts[i] = int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
            s = i < 2 ? s[(dot + 1)..] : ReadOnlySpan<char>.Empty;
        }
        version = new AppVersion(parts[0], parts[1], parts[2]);
        return true;
    }

    /// <summary>アセンブリの版（0.7.2.0）から。4 つ目は使わない。</summary>
    public static AppVersion From(Version? v) =>
        v is null ? default : new AppVersion(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build));

    public int CompareTo(AppVersion other) =>
        Major != other.Major ? Major.CompareTo(other.Major)
      : Minor != other.Minor ? Minor.CompareTo(other.Minor)
      : Patch.CompareTo(other.Patch);

    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(AppVersion a, AppVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(AppVersion a, AppVersion b) => a.CompareTo(b) <= 0;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
    public string Tag => "v" + ToString();
}
