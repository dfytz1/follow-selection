#!/usr/bin/env bash
# Stage a Rhino 8 multi-target Yak package (McNeel "Anatomy of a Package").
# Plug-ins MUST live only under net48/ and net7.0/. Do NOT copy .gha next to manifest.yml —
# Grasshopper loads every .gha under the package install path and duplicates cause FileLoadException.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

proj_ver="$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' SelectionPreview.csproj | head -1)"
man_ver="$(grep '^version:' packaging/manifest.yml | awk '{print $2}')"
if [[ "$proj_ver" != "$man_ver" ]]; then
  echo "Error: SelectionPreview.csproj Version ($proj_ver) must equal packaging/manifest.yml version ($man_ver)" >&2
  exit 1
fi

dotnet build "$ROOT/SelectionPreview.csproj" -c Release

stage="$ROOT/packaging/stage/follow-selection"
rm -rf "$stage"
mkdir -p "$stage/net48" "$stage/net7.0"

cp "$ROOT/bin/Release/net48/FollowSelection.gha" "$stage/net48/"
cp "$ROOT/bin/Release/net7.0/FollowSelection.gha" "$stage/net7.0/"
cp "$ROOT/Resources/toolbar.png" "$stage/icon.png"
cp "$ROOT/packaging/manifest.yml" "$stage/manifest.yml"

yak="${YAK:-/Applications/Rhino 8.app/Contents/Resources/bin/yak}"
cd "$stage"
"$yak" build

echo >&2 "Staged files:"
find "$stage" -type f | sort >&2
