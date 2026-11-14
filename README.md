# SpatialAudioServer

An MCU-style audio conferencing server with spatial audio, written in C# (.NET 10) on GStreamer.

Clients connect with WebRTC. The server mixes the audio itself and sends each listener one stereo stream: everyone
else in the room, without their own voice (mix-minus). In a spatial room each voice is placed by where the speaker
stands and which way the listener faces. Rooms and participants are managed by another program, for example a
virtual-world region, through a JSON HTTP API. The aim is voice for Second Life / OpenSimulator viewers (through an
OpenSim region module, still to come) and for web browsers.

## Features

- **Control API** (API key): create and delete rooms, add and remove participants, set positions and orientations,
  mute and gain. Adding a participant returns a one-time join token for the client.
- **WebRTC signalling over HTTP:** SDP offer/answer and trickle ICE; the server is always the answerer. Opus 48 kHz,
  mono in, stereo out.
- **Mix engines, chosen per room:** `matrix-shared` (default: only the current talkers are mixed, and listeners who
  hear the same mix share one encoder), `matrix` (one encoder per listener, the default for spatial rooms) and
  `pairchains` (a GStreamer chain per source/listener pair, for comparison).
- **Spatial audio:** gain by distance and left/right pan from positions and facing; clients can turn their own
  listener with their session token.
- **Admin UI** (`/admin/`, own password): rooms, live participant levels and WebRTC stats, mute, gain and kick, an
  event log and session history, and a live view of each room's GStreamer pipeline as blocks and wires.
- **Test web client** (`/client/`): join from a browser with a join link, or create rooms in test mode with an API key.
- **Operational hardening:** rooms persist in SQLite; a room's media runs only while someone is in it; dead
  connections and unused joins are removed; graceful shutdown; signalling rate limits and input limits; runs as a
  systemd unit or Windows service.
- **Load tool** (`src/SpatialAudioServer.LoadTest`): fills rooms with fake WebRTC clients that check who hears
  whom and measure the server's CPU, or adds speaking bots to a room for listening tests.

Runs on Linux and Windows (x64). Up to 16 rooms × 32 participants by default; LAN-only for now.

## Status

Under development. Done: the control API, media core and mix engines, signalling and the test web client, the admin
UI, spatial audio and most of the hardening. Still to come: the OpenSim region module and Second Life viewer
support, distance profiles, TURN for use beyond the LAN. See the phases in [PLAN.md](PLAN.md).

## Quick start

Needs the .NET 10 SDK and GStreamer 1.24 or newer with the base, good and bad plugins and libnice
(on Ubuntu: `scripts/setup-ubuntu.sh`; on Windows: the official installer, full install).

```bash
dotnet run --project src/SpatialAudioServer -- apikey create test     # prints a control-API key
dotnet run --project src/SpatialAudioServer -- admin set-password     # admin UI password
dotnet run --project src/SpatialAudioServer                          # HTTP 5000, HTTPS 5001
```

Then open `https://<host>:5001/client/` (test mode, with the key) or `https://<host>:5001/admin/`.
Tests: `dotnet test src`.

## Documents

| document | what it holds |
|---|---|
| [PLAN.md](PLAN.md) | The specification: architecture, API, decisions and their reasons, phases and progress |
| [docs/running.md](docs/running.md) | Installing, configuring and running on Linux and Windows, as a service, firewall, load tool |
| [docs/benchmarks.md](docs/benchmarks.md) | Load-test results: CPU per engine, room counts, test machines |
| [scripts/loadtest-linux-x64/README.md](scripts/loadtest-linux-x64/README.md) | The self-contained load tool for a separate Linux machine |
| [docs/gstreamer_acousticspace_design.md](docs/gstreamer_acousticspace_design.md) | Design notes for a possible acoustic-space DSP element |
| [CLAUDE.md](CLAUDE.md) | Short handoff notes on the current state for AI-assisted sessions |

## Licence

Apache-2.0, see [LICENSE](LICENSE). The admin UI includes [elkjs](https://github.com/kieler/elkjs) for graph layout,
unmodified, under its own licence (EPL-2.0; `src/SpatialAudioServer/wwwroot/admin/vendor/elkjs/`).
