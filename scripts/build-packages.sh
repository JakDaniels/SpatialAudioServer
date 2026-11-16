#!/usr/bin/env bash
# Builds the Linux packages (PACKAGING-PLAN.md): the server and the load tool as .deb and .rpm, into dist/.
#   scripts/build-packages.sh [x64|arm64]      (default x64)
# Needs the .NET SDK and nfpm (https://nfpm.goreleaser.com; CI pins its version). The version comes from git tags
# (MinVer), so run it in a clone with its history. Used by .github/workflows/drafts.yml and by hand.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

arch="${1:-x64}"
# nfpm takes Go's architecture names and writes each format's own (amd64: deb amd64, rpm x86_64; arm64: aarch64).
case "$arch" in
  x64) goarch=amd64 ;;
  arm64) goarch=arm64 ;;
  *) echo "usage: $0 [x64|arm64]" >&2; exit 2 ;;
esac

stage="dist/stage"
rm -rf "$stage"
mkdir -p "$stage" dist

# Self-contained: the .NET runtime is inside, so the target needs only GStreamer and the C libraries. Invariant
# globalisation: no libicu dependency (its package name changes with every distribution release).
publish=(-c Release -r "linux-$arch" --self-contained -p:InvariantGlobalization=true -p:DebugType=none)
dotnet publish src/SpatialAudioServer "${publish[@]}" -o "$stage/server"
dotnet publish src/SpatialAudioServer.LoadTest "${publish[@]}" \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o "$stage/loadtest"
# A developer's own settings (may hold test API keys) never go into a package.
rm -f "$stage"/server/appsettings.Development.json
cp -r scripts/loadtest-linux-x64/voices "$stage/loadtest/voices"
# Modes in the packages must not depend on the builder's umask (or on publish marking every library executable):
# directories 0755, files 0644, and only the programs executable.
find "$stage" -type d -exec chmod 0755 {} +
find "$stage" -type f -exec chmod 0644 {} +
chmod 0755 "$stage/server/SpatialAudioServer" "$stage/server/createdump" "$stage/loadtest/SpatialAudioServer.LoadTest"

# The SemVer version (e.g. 0.2.1-alpha.0.5) from MinVer, as the build used.
version="$(dotnet msbuild src/SpatialAudioServer -t:MinVer -getProperty:MinVerVersion -nologo | tail -n 1)"
if [ -z "$version" ]; then
  echo "Could not determine the version." >&2
  exit 1
fi
echo "Version $version ($goarch)"

for package in server loadtest; do
  for format in deb rpm; do
    VERSION="$version" ARCH="$goarch" nfpm package -f "packaging/linux/nfpm-$package.yaml" -p "$format" -t dist/
  done
done
ls -l dist/*.deb dist/*.rpm
