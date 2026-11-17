#!/usr/bin/env python3
# Copyright 2026 Simon Annetts/Jak Daniels
# SPDX-License-Identifier: Apache-2.0

"""Copies the parts of a GStreamer (MSVC) installation that SpatialAudioServer and its load tool need into a private
tree for the Windows installers (PACKAGING-PLAN.md "GStreamer on Windows"):

    OUT\\bin\\                      GStreamer's libraries and every DLL they need, found by walking the imports
    OUT\\lib\\gstreamer-1.0\\        only the plugins named below
    OUT\\libexec\\gstreamer-1.0\\    gst-plugin-scanner.exe
    OUT\\share\\licenses\\           the licence texts shipped with GStreamer, if any

    python gather-gstreamer.py GSTREAMER_ROOT OUT

GSTREAMER_ROOT is the folder holding bin\\gstreamer-1.0-0.dll (found below it if the installer added levels). Needs
pefile (pip install pefile). Fails if a needed DLL is neither in GStreamer's bin nor a Windows system DLL.
"""
import os
import shutil
import sys

import pefile

# Plugins (lib\gstreamer-1.0\gst<name>.dll). The server's elements (Media/GstRuntime.RequiredElements), what webrtcbin
# uses internally, and what the load tool's fake clients and audio files need.
PLUGINS = [
    # core and base
    "coreelements", "app", "audioconvert", "audioresample", "audiomixer", "audiotestsrc", "volume",
    "playback", "typefindfunctions", "ogg", "opus", "vorbis", "wavparse", "audioparsers", "flac",
    # good
    "rtp", "rtpmanager", "audiofx", "level", "interleave",
    # bad, and libnice
    "webrtc", "dtls", "srtp", "sctp", "audiomixmatrix", "nice",
]
OPTIONAL_PLUGINS = {"vorbis", "flac", "audioparsers"}  # file formats for the load tool: nice to have

# The Microsoft C++ runtime may be taken from System32 when GStreamer's bin lacks it (app-local deployment of these
# redistributable files is allowed); every other DLL outside GStreamer's bin must be part of Windows itself.
VC_RUNTIME = ("vcruntime140", "msvcp140", "concrt140")


def find_root(path):
    for dirpath, _, files in os.walk(path):
        if "gstreamer-1.0-0.dll" in (f.lower() for f in files) and os.path.basename(dirpath).lower() == "bin":
            return os.path.dirname(dirpath)
    sys.exit(f"No bin\\gstreamer-1.0-0.dll under {path}")


def imports(path):
    pe = pefile.PE(path, fast_load=True)
    pe.parse_data_directories(directories=[
        pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_IMPORT"],
        pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_DELAY_IMPORT"],
    ])
    names = []
    for attr in ("DIRECTORY_ENTRY_IMPORT", "DIRECTORY_ENTRY_DELAY_IMPORT"):
        for entry in getattr(pe, attr, []):
            names.append(entry.dll.decode("ascii", "replace"))
    pe.close()
    return names


def main():
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    root = find_root(sys.argv[1])
    out = sys.argv[2]
    bin_dir = os.path.join(root, "bin")
    system32 = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "System32")
    in_bin = {f.lower(): f for f in os.listdir(bin_dir)}

    os.makedirs(os.path.join(out, "bin"), exist_ok=True)
    os.makedirs(os.path.join(out, "lib", "gstreamer-1.0"), exist_ok=True)
    os.makedirs(os.path.join(out, "libexec", "gstreamer-1.0"), exist_ok=True)

    work = []  # files whose imports still need walking
    # GStreamer's own libraries (gst*-1.0-0.dll): the binding loads them by name, not through imports.
    for lower, name in in_bin.items():
        if lower.startswith("gst") and lower.endswith("-1.0-0.dll"):
            work.append(os.path.join(bin_dir, name))
    missing_plugins = []
    for plugin in PLUGINS:
        src = os.path.join(root, "lib", "gstreamer-1.0", f"gst{plugin}.dll")
        if not os.path.exists(src):
            missing_plugins.append(plugin)
            continue
        shutil.copy2(src, os.path.join(out, "lib", "gstreamer-1.0"))
        work.append(src)
    scanner = os.path.join(root, "libexec", "gstreamer-1.0", "gst-plugin-scanner.exe")
    shutil.copy2(scanner, os.path.join(out, "libexec", "gstreamer-1.0"))
    work.append(scanner)

    copied, unresolved = set(), set()
    while work:
        path = work.pop()
        if os.path.dirname(path) == bin_dir:
            name = os.path.basename(path)
            if name.lower() in copied:
                continue
            shutil.copy2(path, os.path.join(out, "bin"))
            copied.add(name.lower())
        for dll in imports(path):
            lower = dll.lower()
            if lower in copied:
                continue
            if lower in in_bin:
                work.append(os.path.join(bin_dir, in_bin[lower]))
            elif lower.startswith(VC_RUNTIME) and os.path.exists(os.path.join(system32, dll)):
                shutil.copy2(os.path.join(system32, dll), os.path.join(out, "bin"))
                copied.add(lower)
            elif lower.startswith(("api-ms-win-", "ext-ms-")) or os.path.exists(os.path.join(system32, dll)):
                pass  # part of Windows
            else:
                unresolved.add(f"{dll} (needed by {os.path.basename(path)})")

    licenses = os.path.join(root, "share", "licenses")
    if os.path.isdir(licenses):
        shutil.copytree(licenses, os.path.join(out, "share", "licenses"), dirs_exist_ok=True)

    size = sum(os.path.getsize(os.path.join(d, f)) for d, _, fs in os.walk(out) for f in fs)
    print(f"GStreamer from {root}: {len(copied)} DLLs in bin, {len(PLUGINS) - len(missing_plugins)} plugins, "
          f"{size / 1e6:.0f} MB")
    for plugin in missing_plugins:
        print(f"{'note' if plugin in OPTIONAL_PLUGINS else 'MISSING'}: plugin {plugin}")
    for item in sorted(unresolved):
        print(f"UNRESOLVED: {item}")
    if unresolved or any(p not in OPTIONAL_PLUGINS for p in missing_plugins):
        sys.exit(1)


if __name__ == "__main__":
    main()
