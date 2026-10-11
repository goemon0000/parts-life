using System.Runtime.InteropServices;
using System.Runtime.Versioning;

// 名前で読み込む DLL（wintrust.dll）は System32 からだけ探す。本体・点検と揃える
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace PartsLife.Update;

public enum SignatureKind { Unsigned, Valid, Invalid }

/// <summary>exe の Authenticode 署名の様子。Subject は署名した証明書の名乗り（Valid のときだけ）。</summary>
public readonly record struct SignatureInfo(SignatureKind Kind, string? Subject)
{
    public static readonly SignatureInfo Unsigned = new(SignatureKind.Unsigned, null);
}

/// <summary>
/// 新しい exe の署名を、いま動いている exe の署名と比べる。**純粋な関数**（試験で中身を確かめる）。
///
///   いまの exe が署名済み → 新しい exe も有効な署名で、名乗り（Subject）が同じであること
///   いまの exe が無署名   → 新しい exe は無署名でも通す（SHA256.txt との照合は必ずする）。
///                          ただし「署名が壊れている」物は通さない
///
/// TODO(署名): SignPath の署名つきの Release が出たら、無署名の更新は断る。
///   いまは署名の申請中で、配っている exe が全部無署名のため、ここで断ると誰も更新できない。
///   署名つきの版に一度上がれば、上の「いまの exe が署名済み」の決まりで以後は無署名を断る。
///   無署名の版のまま残っている人を守るには、ここで Unsigned を断る（RequireSignature を true にする）。
/// </summary>
public static class SignaturePolicy
{
    /// <summary>無署名の更新を断るか。署名つきの Release が出たら true にする（上の TODO）。</summary>
    public const bool RequireSignature = false;

    /// <returns>通すなら null、断るなら理由。</returns>
    public static string? Check(SignatureInfo running, SignatureInfo candidate, string name)
    {
        if (candidate.Kind == SignatureKind.Invalid) return $"{name} の署名が正しくない";
#pragma warning disable CS0162 // RequireSignature は定数
        if (RequireSignature && candidate.Kind != SignatureKind.Valid) return $"{name} に署名が無い";
#pragma warning restore CS0162
        if (running.Kind == SignatureKind.Valid)
        {
            if (candidate.Kind != SignatureKind.Valid) return $"{name} に署名が無い（いまの版は署名つき）";
            if (!string.Equals(running.Subject, candidate.Subject, StringComparison.Ordinal))
                return $"{name} の署名の名乗りが、いまの版と違う";
        }
        return null;
    }
}

/// <summary>
/// WinVerifyTrust で署名を確かめる（P/Invoke。リフレクションを使わないので AOT でも動く）。
///
/// **確かめる間に通信しない。** 失効の確認（CRL / OCSP）は外し、証明書の取りに行きも
/// 手元にある物だけに限る（WTD_CACHE_ONLY_URL_RETRIEVAL）。この更新の仕組みが通信するのは GitHub だけ、という約束のため。
/// その代わり、手元に無い中間証明書を要る署名は「正しくない」になって更新が止まる（安全な側に倒れる）。
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class Authenticode
{
    public static SignatureInfo Inspect(string path)
    {
        var (hr, subject) = Verify(path);
        if (hr == TRUST_E_NOSIGNATURE) return SignatureInfo.Unsigned;
        if (hr != 0 || string.IsNullOrEmpty(subject)) return new SignatureInfo(SignatureKind.Invalid, null);
        return new SignatureInfo(SignatureKind.Valid, subject);
    }

    private const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_REVOCATION_CHECK_NONE = 0x10;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WintrustData data);

    [LibraryImport("wintrust.dll")]
    private static partial IntPtr WTHelperProvDataFromStateData(IntPtr hStateData);

    [LibraryImport("wintrust.dll")]
    private static partial IntPtr WTHelperGetProvSignerFromChain(IntPtr pProvData, uint idxSigner,
        [MarshalAs(UnmanagedType.Bool)] bool fCounterSigner, uint idxCounterSigner);

    [LibraryImport("wintrust.dll")]
    private static partial IntPtr WTHelperGetProvCertFromChain(IntPtr pSgnr, uint idxCert);

    /// <summary>
    /// 確かめた署名の、署名者の証明書の名乗り。**WinVerifyTrust が確かめたその証明書から読む**
    /// （ファイルを別の道具で読み直すと、確かめた物と読んだ物が食い違う余地が残る）。
    /// CRYPT_PROVIDER_CERT の先頭は DWORD cbStruct、その次（ポインタの幅に揃う）が pCert。
    /// </summary>
    private static string? SignerSubject(IntPtr state)
    {
        IntPtr prov = WTHelperProvDataFromStateData(state);
        if (prov == IntPtr.Zero) return null;
        IntPtr sgnr = WTHelperGetProvSignerFromChain(prov, 0, false, 0);
        if (sgnr == IntPtr.Zero) return null;
        IntPtr provCert = WTHelperGetProvCertFromChain(sgnr, 0);
        if (provCert == IntPtr.Zero) return null;
        IntPtr ctx = Marshal.ReadIntPtr(provCert, IntPtr.Size);
        if (ctx == IntPtr.Zero) return null;
        using var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(ctx);   // 写しを作る（元は CLOSE で消える）
        return cert.Subject;
    }

    private static unsafe (int Hr, string? Subject) Verify(string path)
    {
        fixed (char* p = path)
        {
            var file = new WintrustFileInfo
            {
                cbStruct = (uint)sizeof(WintrustFileInfo),
                pcwszFilePath = (IntPtr)p,
            };
            var data = new WintrustData
            {
                cbStruct = (uint)sizeof(WintrustData),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = (IntPtr)(&file),
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL,
            };
            var action = GenericVerifyV2;
            // hwnd に INVALID_HANDLE_VALUE（-1）を渡すと、どんな場合も画面を出さない
            int hr = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            try
            {
                return (hr, hr == 0 ? SignerSubject(data.hWVTStateData) : null);
            }
            finally
            {
                data.dwStateAction = WTD_STATEACTION_CLOSE;
                WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            }
        }
    }
}
