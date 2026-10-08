using System.Runtime.InteropServices;

// 名前で読み込む DLL（d3dcompiler_47.dll・pdh.dll・wevtapi.dll など）は System32 からだけ探す。
// 既定ではアセンブリの隣も探すので、同じフォルダに置かれた同名の偽物を拾う余地がある。
// この library は管理者の補助 exe（PartsLife.Check）にも入る。nvml.dll は Nvml.cs が絶対パスで読む。
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
