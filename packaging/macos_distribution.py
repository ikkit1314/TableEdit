"""Native macOS code signing, optional Apple notarization, and verified DMG creation."""
import base64
from contextlib import contextmanager
import json
import os
from pathlib import Path
import plistlib
import re
import secrets
import subprocess
import tempfile

PKG = Path(__file__).resolve().parent
MACHO_MAGIC = {b'\xfe\xed\xfa\xce', b'\xce\xfa\xed\xfe', b'\xfe\xed\xfa\xcf',
               b'\xcf\xfa\xed\xfe', b'\xca\xfe\xba\xbe', b'\xbe\xba\xfe\xca',
               b'\xca\xfe\xba\xbf', b'\xbf\xba\xfe\xca'}


def run(*args):
    print('+', ' '.join(str(arg) for arg in args), flush=True)
    subprocess.run([str(arg) for arg in args], check=True)


def private_run(*args):
    """Commands containing credentials must never be included in printed exceptions."""
    result = subprocess.run([str(arg) for arg in args], capture_output=True, text=True)
    if result.returncode:
        raise RuntimeError(f'{Path(args[0]).name} failed; check the macOS signing credentials.')
    return result.stdout


class Signer:
    def __init__(self, identity='-', keychain=None, notarize=False):
        self.identity = identity
        self.keychain = keychain
        self.notarize = notarize

    @property
    def status(self):
        if self.notarize:
            return 'Developer ID signed and Apple-notarized'
        if self.identity != '-':
            return 'Developer ID signed; not Apple-notarized'
        return 'Ad-hoc signed; not Apple-notarized'

    def sign(self, path, *, app=False, code=True):
        args = ['codesign', '--force', '--sign', self.identity]
        if self.keychain:
            args += ['--keychain', self.keychain, '--timestamp']
            if code:
                args += ['--options', 'runtime']
        if app:
            args += ['--entitlements', PKG / 'entitlements.plist']
        run(*args, path)

    def notarize_and_staple(self, path):
        result = json.loads(private_run(
            'xcrun', 'notarytool', 'submit', path,
            '--apple-id', os.environ['APPLE_ID'], '--team-id', os.environ['APPLE_TEAM_ID'],
            '--password', os.environ['APPLE_APP_SPECIFIC_PASSWORD'],
            '--wait', '--timeout', '15m', '--output-format', 'json'))
        if result.get('status') != 'Accepted':
            raise RuntimeError(f"Apple notarization was not accepted; submission {result.get('id', 'unknown')}.")
        run('xcrun', 'stapler', 'staple', path)
        run('xcrun', 'stapler', 'validate', path)

    def prepare_app(self, app):
        # Sign nested Mach-O libraries first, then seal the bundle and its apphost.
        host = app / 'Contents' / 'MacOS' / 'TableEdit'
        for path in sorted(app.rglob('*')):
            if path.is_file() and not path.is_symlink() and path != host:
                with path.open('rb') as stream:
                    if stream.read(4) in MACHO_MAGIC:
                        self.sign(path)
        self.sign(app, app=True)
        run('codesign', '--verify', '--deep', '--strict', '--verbose=2', app)
        if self.notarize:
            with tempfile.TemporaryDirectory(prefix='tableedit-notary-') as directory:
                submission = Path(directory) / 'TableEdit.zip'
                run('ditto', '-c', '-k', '--keepParent', app, submission)
                # A ZIP cannot be stapled; staple the app after the ZIP is accepted.
                result = json.loads(private_run(
                    'xcrun', 'notarytool', 'submit', submission,
                    '--apple-id', os.environ['APPLE_ID'], '--team-id', os.environ['APPLE_TEAM_ID'],
                    '--password', os.environ['APPLE_APP_SPECIFIC_PASSWORD'],
                    '--wait', '--timeout', '15m', '--output-format', 'json'))
                if result.get('status') != 'Accepted':
                    raise RuntimeError(f"Apple notarization was not accepted; submission {result.get('id', 'unknown')}.")
            run('xcrun', 'stapler', 'staple', app)
            run('xcrun', 'stapler', 'validate', app)
            run('spctl', '--assess', '--type', 'execute', '--verbose=2', app)


@contextmanager
def signing_from_environment():
    certificate = os.environ.get('MACOS_CERTIFICATE_P12_BASE64', '')
    notary_keys = ('APPLE_ID', 'APPLE_TEAM_ID', 'APPLE_APP_SPECIFIC_PASSWORD')
    notary_values = [bool(os.environ.get(key)) for key in notary_keys]
    if any(notary_values) and (not all(notary_values) or not certificate):
        raise RuntimeError('Notarization requires a Developer ID certificate and all three Apple notarization secrets.')
    if not certificate:
        signer = Signer()
        print('Signing mode:', signer.status, flush=True)
        yield signer
        return
    with tempfile.TemporaryDirectory(prefix='tableedit-signing-') as directory:
        directory = Path(directory)
        certificate_path = directory / 'certificate.p12'
        certificate_path.write_bytes(base64.b64decode(''.join(certificate.split()), validate=True))
        certificate_path.chmod(0o600)
        keychain = directory / 'signing.keychain-db'
        password = secrets.token_urlsafe(32)
        try:
            private_run('security', 'create-keychain', '-p', password, keychain)
            private_run('security', 'set-keychain-settings', '-lut', '21600', keychain)
            private_run('security', 'unlock-keychain', '-p', password, keychain)
            private_run('security', 'import', certificate_path, '-k', keychain,
                        '-P', os.environ.get('MACOS_CERTIFICATE_PASSWORD', ''),
                        '-T', '/usr/bin/codesign', '-T', '/usr/bin/security')
            certificate_path.unlink()
            private_run('security', 'set-key-partition-list', '-S', 'apple-tool:,apple:,codesign:',
                        '-s', '-k', password, keychain)
            identities = private_run('security', 'find-identity', '-v', '-p', 'codesigning', keychain)
            match = re.search(r'([A-Fa-f0-9]{40}) "Developer ID Application:', identities)
            if not match:
                raise RuntimeError('The certificate must contain a valid Developer ID Application identity and private key.')
            signer = Signer(match[1], keychain, all(notary_values))
            print('Signing mode:', signer.status, flush=True)
            yield signer
        finally:
            if keychain.exists():
                subprocess.run(['security', 'delete-keychain', str(keychain)], check=True)


def make_dmg(app, rid, signer):
    with (app / 'Contents' / 'Info.plist').open('rb') as stream:
        version = plistlib.load(stream)['CFBundleShortVersionString']
    output = app.parent.parent / f'TableEdit-{version}-macos-{rid.split("-")[1]}.dmg'
    with tempfile.TemporaryDirectory(prefix='tableedit-dmg-') as directory:
        directory = Path(directory)
        stage = directory / 'contents'
        stage.mkdir()
        run('ditto', app, stage / 'TableEdit.app')
        (stage / 'Applications').symlink_to('/Applications', target_is_directory=True)
        instructions = ('TableEdit ' + version + '\n\n'
                        '1. Quit the previous TableEdit version.\n'
                        '2. Drag TableEdit.app onto Applications.\n'
                        '3. Eject this disk image and open TableEdit from Applications.\n\n'
                        'Signing: ' + signer.status + '.\n')
        if not signer.notarize:
            instructions += ('\nIf macOS blocks the first launch, try opening the installed app once, then go to\n'
                             'System Settings > Privacy & Security > Open Anyway for TableEdit.\n'
                             'You do not need to turn off Gatekeeper or run a Terminal command.\n')
        (stage / 'READ ME.txt').write_text(instructions)
        run('hdiutil', 'create', '-volname', f'TableEdit {version}', '-srcfolder', stage,
            '-fs', 'HFS+', '-format', 'UDZO', '-ov', output)
        if signer.identity != '-':
            signer.sign(output, code=False)
        if signer.notarize:
            signer.notarize_and_staple(output)
        run('hdiutil', 'verify', output)
        mount = directory / 'mounted'
        mount.mkdir()
        run('hdiutil', 'attach', '-readonly', '-nobrowse', '-noautoopen', '-mountpoint', mount, output)
        try:
            bundled_app = mount / 'TableEdit.app'
            with (bundled_app / 'Contents' / 'Info.plist').open('rb') as stream:
                assert plistlib.load(stream)['CFBundleShortVersionString'] == version
            assert os.readlink(mount / 'Applications') == '/Applications'
            assert (bundled_app / 'Contents' / 'MacOS' / 'TableEdit').stat().st_mode & 0o111
            run('codesign', '--verify', '--deep', '--strict', '--verbose=2', bundled_app)
        finally:
            run('hdiutil', 'detach', mount)
    print('Verified DMG:', output, flush=True)
    return output
