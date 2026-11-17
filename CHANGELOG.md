# Changelog

What changed in each release: the same text as its release notes.

## Unreleased

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
