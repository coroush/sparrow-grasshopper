#!/usr/bin/env bash
# Build release artifacts for all three platforms.
# Produces:
#   dist/{mac-arm64,mac-x64,windows-x64}.zip  — manual install zips
#   dist/yak-mac/   + sparrowgh-*-mac.yak      — Rhino Package Manager (mac)
#   dist/yak-win/   + sparrowgh-*-win.yak      — Rhino Package Manager (win)
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

YAK="/Applications/Rhino 8.app/Contents/Resources/bin/yak"

echo "▶ Building sparrow (mac-arm64)..."
(cd sparrow && cargo build --release)

echo "▶ Building sparrow (mac-x64)..."
(cd sparrow && cargo build --release --target x86_64-apple-darwin)

echo "▶ Building sparrow (windows-x64)..."
(cd sparrow && cargo build --release --target x86_64-pc-windows-gnu)

echo "▶ Building SparrowGH.gha (Rhino 7 + Rhino 8)..."
(cd SparrowGH && dotnet build -c Release)

DLL7=SparrowGH/bin/Release/net48/SparrowGH.dll
DLL8=SparrowGH/bin/Release/net7.0/SparrowGH.dll
JSON=SparrowGH/bin/Release/net48/Newtonsoft.Json.dll
JSON8=$JSON
DLL=$DLL7

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

echo "▶ Packaging zips..."
stage mac-arm64   sparrow/target/release/sparrow                         sparrow
stage mac-x64     sparrow/target/x86_64-apple-darwin/release/sparrow     sparrow
stage windows-x64 sparrow/target/x86_64-pc-windows-gnu/release/sparrow.exe sparrow.exe

echo "▶ Staging yak packages..."

# mac yak: bundle both arm64 and x64 binaries; plugin picks the right one at runtime
YAK_MAC=dist/yak-mac
mkdir -p "$YAK_MAC"
cp "$DLL"  "$YAK_MAC/SparrowGH.gha"
cp "$JSON" "$YAK_MAC/Newtonsoft.Json.dll"
cp sparrow/target/release/sparrow                      "$YAK_MAC/sparrow-arm64" && chmod +x "$YAK_MAC/sparrow-arm64"
cp sparrow/target/x86_64-apple-darwin/release/sparrow  "$YAK_MAC/sparrow-x64"  && chmod +x "$YAK_MAC/sparrow-x64"
cp dist/manifest.yml "$YAK_MAC/manifest.yml"
(cd "$YAK_MAC" && "$YAK" build --platform mac)
mv "$YAK_MAC"/sparrow-*.yak dist/ 2>/dev/null || true
echo "  ✓ dist/sparrow-*-mac.yak"

# win yak
YAK_WIN=dist/yak-win
mkdir -p "$YAK_WIN"
cp "$DLL"  "$YAK_WIN/SparrowGH.gha"
cp "$JSON" "$YAK_WIN/Newtonsoft.Json.dll"
cp sparrow/target/x86_64-pc-windows-gnu/release/sparrow.exe "$YAK_WIN/sparrow.exe"
cp dist/manifest.yml "$YAK_WIN/manifest.yml"
(cd "$YAK_WIN" && "$YAK" build --platform win)
mv "$YAK_WIN"/sparrow-*.yak dist/ 2>/dev/null || true
echo "  ✓ dist/sparrow-*-win.yak"

# Rhino 8 mac yak
YAK_MAC8=dist/yak-mac8
mkdir -p "$YAK_MAC8"
cp "$DLL8"  "$YAK_MAC8/SparrowGH.gha"
cp "$JSON8" "$YAK_MAC8/Newtonsoft.Json.dll"
cp sparrow/target/release/sparrow                      "$YAK_MAC8/sparrow-arm64" && chmod +x "$YAK_MAC8/sparrow-arm64"
cp sparrow/target/x86_64-apple-darwin/release/sparrow  "$YAK_MAC8/sparrow-x64"  && chmod +x "$YAK_MAC8/sparrow-x64"
cp dist/manifest.yml "$YAK_MAC8/manifest.yml"
(cd "$YAK_MAC8" && "$YAK" build --platform mac)
mv "$YAK_MAC8"/sparrow-*.yak dist/ 2>/dev/null || true
echo "  ✓ dist/sparrow-*-rh8-mac.yak"

# Rhino 8 win yak
YAK_WIN8=dist/yak-win8
mkdir -p "$YAK_WIN8"
cp "$DLL8"  "$YAK_WIN8/SparrowGH.gha"
cp "$JSON8" "$YAK_WIN8/Newtonsoft.Json.dll"
cp sparrow/target/x86_64-pc-windows-gnu/release/sparrow.exe "$YAK_WIN8/sparrow.exe"
cp dist/manifest.yml "$YAK_WIN8/manifest.yml"
(cd "$YAK_WIN8" && "$YAK" build --platform win)
mv "$YAK_WIN8"/sparrow-*.yak dist/ 2>/dev/null || true
echo "  ✓ dist/sparrow-*-rh8-win.yak"

echo ""
echo "Done."
echo ""
echo "GitHub Release zips:"
ls -lh dist/*.zip
echo ""
echo "Rhino Package Manager (.yak):"
ls -lh dist/*.yak
echo ""
echo "To publish: run 'yak login' then 'yak push dist/sparrow-*-mac.yak' and 'yak push dist/sparrow-*-win.yak'"
