using System.Security.Cryptography;

namespace PartsLife.Update;

/// <summary>
/// Release に添えてある SHA256.txt（release.yml の「中身の指紋を出す」が作る）。
/// 1 行に「64 桁の16進  名前」。Get-FileHash / sha256sum の出す形と同じ。
///
///   72b90b00…78ac  PartsLife.zip
///   2c0f3321…88de  PartsLife.exe
///
/// **名前は大文字小文字まで一致した物だけ。** 同じ名前が違う値で 2 回出てきたら、その名前は照合に使わない。
/// </summary>
public sealed class Sha256List
{
    /// <summary>SHA256.txt の上限。実物は 162 バイト。</summary>
    public const int MaxBytes = 64 * 1024;

    private readonly Dictionary<string, string?> _byName = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Names => _byName.Keys;

    public static Sha256List Parse(string text)
    {
        var list = new Sha256List();
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r', ' ', '\t');
            if (line.Length > 0 && line[0] == '﻿') line = line[1..];
            // 「値・空白・(空白か *)・名前」。* は sha256sum の二進の印
            if (line.Length < 66 || line[64] != ' ' || line[65] is not (' ' or '*')) continue;
            string hex = line[..64], name = line[66..];
            if (!IsHex64(hex) || name.Length == 0) continue;
            hex = hex.ToLowerInvariant();
            if (list._byName.TryGetValue(name, out var seen) && seen != hex) list._byName[name] = null;   // 食い違い
            else list._byName[name] = hex;
        }
        return list;
    }

    /// <summary>その名前の値（小文字の16進）。無い・食い違っていれば null。</summary>
    public string? For(string name) => _byName.GetValueOrDefault(name);

    public static bool IsHex64(string s)
    {
        if (s.Length != 64) return false;
        foreach (char c in s) if (!char.IsAsciiHexDigit(c)) return false;
        return true;
    }

    /// <summary>ファイルの SHA-256（小文字の16進）。</summary>
    public static string HashFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    /// <summary>値が一致するか。大文字小文字は問わない。片方でも空なら偽。</summary>
    public static bool Same(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
