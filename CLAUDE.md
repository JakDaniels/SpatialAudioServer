# SpatialAudioServer – session handoff

Read [PLAN.md](PLAN.md) first: it is the full spec and holds every decision made so far.

## What this is
MCU-style audio conferencing server in C# on GStreamer. Rooms and participants are managed via a JSON HTTP API; web clients
connect with WebRTC (HTTP offer/answer + trickle ICE); one mix-minus mixer chain per listener (stereo out, per-source gain/pan, for
future spatial audio). Later: Second Life/Firestorm viewer support via an OpenSim region module (not in scope yet).

## Where we are
- Planning is complete; all requirements questions are answered (see "Resolved requirements" in PLAN.md).
- The session continues on a new machine running Ubuntu 26.04 (previous machine: Ubuntu 22.04, GStreamer 1.20.3, .NET 10 SDK).
- Machine: Ubuntu 26.04, .NET SDK 10.0.115, GStreamer 1.28.2.
- **Phase 0 mostly done:** binding chosen = `GstSharp.Net` 1.28.17 (GirCore is the fallback). Spike in `spikes/WebRtcEcho`
  (`dotnet run -- --selftest` passes). Findings are in PLAN.md "Binding choice". Firefox browser test passed. Still open: Chrome/Edge test
  and a Windows run.
- **Phase 1 (skeleton) done:** solution `src/SpatialAudioServer.slnx` (server + `SpatialAudioServer.Tests`, all under `src/`).
  `dotnet test src` runs 23 tests; `dotnet run --project src/SpatialAudioServer` serves HTTP 5000 / HTTPS 5001;
  `dotnet run --project src/SpatialAudioServer -- apikey create <label>` makes a control-API key.
- Next: Phase 2 (media core). Ask the user before starting it.

## Key decisions (summary; PLAN.md is authoritative)
- Server is the SDP answerer; Opus 48 kHz; mono in, stereo out; per-(source,listener) chain queue->audioamplify->audiopanorama->listener audiomixer.
- Auth: API key for control API, one-time join token for client signalling, separate admin password for admin UI.
- Room IDs server-generated UUIDs; rooms persist in SQLite until deleted. Max 16 rooms x 32 participants (absolute max), 4-8 cores.
- Admin UI and test web client: static HTML/JS, no build step. Targets: Linux + Windows; evergreen browsers; LAN-only for now.
- Licence stays Apache-2.0.

## Reference code
`docs/reference/` holds copies of the user's earlier aborted experiments (originals were in `~/src/gstreamer-testing` and `~/src/gstreamer-test2`
on the old machine). They are ideas only: `audiomixer-test3` (per-pair chains, dynamic add/remove) and `webrtc-test1`. They use
`gstreamer-sharp-netcore` 0.0.8 and are not part of any build.

## Working preferences
- Commits end with: `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`. Commit only when asked.
- The user wants questions asked when the spec is unclear.
