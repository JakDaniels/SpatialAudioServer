# SpatialAudioServer – Plan

An MCU-style audio conferencing server in C#/.NET using GStreamer (`GstSharp.Net` bindings; see "Binding choice").
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

Local environment (Phase 0, Ubuntu 26.04, 2026-11-11): `dotnet --version` 10.0.115, `gst-inspect-1.0 --version`
1.28.2, with webrtcbin/nice/dtls/srtp/opus/audiomixer/audiopanorama/audioamplify present.
Target **net10.0** (LTS; required by GstSharp.Net). The reference projects used `net8.0` + `gstreamer-sharp-netcore 0.0.8`.

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
POST   /api/rooms                         {name, externalId?, maxParticipants?, metadata?}   -> room (201)
GET    /api/rooms[?externalId=]            -> [room]
GET    /api/rooms/{roomId}                 -> room + participants
DELETE /api/rooms/{roomId}                 -> disconnects everyone, tears down pipeline

POST   /api/rooms/{roomId}/participants    {displayName, externalId?(avatar UUID), position?, orientation?, metadata?}
                                            -> {participantId, roomId, joinToken, signalUrl, expiresAt} (201)
GET    /api/rooms/{roomId}/participants    -> [participant]
GET    /api/rooms/{roomId}/participants/{pid}   -> participant
DELETE /api/rooms/{roomId}/participants/{pid}   -> remove/disconnect
PATCH  /api/rooms/{roomId}/participants/{pid}   {muted?, gain?, position?, orientation?}
                                            (position used later by spatial engine; stored now)

GET    /api/status                         -> version, uptime, GStreamer version, plugin check, counts, limits
```

Addressing by external id (OpenSim region/parcel UUID for rooms, avatar UUID for participants):

```
PUT    /api/rooms/by-external/{rid}       {name?, maxParticipants?, metadata?}  get-or-create: 201 new, 200 existing
                                           (body only used on create; name defaults to the external id)
GET|DELETE /api/rooms/by-external/{rid}
POST|GET   /api/rooms/by-external/{rid}/participants
GET|PATCH|DELETE /api/rooms/by-external/{rid}/participants/by-external/{aid}
```
Every room route accepts `{roomId}` or `by-external/{rid}`, and every participant route `{pid}` or
`by-external/{aid}`, in any combination, so the region module never needs to store our ids.

As implemented in Phase 1:
- IDs (rooms and participants) are always server-generated UUIDs; the draft's optional `id` was dropped. Callers
  correlate via `externalId`. Using OpenSim UUIDs as our ids was considered and rejected (2026-11-11): one avatar can
  be in two rooms at once (region crossings, region + parcel voice), a reconnect would collide with the old
  connection's id, and session history is keyed by participant id.
- Room `externalId` is unique server-wide (409 on a duplicate create; unique index, migration 2). Participant
  `externalId` is unique within a room: **adding a participant whose externalId is already in the room replaces the
  old one** (treated as a reconnect, which also covers a missed leave event). The old one's session closes with reason
  `replaced`, its join token dies, and the response's `replacedParticipantId` names it. Replacement works even in a
  full room. The same avatar may be in different rooms at once.
- External ids that parse as GUIDs are normalised to lowercase `D` form (`{ABC…}` = `abc…`); others are trimmed.
- `position` is `{x,y,z}`; `orientation` is a quaternion `{x,y,z,w}` (SL/OpenSim's rotation form). `gain` is 0–4
  (1 = unity; `audioamplify` range). `metadata` is any JSON object up to 16 KB. Name/displayName ≤ 200 chars.
- Errors are RFC 9457 problem details: 400 validation (with per-field `errors`), 401 missing/invalid key,
  404 unknown room/participant, 409 room/participant/server limit reached.
- API keys: bootstrap keys from config (`Auth:ApiKeys`, e.g. env `Auth__ApiKeys__0`), never stored; plus keys
  created with `SpatialAudioServer apikey create <label>` (`list`, `revoke <id|prefix>`), stored as SHA-256 hashes
  with a display prefix (`sas_xxxxxxxx`). Join tokens: 5 min lifetime (`Auth:JoinTokenLifetime`), one-time.

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
- `rooms` – definitions; every room persists until deleted and is restored (empty) at start-up (see "Resolved
  requirements"; the earlier persistent/ephemeral flag idea was dropped). On start-up, sessions left open are closed
  with reason `server-restart` and all join tokens are deleted.
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
src/SpatialAudioServer.slnx
src/Directory.Build.props        net10.0, nullable, warnings as errors
src/SpatialAudioServer/
  Program.cs                 host, DI, config, Kestrel (HTTP 5000 + HTTPS 5001), `apikey` CLI dispatch
  Configuration/             options classes (Server, Storage, Auth, Limits, Media sections)
  Api/                       minimal API endpoints, DTOs, API-key auth, problem-details mapping
  Rooms/                     RoomManager, models, validation (domain, no GStreamer types)
  Media/                     GStreamer layer: GstRuntime (init/plugin check), IRoomMedia (+ NullRoomMedia until
                             Phase 2), later RoomPipeline, ParticipantEndpoint (webrtcbin), ListenerMix,
                             MixChain, PadProbeHelpers, DotDump
  Hosting/                   StartupService, DevCertificate, ApiKeyCommand (CLI)
  Signalling/                SDP/ICE handling (Phase 3)
  Storage/                   SQLite stores + numbered migrations (PRAGMA user_version)
  wwwroot/admin, wwwroot/client   (Phases 3-4)
  data/                      runtime: SQLite db + generated dev cert (git-ignored)
src/SpatialAudioServer.Tests/    xUnit unit + integration tests (WebApplicationFactory, temp SQLite)
spikes/WebRtcEcho/               Phase 0 spike (not part of the solution)
tools/                           GStreamer-based fake client for load/integration tests (from the spike's FakeClient)
docs/
```
Domain/API code talks to the media layer via an interface (`IRoomMedia`) so the API and storage can be
tested without GStreamer.

## Phases

0. **Spike / de-risk (do first).** Console test: webrtcbin answerer with a browser offer, Opus in → loopback
   out, driven from C# (originally planned with `gstreamer-sharp-netcore 0.0.8`; GstSharp.Net chosen, see "Binding choice"). Confirm the package exposes what's needed
   (webrtcbin promises/`create-answer`, `GstWebRTCSessionDescription`, `add-ice-candidate`, request pads,
   pad probes). If bindings are missing, write thin P/Invoke shims. Also verify on Windows, and with a
   self-built newer GStreamer in a private prefix (see below).
1. **Skeleton.** Solution, config, logging, GStreamer init + plugin check, SQLite + migrations, API key auth,
   `/api/status`, room/participant CRUD against in-memory + SQLite (no media).
   **Done 2026-11-11:** 23 tests pass (incl. external-id addressing) (`dotnet test src`); smoke-tested over HTTPS on the LAN. Participant `state`
   stays `awaitingSignal` until Phase 3. Event log is written (room/participant/mute/apikey events) but only
   readable from code until the admin API (Phase 4).
2. **Media core.** RoomPipeline, participant ingress/egress, per-listener mix-minus with gain/pan, dynamic
   add/remove with pad probes, silence keep-alive. Test with 2, 4, 8, 16 participants; record CPU/latency;
   confirm clean teardown (no leaks, no hangs) when participants/rooms churn.
   Decided 2026-11-11: **basic signalling moves into Phase 2** (`/signal/{pid}/offer`, `/signal/{pid}/ice` POST/GET,
   `DELETE /signal/{pid}`, join-token redemption), so the mix can be heard with real browsers and fake clients test
   over real HTTP. Load testing: correctness and churn tests on the dev box now (4-core Pentium Silver N6005, 5 GB;
   fake clients share the CPU, so CPU numbers are indicative only); the real 32-participant benchmark runs later with
   the fake clients (`tools/`) on a second LAN machine.
   **Stages A+B done 2026-11-11** (33 tests, 3 consecutive clean runs): mix-minus for 2–4 people, late join, leave
   via API and via signalling, mute/unmute, replace-while-connected, room delete with people connected, signalling
   auth, 5-round churn with chain-count and native-thread checks. Fake client + Goertzel tone meter live in
   `src/SpatialAudioServer.LoadTest` (load runs themselves still to do: stage C). Findings, all reflected in code:
   - **Answerer order must be explicit.** Linking the send branch before the offer and letting webrtcbin match it
     by caps is a race (≈1 in 3 endpoints never sent: the send pad stayed blocked forever). Fixed order:
     `set-remote-description` → `get-transceiver` for the audio m-line, set `direction=sendrecv` and
     `codec-preferences` (Opus, offer's PT) → request `sink_<mline>` explicitly → link + start send branch →
     `create-answer`/`set-local-description` (`WebRtcBin.PrepareSendTransceiver`).
   - **Handle LATENCY bus messages** (`RecalculateLatency`) in every pipeline that gains elements while PLAYING
     (`BusWatcher`), otherwise RTP sessions log "can't determine running time … without configured latency".
   - **libnice UPnP leaks a thread per ICE agent** (`gupnp-igd-thread`, +2 per join incl. the client). UPnP is
     disabled on each NiceAgent via raw GObject calls (`NativeGObject`): reading libnice objects through the
     binding's generic wrappers over-releases them and crashes (`GST_IS_WEBRTC_ICE` assertion).
   - Promise waits now time out (10 s) so a silent webrtcbin cannot wedge a room's worker forever.
   - GstSharp.Net: `Pipeline.New(name)`, `SetName()` (Name is read-only), and `Gst.DateTime`/`Gst.Task`/
     `Gst.GLib.DateTime` clash with System types (alias them).
   - Browser stereo: Chrome decodes Opus as stereo only if *its own* offer's fmtp has `stereo=1`, so the Phase 3
     test client must munge its offer; Firefox decodes stereo anyway. The server's answer is not munged.
3. **Signalling + web client.** Test web client, STUN/TURN config,
   reconnect / ICE-failure handling (drop participant after timeout, event logged).
4. **Admin UI.** Rooms/participants views, levels/stats, kick/mute, event log, pipeline graph.
5. **Hardening.** Limits, input validation, token expiry, rate limiting, graceful shutdown, Dockerfile,
   Windows/Linux run docs, load test using the GStreamer fake-client tool.
6. **Spatial hooks (prep, not full spatial).** Position/orientation stored and exposed; a `SpatialPolicy`
   interface computing gain/pan per (source, listener) – default policy = constant gain, centre pan.
   Distance/culling policy comes in the later spatial project.
7. **(Later) SL/OpenSim integration.** Viewer protocol compatibility + region module.

## Risks / things to verify

- ~~gstreamer-sharp-netcore 0.0.8 completeness~~ - resolved: GstSharp.Net chosen and proven in Phase 0. Remaining
  binding risk is its maintenance (single maintainer); keep it behind `Media/`.
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
**Decision (2026-11-11): `GstSharp.Net` 1.28.17** (`GstSharp.Net`, `GstSharp.Net.Sdp`, `GstSharp.Net.WebRTC`; add
`GstSharp.Net.App`/`.Audio` if needed). GirCore stays the fallback; the other candidates were not spiked.

Phase 0 spike: [spikes/WebRtcEcho](spikes/WebRtcEcho/README.md) (ASP.NET minimal API + GStreamer echo; `--selftest` runs a
GStreamer fake client against it). Findings:
- **Works end to end on GStreamer 1.28.2:** HTTP offer → server answer, trickle ICE both ways (server candidates polled),
  DTLS/SRTP, Opus in → `opusdec` → `audiomixer` (with a silent live keep-alive source) → `opusenc` → back out. Self-test:
  connected in < 1 s, ~95% of returned buffers carry the echoed tone, 3/3 runs passed. **Firefox over the LAN (HTTPS, self-signed): works**, own
  voice heard back ~0.3 s later (round trip; likely dominated by webrtcbin's default 200 ms jitterbuffer `latency`, plus
  the browser's jitterbuffer, 40 ms mixer latency and Opus framing). Tune in Phase 2: try webrtcbin `latency` ~50 ms on LAN.
- **HTTPS is needed for any non-localhost browser** (`getUserMedia`). The spike generates a self-signed cert covering
  localhost, host name and local IPv4s (`DevCertificate.cs`, saved to git-ignored `.devcert/`); reuse this in Phase 1.
- **Everything Phase 0 needed is exposed:** `Promise` (`New`, `Wait`, `GetReply`), `WebRTCSessionDescription.New/GetSdp`,
  `SDPMessage.NewFromText/AsText`, typed enums for `ice-gathering-state`/`connection-state`, `RequestPadSimple`,
  `ReleaseRequestPad`, `Pad.AddProbe/RemoveProbe` (used in the fake client), buffer mapping as a `Span<byte>`.
- **webrtcbin has no .gir**, so its signals are called by name (`EmitSignal("create-answer", …)`, `ConnectSignal(…)`).
  Keep all of those in one class (`WebRtcPeer` in the spike) so a typo can only live in one place.
- Server is answerer with the send branch linked **before** the offer arrives: webrtcbin reuses that request-pad
  transceiver for the offered m-line. The send payload type must equal the browser's Opus PT (Chrome 111, Firefox 109),
  so it is read from the offer before building the pipeline.
- Threading as the plan assumed: promise/ICE/notify callbacks on webrtcbin's own thread, `pad-added` on a streaming
  thread; no GLib main loop needed (bus is polled on a thread). Promise waits block, so never call them from a GStreamer
  thread. The binding reports exceptions thrown in callbacks via `ExceptionTrap.UnhandledException`.
- `using Gst;` clashes with `System.Uri`, `System.DateTime`, `System.Threading.Tasks.Task`: alias them, or keep `Gst`
  out of files that do not need it (another reason for the `Media/` boundary).
- GStreamer logs "loop detected in the graph" when one webrtcbin both feeds and is fed by the same mixer (the echo).
  Harmless here; the real mix-minus never routes a source to its own listener, but cycles across participants
  (A→B's mix→B's webrtcbin, B→A's mix→A's webrtcbin) will exist. Check whether this warning matters in Phase 2.
- A bare caps string cannot end a `ParseBinFromDescription` string; use `capsfilter caps="…"`.
- Licence: GstSharp.Net is LGPL-2.1-or-later, consumed as NuGet packages (dynamic linking), compatible with our
  Apache-2.0. The source clone in `docs/reference/GstSharp.Net` is git-ignored and must stay out of this repo.
- Risk: effectively single-maintainer and ~3 months old. Mitigated by the `Media/` boundary and the GirCore fallback.

Still open from Phase 0: Chrome/Edge test of the spike page (Firefox done); Windows run (official MSVC runtime);
self-built newer GStreamer is no longer needed (distro 1.28.2 is current).

Candidates considered:
- `GStreamer-Sharp-NetCore` 0.0.8 - baseline; known from the earlier experiments; likely missing GstWebRTC types.
- `GirCore.Gst-1.0` / `GirCore.GstSdp` / `GirCore.GstWebRTC` (gir.core, generated from GObject Introspection, actively developed) -
  strongest candidate for full, current webrtcbin coverage; needs the matching typelibs (`gir1.2-gst-plugins-bad-1.0`). Verify current versions on NuGet.
- `GstSharp.Net` + `GstSharp.Net.WebRTC` (1.28 bindings, net10.0 only, young project, maintenance unverified) - **chosen**, see above.
- `GstSharpBundle` - bundles native binaries (only a Windows x64 runtime package so far), LGPL-2.1, GStreamer version unstated. Possibly useful for
  Windows packaging, not for the Linux server.
- Official `GstSharp` NuGet is stale (1.18, .NET Framework 4.5). Not an option.
