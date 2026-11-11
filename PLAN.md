# SpatialAudioServer – Plan

An MCU-style audio conferencing server in C#/.NET using GStreamer (`GStreamer-Sharp-NetCore`).
Rooms and participants are managed through a JSON HTTP API. Audio is mixed server-side with a
**separate mix chain per listener**, so spatial audio (per-source gain/pan from avatar position)
can be added later without restructuring.

Phase 1 targets browser clients only. The Second Life / Firestorm viewer (via an OpenSimulator
region module that makes one room per region and joins/removes avatars through this API) comes later,
but nothing in phase 1 should make that harder.

## Decisions so far

| Topic | Decision |
|---|---|
| Signalling | HTTP JSON offer/answer + trickle ICE per participant (same path for web and, later, SL viewer). Optional WebSocket for server→client events can be added later. |
| Auth | Static API key(s) for the control API and admin UI; `join` returns a short-lived one-time token used by the client for signalling. No end-user accounts. |
| Audio | Opus 48 kHz. Mono in, **stereo out from day one** (each source passes through `audiopanorama`, centred by default). |
| Mix topology | Per-listener mix-minus: each listener has own chain; a source is never routed to its own listener. |
| Platforms | Linux and Windows (and ideally macOS). |
| Persistence | SQLite (`Microsoft.Data.Sqlite`). |
| Admin | Built-in web UI (static HTML/JS, no build step) served by the same process. |
| Reference code | `~/src/gstreamer-testing` (`audiomixer-test3` per-pair queue→amplify→panorama chains, `webrtc-test1`), `~/src/gstreamer-test2`. Used for ideas, not copied wholesale. |

Local environment: .NET 10.0.115 SDK, GStreamer 1.20.3 with webrtcbin/nice/dtls/srtp/opus/rtpmanager.
Reference projects used `net8.0` + `gstreamer-sharp-netcore 0.0.8`; target **net8.0** (LTS, known to work
with the package) unless a reason appears to move.

## Architecture

Single ASP.NET Core (Kestrel) process:

```
 Region module / admin tools ──JSON API (API key)──┐
 Browser client ──signalling (join token)──────────┤
 Admin web UI ─────────────────────────────────────┤
                                                    ▼
                            ┌──────────── SpatialAudioServer ────────────┐
                            │ HTTP layer (minimal APIs)                  │
                            │ RoomManager ── Room ── Participant         │
                            │ GStreamer layer (one Pipeline per Room)    │
                            │ SQLite store (rooms, tokens, event log)    │
                            └────────────────────────────────────────────┘
                                    ▲ SRTP/UDP (WebRTC, Opus)  ▼
                                      browsers / SL viewers
```

### GStreamer pipeline design

One `Pipeline` per room. Per participant:

**Ingress (participant → server)**
`webrtcbin` (one sendrecv audio transceiver) → `rtpopusdepay` → `opusdec` → `audioconvert` →
`audioresample` → capsfilter (`audio/x-raw,rate=48000,channels=1`) → `tee` (the participant's "source tee")

**Per (source, listener) chain** – created when both are in the room, for every source ≠ listener
`queue (leaky)` → `audioamplify` (gain) → `audiopanorama` (mono → stereo, pan) → listener's mixer sink pad

**Egress (server → participant)** – the participant's "listener mix"
`audiomixer` (stereo, 48 kHz) → `audioconvert` → `audioresample` → `opusenc` → `rtpopuspay` → `webrtcbin`
plus an `audiotestsrc is-live=true wave=silence` feeding the mixer so it keeps producing output (and the
Opus stream continues) when nobody is speaking or the listener is alone.

Why this shape:
- Gain/pan are per (source, listener) – exactly what spatialisation needs later (distance attenuation + azimuth).
- Later optimisations (skip chains for sources out of range, mute-by-VAD, culling) only touch chain creation.
- Per-pair chain count is O(N²). Mitigations: a configurable max participants per room (default ~16–24),
  `queue` leaky settings, and the culling hook above. Measure early (see Phase 2).

Lessons to apply from the earlier experiments:
- Add/remove of chains while PLAYING must use pad probes (block, flush/EOS, unlink, release request pads,
  set element to NULL, remove) – otherwise deadlocks/crashes. Wrap in one tested helper.
- Don't implement "self-mute" as gain 0; just don't build the self chain.
- All GStreamer graph mutation is serialised per room on a dedicated worker (single-threaded actor/channel);
  webrtcbin signals (`on-negotiation-needed`, `on-ice-candidate`, `pad-added`, `notify::ice-connection-state`)
  arrive on GStreamer threads and only enqueue work.
- A GLib `MainLoop` thread runs for bus messages; bus watch per pipeline logs errors/state changes.
- Pipeline graph `.dot` dump available on demand (admin UI/endpoint) – the old `Visualiser` is useful.

### WebRTC specifics
- Server is the SDP **answerer**: browser creates the offer (audio sendrecv, Opus), server returns the answer.
  This is what the SL viewer does too.
- Trickle ICE both directions: client candidates arrive via HTTP; server candidates are retrieved by the client
  (long-poll or returned in a follow-up response; WebSocket events later). Start with: server gathers
  candidates and includes them in the answer where possible, plus an endpoint for further ones.
- ICE: configurable STUN/TURN servers, optional fixed public IP/port range (see risks).
- Opus: 48 kHz, `useinbandfec`, ~32–64 kbps stereo downstream, mono upstream; DTX optional.
- Browsers need HTTPS (or localhost) for `getUserMedia`; Kestrel certificate configurable, or run behind a
  reverse proxy.

## JSON API (draft)

All control endpoints require `X-Api-Key` (or `Authorization: Bearer`).

```
POST   /api/rooms                         {id?, name, maxParticipants?, metadata?}   -> room
GET    /api/rooms                          -> [room]
GET    /api/rooms/{roomId}                 -> room + participants
DELETE /api/rooms/{roomId}                 -> disconnects everyone, tears down pipeline

POST   /api/rooms/{roomId}/participants    {id?, displayName, externalId?(avatar UUID), position?, metadata?}
                                            -> {participantId, joinToken, signalUrl, expiresAt}
GET    /api/rooms/{roomId}/participants    -> [participant]
DELETE /api/rooms/{roomId}/participants/{pid}   -> remove/disconnect
PATCH  /api/rooms/{roomId}/participants/{pid}   {muted?, gain?, position?, orientation?}
                                            (position used later by spatial engine; stored now)

GET    /api/status                         -> version, uptime, GStreamer version, plugin check, counts
```

Client signalling (authenticated by the one-time join token, not the API key):

```
POST   /signal/{pid}/offer      {sdp}                 -> {sdp (answer)}
POST   /signal/{pid}/ice        {candidates:[...], completed?}
GET    /signal/{pid}/ice        (long-poll)          -> server candidates
DELETE /signal/{pid}            client leaves
```

Region-module notes (future): create room per region on region start, join/leave on avatar enter/leave,
`PATCH` position at a modest rate. SL viewer WebRTC voice (provision/signalling capabilities and any
data-channel messages used for position/mute) must be checked against the current viewer source before that
phase; the signalling endpoints above are shaped so the module can proxy the viewer's requests to them.

## Persistence (SQLite)

Live state (pipelines, ICE state) is in memory. SQLite holds:
- `rooms` – definition so rooms can optionally be restored after restart (flag per room: persistent vs ephemeral).
- `api_keys` – hashed keys, label, created/last used.
- `join_tokens` – hashed token, participant, expiry, used.
- `sessions` – participant join/leave history (room, participant, external id, times, disconnect reason).
- `events` – audit/event log shown in admin UI.
- Schema versioning via simple numbered migrations on startup.

## Admin web UI

Static page(s) under `wwwroot/admin`, vanilla JS, polling `/api/admin/*` (or SSE) with API key login:
- Rooms list: name, participant count, created, pipeline state.
- Room detail: participants with display name, external id, ICE/connection state, muted, gain, position,
  inbound level meter (via `level` element messages), bitrate/packet loss (webrtcbin `get-stats`).
- Actions: create/delete room, kick/mute participant.
- Event log, server status, download pipeline `.dot`/rendered graph.

Also a minimal **test web client** (`wwwroot/client`): join via token, mic capture, plays the mix, shows
connection state. Used for manual testing and as the reference for real clients.

## Project layout

```
SpatialAudioServer.sln
src/SpatialAudioServer/
  Program.cs                 host, DI, config, Kestrel
  Api/                       minimal API endpoints, DTOs, auth filters
  Rooms/                     RoomManager, Room, Participant (domain, no GStreamer types)
  Media/                     GStreamer layer: GstRuntime (init/mainloop/plugin check),
                             RoomPipeline, ParticipantEndpoint (webrtcbin), ListenerMix,
                             MixChain, PadProbeHelpers, DotDump
  Signalling/                SDP/ICE handling
  Storage/                   SQLite store + migrations
  wwwroot/admin, wwwroot/client
tests/SpatialAudioServer.Tests/   unit + integration tests
tools/                            GStreamer-based fake client for load/integration tests
docs/
```
Domain/API code talks to the media layer via an interface (`IRoomMedia`) so the API and storage can be
tested without GStreamer.

## Phases

0. **Spike / de-risk (do first).** Console test: webrtcbin answerer with a browser offer, Opus in → loopback
   out, driven from C# via `gstreamer-sharp-netcore 0.0.8`. Confirm the package exposes what's needed
   (webrtcbin promises/`create-answer`, `GstWebRTCSessionDescription`, `add-ice-candidate`, request pads,
   pad probes). If bindings are missing, write thin P/Invoke shims. Also verify on Windows, and with a
   self-built newer GStreamer in a private prefix (see below).
1. **Skeleton.** Solution, config, logging, GStreamer init + plugin check, SQLite + migrations, API key auth,
   `/api/status`, room/participant CRUD against in-memory + SQLite (no media).
2. **Media core.** RoomPipeline, participant ingress/egress, per-listener mix-minus with gain/pan, dynamic
   add/remove with pad probes, silence keep-alive. Test with 2, 4, 8, 16 participants; record CPU/latency;
   confirm clean teardown (no leaks, no hangs) when participants/rooms churn.
3. **Signalling + web client.** HTTP offer/answer/ICE, join tokens, test web client, STUN/TURN config,
   reconnect / ICE-failure handling (drop participant after timeout, event logged).
4. **Admin UI.** Rooms/participants views, levels/stats, kick/mute, event log, pipeline graph.
5. **Hardening.** Limits, input validation, token expiry, rate limiting, graceful shutdown, Dockerfile,
   Windows/Linux run docs, load test using the GStreamer fake-client tool.
6. **Spatial hooks (prep, not full spatial).** Position/orientation stored and exposed; a `SpatialPolicy`
   interface computing gain/pan per (source, listener) – default policy = constant gain, centre pan.
   Distance/culling policy comes in the later spatial project.
7. **(Later) SL/OpenSim integration.** Viewer protocol compatibility + region module.

## Risks / things to verify

- **gstreamer-sharp-netcore 0.0.8 completeness** for webrtcbin APIs (see Phase 0). Old test code used
  property-style access for offers, which may not be the real API.
- **GStreamer version differences.** Linux distro has 1.20.3; webrtcbin has had many fixes since (1.22/1.24).
  ICE port-range / NAT 1:1 control may only exist in newer versions. Recommend documenting a minimum
  version and providing a Docker image with a newer GStreamer.
- **Windows:** need the official MSVC GStreamer runtime including gst-plugins-bad (webrtc), libnice,
  dtls, srtp; locating native DLLs from .NET (PATH / `GSTREAMER_1_0_ROOT_MSVC_X86_64`).
- **O(N²) chains** – CPU and thread count at larger room sizes; spatial culling mitigates later.
- **Dynamic pipeline modification** is the usual crash/deadlock source; centralise in one helper + per-room worker.
- **Jitter/latency:** `audiomixer` latency and webrtcbin jitterbuffer `latency` need tuning to avoid
  underruns vs. delay; make both configurable.
- **Browser autoplay / HTTPS** constraints for the test client.

## Resolved requirements

- **Scale target (absolute maximum):** 16 rooms x 32 participants. Expected real load is much lower. Per-room
  cap default 32; a server-wide cap on total participants, configurable. 32 participants means up to
  32 x 31 = 992 pair chains in a room, so Phase 2 must benchmark this worst case (all rooms full = ~16k chains;
  document the measured limit) and the culling/VAD optimisations in Phase 6 may be needed sooner than planned.
- **Room IDs:** always generated by the server (UUID) on create. The caller may attach `externalId`/`name`
  (e.g. region UUID) and look rooms up by it (`GET /api/rooms?externalId=`) so the region module can find its room.
- **Persistence:** rooms persist in SQLite until explicitly deleted; restored at startup. Participants/sessions are
  not restored (clients must rejoin), but a restored room is recreated empty and ready.
- **Admin auth:** separate admin password (hashed, set via config/first-run CLI) -> session cookie. Distinct from API keys,
  which are only for the control API.
- **Deferred:** recording and listen-only participant types.
- **Network:** LAN only during development (STUN/TURN optional, plain HTTP on localhost/LAN with a dev certificate for
  getUserMedia); designed for public deployment later (TLS, TURN, rate limiting in Phase 5).
- **Licence:** keep Apache-2.0 (permissive, compatible with OpenSim's BSD and with dynamically linking LGPL GStreamer).

## Using a newer GStreamer without Docker

The .NET binding only P/Invokes the native `libgstreamer-1.0.so.0` etc. by name at runtime, and GStreamer's C ABI is
stable across 1.x, so a self-built GStreamer in a private prefix works with no changes to the binding:

1. Build the GStreamer monorepo (meson) into e.g. `~/gst/1.26` with gst-plugins-bad `webrtc`, libnice (as a subproject),
   `dtls`/`srtp`, `opus`, `rtpmanager`, `gstreamer-sharp` not required.
2. Run the server with `LD_LIBRARY_PATH=~/gst/1.26/lib/x86_64-linux-gnu`, `GST_PLUGIN_SYSTEM_PATH_1_0` and
   `GST_PLUGIN_PATH` pointing at the new plugins (and an isolated `GST_REGISTRY`), so system 1.20 is not mixed in.
   A `run-dev.sh` script will set these, and the `/api/status` endpoint reports the loaded GStreamer version.
3. Caveat: the binding (0.0.8) was generated against an old API, so *new* APIs are not wrapped; anything needed
   (e.g. ICE port range properties) is reachable through `GObject` property/signal calls or small P/Invoke shims.
   Phase 0 confirms this.

This is added to Phase 0 (build/verify newer GStreamer alongside the system one) and Docker becomes an optional
later deployment aid rather than a requirement.

## Hardware and browser targets

- **Server:** 4 or 8 cores. Phase 2 benchmark gate: a full 32-participant room should run on 4 cores with headroom
  (target < ~60% total CPU, no audible underruns); if not, VAD/culling is pulled forward. Record results for 4 and 8 cores.
- **Browsers:** current evergreen browsers (Chrome/Edge, Firefox, Safari 2025+). No legacy/Plan-B SDP or polyfills needed.
- **Dev/deploy VM:** Ubuntu 24.04 or 26.04 LTS. Their distro GStreamer is considerably newer than 22.04's 1.20
  (24.04 ships 1.24; 26.04 should ship a newer one still - verify with `apt-cache policy libgstreamer1.0-0`),
  so the self-built GStreamer path above becomes a fallback, not the default. Phase 0 records the exact versions used,
  and the minimum supported GStreamer version is set from what the spike proves works.

## Binding choice (Phase 0 decision)

Requested package: `GStreamer-Sharp-NetCore` 0.0.8 (a community port of old gstreamer-sharp, roughly 1.14-1.16 era API).
Phase 0 time-boxes a webrtcbin spike against each candidate and picks one. All GStreamer use stays behind the `Media/` layer
so a swap is contained:
- `GStreamer-Sharp-NetCore` 0.0.8 - baseline; known from the earlier experiments; likely missing GstWebRTC types.
- `GirCore.Gst-1.0` / `GirCore.GstSdp` / `GirCore.GstWebRTC` (gir.core, generated from GObject Introspection, actively developed) -
  strongest candidate for full, current webrtcbin coverage; needs the matching typelibs (`gir1.2-gst-plugins-bad-1.0`). Verify current versions on NuGet.
- `GstSharp.Net` + `GstSharp.Net.WebRTC` (1.28 bindings, net10.0 only, young project, maintenance unverified) - promising but unproven.
- `GstSharpBundle` - bundles native binaries (only a Windows x64 runtime package so far), LGPL-2.1, GStreamer version unstated. Possibly useful for
  Windows packaging, not for the Linux server.
- Official `GstSharp` NuGet is stale (1.18, .NET Framework 4.5). Not an option.
