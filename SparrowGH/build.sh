#!/usr/bin/env bash
# Build and install the SparrowGH Grasshopper plugin.
# Requires: dotnet SDK (brew install dotnet)
# Run this after any code change to update both Rhino 7 and Rhino 8.

set -e
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

GH_ID="b45a29b1-4343-4035-989e-044e8580d9cf"
GH7="$HOME/Library/Application Support/McNeel/Rhinoceros/7.0/Plug-ins/Grasshopper ($GH_ID)/Libraries"
GH8="$HOME/Library/Application Support/McNeel/Rhinoceros/8.0/Plug-ins/Grasshopper ($GH_ID)/Libraries"

export DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec
export PATH="$DOTNET_ROOT:$PATH"

echo "Building..."
dotnet build -c Release

DLL=bin/Release/net48/SparrowGH.dll
JSON=bin/Release/net48/Newtonsoft.Json.dll
BIN=../sparrow/target/release/sparrow

mkdir -p "$GH7" "$GH8"
for DIR in "$GH7" "$GH8"; do
  cp "$DLL"  "$DIR/SparrowGH.gha"
  cp "$JSON" "$DIR/Newtonsoft.Json.dll"
  cp "$BIN"  "$DIR/sparrow"
  chmod +x   "$DIR/sparrow"
  echo "Installed → $DIR"
done

echo ""
echo "Done. Restart Rhino and Grasshopper to pick up changes."
