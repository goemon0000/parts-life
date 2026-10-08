using System.Runtime.InteropServices;

// 名前で読み込む DLL（pdh.dll・gdi32.dll・ntdll.dll など）は System32 からだけ探す。
// 既定では exe の隣も探すので、同じフォルダに置かれた同名の偽物を拾う余地がある。
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
