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

One `Pipeline` per room. Since 2026-11-12 the mix topology is pluggable: `RoomPipeline` owns the webrtcbin
endpoints and the room's *mix engine* (`IMixEngine`, chosen per room) owns RTP in → RTP out — see "Mix engines"
under Phase 2. The design below is the first engine, `pairchains`. Per participant:

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
  Media/                     GStreamer layer: GstRuntime, IRoomMedia/GstRoomMedia/NullRoomMedia, RoomPipeline
                             (webrtcbin endpoints per room), WebRtcBin, RoomWorker, BusWatcher, NativeGObject
  Media/Engines/             IMixEngine + MixEngines catalogue, MediaBlocks, one class per engine
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

   **Stage C (2026-11-12): load tool + first numbers.** `dotnet run --project src/SpatialAudioServer.LoadTest --
   --api-key KEY [--server URL] [--participants N] [--rooms R] [--duration S] [--insecure]`; tones 300 Hz + 50 Hz·i,
   volume scaled to avoid clipping; reports server CPU/memory/threads from `/api/status` (now has a `process`
   block). One room, server and load tool on the same N6005 (4 cores), so the tool's own CPU competes:

   | participants | chains | server CPU | server RSS | server threads | heard others (min / mean) | self |
   |---|---|---|---|---|---|---|
   | 4 | 12 | 25% of 1 core (6% of 4) | 143 MB | 85 | 100% / 100% | 0% |
   | 8 | 56 | 63% of 1 core (16% of 4) | 164 MB | 174 | 100% / 100% | 0% |
   | 16 | 240 | 214% of 1 core (53% of 4) | 207 MB | 444 | 100% / 90% | 1.2%* |

   \*16-person run: the tool used another 107% of a core, so the box was ~80% busy; the dropouts and the 1.2% "self"
   (tone detection at 50 Hz spacing, not a routing fault: no self chain exists) are expected to vanish with the
   tool on another machine — to confirm there.
   **Finding: the per-pair chain topology does not reach the 32-person target on this CPU.** Cost is ~0.9–1% of a
   core per chain and chains grow as N(N-1): 32 people = 992 chains ≈ 9 cores, against the gate of < 60% of 4
   cores. Threads also grow as N² (each chain's `queue` is a streaming thread: ~1,500 for one 32-person room).
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
   - Engines so far: `pairchains` (stage A–C design) and `pairchains-lite` (no per-chain `queue`/`audioamplify`;
     gain is the audiomixer pad's `volume`; threads ∝ N instead of N²).
   - First comparison (one room, server + tool on the N6005, `--engine pairchains,pairchains-lite`, 15 s):

     | participants | engine | server CPU | threads | heard min |
     |---|---|---|---|---|
     | 8 | pairchains | 54% of 1 core | 173 | 100% |
     | 8 | pairchains-lite | 57% of 1 core | 117 | 100% |
     | 16 | pairchains | 204% of 1 core (51% of 4) | 445 | 100% |
     | 16 | pairchains-lite | 121% of 1 core (30% of 4) | 204 | 100% |

     Dropping the per-chain queue threads saves ~40% at 16 (context switching grows with threads), but cost is
     still per pair: extrapolating lite to 32 people (992 pairs) gives ~5 cores, still over the gate.
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
     Same machine, 16 people, 15–20 s: matrix 90–123% of one core / ~190 threads; pairchains-lite 114–127% / ~204;
     pairchains ~205–212% / 445. 24 people on matrix: 186% (with the tool using another 173%, so the box was ~90%
     busy and this is inflated). The remaining cost looks per-participant (decode, encode, SRTP, jitterbuffer), not
     per-pair. One cold-server 16-person matrix run failed the 2% self/leak check (later runs: 0.0–0.2%); watch for
     it in the second-machine benchmark.
   - **Second-machine benchmark (2026-11-12)**: load tool on Ubuntu 22.04 (GStreamer 1.20.3 clients work fine),
     server on DevTest26 (a Xen guest, 4 vCPUs of an N6005). 20 s windows:

     | people | engine | server CPU | threads | result |
     |---|---|---|---|---|
     | 16 | matrix | 85% of 1 core (21% of 4) | 192 | PASS |
     | 16 | pairchains-lite | 116% (29%) | 207 | PASS |
     | 16 | pairchains | 176% (44%) | 446 | PASS |
     | 32 | matrix | **310% (78% of 4)** | 352 | PASS (self max 0.7%) |
     | 32 | pairchains-lite | 359% (90%) | 383 | FAIL: nothing heard (overloaded) |

     Matrix passes 32 but is over the 60%-of-4-cores gate, and per-person cost rose from 5.3% of a core at 16 to
     9.7% at 32. The matrix arithmetic is negligible, so the growth is probably contention (VM, ~80% busy, cache)
     rather than a per-pair term. Per-thread CPU at 16 (matrix, from /proc thread names): listener threads
     (matrix + Opus encode + RTP/SRTP send) 56–59%, decode-side queues 26%, nicesrc (receive + SRTP) 10%,
     interleave 3.5%. `Media:OpusComplexity` (new, default 10) at 5 cut the listener threads to 41% and the total
     by ~17%.
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
     First numbers (server + tool on DevTest26, 3 talkers, new defaults): 8 people matrix 50% / matrix-shared 32% of
     one core; 16 people 75% / 64%. What remains at 16 is per participant (decode, payload/SRTP/send).
   - **32-person benchmark from the 22.04 machine (2026-11-12), 3 talkers, Opus complexity 5 / 40 ms:**

     | engine | server CPU | memory | threads | heard min | self max |
     |---|---|---|---|---|---|
     | matrix-shared | **196% of 1 core = 49% of 4** | 194 MB | 356 | 100% | 0.1% |
     | matrix | 257% = 64% of 4 | 219 MB | 353 | 100% | 0.3% |

     **matrix-shared meets the Phase 2 gate (< 60% of 4 cores at 32 participants).** (The tool's "992 pairs" label
     counts n(n−1) regardless of `--talkers`; with 3 talkers 93 pairs are actually mixed.)
   **Decided 2026-11-12:** default engine `matrix-shared`; `pairchains-lite` removed. Phase 2 done.
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
     reports are ignored.
   - Still to do: TURN tested for real; Firefox + Chrome manual tests on the LAN.
   - **Parked (pairchains only):** the user expects pairchains won't be efficient enough, so its issues are parked;
     it stays in the engine suites for comparison. A second intermittent case (2026-11-13, 1 in ~5 full runs):
     `PairChainsSpatial.Sources_are_panned_…`, a source moved into range is never heard (4/4 reruns pass). Intermittent issue: 1 failure in ~6 full runs: in `Deleting_a_room_with_connected_participants` (pairchains), in
     the fresh second room one direction (fake2 → fake3) never came up within 20 s while the other worked; 12/12
     passes when re-run alone. Not yet diagnosed (candidates: a rare ingress/answer race, or interference from the
     deleted room's teardown running concurrently).
4. **Admin UI.** Rooms/participants views, levels/stats, kick/mute, event log, pipeline graph.
   **First version done 2026-11-13** (`/admin/`). Decided: password via CLI with the hash in SQLite; an admin session
   may use the whole control API, admin-only endpoints need the session; graph rendered server-side when Graphviz is
   installed.
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
     client-reported loss, ICE candidate types), `rooms/{id}/graph.dot`, `rooms/{id}/graph.svg` (Graphviz `dot`, 501
     if absent). get-stats is emitted on the room worker and the replies awaited off it, so a participant leaving
     meanwhile cannot free the element under the call.
   - **UI** (`wwwroot/admin`, reuses the client's CSS): sign-in, status line (version, rooms, memory, CPU), rooms
     table + create (engine, spatial, max) / delete, room view (participants: state, level meter, kbit/s in/out
     from byte deltas, loss, jitter, RTT, path, position, gain; mute/unmute, kick; invite link; pipeline graph and
     .dot; session history), event log for all rooms or the selected one, with paging. Polls: room 1 s, rooms 3 s,
     status and events 5 s. Checked in headless Chrome with speaking bots (screenshots).
   - Tests: `AdminAuthTests` (hashing, login, throttle, logout, cookie flags, config precedence, admin ⊇ API, API
     key ≠ admin), `AdminMediaTests` (levels of talking vs silent clients, connection stats, graph).
5. **Hardening.** Limits, input validation, token expiry, rate limiting, graceful shutdown, Dockerfile,
   Windows/Linux run docs, load test using the GStreamer fake-client tool.
6. **Spatial (brought forward 2026-11-13, first version done).** Decided: a per-room `spatial` flag (create/ensure,
   default off, stored: migration 4, shown in room details); `matrix-shared` clusters listeners by rendering; an
   inverse, SL-like distance model.
   - `ISpatialPolicy` → `InverseDistancePolicy` (`Media/Spatial/`): full volume within `Media:Spatial:ReferenceDistance`
     (10 m), then reference/distance, silent beyond `MaxRange` (60 m). Pan = sine of the horizontal angle from the
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
   - **Finding: clustering saves little in a scattered crowd.** Offline simulation of 32 people, 3 talkers, random
     positions and facing (29 listeners): clusters ≈ 29 at the default grid, 26 at 6 dB / 2 pan steps, 21 even at
     10 dB / left-centre-right. Facing the same way helps somewhat (13–28). So a spatial room costs about one encoder
     per listener (the `matrix` engine's cost) however it is clustered. Local 32-person run: clusters 29, encoders 32.
     Options to decide: accept it (spatial rooms cost ~matrix, measure on the 22.04 box), cheaper encoding for
     spatial rooms, or client-side spatialisation for web clients (rejected below).
   - **Benchmark (22.04 client, 2026-11-13), 32 people, 3 talkers, spatial:** matrix-shared 74% of 4 cores (29
     clusters, 32 encoders), matrix 65%; both over the 60% gate (non-spatial: matrix-shared 49%, matrix 64%).
     Locally (16 people) part of the gap was start-up work counted in the window (talkers found, personal mixes and
     clusters built, .NET JIT), so the load tool now warms up (`--warmup`, 10 s) before measuring; a steady ~10% gap
     remains (85% vs 75% of a core), mostly each listener's link queue thread. Removing the queue would let one
     stalled listener freeze its cluster (the blocking push behind the teardown deadlock), so instead
     **`Media:SpatialEngine` (default `matrix`)** is the engine for spatial rooms created without one (status:
     `defaultSpatialEngine`; the client switches the engine menu when Spatial is ticked). The load tool's leak check
     now ignores tones that are exact multiples of a talker's (codec harmonics: 600 Hz = 2 × 300 Hz gave a false
     FAIL).
   - **Encoder experiments (22.04 client, warm-up on, 32 people, 3 talkers, spatial, matrix):** defaults
     (complexity 5, 40 ms) **58%** of 4 cores (meets the gate; the earlier 65% included start-up work); complexity 2
     **54%**, clean; complexity 2 + 60 ms frames **46%** but FAIL: a silent client heard 500 Hz (2 × 400 − 300, an
     intermodulation product of the encoder, ~−24 dB) in 45% of frames, absent at 40 ms, and 60 ms adds latency.
     So: keep 40 ms; complexity 2 for spatial rooms is a candidate once a listening test with speech confirms it
     (would be a separate `Media:SpatialOpusComplexity`, leaving non-spatial rooms at 5).
   - **Decided 2026-11-13: engine tuning is parked.** Complexity 2 with the speaking bots sounds worse but stays
     intelligible; not adopted. The test VM (4 vCPUs on shared low-spec hardware) limits what benchmarks can say, so
     defaults stay (complexity 5, 40 ms; spatial rooms on `matrix`) while the other phases are developed. Revisit on
     production-like hardware: `Media:SpatialOpusComplexity`, grouping listeners by position. Engines stay as they
     are: `matrix-shared` for non-spatial rooms, `matrix` for spatial ones.
   - **Decided: no client-side spatialisation.** The server stays an MCU (one mixed stream per listener); an SFU-style
     design (forwarding talkers for the browser to spatialise) would break compatibility with SL/OpenSim viewers.
   - Start-up switch `--delete-existing-rooms` (testing): deletes every stored room before any pipeline starts
     (logged as room.deleted). Each room's pipeline otherwise runs from creation even when empty (~1–1.5% of a
     core per room); starting media only when someone joins is noted for Phase 5.
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
     listener (camera, tethered to within 50 m of the avatar) position/rotation, region coordinates in **cm** and
     quaternions ×100, as integers. Also `{"j":{"p":true}}` join (p = primary region), `{"ug":{"<agent>":gain×220}}`
     per-peer gain, `{"m":{"<agent>":bool}}` per-peer mute. Server positions are meant to be "tethered" to the
     simulator's authoritative avatar positions (anti-eavesdropping).
   - **Data channel server → viewer**: object keyed by agent UUID: `j` {p} joined, `l` true left, `p` level 0–128,
     `v` speaking, `m` moderator-muted, `V` server version string.
   - Covered by `ViewerCompatibilityTests` (viewer-style fake clients: SLData channel, the munging byte for byte,
     `trickle:false`, never fetching server candidates) and `SdpTests`. Not yet: handling SLData messages.
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
     - **To decide with SL viewers:** add `tethered` (the client may move its listener pose within N m of its
       server-set position; SL uses 50 m) and maybe `free` (web-only rooms with no authority)? Feed the viewer's
       SLData `sp`/`sh`/`lp`/`lh` messages (cm, quaternion ×100) through the same `UpdateOwnPose` path, or treat the
       speaker part (`sp`/`sh`) as a hint only and keep the region module's PATCH as the authority? Rate limits for
       the viewer's ≤ 10/s updates; whether the region module should also send camera positions; what the default
       `clientPose` for region rooms should be.
   - **Guest names (2026-11-13):** a participant created with `clientNamed: true` (both invite buttons) may choose its
     display name, sent as `displayName` with its first offer; `/signal/{pid}/config` reports `displayName` and
     `clientNamed`. Checked before the one-time join token is used, so a refused name (403) can be retried. Others
     keep the name the integration gave (an SL avatar's name from the region module). Logged as
     `participant.renamed` ("Guest → Alice"); the session history takes the new name; the admin UI tags such
     participants "guest". Names are trimmed, control characters dropped, ≤ 200 characters.

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
