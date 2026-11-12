# Load tool (self-contained, Linux x64)

`SpatialAudioServer.LoadTest` fills rooms on a running server with fake WebRTC clients, each sending its own tone. It checks
that every client hears everyone else and not itself, and it reports the server's CPU, memory and threads from
`/api/status`. It is a single file with .NET built in. Rebuild it with `scripts/build-loadtest.sh`.

Run it on a different machine from the server, because the fake clients cost about as much CPU as the server does.
A 32-person run needs roughly 2.5 free cores.

## One-time setup on the load machine (Ubuntu 22.04 or later)

```bash
sudo apt-get install -y gstreamer1.0-tools gstreamer1.0-nice \
  gstreamer1.0-plugins-base gstreamer1.0-plugins-good gstreamer1.0-plugins-bad \
  libgstreamer-plugins-bad1.0-0
chmod +x scripts/loadtest-linux-x64/SpatialAudioServer.LoadTest
gst-inspect-1.0 webrtcbin >/dev/null && echo webrtcbin ok
```

GStreamer 1.20 (22.04) should be enough for the clients: they only use webrtcbin as the offerer plus Opus.
This is untested, so start with the smoke test below.

## On the server machine

Start the server, then create a control-API key and keep the `sas_…` value it prints:

```bash
dotnet run --project src/SpatialAudioServer
dotnet run --project src/SpatialAudioServer -- apikey create loadtest
```

## Runs

```bash
cd scripts/loadtest-linux-x64
export SAS_API_KEY=sas_...                  # the key from the server
S=http://10.2.2.235:5000                    # the server

# Smoke test: 2 people. It should print RESULT: PASS within about a minute.
./SpatialAudioServer.LoadTest --server $S --participants 2 --duration 10

# Compare engines at increasing sizes.
./SpatialAudioServer.LoadTest --server $S --participants 16 --engine matrix,pairchains-lite,pairchains
./SpatialAudioServer.LoadTest --server $S --participants 24 --engine matrix,pairchains-lite
./SpatialAudioServer.LoadTest --server $S --participants 32 --engine matrix,pairchains-lite
```

Realistic talk patterns (needed for `matrix-shared`, which only mixes the current talkers, at most
`Media:ActiveSpeakers` = 4 by default):

```bash
./SpatialAudioServer.LoadTest --server $S --participants 32 --talkers 3 --engine matrix-shared,matrix
```

Other options: `--rooms R` (R rooms of N people each), `--duration S` (measuring window, default 20 s),
`--jitter-ms`, and `--insecure` (when using `https://…:5001` with the self-signed certificate).
The exit code is 0 when every run passes.

**Reading the output.** "server CPU" is measured on the server over the measuring window. A run passes when:

- every client hears each other client's tone in more than half the frames;
- no client hears its own tone in 2% or more of frames;
- no client hears another room's tone in 2% or more of frames.

If a run fails, send the whole output. Also send the server's console log if it shows GStreamer warnings.
