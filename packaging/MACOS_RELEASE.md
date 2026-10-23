# macOS DMG releases

The version-tag workflow runs on macOS and builds both `osx-arm64` and `osx-x64`. It signs each native library and the application bundle before creating a compressed DMG containing `TableEdit.app`, an Applications link, and installation instructions. Each image is verified, mounted read-only, checked for the expected version and executable permissions, and its embedded app signature is verified before publication.

Managed .NET assemblies are bundled into the main executable. Native libraries remain separate and are individually signed. This avoids placing unsigned managed DLLs in `Contents/MacOS`, where Apple's bundle signing rules treat them as nested code.

## Install

1. Quit an older running copy of TableEdit.
2. Download the DMG for Apple Silicon or Intel from GitHub Releases.
3. Open the DMG and drag TableEdit onto Applications, replacing the old copy if prompted.
4. Eject the DMG and open TableEdit from Applications.

DMG is a container format; it does not establish Apple developer trust. Without a Developer ID certificate the workflow uses an ad-hoc signature. This fixes the missing-signature packaging problem but macOS can still block the first downloaded launch. After trying to open the installed app, use **System Settings > Privacy & Security > Open Anyway** for TableEdit (on macOS 12, **System Preferences > Security & Privacy**). Once approved, subsequent launches of that installed copy should work normally. A newly downloaded update can require approval again. No global Gatekeeper change is needed.

## Developer ID signing and Apple notarization

To distribute downloads without the unidentified-developer/notarization warning, configure these repository **Actions secrets** in GitHub Settings. Do not put certificates or passwords in source files, issue comments, or chat.

| Secret | Value |
| --- | --- |
| `MACOS_CERTIFICATE_P12_BASE64` | Base64 of an exported **Developer ID Application** certificate and its private key in `.p12` format |
| `MACOS_CERTIFICATE_PASSWORD` | Password for that `.p12` file; may be empty if the export has no password |
| `APPLE_ID` | Apple Developer account email used for notarization |
| `APPLE_TEAM_ID` | Developer team ID |
| `APPLE_APP_SPECIFIC_PASSWORD` | App-specific Apple password for notarization |

Certificate-only configuration produces Developer ID signatures but does not notarize. All three Apple notarization secrets and the certificate are required to enable notarization. Partial notarization configuration fails the build rather than silently claiming a notarized release.

The certificate is imported into a temporary keychain and deleted after packaging. The .NET host receives JIT entitlements; the notarization path also enables hardened runtime and secure timestamps. The workflow submits the app to Apple, staples the accepted ticket, then creates, signs, notarizes and staples the final DMG. Rejected notarization stops publication. The release notes state the actual signing mode used by that build.

The optional Developer ID/notarization path requires the owner's credentials and cannot be exercised by an ad-hoc build.

## Build locally on macOS

Install .NET 8 and Python 3, then run:

```sh
python3 packaging/package_macos.py --dmg
# Or just this Mac's architecture:
bash packaging/build-on-mac.sh
```

DMGs are written to `dist/TableEdit-<version>-macos-{arm64,x64}.dmg`. The same signing environment variables can be supplied by a local secret manager. Cross-building ZIPs on Linux/Windows remains supported without `--dmg`; native macOS tools are required to produce and verify DMGs.
