#!/usr/bin/env bash
# Build Follow selection .yak for Rhino Package Manager (Yak).
# Run from repo root: ./packaging/build-yak.sh
# Usage: ./packaging/build-yak.sh [r7]
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TARGET="${1:-r8}"
[[ "$TARGET" == "r7" ]] && TFM="net48" || TFM="net7.0"

YAK=""
command -v yak >/dev/null 2>&1 && YAK="yak"
[[ -z "$YAK" && -x "/Applications/Rhino 8.app/Contents/Resources/bin/yak" ]] && YAK="/Applications/Rhino 8.app/Contents/Resources/bin/yak"
[[ -z "$YAK" && -x "/Applications/Rhino 7.app/Contents/Resources/bin/yak" ]] && YAK="/Applications/Rhino 7.app/Contents/Resources/bin/yak"
[[ -z "$YAK" ]] && { echo "Yak not found."; exit 1; }

echo "Building Follow selection ($TFM)…"
dotnet build "$ROOT/SelectionPreview.csproj" -c Release

STAGE="$ROOT/packaging/stage"
rm -rf "$STAGE"
mkdir -p "$STAGE/follow-selection"
cp "$ROOT/packaging/manifest.yml" "$STAGE/follow-selection/"
cp "$ROOT/Resources/toolbar.png" "$STAGE/follow-selection/icon.png"
cp "$ROOT/bin/Release/$TFM/FollowSelection.gha" "$STAGE/follow-selection/"

( cd "$STAGE/follow-selection" && "$YAK" build )

echo "Package: $STAGE/follow-selection/"
echo "Publish: $YAK push $STAGE/follow-selection/*.yak"
