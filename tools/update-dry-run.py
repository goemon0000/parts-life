#!/usr/bin/env python3
"""
自動更新の通しの確かめ。**本物の Release には触らない。** Windows でだけ動く。

いまのソースから「いまの版（0.7.2）」と「偽の新しい版（9.9.9）」を Debug で発行し、
127.0.0.1 に偽の GitHub（releases/latest と添付物）を立てて、一時フォルダに置いた
アプリの写しを起こす。アプリが自分で確かめ・落とし・照合し・入れ替え・起こし直し・後片付けまで
やり切るのを、ファイルとプロセスを見て確かめる。

  場面 A … ZIP の置き方（PartsLife.exe ＋ PartsLife.Check.exe）→ PartsLife.zip で 9.9.9 へ
  場面 B … 単体の exe の置き方 → PartsLife.exe で 9.9.9 へ
  場面 C … 9.9.8 が起動直後に落ちる → 元の版（0.7.2）に戻し、9.9.8 を「失敗した版」に記す
  場面 D … 9.9.7（本物のアプリ。起動 3 秒後に落ちるようにしてある）に入れ替え、戻すときに .old を掴んでおいて
           「.old → PartsLife.exe」の付け替えを失敗させる → PartsLife.exe が消えずに残り、記録を残して
           起こし直した版が戻し直し、最後は 0.7.2 に戻る

行き先の差し替え（PARTSLIFE_UPDATE_TEST_FEED）・保存の置き場の差し替え（PARTSLIFE_TEST_DATA_DIR）・
試験用に落ちる版（PARTSLIFE_TEST_CRASH_VERSION）は
**Debug ビルドだけが読む。** 配布物（Release）では効かない（tests/PartsLife.Update.Tests で確かめている）。
進行の保存も更新の覚え書きも一時フォルダに書くので、普段使っている %APPDATA%\\PartsLife には触らない。

起動した写しの窓が数十秒ずつ画面の右上に出る。終わったら（失敗しても）写しのプロセスは止めて消す。

  python tools/update-dry-run.py            # 4 つとも
  python tools/update-dry-run.py A C        # 選んで
  python tools/update-dry-run.py --keep     # 一時フォルダを残す（中身を見たいとき）

補助 exe（NativeAOT）の発行には Visual Studio の C++ ビルドツールと vswhere.exe が要る（README の「ビルド」）。
"""
import hashlib, http.server, json, os, shutil, subprocess, sys, tempfile, threading, time, zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
APP = ROOT / "src/PartsLife/PartsLife.csproj"
HELPER = ROOT / "src/PartsLife.Check/PartsLife.Check.csproj"
VSWHERE = r"C:\Program Files (x86)\Microsoft Visual Studio\Installer"

CUR, NEW, BAD, CRASHY = "0.7.2", "9.9.9", "9.9.8", "9.9.7"


def run(cmd, **kw):
    print("  $", " ".join(str(c) for c in cmd), flush=True)
    env = dict(os.environ)
    env["PATH"] = env.get("PATH", "") + os.pathsep + VSWHERE
    r = subprocess.run([str(c) for c in cmd], env=env, capture_output=True, text=True, encoding="utf-8", errors="replace", **kw)
    if r.returncode != 0:
        print(r.stdout[-4000:], r.stderr[-4000:])
        raise SystemExit(f"失敗: {cmd[0]} ({r.returncode})")
    return r


def publish_app(out: Path, version: str, compressed: bool):
    """Debug の単一 exe（自己完結）。配る物と同じ形で、試験用の差し替えだけが効く。"""
    run(["dotnet", "publish", APP, "-c", "Debug", "-r", "win-x64", "--self-contained", "-o", out,
         "-p:PublishSingleFile=true", f"-p:EnableCompressionInSingleFile={'true' if compressed else 'false'}",
         "-p:IncludeNativeLibrariesForSelfExtract=true", f"-p:Version={version}"])
    return out / "PartsLife.exe"


def publish_helper(out: Path, version: str):
    run(["dotnet", "publish", HELPER, "-c", "Release", "-o", out, f"-p:Version={version}"])
    return out / "PartsLife.Check.exe"


def publish_crasher(work: Path, version: str):
    """起動してすぐ終わる exe（版の札は 9.9.8・名前は PartsLife.exe）。入れ替えた新しい版が落ちる場面の代わり。"""
    src = work / "crasher-src"
    src.mkdir(parents=True, exist_ok=True)
    (src / "crasher.csproj").write_text(f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
    <AssemblyName>PartsLife</AssemblyName><Version>{version}</Version>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier><SelfContained>false</SelfContained>
    <PublishSingleFile>true</PublishSingleFile>
  </PropertyGroup>
</Project>""", encoding="utf-8")
    (src / "Program.cs").write_text("return 3;\n", encoding="utf-8")
    out = work / "crasher"
    run(["dotnet", "publish", src / "crasher.csproj", "-c", "Release", "-o", out])
    return out / "PartsLife.exe"


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


def processes_outside(root: Path):
    """一時フォルダの外から起きている PartsLife.exe（普段使っている本物）。"""
    r = subprocess.run(["powershell", "-NoProfile", "-Command",
                        "Get-CimInstance Win32_Process -Filter \"Name='PartsLife.exe'\" | "
                        "ForEach-Object { $_.ExecutablePath }"],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    base = os.path.realpath(root).lower()
    return [p for p in r.stdout.splitlines() if p.strip() and not os.path.realpath(p).lower().startswith(base)]


def kill_under(root: Path):
    for pid, _ in processes_under(root):
        subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True)


def real_state_stamp():
    """
    普段の進行の保存（%APPDATA%\\PartsLife\\state.json）の更新時刻と大きさ。写しが触っていないことを確かめる。
    普段のアプリが動いていると 60 秒ごとに書くので、そのときは比べない（"skip"）。
    """
    if processes_outside(Path(tempfile.gettempdir())):
        return "skip"
    p = Path(os.environ["APPDATA"]) / "PartsLife" / "state.json"
    return (p.stat().st_mtime_ns, p.stat().st_size) if p.exists() else None


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


class OldHolder(threading.Thread):
    """
    場面 D: PartsLife.exe.old ができたらすぐ掴む（削除の共有なしで開く → 名前を変えられない）。
    戻しが 1 回失敗した（swap.json の RollbackAttempts が 1 以上）のを見たら、そのときのフォルダの様子を控えて放す。
    """
    def __init__(self, app: Path, upd: Path):
        super().__init__(daemon=True)
        self.app, self.upd = app, upd
        self.seen = None
        self.stop = False

    def run(self):
        old = self.app / "PartsLife.exe.old"
        fh = None
        t0 = time.time()
        while not self.stop and time.time() - t0 < 180:
            if fh is None and old.exists():
                try:
                    fh = open(old, "rb")
                except OSError:
                    pass
            if fh is not None:
                try:
                    rec = json.loads((self.upd / "swap.json").read_text("utf-8"))
                except (OSError, ValueError):
                    rec = {}
                if rec.get("RollbackAttempts", 0) >= 1:
                    self.seen = {
                        "exe": (self.app / "PartsLife.exe").exists(),
                        "old": old.exists(),
                        "requested": rec.get("RollbackRequested"),
                    }
                    fh.close()
                    return
            time.sleep(0.05)
        if fh is not None:
            fh.close()


def scenario(name, work, port, srv_feed, layout, release_version, release_assets, app_files, expect_version, rolled_back,
             extra_env=None, hold_old=False):
    print(f"\n== 場面 {name} ==")
    root = work / f"run-{name}"
    app, data = root / "app", root / "data"
    if root.exists():
        shutil.rmtree(root)
    app.mkdir(parents=True)
    data.mkdir(parents=True)
    for dst, src in app_files.items():
        shutil.copyfile(src, app / dst)
    shutil.rmtree(srv_feed, ignore_errors=True)
    make_feed(srv_feed, port, release_version, release_assets)
    Quiet.hits.clear()

    env = dict(os.environ)
    env["PARTSLIFE_UPDATE_TEST_FEED"] = f"http://127.0.0.1:{port}/"
    env["PARTSLIFE_TEST_DATA_DIR"] = str(data)
    env.update(extra_env or {})
    upd = data / "updates"
    real_before = real_state_stamp()
    holder = OldHolder(app, upd) if hold_old else None
    try:
        if holder:
            holder.start()
        subprocess.Popen([str(app / "PartsLife.exe")], cwd=str(app), env=env)
        wait_for("最新の Release を尋ねた", lambda: "/releases/latest" in Quiet.hits, 60)
        if holder:
            wait_for("戻しの 1 回目が失敗した（.old を掴んでいた）", lambda: holder.seen is not None, 120)
            check("そのとき PartsLife.exe は消えずに残っていた", holder.seen["exe"])
            check("そのとき .old も残っていた（次で戻し直せる）", holder.seen["old"])
            check("記録に「戻す」を残していた", holder.seen["requested"])
        if rolled_back:
            wait_for("戻した版が記録された", lambda: (upd / "update-state.json").exists()
                     and release_version in json.loads((upd / "update-state.json").read_text("utf-8")).get("FailedVersions", []), 180)
            wait_for("元の版がまた起きている", lambda: any(file_version(Path(p)).startswith(CUR) for _, p in processes_under(root)), 60)
            time.sleep(3)
        else:
            wait_for("入れ替えた（PartsLife.exe.old ができた）", lambda: (app / "PartsLife.exe.old").exists(), 120)
            wait_for("新しい版が「起動できた」と書いた", lambda: not (upd / "swap.json").exists()
                     or json.loads((upd / "swap.json").read_text("utf-8")).get("Confirmed"), 120)
            wait_for("後片付け（.old が消え、swap.json が消えた）",
                     lambda: not (app / "PartsLife.exe.old").exists() and not (upd / "swap.json").exists(), 120)

        procs = processes_under(root)
        check(f"写しのプロセスが 1 つだけ動いている（{len(procs)}）", len(procs) == 1)
        check(f"動いているのは v{expect_version}", file_version(Path(procs[0][1])).startswith(expect_version))
        check(f"置いてある PartsLife.exe は v{expect_version}", file_version(app / "PartsLife.exe").startswith(expect_version))
        if layout == "zip":
            check("PartsLife.Check.exe も同じ版", file_version(app / "PartsLife.Check.exe").startswith(expect_version))
        left = sorted(p.name for p in app.iterdir())
        want = ["PartsLife.Check.exe", "PartsLife.exe"] if layout == "zip" else ["PartsLife.exe"]
        check(f"フォルダに残っているのは {want}（{left}）", left == want)
        state = json.loads((upd / "update-state.json").read_text("utf-8"))
        check("用意（Pending）が残っていない", state.get("Pending") is None)
        staged = [p.name for p in upd.iterdir() if p.is_dir()]
        check(f"置き場が片付いている（{staged}）", staged == [])
        asked = [h for h in Quiet.hits]
        check(f"通信したのは偽の GitHub の決まった場所だけ（{asked}）",
              all(h == "/releases/latest" or h.startswith(f"/releases/download/v{release_version}/") for h in asked))
        check("進行は一時フォルダの state.json に書いた", (data / "state.json").exists())
        after = real_state_stamp()
        if "skip" in (real_before, after):
            print("  --: 普段のアプリが動いているので、普段の state.json の比べはしない")
        else:
            check("普段の %APPDATA%\\PartsLife\\state.json には触っていない", after == real_before)
    finally:
        if holder:
            holder.stop = True
        kill_under(root)


def main():
    if os.name != "nt":
        raise SystemExit("Windows でだけ動く")
    keep = "--keep" in sys.argv
    picked = [a for a in sys.argv[1:] if a in ("A", "B", "C", "D")] or ["A", "B", "C", "D"]
    work = Path(tempfile.mkdtemp(prefix="pl-update-dry-run-"))
    print("一時フォルダ:", work)
    try:
        print("\n== 発行 ==")
        cur_slim = publish_app(work / f"app-{CUR}-slim", CUR, compressed=False)
        new_slim = publish_app(work / f"app-{NEW}-slim", NEW, compressed=False)
        new_full = publish_app(work / f"app-{NEW}-full", NEW, compressed=True)
        cur_helper = publish_helper(work / f"helper-{CUR}", CUR)
        new_helper = publish_helper(work / f"helper-{NEW}", NEW)
        crasher = publish_crasher(work, BAD)
        crashy = publish_app(work / f"app-{CRASHY}-slim", CRASHY, compressed=False) if "D" in picked else None
        new_zip = work / "PartsLife.zip"
        with zipfile.ZipFile(new_zip, "w", zipfile.ZIP_DEFLATED) as z:
            z.write(new_slim, "PartsLife.exe")
            z.write(new_helper, "PartsLife.Check.exe")

        feed = work / "feed"
        feed.mkdir()
        srv = serve(feed)
        port = srv.server_address[1]
        print("偽の GitHub:", f"http://127.0.0.1:{port}/")

        if "A" in picked:
            scenario("A", work, port, feed, "zip", NEW,
                     {"PartsLife.zip": new_zip, "PartsLife.exe": new_full},
                     {"PartsLife.exe": cur_slim, "PartsLife.Check.exe": cur_helper}, NEW, rolled_back=False)
        if "B" in picked:
            scenario("B", work, port, feed, "exe", NEW,
                     {"PartsLife.zip": new_zip, "PartsLife.exe": new_full},
                     {"PartsLife.exe": cur_slim}, NEW, rolled_back=False)
        if "C" in picked:
            scenario("C", work, port, feed, "exe", BAD,
                     {"PartsLife.exe": crasher},
                     {"PartsLife.exe": cur_slim}, CUR, rolled_back=True)
        if "D" in picked:
            scenario("D", work, port, feed, "exe", CRASHY,
                     {"PartsLife.exe": crashy},
                     {"PartsLife.exe": cur_slim}, CUR, rolled_back=True,
                     extra_env={"PARTSLIFE_TEST_CRASH_VERSION": CRASHY}, hold_old=True)
        srv.shutdown()
        print("\nすべて通った")
    finally:
        kill_under(work)
        if keep:
            print("残した:", work)
        else:
            shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    main()
