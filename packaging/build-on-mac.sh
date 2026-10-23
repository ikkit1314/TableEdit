#!/bin/bash
# Native build on a Mac (needs .NET 8 and python3): signs and packages for this Mac's CPU.
set -euo pipefail
cd "$(dirname "$0")/.."

case "$(uname -m)" in
  arm64) RID=osx-arm64 ;;
  *)     RID=osx-x64 ;;
esac

python3 packaging/package_macos.py --dmg "$RID"
echo "Built dist/$RID/TableEdit.app and a versioned DMG in dist/."
