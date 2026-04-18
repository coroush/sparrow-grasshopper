#!/usr/bin/env bash
# Build release artifacts for all three platforms.
# Produces dist/{mac-arm64,mac-x64,windows-x64}.zip, each containing:
#   SparrowGH.gha, Newtonsoft.Json.dll, sparrow (or sparrow.exe)
#
# Requirements (one-time setup):
#   rustup target add x86_64-apple-darwin x86_64-pc-windows-gnu
#   brew install mingw-w64
#   brew install dotnet

set -e
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

export DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec
export PATH="$DOTNET_ROOT:$PATH"

echo "▶ Building sparrow (mac-arm64)..."
(cd sparrow && cargo build --release)

echo "▶ Building sparrow (mac-x64)..."
(cd sparrow && cargo build --release --target x86_64-apple-darwin)

echo "▶ Building sparrow (windows-x64)..."
(cd sparrow && cargo build --release --target x86_64-pc-windows-gnu)

echo "▶ Building SparrowGH.gha..."
(cd SparrowGH && dotnet build -c Release)

DLL=SparrowGH/bin/Release/net48/SparrowGH.dll
JSON=SparrowGH/bin/Release/net48/Newtonsoft.Json.dll

stage() {
  local platform=$1
  local bin_src=$2
  local bin_name=$3
  local dir=dist/$platform
  mkdir -p "$dir"
  cp "$DLL"     "$dir/SparrowGH.gha"
  cp "$JSON"    "$dir/Newtonsoft.Json.dll"
  cp "$bin_src" "$dir/$bin_name"
  [[ "$bin_name" == "sparrow" ]] && chmod +x "$dir/$bin_name"
  rm -f "dist/$platform.zip"
  (cd "$dir" && zip -q "../$platform.zip" *)
  echo "  ✓ dist/$platform.zip"
}

echo "▶ Packaging..."
stage mac-arm64   sparrow/target/release/sparrow                         sparrow
stage mac-x64     sparrow/target/x86_64-apple-darwin/release/sparrow     sparrow
stage windows-x64 sparrow/target/x86_64-pc-windows-gnu/release/sparrow.exe sparrow.exe

echo ""
echo "Done. Upload the three zips to a GitHub Release:"
ls -lh dist/*.zip
