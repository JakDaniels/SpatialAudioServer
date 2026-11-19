# Changelog

What changed in each release: the same text as its release notes.

## Unreleased

- Server: a new `acoustic` mix engine, which mixes the whole room in one acousticmultimixer element (a separate
  GStreamer plugin): offered, and listed in `GET /api/status`, only where that plugin is installed.
- Server: rooms have `acoustics` (`outdoor`, the default, `tiny-room`, `small-room`, `big-room`, `hall`,
  `long-hall`, `cave` or `cathedral`), set when created or changed later with `PATCH` and in the admin UI;
  `acoustic` rooms hear that space's reflections and reverb.
- Server: in `acoustic` rooms a voice closer than `Media:Spatial:NearDistance` (2 m) stands out from the room,
  whose sound is turned down instead (to −12 dB at 0.5 m), as in real life.
- Server: voices fade out smoothly over the last quarter of the range (`Media:Spatial:Taper`, 0.75) instead of
  cutting off at `MaxRange`, where they could still be clearly heard.
- Load tool: `--audio white-noise` and `pink-noise` send steady noise, to hear treble loss with distance and
  direction, which speech hides.

## 0.2.2 (2026-11-18)

- Server: `--help` points to the new settings guide, docs/configuration.md (running.md is now split into guides for
  the packages and for building from source).

## 0.2.1 (2026-11-17)

- Load tool: GstSharp.Net's libraries are separate files beside the program instead of inside it, as their LGPL
  licence asks (they can be replaced); copy them along when moving a hand-built load tool.

## 0.2.0 (2026-11-17)

The first release.

- Server: rooms and participants through a JSON control API (API keys), WebRTC clients joining with one-time
  tokens, and a mix-minus mix per listener (Opus, mono in, stereo out), with mix engines chosen per room.
- Server: spatial rooms: voices placed by distance and direction, with outdoor and meeting distance profiles;
  Second Life / OpenSimulator viewers supported through the os-webrtc-sas region module.
- Server: an admin UI (rooms, live levels and WebRTC stats, mute, gain, kick, event log, pipeline view) and a test
  web client for browsers.
- Server: rooms kept in SQLite; media runs only while a room is in use; dead connections and unused joins removed;
  rate and input limits; runs as a systemd service or a Windows service.
- Server: GStreamer 1.24 or later (Ubuntu 24.04, Debian 13, Fedora); Windows installers carry their own GStreamer.
- Server: `--help`, and a plain message instead of an error trace when an ordinary user runs the installed server's
  commands without the rights to its configuration and data.
- Load tool: fills rooms with test clients that check who hears whom and measure the server's CPU, or adds talking
  bots to a room for listening tests; `--help`.
