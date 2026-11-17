#!/usr/bin/env bash
# Copyright 2026 Simon Annetts/Jak Daniels
# SPDX-License-Identifier: Apache-2.0

# Dev machine setup for Ubuntu 24.04/26.04. Safe to re-run.
set -euo pipefail
sudo apt-get update
sudo apt-get install -y \
  git build-essential pkg-config \
  gstreamer1.0-tools gstreamer1.0-nice gstreamer1.0-libav \
  gstreamer1.0-plugins-base gstreamer1.0-plugins-good \
  gstreamer1.0-plugins-bad gstreamer1.0-plugins-ugly \
  libgstreamer1.0-dev libgstreamer-plugins-base1.0-dev libgstreamer-plugins-bad1.0-dev \
  gir1.2-gstreamer-1.0 gir1.2-gst-plugins-base-1.0 gir1.2-gst-plugins-bad-1.0 \
  sqlite3
# .NET SDK: 26.04 should have dotnet-sdk-10.0 in the archive; 24.04 has 8.0. Install whichever exists.
sudo apt-get install -y dotnet-sdk-10.0 || sudo apt-get install -y dotnet-sdk-8.0
echo "--- versions"
dotnet --version
gst-inspect-1.0 --version
for e in webrtcbin audiomixer opusenc opusdec rtpopuspay rtpopusdepay audiopanorama audioamplify level; do
  gst-inspect-1.0 "$e" >/dev/null 2>&1 && echo "ok   $e" || echo "MISSING $e"
done
