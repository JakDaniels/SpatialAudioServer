# SpatialAudioServer

[![ci (develop)](https://github.com/JakDaniels/SpatialAudioServer/actions/workflows/ci.yml/badge.svg?branch=develop)](https://github.com/JakDaniels/SpatialAudioServer/actions/workflows/ci.yml?query=branch%3Adevelop) [![drafts (develop)](https://github.com/JakDaniels/SpatialAudioServer/actions/workflows/drafts.yml/badge.svg?branch=develop)](https://github.com/JakDaniels/SpatialAudioServer/actions/workflows/drafts.yml)

An MCU-style audio conferencing server with spatial audio, written in C# (.NET 10) on GStreamer.

Clients connect with WebRTC. The server mixes the audio itself and sends each listener one stereo stream: everyone
else in the room, without their own voice (mix-minus). In a spatial room each voice is placed by where the speaker
stands and which way the listener faces. Rooms and participants are managed by another program, for example a
virtual-world region, through a JSON HTTP API. The aim is voice for Second Life / OpenSimulator viewers (through an
OpenSim region module, here: https://github.com/JakDaniels/os-webrtc-sas) and for web browsers.

## Features

- **Control API** (API key): create and delete rooms, add and remove participants, set positions and orientations,
  mute and gain. Adding a participant returns a one-time join token for the client.
- **WebRTC signalling over HTTP:** SDP offer/answer and trickle ICE; the server is always the answerer. Opus 48 kHz,
  mono in, stereo out.
- **Mix engines, chosen per room:** `matrix-shared` (default: only the current talkers are mixed, and listeners who
  hear the same mix share one encoder), `matrix` (one encoder per listener, the default for spatial rooms),
  `pairchains` (a GStreamer chain per source/listener pair, for comparison) and `acoustic` (the whole room in one
  [acousticmultimixer](https://github.com/JakDaniels/acousticmultimixer) element; offered when that plugin is
  installed).
- **Spatial audio:** gain by distance and left/right pan from positions and facing; clients can turn their own
  listener with their session token. With the `acoustic` engine, rooms can also choose their acoustics (`outdoor`,
  `tiny-room`, `small-room`, `big-room`, `hall`, `long-hall`, `cave`, `cathedral`: reflections and reverb).
- **Admin UI** (`/admin/`, own password): rooms, live participant levels and WebRTC stats, mute, gain and kick, an
  event log and session history, and a live view of each room's GStreamer pipeline as blocks and wires.
- **Test web client** (`/client/`): join from a browser with a join link, or create rooms in test mode with an API key.
- **Operational hardening:** rooms persist in SQLite; a room's media runs only while someone is in it; dead
  connections and unused joins are removed; graceful shutdown; signalling rate limits and input limits; runs as a
  systemd unit or Windows service.
- **Load tool** (`src/SpatialAudioServer.LoadTest`): fills rooms with fake WebRTC clients that check who hears
  whom and measure the server's CPU, or adds speaking bots to a room for listening tests.

Runs on Linux and Windows (x64). Up to 16 rooms × 32 participants by default; on a LAN, or on an internet-facing host
with a fixed media port range, NAT support, TURN and network allow-lists (see [docs/internet.md](docs/internet.md)).
Not yet tried on a real public host.

## Status

Under development. Done: the control API, media core and mix engines, signalling and the test web client, the admin
UI, spatial audio with distance profiles, the hardening and settings for internet-facing hosts, and Second Life
viewer support through the OpenSim region add-on (https://github.com/JakDaniels/os-webrtc-sas): region and parcel
voice, neighbouring regions, group and P2P calls, web users and bots shown in-world as NPCs. Parked: ringing callees
on other simulators, voice agents. See the phases in [PLAN.md](PLAN.md).

## Installing

Packages for each release are on the [Releases page](https://github.com/JakDaniels/SpatialAudioServer/releases)
(what changed: [CHANGELOG.md](CHANGELOG.md)). The server needs GStreamer 1.24 or newer, which the Linux packages
pull in from the distribution; the Windows installers carry their own.

**Ubuntu 24.04 or later, Debian 13 or later** (x64 or arm64; the load tool also on Ubuntu 22.04), from the
signed package repository, so updates come with `apt upgrade`:

```bash
curl -fsSL https://jakdaniels.github.io/SpatialAudioServer/key.asc | sudo gpg --dearmor -o /usr/share/keyrings/spatialaudioserver.gpg
echo "deb [signed-by=/usr/share/keyrings/spatialaudioserver.gpg] https://jakdaniels.github.io/SpatialAudioServer/deb stable main" |
  sudo tee /etc/apt/sources.list.d/spatialaudioserver.list
sudo apt update && sudo apt install spatialaudioserver       # or a downloaded file: sudo apt install ./spatialaudioserver_<version>_amd64.deb
sudo spatialaudioserver admin set-password          # the admin UI's password
sudo spatialaudioserver apikey create <label>       # a control-API key, e.g. for the OpenSim add-on
sudo systemctl start spatialaudioserver             # installed and enabled, not started until now
```

**Fedora** (x64 or arm64): add the repository and install, then the same set-up commands as above:

```bash
sudo curl -fsSL -o /etc/yum.repos.d/spatialaudioserver.repo https://jakdaniels.github.io/SpatialAudioServer/rpm/spatialaudioserver.repo
sudo dnf install spatialaudioserver        # or a downloaded file: sudo dnf install ./spatialaudioserver-<version>-1.x86_64.rpm
```

The repositories, with the key's fingerprint: https://jakdaniels.github.io/SpatialAudioServer/. They hold the latest
two releases; every release stays on the Releases page.

On Linux the server runs as the `spatialaudioserver` systemd service under its own user; site settings go in
`/etc/spatialaudioserver/appsettings.json`, data in `/var/lib/spatialaudioserver`, logs in
`journalctl -u spatialaudioserver`. Open TCP 5000-5001 and UDP 40000-40999 (WebRTC media) in the firewall.

**Windows 10 or later, Windows Server 2019 or later** (x64): run `SpatialAudioServer-<version>-x64.msi` (not
signed yet: "More info", then "Run anyway"). It carries its own GStreamer, so nothing else is needed, and it
installs and starts the `SpatialAudioServer` service. To have it open the firewall (TCP 5000-5001, UDP
40000-40999), select the **Windows Firewall rules** feature on the Custom Setup page: it is off by default. Then,
from a command prompt opened with "Run as administrator": `SpatialAudioServer admin set-password` and
`SpatialAudioServer apikey create <label>`. Site settings go in `C:\ProgramData\SpatialAudioServer\appsettings.json`.

Then open `https://<host>:5001/admin/`, or the test web client at `https://<host>:5001/client/`. The load tool has
its own packages (`spatialaudioserver-loadtest`, command `sas-loadtest`). The full guide to the packages (firewall,
upgrades, removal, unattended installs): [docs/running-from-prebuilt-binaries.md](docs/running-from-prebuilt-binaries.md).
Settings: [docs/configuration.md](docs/configuration.md); internet hosting: [docs/internet.md](docs/internet.md). For Second Life / OpenSim
viewers, add the region module [os-webrtc-sas](https://github.com/JakDaniels/os-webrtc-sas) to OpenSim.

## Building from source

Needs the .NET 10 SDK and GStreamer 1.24 or newer with the base, good and bad plugins and libnice
(on Ubuntu: `scripts/setup-ubuntu.sh`; on Windows: the official installer, full install).

```bash
dotnet run --project src/SpatialAudioServer -- apikey create test     # prints a control-API key
dotnet run --project src/SpatialAudioServer -- admin set-password     # admin UI password
dotnet run --project src/SpatialAudioServer                          # HTTP 5000, HTTPS 5001
```

Then open `https://<host>:5001/client/` (test mode, with the key) or `https://<host>:5001/admin/`.
Tests: `dotnet test src`. The full guide: [docs/running-from-source.md](docs/running-from-source.md).

## Documents

| document | what it holds |
|---|---|
| [PLAN.md](PLAN.md) | The specification: architecture, API, decisions and their reasons, phases and progress |
| [docs/running-from-prebuilt-binaries.md](docs/running-from-prebuilt-binaries.md) | Installing and running the packages and the Windows installer: service, firewall, upgrades, load tool |
| [docs/running-from-source.md](docs/running-from-source.md) | Building and running from the repository on Linux and Windows, as a service, load tool |
| [docs/configuration.md](docs/configuration.md) | Every setting, where settings come from, the ports to open |
| [docs/internet.md](docs/internet.md) | On an internet-facing host: TLS, the media port range, NAT, TURN, hardening |
| [CHANGELOG.md](CHANGELOG.md) | What changed in each release |
| [CONTRIBUTING.md](CONTRIBUTING.md) | Branches, building and testing, and how changes are released |
| [SECURITY.md](SECURITY.md) | How to report a vulnerability |
| [PACKAGING-PLAN.md](PACKAGING-PLAN.md) | The packages (.deb, .rpm, Windows MSIs), versioning, CI and the release workflow |
| [docs/benchmarks.md](docs/benchmarks.md) | Load-test results: CPU per engine, room counts, test machines |
| [scripts/loadtest-linux-x64/README.md](scripts/loadtest-linux-x64/README.md) | The self-contained load tool for a separate Linux machine |
| [docs/gstreamer_acousticspace_design.md](docs/gstreamer_acousticspace_design.md) | Design notes for a possible acoustic-space DSP element |
| [CLAUDE.md](CLAUDE.md) | Short handoff notes on the current state for AI-assisted sessions |

## Supporting the project

If SpatialAudioServer is useful to you, you can support its development through
[GitHub Sponsors](https://github.com/sponsors/JakDaniels) (also the **Sponsor** button at the top of this page).
Thank you.

## Licence

Copyright 2026 Simon Annetts/Jak Daniels. Apache-2.0, see [LICENSE](LICENSE); attribution and third-party notices
in [NOTICE](NOTICE), which copies and derived works must carry along (licence section 4(d)). The admin UI includes [elkjs](https://github.com/kieler/elkjs) for graph layout,
unmodified, under its own licence (EPL-2.0; `src/SpatialAudioServer/wwwroot/admin/vendor/elkjs/`).
