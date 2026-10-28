"""Verify native startup, multiple document windows, and macOS file-open activation."""
import ctypes
import os
from pathlib import Path
import platform
import subprocess
import tempfile
import time

if platform.system() != 'Darwin':
    raise SystemExit('This startup check requires macOS.')

# Window owner PID and layer are available without requesting screen recording or
# Accessibility permission. We do not capture pixels or inspect other apps' titles.
cg = ctypes.CDLL('/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics')
cf = ctypes.CDLL('/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation')
cg.CGWindowListCopyWindowInfo.argtypes = [ctypes.c_uint32, ctypes.c_uint32]
cg.CGWindowListCopyWindowInfo.restype = ctypes.c_void_p
cf.CFArrayGetCount.argtypes = [ctypes.c_void_p]
cf.CFArrayGetCount.restype = ctypes.c_long
cf.CFArrayGetValueAtIndex.argtypes = [ctypes.c_void_p, ctypes.c_long]
cf.CFArrayGetValueAtIndex.restype = ctypes.c_void_p
cf.CFDictionaryGetValue.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
cf.CFDictionaryGetValue.restype = ctypes.c_void_p
cf.CFNumberGetValue.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p]
cf.CFNumberGetValue.restype = ctypes.c_bool
cf.CFRelease.argtypes = [ctypes.c_void_p]
cf.CFRelease.restype = None
owner_key = ctypes.c_void_p.in_dll(cg, 'kCGWindowOwnerPID').value
layer_key = ctypes.c_void_p.in_dll(cg, 'kCGWindowLayer').value


def window_count(pid):
    windows = cg.CGWindowListCopyWindowInfo(1, 0)  # On-screen windows, including obscured windows.
    if not windows:
        raise RuntimeError('macOS did not return a window list.')
    try:
        count = 0
        for index in range(cf.CFArrayGetCount(windows)):
            window = cf.CFArrayGetValueAtIndex(windows, index)
            owner = cf.CFDictionaryGetValue(window, owner_key)
            layer = cf.CFDictionaryGetValue(window, layer_key)
            owner_value, layer_value = ctypes.c_int(), ctypes.c_int()
            if owner and layer and cf.CFNumberGetValue(owner, 9, ctypes.byref(owner_value)) and \
                    cf.CFNumberGetValue(layer, 9, ctypes.byref(layer_value)):
                if owner_value.value == pid and layer_value.value == 0:
                    count += 1
        return count
    finally:
        cf.CFRelease(windows)


arch = 'arm64' if platform.machine() == 'arm64' else 'x64'
root = Path(__file__).resolve().parent.parent
app = root / f'dist/osx-{arch}/TableEdit.app'
host = app / 'Contents/MacOS/TableEdit'
with tempfile.TemporaryDirectory(prefix='tableedit-mac-smoke-') as directory, \
        tempfile.TemporaryFile(mode='w+', encoding='utf-8', errors='replace') as log:
    paths = [Path(directory) / f'document {index}.csv' for index in range(5)]
    for index, path in enumerate(paths):
        path.write_text(f'Name,Value,Group\nDocument {index},{index},test\n')
    process = subprocess.Popen([str(host), *(str(path) for path in paths[:3])], stdout=log, stderr=subprocess.STDOUT)
    try:
        def wait_for_windows(expected, description):
            deadline = time.monotonic() + 15
            observed = 0
            while time.monotonic() < deadline:
                if process.poll() is not None:
                    raise RuntimeError('TableEdit exited during the document smoke check.')
                observed = window_count(process.pid)
                if observed >= expected:
                    print(f'PASS: {description}: {observed} document windows in PID {process.pid}.', flush=True)
                    return
                time.sleep(0.25)
            raise RuntimeError(f'{description}: expected {expected} windows, observed {observed} in PID {process.pid}.')

        wait_for_windows(3, 'command-line startup opens all three CSVs')
        subprocess.run(['open', '-a', str(app), *(str(path) for path in paths[3:])], check=True, timeout=15)
        wait_for_windows(5, 'macOS file activation opens two more CSVs in the running app')
        subprocess.run(['open', '-a', str(app), str(paths[0])], check=True, timeout=15)
        time.sleep(2)
        if process.poll() is not None or window_count(process.pid) != 5:
            raise RuntimeError('Reopening an existing CSV changed the document window count or terminated the app.')
        print(f'PASS: native macOS {arch} app keeps five independent document windows; reopening reuses its window.', flush=True)
    except Exception as error:
        log.seek(0)
        pids = subprocess.run(['pgrep', '-x', 'TableEdit'], capture_output=True, text=True).stdout.strip()
        message = f'{error}\nTableEdit process IDs: {pids}\n{log.read()[-2200:]}'
        if os.environ.get('GITHUB_ACTIONS'):
            escaped = message.replace('%', '%25').replace('\r', '%0D').replace('\n', '%0A')
            print(f'::error title=macOS document smoke check failed::{escaped}', flush=True)
        raise RuntimeError(message) from None
    finally:
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()
