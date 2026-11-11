# SpatialAudioServer – session handoff

Read [PLAN.md](PLAN.md) first: it is the full spec and holds every decision made so far. No code exists yet.

## What this is
MCU-style audio conferencing server in C# on GStreamer. Rooms and participants are managed via a JSON HTTP API; web clients
connect with WebRTC (HTTP offer/answer + trickle ICE); one mix-minus mixer chain per listener (stereo out, per-source gain/pan, for
future spatial audio). Later: Second Life/Firestorm viewer support via an OpenSim region module (not in scope yet).

## Where we are
- Planning is complete; all requirements questions are answered (see "Resolved requirements" in PLAN.md).
- The session continues on a new machine running Ubuntu 26.04 (previous machine: Ubuntu 22.04, GStreamer 1.20.3, .NET 10 SDK).
- **Next step: Phase 0 spike** (see PLAN.md "Phases" and "Binding choice"):
  1. Run `scripts/setup-ubuntu.sh`, record `dotnet --version` and `gst-inspect-1.0 --version` in PLAN.md.
  2. Spike webrtcbin (browser offer -> server answer, Opus in -> loopback out) with candidate bindings, in order of preference:
     GirCore (`GirCore.Gst-1.0`, `GirCore.GstWebRTC-1.0`, `GirCore.GstSdp-1.0`), `GstSharp.Net` + `GstSharp.Net.WebRTC`,
     `GStreamer-Sharp-NetCore` 0.0.8. Check current NuGet versions first. Pick one; keep GStreamer use behind the `Media/` layer.
  3. Record the choice and findings in PLAN.md.
- Then Phase 1 (skeleton), per PLAN.md. Ask the user before starting anything beyond Phase 0.

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
