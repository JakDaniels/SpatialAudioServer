#!/usr/bin/env bash
# Prints the newest stable GStreamer with an MSVC x86_64 Windows installer, and that installer's SHA-256 as published
# beside it, for gstreamer-watch.yml (compared with the Windows installers' pin in packages.yml):
#   scripts/gstreamer-latest.sh        →  1.28.9 <sha256>
# Stable releases have an even minor version (1.28.x); odd ones (1.29.x) are development snapshots.
set -euo pipefail

base="https://gstreamer.freedesktop.org/data/pkg/windows"
versions="$(curl -fsSL "$base/" | grep -o 'href="[0-9]*\.[0-9]*\.[0-9]*/"' | grep -o '[0-9][0-9.]*[0-9]' \
  | awk -F. '$2 % 2 == 0' | sort -V -r)"
for version in $versions; do
  sums="$base/$version/msvc/gstreamer-1.0-msvc-x86_64-$version.exe.sha256sum"
  # A folder may exist before its installers are uploaded: take the newest one that has them.
  if sha="$(curl -fsSL "$sums" 2>/dev/null | cut -d' ' -f1)" && [[ "$sha" =~ ^[0-9a-f]{64}$ ]]; then
    echo "$version $sha"
    exit 0
  fi
done
echo "No GStreamer Windows installer found at $base" >&2
exit 1
