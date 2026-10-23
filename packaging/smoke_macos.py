"""Check that the packaged app for this Mac's architecture survives initial startup."""
import os
from pathlib import Path
import platform
import subprocess
import tempfile
import time

if platform.system() != 'Darwin':
    raise SystemExit('This startup check requires macOS.')

arch = 'arm64' if platform.machine() == 'arm64' else 'x64'
root = Path(__file__).resolve().parent.parent
host = root / f'dist/osx-{arch}/TableEdit.app/Contents/MacOS/TableEdit'
with tempfile.TemporaryFile(mode='w+') as log:
    process = subprocess.Popen([str(host)], stdout=log, stderr=subprocess.STDOUT)
    try:
        time.sleep(5)
        if process.poll() is not None:
            log.seek(0)
            message = f'TableEdit exited during startup: {log.read()[-3000:]}'
            if os.environ.get('GITHUB_ACTIONS'):
                escaped = message.replace('%', '%25').replace('\r', '%0D').replace('\n', '%0A')
                print(f'::error title=macOS startup failed::{escaped}')
            raise RuntimeError(message)
        print(f'Native macOS {arch} app remained running during startup smoke check.')
    finally:
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()
