#!/bin/bash
# Native build on a Mac (needs the .NET 8 SDK and python3): builds for this Mac's CPU and ad-hoc signs the app.
set -euo pipefail
cd "$(dirname "$0")/.."

case "$(uname -m)" in
  arm64) RID=osx-arm64 ;;
  *)     RID=osx-x64 ;;
esac

python3 packaging/package_macos.py "$RID"
codesign --force --deep --sign - "dist/$RID/TableEdit.app"
echo "Built dist/$RID/TableEdit.app — drag it to /Applications."
