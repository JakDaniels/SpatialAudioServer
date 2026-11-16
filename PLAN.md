# SpatialAudioServer – Plan

An MCU-style audio conferencing server in C# (.NET 10) on GStreamer, through the `GstSharp.Net` bindings.
Rooms and participants are managed through a JSON HTTP API. Audio is mixed on the server with a **separate
mix-minus for each listener** (stereo, with per-source gain and pan), which is what spatial audio needs.

Browser clients come first. The Second Life / Firestorm viewer (via an OpenSimulator region module that makes one
room per region and joins/removes avatars through this API) comes later, and nothing in the design makes that harder.

## Decisions so far

| Topic | Decision |
|---|---|
| Topic | Decision |
|---|---|
| Stack | .NET 10 (C#), ASP.NET Core minimal APIs on Kestrel; GStreamer 1.24 or newer (developed on 1.28.2) through `GstSharp.Net` 1.28 (see "Binding choice"; GirCore is the fallback); SQLite through `Microsoft.Data.Sqlite`; xUnit tests. |
| Signalling | HTTP JSON offer/answer + trickle ICE per participant (same path for web and, later, SL viewer). Optional WebSocket for server→client events can be added later. |
| Auth | API keys for the control API; `join` returns a short-lived one-time token the client uses for signalling (exchanged for a session token); a separate admin password for the admin UI. No end-user accounts. |
| Audio | Opus 48 kHz. Mono in, **stereo out** (per-source gain and pan for every listener; centred unless the room is spatial). |
| Mix topology | Per-listener mix-minus: a source is never mixed into its own listener's stream. Pluggable mix engines per room: `matrix-shared` (default), `matrix` (default for spatial rooms), `pairchains`. |
| Platforms | Linux and Windows, x64. |
| Persistence | SQLite: rooms, API keys, tokens, event log, session history. |
| Admin | Built-in web UI (static HTML/JS, no build step) served by the same process; so is the test web client. |

Development machine: Ubuntu 26.04, .NET SDK 10.0.115, GStreamer 1.28.2. The server checks at start-up that every
GStreamer element it needs is present.

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

One `Pipeline` per room, created when the first participant joins. `RoomPipeline` owns the participants'
`webrtcbin` endpoints (signalling, ICE, connection state, teardown order); the room's **mix engine** (`IMixEngine`,
`Media/Engines/`, chosen per room) owns everything from RTP in to RTP out. See "Mix engines" under Phase 2.

**Ingress (every engine)**
`webrtcbin` (one sendrecv audio transceiver) → `rtpopusdepay` → `opusdec` → mono 48 kHz raw audio.

**`matrix` and `matrix-shared`** (`RoomInterleave` + `MatrixMixer`)
- Room: a live silent source in slot 0 and each source's decoded audio in its own slot → `audiointerleave` → `tee`.
- `matrix`, per listener: `tee` → `queue` → `audiomixmatrix` (a 2 × slots gain matrix: L/R gain of every source as
  this listener hears it, own slot off) → Opus encoder → `rtpopuspay` → `webrtcbin`. One encoder per listener.
- `matrix-shared`: only the current talkers are mixed. Each talker has a personal mix (talkers minus themselves);
  everyone else hears a cluster mix shared by all listeners who hear the talkers the same way (one cluster in a
  non-spatial room). Each listener has an `input-selector` over Opus frames (cluster | personal) → `rtpopuspay`, so
  switching keeps the RTP stream continuous. Encoders = talkers + clusters.

**`pairchains`** (for comparison): per (source, listener) `queue` → `audioamplify` → `audiopanorama` → the
listener's `audiomixer` (with a live silent source) → Opus encoder → `webrtcbin`. Chains grow as N(N−1).

Why this shape:
- Gain/pan are per (source, listener): what spatialisation needs (distance attenuation + azimuth); in the matrix
  engines they are matrix coefficients, so changing them touches no elements.
- Mix-minus by construction: a listener's own slot is always off, and no self chain exists.

Rules for the pipeline code:
- Adding and removing branches while PLAYING uses pad probes (idle probe, unlink, release request pads, element to
  NULL, remove), in shared helpers (`MediaBlocks`); otherwise deadlocks and crashes.
- All graph changes for a room are serialised on its worker (`RoomWorker`); webrtcbin signals (`on-ice-candidate`,
  `pad-added`, `notify::connection-state`) arrive on GStreamer threads and only enqueue work.
- Each pipeline's bus is watched on its own thread (`BusWatcher`; no GLib main loop): errors, state changes,
  latency recalculation.
- The pipeline is visible in the admin UI's pipeline view (JSON snapshot from the room's worker), and GStreamer's own
  `.dot` dump can be downloaded.

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
POST   /api/rooms                         {name, externalId?, maxParticipants?, metadata?, engine?}   -> room (201)
                                            (engine: mix engine name, default Media:Engine; fixed for the room's life)
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

GET    /api/status                         -> version, uptime, GStreamer version, plugin check, counts, limits,
                                              process stats, defaultEngine, engines [{name, description}]
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
- Event log, server status, pipeline view (blocks and wires, see Phase 4), download pipeline `.dot`.

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
  Media/                     GStreamer layer: GstRuntime, IRoomMedia/GstRoomMedia/NullRoomMedia, RoomPipeline
                             (webrtcbin endpoints per room), WebRtcBin, RoomWorker, BusWatcher, NativeGObject
  Media/Engines/             IMixEngine + MixEngines catalogue, MediaBlocks, one class per engine
  Hosting/                   StartupService, ParticipantSweeper, DevCertificate, ApiKeyCommand/AdminCommand (CLI)
  Signalling/                SDP/ICE handling (Phase 3)
  Storage/                   SQLite stores + numbered migrations (PRAGMA user_version)
  wwwroot/admin, wwwroot/client   (Phases 3-4)
  data/                      runtime: SQLite db + generated dev cert (git-ignored)
src/SpatialAudioServer.Tests/    xUnit unit + integration tests (WebApplicationFactory, temp SQLite)
src/SpatialAudioServer.LoadTest/ load tool: GStreamer fake clients (FakeClient, ToneMeter, bot voices); the tests use it too
spikes/WebRtcEcho/               Phase 0 spike (not part of the solution)
scripts/                         setup-ubuntu.sh, build-loadtest.sh, render-voices.py,
                                 loadtest-linux-x64/ (voices, README; the self-contained load tool is built here, not committed)
docs/                            running.md (Linux/Windows run docs), benchmarks.md (load-test results), design notes;
                                 reference/ (git-ignored sources)
```
Domain/API code talks to the media layer via an interface (`IRoomMedia`) so the API and storage can be
tested without GStreamer.

## Phases

0. **Spike / de-risk (do first).** Console test: webrtcbin answerer with a browser offer, Opus in → loopback
   out, driven from C# through GstSharp.Net (see "Binding choice"). Confirm the binding exposes what's needed
   (webrtcbin promises/`create-answer`, `GstWebRTCSessionDescription`, `add-ice-candidate`, request pads,
   pad probes); anything missing goes through thin P/Invoke shims. Also verify on Windows.
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
   the fake clients (`src/SpatialAudioServer.LoadTest`) on a second LAN machine.
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

   **Stage C (2026-11-12): load tool + first numbers.** `dotnet run --project src/SpatialAudioServer.LoadTest --
   --api-key KEY [--server URL] [--participants N] [--rooms R] [--duration S] [--insecure]`; tones 300 Hz + 50 Hz·i,
   volume scaled to avoid clipping; reports server CPU/memory/threads from `/api/status` (now has a `process`
   block). Results: [docs/benchmarks.md](docs/benchmarks.md).
   **Finding: the per-pair chain topology does not reach the 32-person target.** Cost is ~1% of a core per chain
   and chains grow as N(N−1) (32 people = 992 chains ≈ 9 cores of the N6005), and threads grow as N².
   **Stage D (2026-11-12): pluggable mix engines**, so alternative topologies can be compared under the same tests
   and load runs (user's decision, instead of picking one optimisation up front).
   - Boundary is **RTP in → RTP out**: `RoomPipeline` keeps webrtcbin, signalling, ICE, connection state and the
     teardown order; `IMixEngine` (`Media/Engines/`) gets `AttachListener(pid, payloadType, webrtcbinSinkPad)` on
     offer, `AttachSource(pid, rtpPad)` synchronously in pad-added, `Disconnect` (everything still running: take the
     participant out of every mix) → webrtcbin to NULL → `Release` (remove the rest), plus participant
     added/updated/removed, `MixedPairs` and `Counters`. Engines can therefore share encoders, skip decoding, or mix
     in C#. Shared pieces live in `MediaBlocks` (Opus decoder/encoder bins, silence, link, idle-probe unlink).
   - **Chosen per room**: `engine` on `POST /api/rooms` and `PUT /api/rooms/by-external/{id}`, default
     `Media:Engine`; stored in `rooms.engine` (migration 3); an unknown stored engine falls back to the default with
     a warning; an unknown `Media:Engine` stops start-up. `GET /api/status` lists the engines.
   - **Same tests for every engine**: `MixSuite<TEngine>` and `MixChangeSuite<TEngine>` hold the end-to-end tests;
     `EngineSuites.cs` has one marker type + two subclasses per engine, and `EngineCoverageTests` fails if a
     registered engine has no suites. Adding an engine = implement `IMixEngine`, register it in `MixEngines`, add
     the subclasses.
   - **Load tool**: `--engine a,b,…` runs the same load against each engine in turn and prints a comparison table.
   - First engine: `pairchains` (the stage A–C design).
   - **`matrix` engine (user's design, 2026-11-12):** one `audiointerleave` per room → `tee` → per listener
     `queue` → `audiomixmatrix` (2 × slots gain matrix, own slot off) → Opus encoder. No per-pair elements; per-pair
     gain/pan are matrix coefficients. Constraints found in the 1.28 sources (`docs/reference/gstreamer`, git-ignored):
     - audiointerleave's channel count = its sink pads, and releasing one renumbers the rest; audiomixmatrix in
       manual mode needs the exact input channel count and reads out of bounds on a width change. So all slot pads
       (room `maxParticipants` + 1; slot 0 = silence keep-alive) are requested up front and never released. A
       participant takes a free slot when audio arrives and frees it on leave.
     - `channel-positions-from-input=false` keeps every position NONE, so slot k is channel k (by default pads
       with and without caps sort differently and channels shuffle).
     - The `matrix` setter frees/rebuilds tables without a lock → applied from a buffer probe on the matrix sink
       pad (streaming thread). The sparse/dense mixer is chosen at caps time by density (≥ 50% non-zero = dense), and
       crossing that line later would silence the sparse one → "off" is 1e-9 (−180 dB), so it is always dense.
     The remaining cost is per participant (decode, encode, SRTP, jitter buffer), not per pair.
   - **Second-machine benchmark (2026-11-12)**, server on DevTest26 (4 vCPUs), tool on the 22.04 machine: matrix
     passes 32 people but at 78% of 4 cores, over the gate; listener threads (matrix + Opus encode + send) are
     the largest share, and Opus complexity 5 instead of 10 saves ~17% ([docs/benchmarks.md](docs/benchmarks.md)).
     The first cross-machine run found a load-tool bug (signalling JSON enums), hidden on one machine because ICE
     connected from one-sided candidates; tests now assert both directions of trickle ICE.
   - **Decided 2026-11-12:** `Media:OpusComplexity` default 5; new `Media:OpusFrameMs` (10/20/40/60, checked at
     start-up) default **40 ms** for what the server sends (clients keep their own, typically 20 ms); and a new
     engine **`matrix-shared`**:
     - Only current talkers are mixed. A decoded buffer louder than `Media:VadThresholdDb` (−50 dBFS) is voice; a
       talker stays active for `Media:VadHangoverMs` (800 ms); at most `Media:ActiveSpeakers` (4), current talkers
       keep their place. Muted sources never talk.
     - Everyone not talking hears one shared mix (all talkers) from one shared encoder; each talker gets a personal
       mix-minus with its own encoder. Encoders = talkers + 1.
     - Each listener has `input-selector` (shared Opus | personal Opus) → `rtpopuspay`, so switching happens on Opus
       frames and the RTP stream stays continuous. Mix-minus is kept across switches: a new talker is switched to their
       personal branch on its first frame and only then added to the mixes; a talker who stops is removed from every
       mix and switched back 300 ms later.
     - **Spatial**: the shared mix is one rendering for everyone, so it is centred; personal (talker) mixes can be
       spatial. Options for later: cluster listeners whose talker directions quantise the same (one encoder per
       cluster). (Forwarding talkers unmixed for the client to spatialise was considered and rejected: SFU, not MCU;
       SL/OpenSim viewers expect one mixed stream. See Phase 6.)
     - The interleave and safe matrix updates moved to `RoomInterleave`/`MatrixMixer`, shared by both matrix engines.
     - Load tool `--talkers K`: only K clients per room send their tone; the rest send silence. With matrix-shared use
       K ≤ `Media:ActiveSpeakers` (everyone talking at once means only the top talkers are mixed, by design).
   - **32-person benchmark (2026-11-12), 3 talkers, complexity 5 / 40 ms:** matrix-shared **49% of 4 cores**
     (meets the Phase 2 gate), matrix 64% ([docs/benchmarks.md](docs/benchmarks.md)).
   **Decided 2026-11-12:** default engine `matrix-shared`. Phase 2 done.
3. **Signalling + web client.** Test web client, STUN/TURN config,
   reconnect / ICE-failure handling (drop participant after timeout, event logged).
   **Started 2026-11-12:**
   - **Test web client** at `/client/` (`wwwroot/client/`: `index.html`, `client.js`, `client.css`; no build step;
     `/` redirects there). Two ways in: a **join link** `?participant=…&token=…[&room=…]` (what an integration hands a
     user; a click is needed for mic permission/autoplay), or **test mode** with a control-API key kept in
     localStorage: list/create rooms (engine selectable), join, and "Invite link" (creates another participant and
     copies its one-time link, valid 5 min). Mic with echo cancellation / noise suppression / auto gain toggles;
     offer munged to `stereo=1` (Chrome); trickle ICE both ways; mic and mix level meters; mute, volume, leave;
     live stats (bitrate in/out, codec + channels, loss, jitter, RTT, candidate path); automatic reconnect on
     failed / prolonged disconnected (new RTCPeerConnection, re-offer with the session token, backoff 1–15 s, 6
     tries); leaves on page hide. Needs HTTPS off-localhost: `https://<server>:5001/client/`.
   - Verified in headless Chrome 157 (fake mic, two tabs via join links): connected in ~250 ms, Opus 48 kHz **2 ch**
     received at 64 kbit/s, no loss, host path; both participants `connected` on the server.
   - **ICE servers**: `Media:IceServers` (RTCIceServer shape: `urls`, `username`, `credential`). webrtcbin uses the
     first `stun:` URL and every `turn:`/`turns:` URL; clients get the list from `GET /signal/{id}/config`, which
     accepts the join token without consuming it (or the session token).
   - **Listening-test bots**: the load tool's room mode (`--room ID`) adds fake participants to an existing room
     until Ctrl+C, then removes only them. `--audio` loops decoded files (appsrc → clocksync, 20 ms buffers, each
     client at its own offset). `--audio flite` / `flite-together` synthesises a phrase per bot with libflite
     (P/Invoke to the library behind GStreamer's flite plugin; 4 rotating voices): its number, and with `--spatial`
     (bots in a circle round the browser client at the centre) where it stands, e.g. "Bot 1. North, on your left";
     taking turns or all at once. The browser client shows the room ID once joined. Covered by `AudioClipTests` (a sine clip through a real
     room checks decode, resample, loop and pacing).
   - **Dead connections**: a participant whose connection has been failed/disconnected for
     `Media:DeadConnectionTimeout` (default 10 s, 0 = off) is removed by `DeadConnectionSweeper` (reason
     `connection-lost` in the event log; the web client's next reconnect gets 404 and ends the call). Measured: when a
     client vanishes, the server's webrtcbin reports `failed` (no `disconnected` first) after ~27 s, when ICE
     consent freshness lapses (RFC 7675; libnice's 30 s, not configurable). So removal comes ~27 s + timeout after
     the client goes. A re-offer resets the clock (state goes back to connecting); a replaced endpoint's late state
     reports are ignored. GStreamer 1.24 has no consent freshness (its connection stays "connected" for ever), so
     there is also an inactivity rule on every version (2026-11-16): a connected participant that has delivered no
     audio for `Media:InactiveConnectionTimeout` (30 s) is reported failed, and connected again if audio returns.
     Clients send RTP all the time, also when muted (silence or DTX frames); to confirm with Firestorm (mic off
     for over 30 s must not drop the viewer).
   - Still to do: TURN tested for real; Firefox + Chrome manual tests on the LAN.
   - **Parked (pairchains only):** the user expects pairchains won't be efficient enough, so its issues are parked;
     it stays in the engine suites for comparison. A second intermittent case (2026-11-13, 1 in ~5 full runs):
     `PairChainsSpatial.Sources_are_panned_…`, a source moved into range is never heard (4/4 reruns pass). Intermittent issue: 1 failure in ~6 full runs: in `Deleting_a_room_with_connected_participants` (pairchains), in
     the fresh second room one direction (fake2 → fake3) never came up within 20 s while the other worked; 12/12
     passes when re-run alone. Not yet diagnosed (candidates: a rare ingress/answer race, or interference from the
     deleted room's teardown running concurrently).
   - **Parked: one test-host crash (2026-11-15).** One full run died with "Fatal error. Internal CLR error.
     (0x80131506)" and no other output, so the test it was in is unknown; the next three full runs passed (148/148).
     It came with the data channel work (handlers on viewers' SLData channels, server and fake clients), so that is
     the first suspect, though the binding's handler release looked safe on reading. If it recurs: `dotnet test src
     --blame-crash` names the test and keeps a dump.
4. **Admin UI.** Rooms/participants views, levels/stats, kick/mute, event log, pipeline graph.
   **First version done 2026-11-13** (`/admin/`). Decided: password via CLI with the hash in SQLite; an admin session
   may use the whole control API, admin-only endpoints need the session.
   - **Password:** `SpatialAudioServer admin set-password` (no echo, twice; or piped on stdin) stores a PBKDF2-SHA256
     hash (210k iterations) in the new `settings` table (migration 5); `Admin:PasswordHash` (from `admin
     hash-password`) wins when set; `admin clear-password`. Start-up warns when none is set.
   - **Session:** `POST /api/admin/login` → cookie `sas_admin` (HttpOnly, SameSite=Strict, sliding
     `Admin:SessionLifetime` 8 h); `/logout`, `GET /api/admin/session`. 5 failures per address per minute → 429.
     Events `admin.login`, `admin.login-failed`. Requests with an API key use the key scheme, others the cookie;
     `/api/*` accepts either, 401 (no redirect) without.
   - **Admin-only endpoints** (`/api/admin/…`): `events` (?room, ?before paging, ?limit), `rooms/{id}/sessions`,
     `rooms/{id}/live` (engine counters + per participant: input level dBFS, peak-held RMS from `RoomInterleave` for
     both matrix engines, none for pairchains; webrtcbin get-stats: packets/bytes in and out, loss, jitter, RTT,
     client-reported loss, ICE candidate types), `rooms/{id}/graph` and `rooms/{id}/graph/element` (pipeline view,
     below), `rooms/{id}/graph.dot` (GStreamer's dump). get-stats is emitted on the room worker and the replies awaited off it, so a participant leaving
     meanwhile cannot free the element under the call.
   - **UI** (`wwwroot/admin`, reuses the client's CSS): sign-in, status line (version, rooms, memory, CPU), rooms
     table + create (engine, spatial, max) / delete, room view (participants: state, level meter, kbit/s in/out
     from byte deltas, loss, jitter, RTT, path, position, gain; mute/unmute, kick; invite link; pipeline view and
     .dot; session history), event log for all rooms or the selected one, with paging. Polls: room 1 s, rooms 3 s,
     status and events 5 s. Checked in headless Chrome with speaking bots (screenshots).
   - Tests: `AdminAuthTests` (hashing, login, throttle, logout, cookie flags, config precedence, admin ⊇ API, API
     key ≠ admin), `AdminMediaTests` (levels of talking vs silent clients, connection stats, graph endpoints).
   - **Pipeline view (2026-11-14)**, `/admin/graph.html?room=…` (the room view's "Pipeline" button):
     - `GET /api/admin/rooms/{id}/graph[?expand=bin path…]`: top-level elements with pads (negotiated caps), links,
       state and a few live properties (queue fill, selector input, connection state, encoder bitrate); a bin's
       children only when its path is in `expand`. Taken on the room worker (`PipelineGraphReader`).
       `…/graph/element?path=` returns every readable property of one element, read with raw GObject calls
       (`NativeGObject.ReadProperties`), so webrtcbin's libnice objects are never wrapped by the binding.
     - Each top-level element is put in a **summary block** from the names the engines give their elements: a
       participant's receive, decode, own mix (talker) and listener's mix, the room input, each shared mix
       (cluster). `GraphSuite` (per engine) fails if an element lands in no block.
     - The page lays the graph out with **ELK** (elkjs 0.12.1, EPL-2.0, vendored unmodified in
       `wwwroot/admin/vendor/elkjs`, runs in a worker) and draws SVG blocks with pads as ports and orthogonal wires.
       Summary first; a block or bin opens in place (double-click). A participant's webrtcbin is drawn twice
       (receive on the left, send on the right) so audio reads left to right. Hover: caps of a wire or pad, live
       values; click: side panel with what the block does, pads and links, all properties. Highlight one
       participant's blocks and their neighbours; pan/zoom/fit; refreshes every 2 s and lays out again only when
       the structure changes.
5. **Hardening.** Limits, input validation, token expiry, rate limiting, graceful shutdown, Dockerfile,
   Windows/Linux run docs, load test using the GStreamer fake-client tool.
   **Started 2026-11-14.** Decided: no Dockerfile for now;
   participants who never connect are removed when their join token expires; an empty room's media stops after 30 s.
   - **Media on demand:** a room's pipeline starts with its first participant and stops once the room has been empty
     for `Media:IdleRoomTimeout` (30 s; 0 = at once), so stored rooms cost nothing. `RoomMediaStats.running`; the
     admin UI shows "Media idle". (`GstRoomMedia` tracks each room's participants; `RoomManager` is unchanged.)
   - **Stale participants:** `ParticipantSweeper` (was DeadConnectionSweeper) also removes participants whose join
     token expired unused (reason `join-expired`) and purges used/expired tokens from SQLite.
   - **Graceful shutdown:** on stop every session is closed as `server-shutdown`, new joins get 503, ICE long-polls end
     at once (503), and all room pipelines stop in parallel (≈1 s; `ShutdownTests`). SIGTERM checked on a published build.
   - **Rate limiting** (ASP.NET rate limiter, token buckets): `/signal/…` per participant
     (`Limits:SignalRequestsPerSecond` 30) and per client address (`…PerAddress` 300), 429 + Retry-After. The control
     API is not limited (key holders; the region module may send many position updates); admin login keeps its own throttle.
   - **Input limits:** Kestrel body ≤ `Limits:MaxRequestBodyBytes` (64 KB); SDP ≤ 32 K chars; ≤ 50 candidates per
     request, ≤ 1024 chars each, ≤ 200 per connection. Malformed offers (no Opus, broken SDP, NUL characters) are 400
     and leave the room working (test).
   - **Headers:** CSP (self only), `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer` (join links
     carry the token in the query), `frame-ancestors 'none'`.
   - **Token expiry, as built:** join tokens 5 min, one use; session tokens live as long as the participant; admin
     sessions sliding 8 h. API keys do not expire unless given an expiry (see below).
   - **Run docs:** `docs/running.md` (Linux + Windows: prerequisites, first-time setup, configuration table,
     firewall, systemd unit, Windows service, load tool). A published build uses its own folder as content root when
     started elsewhere (Windows services start in System32). `dotnet publish -r win-x64` checked from Linux (SQLite
     native DLL included); **the Windows run itself is for the user** (GStreamer 1.28 installers are now Inno Setup
     `.exe`, not MSI: choose the full install).
   - **Internet-facing hosts (2026-11-15):** settings, all off by default (LAN unchanged): `Media:Network:MinPort/
     MaxPort` (the ICE agent's `min/max-rtp-port`, set as raw GObject properties like the UPnP switch; covers UDP and
     ICE-TCP passive; tested on a real connection), `Media:Network:PublicAddress` (1:1 NAT, Janus-style: every local
     host candidate is also given with the public address, same port, its own foundation, priority − 1),
     `Server:ListenAddress` (e.g. 127.0.0.1 behind a proxy), `Server:CertificateKeyPath` (PEM, e.g. Let's Encrypt,
     re-loaded as PKCS#12 for Windows), `Server:TrustedProxies` (forwarded headers only from those), and network
     allow-lists `Admin:AllowedNetworks` (admin UI + `/api/admin`) and `Auth:AllowedNetworks` (control API), others
     404, signalling and the web client unaffected. Guide: `docs/internet.md` (TLS directly or behind a proxy,
     firewall, NAT, coturn, hardening, checklist). Still to do: a run on a real public host; TURN tested for real.
   - **Before public deployment (2026-11-15):** API keys may expire (`apikey create <label> --expires 90d|date`,
     migration 9; `apikey list` shows expiry and flags keys unused for 90 days; last-use tracking already existed).
     TURN credentials per client from a shared `Secret` (coturn `use-auth-secret`: `{expiry}:{participant id}`,
     base64 HMAC-SHA1; `CredentialLifetime` 12 h; the server's own TURN use gets its own), instead of one fixed pair
     handed to everyone. Admin review: the session carries a fingerprint of the password hash, so changing the
     password ends every session; the login throttle prunes itself and also caps failures overall (30/min from
     anywhere, against guessers spread over many addresses); cookie-authenticated changes under `/api` with a foreign
     `Origin` are refused (SameSite=Strict does not cover sibling subdomains); `Admin:RequireHttps` (login only over
     HTTPS, Secure cookie; the one exception is a request on the machine itself, and a loopback request still
     carrying `X-Forwarded-For`/`Forwarded`, i.e. through an untrusted proxy on the same host, does not count, so
     behind a proxy it needs `Server:TrustedProxies`); the cookie protection keys live in `data/keys`, so sessions survive
     restarts and a hardened unit (no home) does not fall back to in-memory keys.
6. **Spatial (brought forward 2026-11-13, first version done).** Decided: a per-room `spatial` flag (create/ensure,
   default off, stored: migration 4, shown in room details); `matrix-shared` clusters listeners by rendering; an
   inverse, SL-like distance model.
   - `ISpatialPolicy` → `DistancePolicy` (`Media/Spatial/`): full volume within `Media:Spatial:ReferenceDistance`,
     then `DistanceModel` (`Inverse`, `Linear`, `Exponential`; OpenAL formulas) with `Rolloff`, silent beyond
     `MaxRange` (60 m). Default since 2026-11-15: exponential from 2 m, rolloff 1 (physical, −6 dB per doubling);
     rooms may name a profile instead (`spatialProfile`, below). Pan = sine of the horizontal angle from the
     listener's facing (SL axes: x east, y north, z up; identity faces +x), so front and back are both centred.
     Elevation only counts towards distance. No position on either side = centred, full volume. The participant's
     gain/mute multiplies in (`MixEngineContext.Render`).
   - Stereo law (`Rendering.Stereo`): constant power with the centre at unity on both sides, capped at 1 (centre
     1/1, hard left 1/0), so a centred source is exactly as loud as in a non-spatial room.
   - Engines: `matrix` puts L/R rows in each listener's audiomixmatrix. `pairchains` sets audioamplify + audiopanorama
     (switched to `method=simple`: the default psychoacoustic law had every centred source at half level).
   - `matrix-shared` now has **clusters** instead of one shared branch: listeners hearing the talkers the same way
     (quantised to `ClusterGainStepDb` 3 dB and `ClusterPanSteps` 8 per side) share a mix + encoder; non-spatial
     rooms always have exactly one. Clusters are re-keyed in place when all their listeners agree (no switch),
     otherwise listeners move (link new branch, switch the selector on its first frame, drop the old). Empty
     clusters go after 1 s. Positions are applied on the 100 ms tick. A bug found by the load tool (a listener moved
     away and straight back could leave its cluster looking empty and removed under it) is fixed by deriving each
     cluster's listeners from the members' links.
   - `GET /api/rooms/{id}/media`: engine, spatial, mixed pairs and counters (`clusters`, `encoders`, `talkers`…).
     Load tool `--spatial`: everyone at random in a 30 m square facing a random way; prints the room's counters.
   - Tests: policy/pan law/quantiser units, the flag through the API and a restart, `SpatialSuite` per engine
     (hard left/right by facing, distance ratio, out of range, turning round, moving in range, unplaced = centred),
     and `SpatialClusterTests` (shared and split clusters, regrouping when a listener turns).
   - **Finding: clustering saves little in a scattered crowd** (offline simulation of 32 people: ~29 clusters for
     29 listeners at the default grid, 21 even at a coarse one). So a spatial room costs about one encoder per
     listener (the `matrix` engine's cost) however it is clustered ([docs/benchmarks.md](docs/benchmarks.md)).
   - **Spatial benchmark (2026-11-13):** matrix-shared costs ~10% more than matrix in a spatial room (each
     listener's link queue thread; removing it would let one stalled listener freeze its cluster), so
     **`Media:SpatialEngine` (default `matrix`)** is the engine for spatial rooms created without one (status:
     `defaultSpatialEngine`; the client switches the engine menu when Spatial is ticked). The load tool now warms
     up (`--warmup`, 10 s) before measuring.
   - **Encoder experiments:** complexity 2 saves a little and stays clean; 60 ms frames save more but add
     latency and an audible intermodulation product. Keep 40 ms; complexity 2 for spatial rooms stays a
     candidate (`Media:SpatialOpusComplexity`) ([docs/benchmarks.md](docs/benchmarks.md)).
   - **Decided 2026-11-13: engine tuning is parked.** Complexity 2 with the speaking bots sounds worse but stays
     intelligible; not adopted. The test VM (4 vCPUs on shared low-spec hardware) limits what benchmarks can say, so
     defaults stay (complexity 5, 40 ms; spatial rooms on `matrix`) while the other phases are developed. Revisit on
     production-like hardware: `Media:SpatialOpusComplexity`, grouping listeners by position. Engines stay as they
     are: `matrix-shared` for non-spatial rooms, `matrix` for spatial ones.
   - **Windows run (2026-11-14):** the server runs on Windows (Xeon W-10855M, 6 cores). matrix-shared costs 56% of a
     core per full 32-person room, linear up to 6 rooms; 8 × 32 passes. Working figure for that machine: ~8
     non-spatial or ~4 spatial full rooms; 16 × 32 needs a bigger machine ([docs/benchmarks.md](docs/benchmarks.md)).
   - **Decided: no client-side spatialisation.** The server stays an MCU (one mixed stream per listener); an SFU-style
     design (forwarding talkers for the browser to spatialise) would break compatibility with SL/OpenSim viewers.
   - Start-up switch `--delete-existing-rooms` (testing): deletes every stored room before any pipeline starts
     (logged as room.deleted). Each room's pipeline otherwise runs from creation even when empty (~1–1.5% of a
     core per room); starting media only when someone joins is noted for Phase 5.
   **Future direction (recorded 2026-11-14, not scheduled):**
   - **Distance profiles per room.** SL's voice attenuation is very non-linear (aimed at people within ~50 m talking
     as if nearby); for other rooms a model based on real sound propagation is preferred. Idea: named profiles in
     server config, chosen per room like `engine`, using the standard distance models (OpenAL / Web Audio):
     `inverse`, `exponential` (rolloff 1 = physical 1/r, about −6 dB per doubling from a reference of ~1.5–2 m, with
     a near-field clamp ~0.5 m), `linear`, plus `table` (interpolated gain at given distances) to match SL by ear.
     Each with a maximum range for culling. To decide: which profiles ship, the default, the physical reference.
   - **Richer per-listener acoustics: a custom GStreamer element** (`acousticspace`, see
     `docs/gstreamer_acousticspace_design.md`): per pair distance, ILD/ITD, early-reflection taps and a shared late
     reverb, with environment presets and later geometry; expensive work only when geometry changes. Today each
     listener's mix only has a left/right gain per participant, so it would come in as a new mix engine (engines
     are pluggable per room and the engine suites test them). The spatial policy's per-pair result (today gain + pan)
     would grow into per-path coefficients (L/R gain, ITD, reflection taps). The control API will need more
     parameters from the region module (environment preset per room, geometry, per-participant voice level, …)
     carried on the same JSON update path to the engines.
7. **(Later) SL/OpenSim integration.** Viewer protocol compatibility + region module.
   **Viewer WebRTC protocol, from Firestorm's source (2026-11-12; `docs/reference/phoenix-firestorm`, git-ignored;
   `indra/newview/llvoicewebrtc.cpp`, `indra/llwebrtc/llwebrtc.cpp`):**
   - **Signalling goes through the simulator's capabilities**, which the region module will have to serve/proxy:
     `ProvisionVoiceAccountRequest` POST (LLSD) `{jsep:{type:"offer", sdp}, channel_type:"local",
     parcel_local_id?, voice_server_type:"webrtc"}` → `{jsep:{type:"answer", sdp}, viewer_session}`; logout with
     `{logout:true, viewer_session}`. `VoiceSignalingRequest` POST carries the viewer's ICE candidates in batches
     (`candidates:[{sdpMid, sdpMLineIndex, candidate}]`, then `candidate:{completed:true}`). There is **no path
     for server candidates to the viewer**: they must be in the answer. → Our offer endpoint now takes
     `trickle:false`, which waits (≤ 5 s) for ICE gathering and embeds all candidates + `a=end-of-candidates`.
   - **The viewer is the offerer** (we answer: fits). It creates an ordered data channel `"SLData"` *before* its
     audio track, so its offer has `m=application` (possibly first) plus `m=audio`; webrtcbin answers both (needs
     `sctpenc`/`sctpdec`, now in the start-up check). Our m-line lookup handles any order.
   - **SDP munging**: the viewer rewrites the Opus rtpmap to `opus/48000/2` (LF only) and appends a second
     `a=fmtp:<pt> minptime=10;useinbandfec=1;stereo=1;sprop-stereo=1;maxplaybackrate=48000;…` *after the original
     fmtp line's CR*. GStreamer's SDP parser treats CR as a line end, so it parses as two fmtp lines; our helpers now
     split on CR and LF too. The viewer accepts stereo from us; it may send stereo (we downmix to mono).
   - Viewer ICE uses UDP ports 60000–60100 and SL's STUN servers (`stun{1..3}.<grid>.secondlife.io:3478`).
   - **Data channel (JSON) viewer → server**: positions every ≥ 0.1 s when changed:
     `{"sp":{x,y,z}, "sh":{x,y,z,w}, "lp":{x,y,z}, "lh":{x,y,z,w}}` = speaker (avatar) position/rotation and
     listener (camera, tethered to within 50 m of the avatar) position/rotation, **global (grid) coordinates** in
     **cm** and quaternions ×100, as integers, the same values on every connection (own and neighbouring regions).
     Also `{"j":{"p":true}}` join (p = primary region), `{"ug":{"<agent>":gain}}` per-peer gain (volume × 220 when
     changed, but × 200 for the initial values sent on a join), `{"m":{"<agent>":bool}}` per-peer mute. Server positions are meant to be "tethered" to the
     simulator's authoritative avatar positions (anti-eavesdropping).
   - **Data channel server → viewer**: object keyed by agent UUID: `j` {p} joined, `l` true left, `p` level 0–128,
     `v` speaking, `m` moderator-muted, `V` server version string.
   - Covered by `ViewerCompatibilityTests` (viewer-style fake clients: SLData channel, the munging byte for byte,
     `trickle:false`, never fetching server candidates) and `SdpTests`. The server reads SLData (2026-11-14): poses in
     tethered rooms (below); `m` and `ug` (2026-11-15) as the viewer's own per-person mute and volume: kept on
     the listener as `peers` (by viewer key: avatar UUID, or participant id for browsers and bots), gain = `ug` / 100
     (the viewer's 0–1 slider × 220, or × 200 for values it restores on a join; capped at 4), and applied in
     `MixEngineContext.Render`, so every engine has it (matrix-shared puts that listener in a mix of its own).
     It writes to viewers (`Media/ViewerData.cs`,
     2026-11-15): on a viewer's `j`, everyone in the room (`j {p:true}`, itself included) and `V` under its own
     UUID; `j`/`l` to listening viewers as others join and leave; every 200 ms the `p` (level × 128, −60…0 dBFS)
     and `v` (above `Media:VadThresholdDb`, with `VadHangoverMs`) that changed. Keys are avatar UUIDs; browsers and
     bots (no UUID external id) are keyed by participant id and show as unknown avatars. Engines without input
     levels (pairchains) send no `p`/`v`. **Neighbouring regions:** a viewer near a border connects to each nearby
     region's room; its `j` has `p: true` only in the room of the region it stands in (sent again when that changes;
     the mic is live only there), and a viewer lists people only from `j {p:true}`. So an avatar is announced only
     in its primary room and withdrawn (`l`) when it stops being primary there; browsers and bots are always primary.
     Positions in the neighbouring room need nothing extra: the module's `TryGetScenePresence` also finds child agents,
     whose positions OpenSim keeps current (seen within a few cm of the viewer's own report).
     The viewer keeps one list for all connections, each entry remembering the region it came from; it ignores `j`
     for someone listed and takes `l` only from that region. On a crossing the new room's `j` can beat the old
     room's `l` (different connections), which dropped the avatar from neighbours' lists (seen in Firestorm: dots
     flipped between the two avatars as they crossed), so every announcement is repeated 2 s later.
     The viewer looks every UUID up with its simulator to show a name (OpenSim leaves unknown ids out; the viewer
     asks again after 60 s), so the OpenSim module polls each room once a second and registers non-avatar
     participants' names with OpenSim's user management (`AddSystemUser`: "Simon (web)", `GuestLastName`; `RemoveUser`
     once they have gone), and the
     server announces those participants `GuestAnnounceDelay` (2 s) after they connect so the name is there first.
   - **Client-set poses (decided in part 2026-11-13; FOR DISCUSSION AND DECISION when testing with SL viewers).**
     The question: may participants set their own position/orientation, and how does that fit security? Positions
     decide what you hear in a spatial room, so a client that can move anywhere can walk up to any conversation
     (eavesdropping); SL guards against this (the viewer tethers its camera to within 50 m of the avatar, and the
     voice server enforces it against the simulator's authoritative avatar position).
     - **Done now:** (1) speaker and listener poses are separate: `position`/`orientation` = where others hear you
       from (an avatar), `listenerPosition`/`listenerOrientation` = where you hear from (SL's camera; falls back to
       the speaker pose); the control API may set all four, and mixing uses the listener pose for the listener.
       (2) a per-room `clientPose` (stored, migration 6): `none` (default for non-spatial rooms) or `orientation`
       (default for spatial: a participant may turn, not move). (3) `PATCH /signal/{pid}/pose`
       `{listenerOrientation}` with the session token (403 when the room forbids it or for any position, 429 above
       20 updates/s); `/signal/{pid}/config` reports `spatial` and `clientPose`. The test client's Facing slider uses
       it, so invited guests can turn; invites into spatial rooms place the guest at the centre facing east. The admin
       UI shows each participant's facing.
     - **Decided and done (2026-11-14): `clientPose: tethered`**, which the OpenSim module asks for when it creates
       region and parcel rooms. The viewer's SLData `sp/sh/lp/lh` (cm, quaternion ×100) are kept as its report
       (`clientReport` on the participant); the control API's `position` (the simulator's avatar position) stays
       the authority. Mixing hears the avatar from the viewer's `sp` while that is within `Media:Spatial:SpeakerTether`
       (10 m) of the simulator's position, else from the simulator's; the listener is the viewer's camera (`lp/lh`)
       pulled back to within `Media:Spatial:ListenerTether` (50 m); no simulator position → the report is ignored.
       Messages closer than 20 ms are dropped (viewers send ≤ 10/s). A room made before this keeps its old
       `clientPose` (the module warns: delete the room to have it made again). `free` was not needed.
     - **Distance model, to check against a real SL viewer (2026-11-14):** the defaults (full volume within 10 m,
       then 10/d, silent beyond 60 m) were chosen as "SL-like" but have not been compared with SL's own voice service.
       In listening tests the 15 m ring is only 3.5 dB below the 5 m ring, so distance is subtle;
       `Media__Spatial__ReferenceDistance=2` makes it obvious (5 m −8 dB … 35 m −25 dB) and sounded better, but the
       defaults stay until they can be matched with SL by ear and by measurement (a rolloff exponent may be needed).
       **2026-11-15:** `DistanceModel` and `Rolloff` added. OpenSim's Vivox module carries SL's settings (README: leave
       them alone): linear, clamping 10 m, rolloff 2, max range 60 m, i.e. 20 m −4.4 dB, 30 m −14 dB, silent from 35 m.
       To compare by ear: `Media__Spatial__DistanceModel=Linear`, `Media__Spatial__Rolloff=2`.
       **Decided by ear (2026-11-15, one Firestorm viewer, one NPC bot):** the Vivox curve suits a room or meeting;
       the physical one (exponential, 2 m, rolloff 1: 5 m −8 dB, 20 m −20 dB, 50 m −28 dB) sounds natural, like
       outside, and is the **default**. Rooms may choose a named **spatial profile** at creation (`spatialProfile`,
       migration 8, fixed for the room's life): built in `outdoor` (= the default) and `meeting` (Vivox: linear, 10 m,
       rolloff 2, 60 m); `Media:Spatial:Profiles:{name}` changes them or adds more. The OpenSim module's
       `SpatialProfile` picks one for the rooms it creates. Media tests pin the old inverse 10 m curve, which their
       positions were laid out for; the curves are tested in `SpatialPolicyTests`.
   - **Guest names (2026-11-13):** a participant created with `clientNamed: true` (both invite buttons) may choose its
     display name, sent as `displayName` with its first offer; `/signal/{pid}/config` reports `displayName` and
     `clientNamed`. Checked before the one-time join token is used, so a refused name (403) can be retried. Others
     keep the name the integration gave (an SL avatar's name from the region module). Logged as
     `participant.renamed` ("Guest → Alice"); the session history takes the new name; the admin UI tags such
     participants "guest". Names are trimmed, control characters dropped, ≤ 200 characters.
   - **Speaking bots, natural voices (2026-11-14):** the load tool's `--audio voices` uses pre-rendered clips committed
     in `scripts/loadtest-linux-x64/voices` (Ogg Opus, 2.3 MB: 8 voices × 32 names, 8 directions, 12 distances),
     made once with Piper by `scripts/render-voices.py`, so nothing is needed where the load tool runs (Linux and
     Windows; flite is Linux-only). Voices chosen by ear and licence: Jenny (Dioco) and Cori (public domain), plus 6
     LibriTTS-R speakers (CC BY 4.0); credits in `voices/CREDITS.md`. Ruled out on licence: alan, lessac, ryan,
     hfc_male/female. Spatial room mode places bots on rings of the 8 compass points (5, 15, 25, 35 m; decided over
     16-point compass, bearings or clock positions): every bot's "North-west, behind, to your left. 15 metres." is
     exact, and the outer rings test distance.
   - **Plan for the OpenSim side (decided 2026-11-14).** Studied `OpenSim/Addons/os-webrtc-janus` (part of OpenSim
     master; `docs/reference/opensim`, git-ignored) and the Janus back end it ships.
     - **What exists:** `WebRtcVoiceRegionModule` serves the viewer's `ProvisionVoiceAccountRequest`,
       `VoiceSignalingRequest` and `ChatSessionRequest` capabilities, checks estate/parcel voice permissions, bans,
       child agents and estate managers, cleans up duplicate sessions, logouts and leaving avatars, and sets the
       `VoiceServerType=webrtc` simulator feature. It loads its back end from configuration
       (`[WebRtcVoice] SpatialVoiceService = Dll:Class`, an `IWebRtcVoiceService` with `IVoiceViewerSession`s in a
       static registry). The Janus back end (sessions, AudioBridge rooms) is what we replace. The Janus version has no
       spatial audio, white dots, mute or per-peer volume because Janus ignores the SLData channel; for us that
       channel ends at our webrtcbin, so those are server work.
     - **Decided:** (1) **plug in under OpenSim's region module** (no fork): a new back end plus a small companion
       region module, so upstream keeps the capability/permission handling. (2) The module is its own repository,
       **`os-webrtc-sas`** (MPL-2.0, github JakDaniels/os-webrtc-sas), cloned into an OpenSim tree's
       `addon-modules/os-webrtc-sas` (here: `docs/reference/opensim/addon-modules/`) and built by OpenSim's prebuild
       (`addon-modules/*/prebuild*.xml`); net8.0 like OpenSim, so it shares no code with the server and talks HTTP
       only. (3) **First milestone: region and parcel voice** (`channel_type: local`); P2P, group and ad-hoc
       (`multiagent`) later. (4) Against **OpenSim master**, LAN only, with the **.NET 8 runtime installed** next to
       .NET 10. (5) A **speaker** is heard from the viewer's own (low-latency) position while it stays within a few
       metres of the simulator's position for the avatar, otherwise from the simulator's; the **listener** may be
       up to 50 m from it (SL's camera tether). (6) Test setup spread across LAN machines: OpenSim on the dev machine
       (Ubuntu 26.04), Firestorm on the Ubuntu 22.04 machine, our server on the Windows machine; so addresses and
       firewalls matter from the start.
     - **Mapping:** provision with an offer → `PUT /api/rooms/by-external/{room}` (`spatial: true`) → `POST
       …/participants` (`externalId` = avatar UUID, display name, global position) → `POST /signal/{pid}/offer`
       (`trickle:false`, join token) → `{jsep: answer, viewer_session}`. Room external ids: the region's estate
       channel and parcels using it → `region:{regionId}`; a parcel with its own channel → `parcel:{regionId}:{parcel
       GlobalID}` (stable across subdivide/join, unlike the local id). `VoiceSignalingRequest` candidates/completed →
       `POST /signal/{pid}/ice` (session token); logout, leaving or a duplicate → `DELETE /signal/{pid}`. A re-provision
       by the same avatar replaces its participant (existing `externalId` rule). Join and session tokens stay in the
       simulator.
     - **Module work (`os-webrtc-sas`):** `prebuild.xml`; `SasVoiceService : IWebRtcVoiceService` and its viewer
       session (config `[SasWebRtcVoice]`: server URL, API key, timeouts, accept a self-signed certificate); region
       name and global origin, avatar names, parcels and positions come from OpenSim's `SceneManager`, so no
       companion region module was needed: a timer in the back end sends the simulator's avatar positions every
       `PositionUpdateMilliseconds` (250) for avatars with voice sessions that moved (5 cm or 2°; every 5 s anyway),
       one `POST /api/rooms/by-external/{room}/poses` per room (global = region origin + local); console commands (`sas status`, `sas rooms`); an
       example `.ini`, README and build/install notes. Blocking HTTP calls (the capability handlers are synchronous)
       done without the `.Result` deadlock pattern the Janus code uses. The Robust/grid route is not needed: every
       simulator calls the server directly.
     - **Server work:** (1) read the **SLData channel**: `sp/sh/lp/lh` through the pose path under a new
       `clientPose: tethered` (speaker near the simulator's position, listener within 50 m), `j` join, `m` per-peer
       mute, `ug` per-peer volume; (2) write to it, keyed by avatar UUID: `j`/`l` on join and leave, `p` level and `v`
       speaking at a modest rate, `m` when muted by us, `V` version to the joiner (white dots, speaker list,
       indicators); (3) **per-listener mute and volume of one source** on top of the spatial gain (matrix
       coefficients; pairchains' amplify); (4) SL rooms in **global coordinates**, so neighbouring regions' rooms
       agree (a viewer also connects to nearby regions as a non-primary listener); (5) match SL's distance model.
     - **Testing:** viewer-style fake clients extended to send and check SLData messages; then OpenSim standalone +
       Firestorm: one region with two avatars; parcel voice and voice-off parcels; crossing into a neighbouring
       region; logout, crash and reconnect.
     - **Progress (2026-11-14):** (a) and (b) done: Firestorm 7.2 (Windows) talks and hears bots in stereo. Then,
       by choice, reading SLData from (c) and the first part of (d): simulator position push, viewer `sp/lp` in
       tethered rooms. Then the rest of (c) (2026-11-15): writing `j/l/p/v/V` to viewers. Then (f), per-peer mute/volume.
       Second avatar and a neighbouring region (`SAS Test2` at 1001,1000) tested
       2026-11-15: an avatar in region 2 hears region 1's bots near the border; primary-only announcing added
       for the white dots; then avatar to avatar across the border (audio, dots, listed once on either side) works.
     - **Order:** (a) module skeleton that builds in the OpenSim tree and loads under the core module; (b) provision
       and signalling → audio between two Firestorm viewers in one region (non-spatial mix is fine at first);
       (c) server SLData: join/leave/levels/speaking (white dots); (d) positions: simulator push + viewer `sp/lp` with
       tethering, global coordinates, then neighbouring regions; (e) parcels; (f) per-peer mute/volume; (g) distance
       model by ear against SL. (g) is postponed (2026-11-15): two viewers at once are awkward to drive; NPC
       speakers (Phase 8) should make it a one-viewer test. Until then bots placed with `--around` can stand in.
       (g) done later that day with NPC bots (see Phase 6: the exponential default and the `meeting` profile).
     - **(h) Group and P2P calls (2026-11-15, v1 built).** Found: OpenSim's `WebRtcVoiceRegionModule` answers the
       viewer's `ChatSessionRequest` `call` with a bare OK, but the viewer needs `voice_credentials {channel_uri,
       channel_credentials}` (else "invalid credentials" and the call ends), and it never rings a P2P callee (an
       event-queue `ChatterBoxInvitation` with `voice {invitation_type: 2, channel_uri…}`); the viewer then provisions
       `channel_type: multiagent` with `channel` (Janus reads `channel_id`). Decided: a core patch written up for
       Mantis (os-webrtc-sas `core-patches/0001`, not applied to the tree here); meanwhile the add-on's own region module
       (`SasCallsModule`) re-registers `ChatSessionRequest` once each region is ready (after every module's caps, so
       it replaces the core handler), and steps aside if the core has `MultiAgentCallsVersion`. Access: a group call
       needs membership; a P2P channel XORed with the caller's id must be another avatar (so A⊕B cannot be used by a
       third person); checked on `call` and again on provisioning. Each call is a non-spatial room
       `call:{gridhash}:{session}`, deleted when its last viewer leaves. v1 scope: P2P callees rung only on the same
       simulator; group members are not rung (they use Join Call); ad-hoc conferences refused. **Live test (P2P, Firestorm 7.2, two PCs):**
       ringing works (fixed: a viewer whose IM window is already open sends only `call`, so ringing hangs off `call`,
       skipped when the other party is already in the call), answering, both in one non-spatial room, the room
       deleted at hang-up; bots joined to the call's room were heard clearly, with names. Test Two's sound arrived
       garbled at −46 dB: PulseAudio on that Linux PC used the wrong microphone input for the call's WebRTC stream
       (fixed there; not ours). Declines: the callee's `decline p2p voice` (also sent when its 30 s incoming-call
       dialog runs out) now reaches the caller as `ChatterBoxSessionAgentListUpdates` with the callee's
       `can_voice_chat: false`, which the viewer shows as declined and ends the call. Group calls tested too (2026-11-15): the membership check, both
       avatars in the group's call room, speech heard; the viewer joins group calls with the microphone off (unmute
       with Speak). Groups on this SQLite standalone: groups V2 alone on MariaDB (`opensim_groups`;
       `MessageOnlineUsersOnly = true`, or V2 messaging switches itself off; `[GridUserService] StorageProvider` named
       in `SQLiteStandalone.ini`, or the groups service pairs SQLite's connection string with the MySQL driver and
       stops the simulator), written up in the add-on README. **Parked (2026-11-15):** ringing
       callees (and telling callers of a decline) on another simulator: it needs a grid to test (a local Robust grid
       with two simulators is the plan when it comes up); meanwhile the other side can call back. Call rooms keep the
       non-spatial default `matrix-shared`: re-tested after the PulseAudio fix, speech both ways is clean. One
       Firestorm crash (Windows, Test One calling) came right after a server restart, between `call` and joining the
       call room; not repeatable since, put down to the viewer meeting a restarted voice server mid-reconnect.
8. **(Parked 2026-11-15 after (a); ideas otherwise; FOR DISCUSSION AND DECISION) In-world presence and voice agents (OpenSim NPCs).**
   Written up 2026-11-15 before any implementation. The principle: **OpenSim decides who exists in the region and
   what voice it has; the audio server only carries the sound.** The audio server never creates anything in a
   region. An NPC's voice is an ordinary room participant whose viewer key is the NPC's UUID: viewers then show a
   real avatar with its name and a white dot, and its position comes from the module's existing pose push (NPCs are
   scene presences). What differs is who is behind the participant.
   - **(a) Web users as NPCs.** When a browser participant joins a region room, the module (from the room poll it
     already runs for guest names) creates an NPC for them and ties the participant to the NPC's UUID (the one
     server change: the integration sets the viewer key; the "(web)" system-user names are then no longer needed
     for them). The NPC's position is the master: the web client's Stand picker and facing control become "move /
     turn my NPC", applied in OpenSim, and the server follows the simulator's pose as for any avatar. Removing the
     participant removes the NPC, and the other way round.
     **Optional, per region (2026-11-15):** a module ini option, e.g. `WebUserAvatars = off | optional | always`,
     which the module records on the region's room when it creates or updates it, as a named room field (e.g.
     `inWorldAvatars`; not free-form metadata, since the web client reads it). The server stores and reports it
     but never acts on it, and has no notion of NPCs. `/signal/{id}/config` reports it; the web client shows a
     "Create an NPC in-world?" tick box only for `optional`, and sends the answer with its first offer (like the
     chosen display name), stored on the participant (e.g. `inWorldAvatar: true`). On its room poll the module
     creates the NPC only if the region allows it (a client cannot force one into an `off` region), and removes it
     when the participant leaves. `always`: every web user gets an NPC and there is no tick box; this answers the
     eavesdropping concern (no invisible listeners in the region). The load tool gets the same request (e.g.
     `--npc`), under the same region setting, so bots get bodies, names, dots and real positions (Phase 7 (g)).
     Without a tick a web user behaves as today.
     **First version built (2026-11-15), brought forward for Phase 7 (g).** Decided for it: the **participant drives
     the NPC** (not the NPC as master, for now): the module mirrors the participant's position and facing
     (`listenerOrientation`, else `orientation`) onto the NPC, so the Stand picker, `--around` and the Facing control
     work unchanged; the NPC as master can come with script-driven NPCs. **Default appearance**; name "{display name}
     {GuestLastName}" ("Bot 1 (web)"), registered with user management. Server: room `inWorldAvatars`
     (`off`/`optional`/`always`, migration 7, `PATCH /api/rooms/{id}`, reported by `/signal/{id}/config`),
     participant `inWorldAvatar` (asked with the first offer) and `avatarId` (set by the module with
     `PATCH …/participants/{id}`; the viewer key becomes the NPC's UUID: `ViewerData` withdraws the old key and
     announces the new one). The server only stores and reports; the module decides (`WebUserAvatars`), polls each
     region's room once a second (also rooms no viewer here is in), creates the NPC only inside the room's region,
     and removes it when the participant leaves or the room is no longer polled.
     **Fixes after the first live test (2026-11-15):** NPCs looked like clouds (the default appearance has no baked
     textures), so they copy `NpcAppearancesFrom` (a list of avatar names or UUIDs, used in turn; the avatar's
     current look while it is in a region here, else its stored appearance), or appearance notecards by asset UUID
     (saved with `osAgentSaveAppearance`, which stores the baked textures as assets, so they keep working; a stored
     appearance's bakes may have expired, and a server cannot bake wearables itself, so stock items alone are not
     enough: a viewer must have worn and baked them).
     **Planned: an appearance pack (2026-11-15, not started).** A known set of looks for test bots (8, 16 or 32),
     shipped with the module and available in every region it runs in, with no inventory or Library items: an NPC's
     look needs only assets. A classic system avatar is its visual parameters (the shape sliders, numbers) plus about
     six baked textures (head, upper, lower, eyes, skirt, hair) a viewer rendered once. (1) **Export once:** a console
     command (e.g. `sas npc export-look <avatar|notecard> <name>`) writes the appearance and its baked textures into a
     folder in the module's repo. (2) **Seed at start-up:** the module stores any of those textures the asset service
     lacks, under their fixed ids (on a grid: the central asset store, shared by every simulator; permanent assets,
     so they never expire like a stored appearance's bakes). (3) **Fixed look per bot number:** "Bot N" always gets
     look N (wrapping round), so a bot is recognisable from test to test; other participants take looks in turn.
     Configured as `NpcAppearancesFrom = pack` (names and notecards still work). Variety without re-baking: the shape
     is only numbers, so e.g. 8 baked looks × varied height, build and head give 32 distinct bots. Size: about 6 × 100–300
     KB per look, a few MB for 8. Licensing: make the looks from OpenSim's own Library items (Ruth, stock clothes),
     which can ship under the module's MPL-2.0; other creators' items may carry their rights. Decided: classic
     avatars only, no mesh bodies or attachments (NPCs we create cannot wear those: they rez from inventory). A server restart left "Bot 1 (web)" in
     viewers' lists, and a rejoining bot added a second: the viewer only retries a lost connection and keeps its
     list, and the new server has no record of who was there. So NPC ids are **stable** (a name-based, version 3 UUID
     from a secret made at module start-up, the room, the name and a slot: stable while the simulator runs, not
     guessable, never equal to an account's random version 4 UUID), so a rejoining bot gets its old entry back, and the server keeps **recent
     departures** per room (`ViewerDepartures`, 10 min, outside the pipeline so it outlives media stops) and sends a
     joining or reconnecting viewer `l` for those not in the room now; the module reports NPCs and guests it removes
     (`POST /api/rooms/{id}/viewers-gone`), which covers leaves the restarted server never saw. The load tool's room
     mode notices a server restart (`/api/status` `startedAt` changes while its bots have lost their connection) and
     joins again with the same names, places and audio.
   - **(b) Voice agents.** An agent needs what any participant has: a listener mix rendered at the NPC's position
     (distance, direction, mix-minus) and an input placed there. So the agent is a **separate process, an "agent
     bridge"**, that connects over WebRTC like the load tool's bots (reusing `FakeClient`) and chains speech to
     text → an AI agent → text to speech. The server stays a plain MCU, with no AI inside it. Who said what: the
     bridge opens the SLData channel like a viewer and uses the `v` speaking flags (separate per-speaker streams for
     an agent could be a later server option). Expect about 1–3 s per spoken turn: fine for commands.
   - **(c) Set-up from OSSL scripts.** A region module can expose functions to scripts
     (`IScriptModuleComms.RegisterScriptInvocation`, as `modInvoke` uses), so a script can do
     `key npc = osNpcCreate(...); sasNpcVoice(npc, "assistant");`. `"assistant"` names an **audio profile in the
     module's ini** (which bridge, speech engines, AI endpoint, credentials), so scripts never hold keys and the
     region owner decides what exists. The module creates the participant and hands the join to the bridge (a
     running bridge service told about it, or polling for work). A module console command (e.g. `sas npc add NAME
     X,Y,Z PROFILE`) would cover tests without scripts.
   - **Simplest profiles first:** play a file or flite speech through a bridge; this also gives speakers with names,
     dots and real positions for Phase 7 (g) and for load tests.
   - **To decide:** privacy (an agent turns nearby speech into text: at least a visible notice, probably a parcel or
     estate opt-in); whether a web user's moves always go through OpenSim (the NPC as the single source of truth);
     NPC lifetime (NPCs vanish on a region restart unless a script recreates them); where the bridge lives (its own
     project, from the load tool's client code); whether agents also take local chat as text.
   - **Order:** (a) NPCs for web users (module work); (b) the script function and a simple file/flite profile;
     (c) the speech-to-text → AI → text-to-speech agent.

## Risks / things to verify

- **Binding maintenance:** GstSharp.Net has effectively one maintainer; all GStreamer use stays behind `Media/`, and
  GirCore is the fallback.
- **GStreamer version differences:** minimum 1.24, developed on 1.28.2; webrtcbin keeps getting fixes. ICE
  port-range / NAT 1:1 control may be needed for public deployment.
- **Windows:** needs the official MSVC GStreamer runtime (full install: gst-plugins-bad for webrtc, libnice, dtls,
  srtp). Verified 2026-11-14: the binding finds it and the server runs, load tests pass.
- **CPU at scale:** encoders per listener dominate; measured in [docs/benchmarks.md](docs/benchmarks.md). Distance
  culling and listener grouping are the levers.
- **Dynamic pipeline modification** is the usual crash/deadlock source; centralised in shared helpers + the per-room worker.
- **Jitter/latency:** `audiomixer` latency and webrtcbin jitterbuffer `latency` need tuning to avoid
  underruns vs. delay; make both configurable.
- **Browser autoplay / HTTPS** constraints for the test client.

## Resolved requirements

- **Scale target (absolute maximum):** 16 rooms x 32 participants. Expected real load is much lower. Per-room
  cap default 32; a server-wide cap on total participants, configurable. 32 participants means up to
  32 x 31 = 992 source→listener pairs in a room; Phase 2 benchmarks this worst case
  ([docs/benchmarks.md](docs/benchmarks.md)).
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

## Hardware and browser targets

- **Server:** 4 or 8 cores. Phase 2 benchmark gate: a full 32-participant room should run on 4 cores with headroom
  (target < ~60% total CPU, no audible underruns); if not, VAD/culling is pulled forward. Record results for 4 and 8 cores.
- **Browsers:** current evergreen browsers (Chrome/Edge, Firefox, Safari 2025+). No legacy/Plan-B SDP or polyfills needed.
- **Linux server:** Ubuntu 24.04 (GStreamer 1.24) or 26.04 (1.28) LTS, distro GStreamer packages.
- **Windows server:** Windows 10/11 x64 with the official GStreamer 1.28 MSVC runtime.

## Binding choice (Phase 0 decision)

**`GstSharp.Net` 1.28.17** (`GstSharp.Net`, `GstSharp.Net.Sdp`, `GstSharp.Net.WebRTC`; chosen 2026-11-11 after the
Phase 0 spike). It is generated for GStreamer 1.28 and targets net10.0. All GStreamer use stays behind the `Media/`
layer, so a swap is contained. **Fallback: GirCore** (`GirCore.Gst-1.0`, `GirCore.GstSdp`, `GirCore.GstWebRTC`,
generated from GObject Introspection; needs the matching typelibs).

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

Still open from Phase 0: a Chrome/Edge test of the spike page (Firefox works, and the test web client works in
headless Chrome). The Windows run was done on 2026-11-14.
