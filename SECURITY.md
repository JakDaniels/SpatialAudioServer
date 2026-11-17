# Security

## Reporting a vulnerability

Please do not open a public issue for a security problem. Report it privately through GitHub instead:
**Security** tab of this repository, **Report a vulnerability**. Include what you found, how to reproduce it, and which
version you ran (`spatialaudioserver --version`, or `GET /api/status`).

You should get an answer within a week. Once a fix is released, the report can be published as a security advisory,
with credit to you if you want it.

## Supported versions

Only the latest release gets security fixes while the project is at 0.x. Fixes come as a new release; see
[CHANGELOG.md](CHANGELOG.md).

## Scope

The server, its admin UI and test web client, the load tool, and the packages and installers built here. Problems in
GStreamer or other libraries the server uses belong with those projects, but tell us too if the server is affected.
The OpenSim region module has its own repository: [os-webrtc-sas](https://github.com/JakDaniels/os-webrtc-sas).

For running the server on an internet-facing host (TLS, network allow-lists, TURN, limits), see
[docs/internet.md](docs/internet.md).
