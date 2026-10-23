"""Build macOS app bundles and ZIPs; --dmg on macOS also creates signed, verified DMGs.

Usage:  python packaging/package_macos.py            (run from any directory)
Output: dist/TableEdit-macos-arm64.zip, dist/TableEdit-macos-x64.zip

Zip entries carry Unix permissions so the executable bit survives building on Windows.
Native macOS builds sign before packaging. Cross-builds retain the first-run signing helper.
"""
import argparse
from contextlib import nullcontext
import platform
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path
from macos_distribution import make_dmg, signing_from_environment

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "src" / "TableEdit.csproj"
PKG = ROOT / "packaging"
DIST = ROOT / "dist"
RIDS = ["osx-arm64", "osx-x64"]

FIRST_RUN = """#!/bin/bash
# Removes the download quarantine flag and ad-hoc signs TableEdit so macOS will launch it.
set -euo pipefail
cd "$(dirname "$0")"
xattr -cr TableEdit.app
codesign --force --deep --sign - TableEdit.app
echo "TableEdit is ready. You can drag it to /Applications."
open TableEdit.app
"""


def run(*args: str) -> None:
    print("+", " ".join(args))
    subprocess.run(args, check=True)


def build(rid: str, icns: Path, signer=None, dmg=False) -> Path:
    publish = DIST / "publish" / rid
    shutil.rmtree(publish, ignore_errors=True)
    run("dotnet", "publish", str(SRC), "-c", "Release", "-r", rid, "--self-contained", "true",
        "-p:UseAppHost=true", "-p:PublishSingleFile=true", "-p:DebugType=None", "-o", str(publish))

    app = DIST / rid / "TableEdit.app"
    shutil.rmtree(app.parent, ignore_errors=True)
    macos = app / "Contents" / "MacOS"
    resources = app / "Contents" / "Resources"
    shutil.copytree(publish, macos)
    resources.mkdir(parents=True)
    shutil.copy2(PKG / "Info.plist", app / "Contents" / "Info.plist")
    shutil.copy2(icns, resources / "TableEdit.icns")
    # Cross-publishing from Windows may not preserve the executable mode on disk.
    (macos / 'TableEdit').chmod(0o755)
    for library in macos.glob('*.dylib'):
        library.chmod(0o755)
    if signer:
        signer.prepare_app(app)
    if dmg:
        make_dmg(app, rid, signer)

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
        helper = FIRST_RUN if signer is None else '#!/bin/bash\nset -euo pipefail\ncd "$(dirname "$0")"\nopen TableEdit.app\n'
        z.writestr(info, helper.replace("\r\n", "\n"))
    print(f"-> {out}")
    return out


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dmg', action='store_true', help='Create disk images (requires macOS).')
    parser.add_argument('rids', nargs='*', help='osx-arm64 and/or osx-x64; defaults to both.')
    args = parser.parse_args()
    if args.dmg and platform.system() != 'Darwin':
        parser.error('--dmg requires macOS (hdiutil and codesign); use the GitHub release workflow.')
    rids = args.rids or RIDS
    if any(rid not in RIDS for rid in rids):
        parser.error('Only osx-arm64 and osx-x64 are supported.')
    DIST.mkdir(exist_ok=True)
    icns = DIST / "TableEdit.icns"
    run(sys.executable, "-I", str(PKG / "make_icon.py"), str(icns))
    with signing_from_environment() if platform.system() == 'Darwin' else nullcontext(None) as signer:
        for rid in rids:
            build(rid, icns, signer, args.dmg)
        if signer:
            (DIST / 'macos-signing-status.txt').write_text(signer.status + '\n')


if __name__ == "__main__":
    main()
