#!/usr/bin/env bash
# Rebuilds the self-contained load tool in scripts/loadtest-linux-x64/ (no .NET needed on the machine that runs it;
# GStreamer is still needed: see scripts/loadtest-linux-x64/README.md).
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
out="$(mktemp -d)"
trap 'rm -rf "$out"' EXIT
dotnet publish "$root/src/SpatialAudioServer.LoadTest" -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none \
  -p:InvariantGlobalization=true -o "$out"
cp "$out/SpatialAudioServer.LoadTest" "$root/scripts/loadtest-linux-x64/"
ls -la "$root/scripts/loadtest-linux-x64/SpatialAudioServer.LoadTest"
