#!/usr/bin/env python3
"""
自動更新の通しの確かめ。**本物の Release には触らない。** Windows でだけ動く。

このリポジトリの最後の版 v0.8.0 のために、**実際の v0.7.4 → v0.8.0** を確かめる。
  - 古い側（v0.7.4）は、コミット 25e4571 を git archive で一時フォルダへ取り出して Debug で発行する
    （この作業ツリーにも、ほかの作業フォルダにも置かない。終わったら一時フォルダごと消す）
  - 新しい側（v0.8.0）は、いまの作業ツリーを Debug で発行する
127.0.0.1 に偽の GitHub（releases/latest と添付物）を立て、一時フォルダに置いた古い版の写しを起こす。
古い版が自分で確かめ・落とし・照合し・入れ替え・起こし直し・後片付けまでやり切るのを、ファイルとプロセスを見て確かめる。

  場面 A … ZIP の置き方（PartsLife.exe ＋ PartsLife.Check.exe）→ PartsLife.zip で 0.8.0 へ。
           入れ替わったあと、案内の窓が出ている・案内を押していないので final-notice.json が無い・
           常駐の Mutex を本当に握っている（名前を別の実装で組み立てて確かめる）・0.8.0 は更新を確かめに行かない
  場面 B … 単体の exe の置き方 → PartsLife.exe で 0.8.0 へ（A と同じ確かめ。補助 exe の有無だけが違う）
  場面 C … 0.8.0 が起動直後に落ちる（PARTSLIFE_TEST_CRASH_VERSION=0.8.0）→ 0.7.4 に戻し、0.8.0 を「失敗した版」に記す
  場面 D … 常駐の Mutex（試験用の置き場の目印つき）をこのスクリプトが握っておく → 入れ替えた 0.8.0 は
           「もう一方が動いています」のお知らせだけ出して終わり、
           保存も swap.json（起動の回数も）も読まず・書かず、起動確認（Confirmed）を書かない → 0.7.4 に戻る

以前あった 9.9.x の偽の版の場面（.old を掴んで戻しを失敗させる場面など）は、この版の確かめには要らないので外した
（git の履歴にある）。

行き先の差し替え（PARTSLIFE_UPDATE_TEST_FEED）・保存の置き場の差し替え（PARTSLIFE_TEST_DATA_DIR）・
試験用に落ちる版（PARTSLIFE_TEST_CRASH_VERSION）は **Debug ビルドだけが読む。**
配布物（Release）では効かない（tests/PartsLife.Update.Tests で確かめている）。
進行の保存も更新の覚え書きも最後の案内の既読も一時フォルダに書くので、普段使っている
%APPDATA%\\PartsLife・%LOCALAPPDATA%\\PartsLife には触らない。**このスクリプトは、それらを読みも書きもしない**
（以前あった普段の state.json の更新時刻を見る確かめは、外した）。レジストリにも触らない。

起動した写しの窓が数十秒ずつ画面に出る。**窓を触らないこと（右クリックの献立は本物の Run の設定を読む）。**
終わったら（失敗しても）写しのプロセスは止めて消す。

  python tools/update-dry-run.py            # 4 つとも
  python tools/update-dry-run.py A C        # 選んで
  python tools/update-dry-run.py --keep     # 一時フォルダを残す（中身を見たいとき）

補助 exe（NativeAOT）の発行には Visual Studio の C++ ビルドツールと vswhere.exe が要る（README の「ビルド」）。
発行できなければ A は走らせられない（B・C・D は単体の exe だけで走る）。そのときは終了コード 3 で、走らせなかった場面を言う。
"""
import csv, ctypes, hashlib, http.server, io, json, os, shutil, subprocess, sys, tarfile, tempfile, threading, time, zipfile
from ctypes import wintypes
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
APP = "src/PartsLife/PartsLife.csproj"
HELPER = "src/PartsLife.Check/PartsLife.Check.csproj"
VSWHERE = r"C:\Program Files (x86)\Microsoft Visual Studio\Installer"

OLD_REV = "25e4571"          # v0.7.4
CUR, NEW = "0.7.4", "0.8.0"
NOTICE_TITLE = "パーツぐらし"   # 案内の窓・「もう一方が動いています」の窓の題（主画面は "Parts Party"）


def run(cmd, **kw):
    print("  $", " ".join(str(c) for c in cmd), flush=True)
    env = dict(os.environ)
    env["PATH"] = env.get("PATH", "") + os.pathsep + VSWHERE
    r = subprocess.run([str(c) for c in cmd], env=env, capture_output=True, text=True, encoding="utf-8", errors="replace", **kw)
    if r.returncode != 0:
        print(r.stdout[-4000:], r.stderr[-4000:])
        raise SystemExit(f"失敗: {cmd[0]} ({r.returncode})")
    return r


def publish_app(proj_root: Path, out: Path, version: str, compressed: bool):
    """Debug の単一 exe（自己完結）。配る物と同じ形で、試験用の差し替えだけが効く。"""
    run(["dotnet", "publish", proj_root / APP, "-c", "Debug", "-r", "win-x64", "--self-contained", "-o", out,
         "-p:PublishSingleFile=true", f"-p:EnableCompressionInSingleFile={'true' if compressed else 'false'}",
         "-p:IncludeNativeLibrariesForSelfExtract=true", f"-p:Version={version}"])
    return out / "PartsLife.exe"


def publish_helper(proj_root: Path, out: Path, version: str):
    run(["dotnet", "publish", proj_root / HELPER, "-c", "Release", "-o", out, f"-p:Version={version}"])
    return out / "PartsLife.Check.exe"


def extract_old_source(work: Path) -> Path:
    """古い版のソースを、git archive で一時フォルダへ取り出す（作業ツリーは触らない）。"""
    r = subprocess.run(["git", "-C", str(ROOT), "archive", "--format=tar", OLD_REV], capture_output=True)
    if r.returncode != 0:
        raise SystemExit("git archive に失敗: " + r.stderr.decode("utf-8", "replace"))
    dest = work / "old-src"
    dest.mkdir()
    with tarfile.open(fileobj=io.BytesIO(r.stdout)) as t:
        t.extractall(dest)   # 自分のリポジトリから取り出した物だけ
    return dest


def sha(p: Path) -> str:
    return hashlib.sha256(p.read_bytes()).hexdigest()


def file_version(p: Path) -> str:
    r = run(["powershell", "-NoProfile", "-Command", f"(Get-Item -LiteralPath '{p}').VersionInfo.FileVersion"])
    return r.stdout.strip()


def make_feed(feed: Path, port: int, version: str, assets: dict):
    """偽の GitHub。release.yml と同じ添付物の名前・SHA256.txt の形・API の欄で。"""
    tag = "v" + version
    dl = feed / "releases/download" / tag
    dl.mkdir(parents=True, exist_ok=True)
    for name, src in assets.items():
        shutil.copyfile(src, dl / name)
    lines = [f"{sha(dl / n)}  {n}" for n in assets]
    (dl / "SHA256.txt").write_bytes(("\r\n".join(lines) + "\r\n").encode("utf-8"))
    base = f"http://127.0.0.1:{port}/releases"
    names = list(assets) + ["SHA256.txt"]
    rel = {
        "tag_name": tag, "html_url": f"{base}/tag/{tag}", "draft": False, "prerelease": False,
        "assets": [{"name": n, "state": "uploaded", "size": (dl / n).stat().st_size,
                    "browser_download_url": f"{base}/download/{tag}/{n}",
                    "digest": "sha256:" + sha(dl / n)} for n in names],
    }
    (feed / "releases").mkdir(parents=True, exist_ok=True)
    (feed / "releases/latest").write_text(json.dumps(rel), encoding="utf-8")


class Quiet(http.server.SimpleHTTPRequestHandler):
    hits = []

    def log_message(self, fmt, *args):
        Quiet.hits.append(self.path)


def serve(feed: Path):
    handler = lambda *a, **k: Quiet(*a, directory=str(feed), **k)
    srv = http.server.ThreadingHTTPServer(("127.0.0.1", 0), handler)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    return srv


def processes_under(root: Path):
    """写しのフォルダから起きている PartsLife.exe（ほかの場所の本物は数えない・止めない）。"""
    r = subprocess.run(["powershell", "-NoProfile", "-Command",
                        "Get-CimInstance Win32_Process -Filter \"Name='PartsLife.exe'\" | "
                        "ForEach-Object { \"$($_.ProcessId)|$($_.ExecutablePath)\" }"],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    out = []
    base = os.path.realpath(root).lower()   # 一時フォルダは 8.3 の短い名前で来ることがある。長い名前に揃えて比べる
    for line in r.stdout.splitlines():
        pid, _, path = line.partition("|")
        if path and os.path.realpath(path).lower().startswith(base):
            out.append((int(pid), path))
    return out


def kill_under(root: Path):
    for pid, _ in processes_under(root):
        subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True)


def wait_for(what, cond, timeout):
    t0 = time.time()
    while time.time() - t0 < timeout:
        v = cond()
        if v:
            print(f"  ok: {what} ({time.time() - t0:.0f} 秒)")
            return v
        time.sleep(1)
    raise AssertionError(f"時間内にならなかった: {what}")


def check(what, ok):
    if not ok:
        raise AssertionError(what)
    print(f"  ok: {what}")


# ---------------------------------------------------------------------------
# Windows の窓と Mutex（自分の写しのプロセスの窓の題だけ見る。レジストリには触らない）
# ---------------------------------------------------------------------------
_user32 = ctypes.WinDLL("user32", use_last_error=True)
_kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
_WNDENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
_user32.EnumWindows.argtypes = [_WNDENUMPROC, wintypes.LPARAM]
_user32.IsWindowVisible.argtypes = [wintypes.HWND]
_user32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
_user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
_kernel32.OpenProcess.restype = wintypes.HANDLE
_kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
_kernel32.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
_kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
_kernel32.CreateMutexW.restype = wintypes.HANDLE
_kernel32.CreateMutexW.argtypes = [wintypes.LPVOID, wintypes.BOOL, wintypes.LPCWSTR]
_kernel32.OpenMutexW.restype = wintypes.HANDLE
_kernel32.OpenMutexW.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.LPCWSTR]
_kernel32.WaitForSingleObject.restype = wintypes.DWORD
_kernel32.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
_kernel32.ReleaseMutex.argtypes = [wintypes.HANDLE]

WAIT_OBJECT_0, WAIT_ABANDONED, WAIT_TIMEOUT = 0, 0x80, 0x102
SYNCHRONIZE = 0x00100000


def _image_path(pid: int) -> str:
    h = _kernel32.OpenProcess(0x1000, False, pid)   # PROCESS_QUERY_LIMITED_INFORMATION
    if not h:
        return ""
    try:
        buf = ctypes.create_unicode_buffer(1024)
        n = wintypes.DWORD(1024)
        return buf.value if _kernel32.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(n)) else ""
    finally:
        _kernel32.CloseHandle(h)


def visible_window_titles(root: Path):
    """写しのフォルダの exe が持つ、見えている窓の題。ほかのプロセスの窓は見ない。"""
    base = os.path.realpath(root).lower()
    found = []

    def cb(hwnd, _):
        if not _user32.IsWindowVisible(hwnd):
            return True
        pid = wintypes.DWORD()
        _user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        path = _image_path(pid.value)
        if path and os.path.realpath(path).lower().startswith(base):
            buf = ctypes.create_unicode_buffer(256)
            _user32.GetWindowTextW(hwnd, buf, 256)
            found.append(buf.value)
        return True

    _user32.EnumWindows(_WNDENUMPROC(cb), 0)
    return found


def current_sid() -> str:
    r = subprocess.run(["whoami", "/user", "/fo", "csv", "/nh"], capture_output=True, text=True, encoding="utf-8", errors="replace")
    return next(csv.reader(io.StringIO(r.stdout.strip())))[1]


def resident_mutex_name(data: Path) -> str:
    """
    常駐の Mutex の名前（試験用の置き場の目印つき）。C# 側（ResidentMutex）とは別に、規則だけから組み立てる:
    Global\\PCMieru.PartsLife.Resident.v1.<SID の SHA-256 先頭 16 桁> + "." + <置き場（小文字）の SHA-256 先頭 16 桁>
    """
    h = lambda s: hashlib.sha256(s.encode("utf-8")).hexdigest()[:16].lower()
    return r"Global\PCMieru.PartsLife.Resident.v1." + h(current_sid()) + "." + h(str(data).lower())


def mutex_is_held_by_someone(name: str):
    """名前の Mutex があって、いま誰かが握っているか（None=その名前の Mutex が無い）。SYNCHRONIZE だけで開く。"""
    h = _kernel32.OpenMutexW(SYNCHRONIZE, False, name)
    if not h:
        return None
    try:
        r = _kernel32.WaitForSingleObject(h, 0)
        return r == WAIT_TIMEOUT   # 取れてしまったら握られていなかった（その場で閉じる。放すのは、握った側が消えるとき）
    finally:
        _kernel32.CloseHandle(h)


class MutexHolder(threading.Thread):
    """場面 D: 常駐の Mutex をこのスレッドで握っておく（Mutex は握ったスレッドが生きている間だけ持てる）。"""
    def __init__(self, name):
        super().__init__(daemon=True)
        self.name_ = name
        self.ready = threading.Event()
        self.release = threading.Event()
        self.ok = False

    def run(self):
        h = _kernel32.CreateMutexW(None, False, self.name_)
        if h:
            self.ok = _kernel32.WaitForSingleObject(h, 0) in (WAIT_OBJECT_0, WAIT_ABANDONED)
        self.ready.set()
        self.release.wait()
        if h:
            if self.ok:
                _kernel32.ReleaseMutex(h)
            _kernel32.CloseHandle(h)


class Watcher(threading.Thread):
    """場面 C・D: 入れ替えの間じゅう swap.json を見て、起動の回数と、起動確認（Confirmed）が一度でも書かれたかを控える。"""
    def __init__(self, upd: Path, root: Path, want_windows: bool):
        super().__init__(daemon=True)
        self.upd, self.root, self.want_windows = upd, root, want_windows
        self.stop = False
        self.max_launches = 0
        self.ever_confirmed = False
        self.saw_notice_window = False

    def run(self):
        last_win = 0.0
        while not self.stop:
            try:
                rec = json.loads((self.upd / "swap.json").read_text("utf-8"))
                self.max_launches = max(self.max_launches, rec.get("Launches", 0))
                if rec.get("Confirmed"):
                    self.ever_confirmed = True
            except (OSError, ValueError):
                pass
            if self.want_windows and not self.saw_notice_window and time.time() - last_win > 0.3:
                last_win = time.time()
                self.saw_notice_window = NOTICE_TITLE in visible_window_titles(self.root)
            time.sleep(0.05)


def scenario(name, work, port, feed, layout, old_files, release_assets, expect_version, rolled_back, extra_env=None, hold_mutex=False):
    print(f"\n== 場面 {name} ==")
    root = work / f"run-{name}"
    app, data = root / "app", root / "data"
    if root.exists():
        shutil.rmtree(root)
    app.mkdir(parents=True)
    data.mkdir(parents=True)
    # 本物の置き場に触らない歯止め: 試験用の置き場は、必ずこの一時フォルダの中
    check("試験用の置き場は一時フォルダの中", os.path.realpath(data).lower().startswith(os.path.realpath(work).lower()))
    for dst, src in old_files.items():
        shutil.copyfile(src, app / dst)
    shutil.rmtree(feed, ignore_errors=True)
    make_feed(feed, port, NEW, release_assets)
    Quiet.hits.clear()

    env = dict(os.environ)
    env["PARTSLIFE_UPDATE_TEST_FEED"] = f"http://127.0.0.1:{port}/"
    env["PARTSLIFE_TEST_DATA_DIR"] = str(data)
    env.update(extra_env or {})
    upd = data / "updates"
    mutex_name = resident_mutex_name(data)
    holder = MutexHolder(mutex_name) if hold_mutex else None
    watcher = Watcher(upd, root, want_windows=hold_mutex)
    try:
        if holder:
            holder.start()
            holder.ready.wait(5)
            check("このスクリプトが常駐の Mutex を握った", holder.ok)
        watcher.start()
        subprocess.Popen([str(app / "PartsLife.exe")], cwd=str(app), env=env)
        wait_for("最新の Release を尋ねた（0.7.4）", lambda: "/releases/latest" in Quiet.hits, 120)

        if rolled_back:
            wait_for("戻した版（0.8.0）が記録された", lambda: (upd / "update-state.json").exists()
                     and NEW in json.loads((upd / "update-state.json").read_text("utf-8")).get("FailedVersions", []), 240)
            wait_for("元の版（0.7.4）がまた起きている", lambda: any(file_version(Path(p)).startswith(CUR) for _, p in processes_under(root)), 90)
            time.sleep(3)
            check("0.8.0 は起動確認（Confirmed）を一度も書かなかった", not watcher.ever_confirmed)
            if hold_mutex:
                # 常駐の Mutex は保存を読む前に握る。取れなければ、読まず・書かず（swap.json の起動の回数にも触れず）に終わる
                check("0.8.0 は「もう一方が動いています」のお知らせの窓を出した（これで起こされたと分かる）", watcher.saw_notice_window)
                check(f"0.8.0 は swap.json にも触れずに終わった（起動の回数 {watcher.max_launches}）", watcher.max_launches == 0)
            else:
                check(f"0.8.0 は起こされた（起動の回数 {watcher.max_launches}）", watcher.max_launches >= 1)
        else:
            wait_for("入れ替えた（PartsLife.exe.old ができた）", lambda: (app / "PartsLife.exe.old").exists(), 180)
            wait_for("0.8.0 が「起動できた」と書いた", lambda: not (upd / "swap.json").exists()
                     or json.loads((upd / "swap.json").read_text("utf-8")).get("Confirmed"), 120)
            hits_at_confirmed = len(Quiet.hits)
            wait_for("後片付け（.old が消え、swap.json が消えた）",
                     lambda: not (app / "PartsLife.exe.old").exists() and not (upd / "swap.json").exists(), 120)
            # 0.8.0 は更新を確かめに行かない（古い版なら、試験の置き場では起動の 3 秒後に最初の確認をする）。
            # 起動確認のあと十分に待って、偽の GitHub への要求が増えていないこと
            time.sleep(10)
            check(f"0.8.0 は更新を確かめに行かなかった（起動確認の時点 {hits_at_confirmed} 件・10 秒以上あと {len(Quiet.hits)} 件）",
                  len(Quiet.hits) == hits_at_confirmed)
            titles = visible_window_titles(root)
            check(f"案内の窓が出ている（見えている窓: {titles}）", NOTICE_TITLE in titles)
            check("案内は押していないので final-notice.json は無い", not (data / "final-notice.json").exists())
            held = mutex_is_held_by_someone(mutex_name)
            check("0.8.0 は常駐の Mutex を握っている（名前は別の実装で組み立てた）", held is True)

        procs = processes_under(root)
        check(f"写しのプロセスが 1 つだけ動いている（{len(procs)}）", len(procs) == 1)
        check(f"動いているのは v{expect_version}", file_version(Path(procs[0][1])).startswith(expect_version))
        check(f"置いてある PartsLife.exe は v{expect_version}", file_version(app / "PartsLife.exe").startswith(expect_version))
        if layout == "zip":
            check(f"PartsLife.Check.exe も v{expect_version}", file_version(app / "PartsLife.Check.exe").startswith(expect_version))
        left = sorted(p.name for p in app.iterdir())
        want = ["PartsLife.Check.exe", "PartsLife.exe"] if layout == "zip" else ["PartsLife.exe"]
        check(f"フォルダに残っているのは {want}（{left}）", left == want)
        state = json.loads((upd / "update-state.json").read_text("utf-8"))
        check("用意（Pending）が残っていない", state.get("Pending") is None)
        if rolled_back:
            check(f"失敗した版に {NEW} が記されている", NEW in state.get("FailedVersions", []))
        staged = [p.name for p in upd.iterdir() if p.is_dir()]
        check(f"置き場が片付いている（{staged}）", staged == [])
        asked = list(Quiet.hits)
        check(f"通信したのは偽の GitHub の決まった場所だけ（{len(asked)} 件）",
              all(h == "/releases/latest" or h.startswith(f"/releases/download/v{NEW}/") for h in asked))
        check("進行は一時フォルダの state.json に書いた", (data / "state.json").exists())
        print("  置き場の中身:", sorted(p.name for p in data.iterdir()))
    finally:
        watcher.stop = True
        if holder:
            holder.release.set()
        kill_under(root)


def main():
    if os.name != "nt":
        raise SystemExit("Windows でだけ動く")
    keep = "--keep" in sys.argv
    picked = [a for a in sys.argv[1:] if a in ("A", "B", "C", "D")] or ["A", "B", "C", "D"]
    work = Path(tempfile.mkdtemp(prefix="pl-update-dry-run-"))
    print("一時フォルダ:", work)
    head = subprocess.run(["git", "-C", str(ROOT), "rev-parse", "--short", "HEAD"], capture_output=True, text=True).stdout.strip()
    dirty = subprocess.run(["git", "-C", str(ROOT), "status", "--porcelain"], capture_output=True, text=True).stdout.strip()
    print(f"新しい側: 作業ツリー（HEAD {head}、未コミットの変更 {len(dirty.splitlines())} 件）／古い側: {OLD_REV}")
    skipped = []
    try:
        print("\n== 発行 ==")
        old_root = extract_old_source(work)
        cur_slim = publish_app(old_root, work / f"app-{CUR}-slim", CUR, compressed=False)
        new_slim = publish_app(ROOT, work / f"app-{NEW}-slim", NEW, compressed=False)
        new_full = publish_app(ROOT, work / f"app-{NEW}-full", NEW, compressed=True)
        cur_helper = new_helper = None
        if "A" in picked:
            try:
                cur_helper = publish_helper(old_root, work / f"helper-{CUR}", CUR)
                new_helper = publish_helper(ROOT, work / f"helper-{NEW}", NEW)
            except SystemExit as e:
                print(f"補助 exe（NativeAOT）を発行できなかった: {e}")
                skipped.append("A")
        new_zip = work / "PartsLife.zip"
        if new_helper is not None:
            with zipfile.ZipFile(new_zip, "w", zipfile.ZIP_DEFLATED) as z:
                z.write(new_slim, "PartsLife.exe")
                z.write(new_helper, "PartsLife.Check.exe")
        else:
            # 単体の exe の置き方（B・C・D）では ZIP は使われない。ZIP の添付物の欄だけ埋める
            with zipfile.ZipFile(new_zip, "w", zipfile.ZIP_DEFLATED) as z:
                z.write(new_slim, "PartsLife.exe")

        feed = work / "feed"
        feed.mkdir()
        srv = serve(feed)
        port = srv.server_address[1]
        print("偽の GitHub:", f"http://127.0.0.1:{port}/")

        assets = {"PartsLife.zip": new_zip, "PartsLife.exe": new_full}
        if "A" in picked and "A" not in skipped:
            scenario("A", work, port, feed, "zip", {"PartsLife.exe": cur_slim, "PartsLife.Check.exe": cur_helper},
                     assets, NEW, rolled_back=False)
        if "B" in picked:
            scenario("B", work, port, feed, "exe", {"PartsLife.exe": cur_slim}, assets, NEW, rolled_back=False)
        if "C" in picked:
            scenario("C", work, port, feed, "exe", {"PartsLife.exe": cur_slim}, assets, CUR, rolled_back=True,
                     extra_env={"PARTSLIFE_TEST_CRASH_VERSION": NEW})
        if "D" in picked:
            scenario("D", work, port, feed, "exe", {"PartsLife.exe": cur_slim}, assets, CUR, rolled_back=True,
                     hold_mutex=True)
        srv.shutdown()
        ran = [s for s in picked if s not in skipped]
        print(f"\n通った場面: {' '.join(ran)}")
        if skipped:
            print(f"走らせられなかった場面: {' '.join(skipped)}（補助 exe を発行できない）")
            sys.exit(3)
        print("すべて通った")
    finally:
        kill_under(work)
        if keep:
            print("残した:", work)
        else:
            shutil.rmtree(work, ignore_errors=True)
            if work.exists():
                print("一時フォルダを消し切れなかった（掴まれている物がある）:", work)


if __name__ == "__main__":
    main()
