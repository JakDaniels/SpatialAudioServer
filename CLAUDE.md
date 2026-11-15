# SpatialAudioServer – session handoff

Read [PLAN.md](PLAN.md) first: it is the full spec and holds every decision made so far.

## What this is
MCU-style audio conferencing server in C# on GStreamer. Rooms and participants are managed via a JSON HTTP API; web clients
connect with WebRTC (HTTP offer/answer + trickle ICE); one mix-minus mixer chain per listener (stereo out, per-source gain/pan, for
future spatial audio). Later: Second Life/Firestorm viewer support via an OpenSim region module (not in scope yet).

## Where we are
- Planning is complete; all requirements questions are answered (see "Resolved requirements" in PLAN.md).
- Machine: Ubuntu 26.04, .NET SDK 10.0.115, GStreamer 1.28.2.
- **Phase 0 mostly done:** binding chosen = `GstSharp.Net` 1.28.17 (GirCore is the fallback). Spike in `spikes/WebRtcEcho`
  (`dotnet run -- --selftest` passes). Findings are in PLAN.md "Binding choice". Firefox browser test passed. Still open: Chrome/Edge test.
- **Phase 1 (skeleton) done:** solution `src/SpatialAudioServer.slnx` (server + `SpatialAudioServer.Tests`, all under `src/`).
  `dotnet test src` runs 23 tests; `dotnet run --project src/SpatialAudioServer` serves HTTP 5000 / HTTPS 5001;
  `dotnet run --project src/SpatialAudioServer -- apikey create <label>` makes a control-API key.
- **Phase 2 (media core) stages A–D done:** mix-minus over real WebRTC, live join/leave/mute, load tool
  (`src/SpatialAudioServer.LoadTest`), and pluggable mix engines (`Media/Engines/`, chosen per room): `pairchains`,
  `matrix` (audiointerleave + per-listener audiomixmatrix) and `matrix-shared` (default: talkers get personal
  mixes, everyone else shares one encoder). Server Opus defaults: complexity 5, 40 ms frames.
  `dotnet test src` runs 65 tests, with the end-to-end suites run once per engine.
- GStreamer 1.28.2 source is cloned (git-ignored) in `docs/reference/gstreamer` for reading element internals.
- Load runs go from a Ubuntu 22.04 box using the committed self-contained tool (`scripts/loadtest-linux-x64/`,
  rebuild with `scripts/build-loadtest.sh`). 32 people / 3 talkers: matrix-shared 49% of 4 cores (meets the
  < 60% gate), matrix 64%. Phase 2 done.
- Firestorm viewer source (git-ignored) is in `docs/reference/phoenix-firestorm`; its WebRTC protocol is summarised
  in PLAN.md Phase 7 (viewer-style fake clients + `trickle:false` answers cover it in tests).
- **Phase 3 started:** test web client at `/client/` (join link or API-key test mode), `Media:IceServers`,
  `GET /signal/{id}/config`. Verified in headless Chrome. Load tool room mode (`--room ID --audio file|flite|flite-together`)
  adds talking bots to a room you are in from the browser. Dead connections are dropped after `Media:DeadConnectionTimeout` (~27 s detection + 10 s).
- **Spatial (Phase 6, brought forward):** per-room `spatial` flag, inverse-distance policy, L/R matrix rows, matrix-shared
  clusters by rendering; finding: clusters ≈ listeners in a scattered crowd, so spatial rooms default to `matrix`
  (`Media:SpatialEngine`; see PLAN.md Phase 6). Engine tuning parked on the test VM; the server stays an MCU
  (no client-side spatialisation: SL/OpenSim viewers need one mixed stream). `--delete-existing-rooms` start-up switch for clean test runs.
- **Phase 4 (admin UI) first version done:** `/admin/` (password: `dotnet run --project src/SpatialAudioServer -- admin
  set-password`); rooms, participants with levels and WebRTC stats, mute/gain/kick, event log, pipeline view
  (`/admin/graph.html`: ELK layout of a JSON snapshot, summary blocks per participant stage).
- **Client-set poses (PLAN.md Phase 7):** listener vs speaker pose, per-room `clientPose` (`none`/`orientation`/
  `tethered`), `PATCH /signal/{pid}/pose` with the session token (guests can turn). Tethered (SL rooms): the viewer's
  SLData `sp/lp` are used near the simulator's position (`Media:Spatial:SpeakerTether` 10 m, `ListenerTether` 50 m).
- **Phase 7 (SL/OpenSim) under way** (PLAN.md Phase 7, "Plan for the OpenSim side"): a back end for OpenSim's
  own WebRTC region module, in the separate repo `os-webrtc-sas` (cloned at `docs/reference/opensim/addon-modules/`,
  OpenSim master, net8). Working with Firestorm 7.2 (on the Windows machine; 22.04's glibc is too old): region and
  parcel voice, simulator position push (`POST …/poses`), the server reads SLData and writes `j/l/p/v/V` to viewers
  (`Media/ViewerData.cs`), names for web/bot participants, per-person mute and volume. Neighbouring regions: second region
  `SAS Test2` (1001,1000, port 9001); avatars announced only in their primary region's room (viewer `j {p}`).
  Tested avatar to avatar across the border. Next: distance model by ear (`Media:Spatial:DistanceModel`/`Rolloff`).
  Test OpenSim standalone runs in `screen -S opensim` from `docs/reference/opensim/bin` (avatars Test One/Test Two).
- **Phase 5 (hardening) under way** (PLAN.md Phase 5): media on demand (`Media:IdleRoomTimeout`), stale joins removed at
  token expiry, graceful shutdown, signalling rate limits, input limits, security headers, run docs in
  `docs/running.md` (Linux + Windows). No Dockerfile for now. Windows run done 2026-11-14 (server + load tests pass;
  results in `docs/benchmarks.md`, which holds all benchmark numbers: keep PLAN.md to conclusions).
  Parked: the intermittent pairchains test failure; one unexplained test-host crash (PLAN.md Phase 5).

## Key decisions (summary; PLAN.md is authoritative)
- Server is the SDP answerer; Opus 48 kHz; mono in, stereo out; per-(source,listener) chain queue->audioamplify->audiopanorama->listener audiomixer.
- Auth: API key for control API, one-time join token for client signalling, separate admin password for admin UI.
- Room IDs server-generated UUIDs; rooms persist in SQLite until deleted. Max 16 rooms x 32 participants (absolute max), 4-8 cores.
- Admin UI and test web client: static HTML/JS, no build step. Targets: Linux + Windows; evergreen browsers; LAN-only for now.
- Licence stays Apache-2.0.

## Reference code
`docs/reference/` holds the user's earlier experiments (`audiomixer-test3`: per-pair chains, dynamic add/remove;
`webrtc-test1`). Ideas only, not part of any build. The GStreamer, GstSharp.Net and Firestorm source clones there are git-ignored.

## Working preferences
- Commits end with: `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Commit only when asked.
- The user wants questions asked when the spec is unclear.
