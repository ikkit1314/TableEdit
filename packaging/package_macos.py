"""Cross-publishes TableEdit for macOS (arm64 + x64) and assembles zipped .app bundles.

Usage:  python packaging/package_macos.py            (run from any directory)
Output: dist/TableEdit-macos-arm64.zip, dist/TableEdit-macos-x64.zip

Zip entries carry Unix permissions so the executable bit survives building on Windows.
The bundle is not code-signed here; first-run.command (inside the zip) ad-hoc signs it on the Mac.
"""
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "src" / "TableEdit.csproj"
PKG = ROOT / "packaging"
DIST = ROOT / "dist"
RIDS = ["osx-arm64", "osx-x64"]

FIRST_RUN = """#!/bin/bash
# Removes the download quarantine flag and ad-hoc signs TableEdit so macOS will launch it.
cd "$(dirname "$0")"
xattr -cr TableEdit.app
codesign --force --deep --sign - TableEdit.app
echo "TableEdit is ready. You can drag it to /Applications."
open TableEdit.app
"""


def run(*args: str) -> None:
    print("+", " ".join(args))
    subprocess.run(args, check=True)


def build(rid: str, icns: Path) -> Path:
    publish = DIST / "publish" / rid
    shutil.rmtree(publish, ignore_errors=True)
    run("dotnet", "publish", str(SRC), "-c", "Release", "-r", rid, "--self-contained", "true",
        "-p:UseAppHost=true", "-p:DebugType=None", "-o", str(publish))

    app = DIST / rid / "TableEdit.app"
    shutil.rmtree(app.parent, ignore_errors=True)
    macos = app / "Contents" / "MacOS"
    resources = app / "Contents" / "Resources"
    shutil.copytree(publish, macos)
    resources.mkdir(parents=True)
    shutil.copy2(PKG / "Info.plist", app / "Contents" / "Info.plist")
    shutil.copy2(icns, resources / "TableEdit.icns")

    out = DIST / f"TableEdit-macos-{rid.split('-')[1]}.zip"
    out.unlink(missing_ok=True)
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        def add(path: Path, arcname: str, executable: bool) -> None:
            info = zipfile.ZipInfo.from_file(path, arcname)
            info.create_system = 3  # Unix, so external_attr permissions are honoured
            info.external_attr = ((0o100755 if executable else 0o100644) << 16)
            info.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(info, path.read_bytes())

        for f in sorted(app.rglob("*")):
            if f.is_file():
                rel = f.relative_to(app.parent).as_posix()
                exe = f.parent == macos and (f.name == "TableEdit" or f.suffix == ".dylib")
                add(f, rel, exe)

        info = zipfile.ZipInfo("first-run.command")
        info.create_system = 3
        info.external_attr = 0o100755 << 16
        z.writestr(info, FIRST_RUN.replace("\r\n", "\n"))
    print(f"-> {out}")
    return out


def main() -> None:
    DIST.mkdir(exist_ok=True)
    icns = DIST / "TableEdit.icns"
    run(sys.executable, "-I", str(PKG / "make_icon.py"), str(icns))
    rids = sys.argv[1:] or RIDS
    for rid in rids:
        build(rid, icns)


if __name__ == "__main__":
    main()
