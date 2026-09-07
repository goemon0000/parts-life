using Microsoft.Win32;

namespace PartsLife;

/// <summary>
/// Windows と一緒に開くかどうか。
///
/// **HKCU の Run に書く。**（HKLM ではない）
/// HKLM だと管理者権限が要る。常駐の玩具がそれを求めるのは筋が悪いし、
/// 「管理者権限を要求しない」という約束を破ることになる。
/// HKCU なら本人の設定として書けて、消すのも本人だけで済む。
///
/// **タスクスケジューラは使わない。** 確実だが、消し方が分かりにくい。
/// レジストリの Run なら「スタートアップアプリ」の一覧に出て、
/// 利用者が Windows の設定画面からいつでも切れる。
/// </summary>
public static class Startup
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "PartsLife";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(Key);
                return k?.GetValue(Name) is string s && s.Length > 0;
            }
            catch { return false; }
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            if (k is null) return;
            if (enabled)
            {
                string? exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return;
                // 空白を含む場所に置かれることがあるので引用符で囲む
                k.SetValue(Name, $"\"{exe}\"");
            }
            else k.DeleteValue(Name, throwOnMissingValue: false);
        }
        catch { /* 書けなくても本体は動く */ }
    }
}
