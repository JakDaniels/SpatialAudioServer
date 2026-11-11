# WebRtcEcho – Phase 0 spike

Throwaway spike, not part of the product build. It proves the chosen binding (GstSharp.Net) can run the server
as the SDP answerer for a browser: browser offer → server answer over HTTP, trickle ICE both ways (server
candidates are polled), Opus in → decoded → audiomixer (with a silent keep-alive source) → Opus back out.

```sh
dotnet run                      # http://*:5000 and https://*:5001 – open the page, press Start, use headphones
dotnet run -- --selftest        # GStreamer fake client offers a 440 Hz tone and checks it comes back; exit 0/1
```

Browsers only allow the microphone on HTTPS or `localhost`, so from another machine use `https://<server-ip>:5001`.
The first run creates a self-signed certificate in `.devcert/devcert.pfx` (git-ignored) covering localhost, the host
name and the machine's IPv4 addresses; accept the browser warning once. Delete the file to regenerate it if the
address changes. Ports are `HttpPort`/`HttpsPort` in `appsettings.json`.

| Endpoint | |
| --- | --- |
| `POST /api/sessions` `{sdp}` | Browser offer in, `{id, sdp}` answer out |
| `POST /api/sessions/{id}/candidates` `{candidate, sdpMLineIndex}` | Browser ICE candidate |
| `GET /api/sessions/{id}/candidates?since=n` | Server candidates from index n, plus `complete` and `connectionState` |
| `DELETE /api/sessions/{id}` | Tear down |

`Media/WebRtcPeer.cs` holds the webrtcbin plumbing (promises, SDP, candidates, bus); `Media/EchoSession.cs` is
the server side; `Media/FakeClient.cs` is the test client.
