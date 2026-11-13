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

# Compare engines at increasing sizes (everyone talking).
./SpatialAudioServer.LoadTest --server $S --participants 16 --engine matrix,pairchains
./SpatialAudioServer.LoadTest --server $S --participants 32 --engine matrix
```

Realistic talk patterns (needed for `matrix-shared`, which only mixes the current talkers, at most
`Media:ActiveSpeakers` = 4 by default):

```bash
./SpatialAudioServer.LoadTest --server $S --participants 32 --talkers 3 --engine matrix-shared,matrix
```

Spatial rooms (positions matter; `matrix-shared` then needs one cluster per distinct rendering):

```bash
./SpatialAudioServer.LoadTest --server $S --participants 32 --talkers 3 --spatial --engine matrix-shared,matrix
```

`--spatial` places everyone at random in a 30 m square, facing a random way. Each run also prints the room's
engine counters (`clusters`, `encoders`, …) from `GET /api/rooms/{id}/media`.

Other options: `--rooms R` (R rooms of N people each), `--duration S` (measuring window, default 20 s),
`--jitter-ms`, and `--insecure` (when using `https://…:5001` with the self-signed certificate).
The exit code is 0 when every run passes.

## Extra speakers in your own room (listening tests)

Room mode adds fake participants to an existing room, for example one you are in with the browser client at
`/client/`. They stay until you press Ctrl+C (or `--duration S` runs out). They then leave, and the room is kept.
There is no pass/fail, just a "receiving audio" line every 10 s.

```bash
R=1806b6ef-...                              # room ID: shown under the room name once you join in the browser client
./SpatialAudioServer.LoadTest --server $S --room $R --participants 4 --audio flite            # synthesised, taking turns
./SpatialAudioServer.LoadTest --server $S --room $R --participants 4 --audio flite-together   # all at once
./SpatialAudioServer.LoadTest --server $S --room $R --participants 3 --audio speech.wav
./SpatialAudioServer.LoadTest --server $S --room $R --participants 4 --audio a.ogg,b.wav --talkers 2 --volume 0.5
```

- `--audio` takes any file GStreamer can decode (WAV, OGG/Opus/Vorbis, FLAC, MP3). It is decoded once at start-up
  and looped. Several files are handed out round-robin. Clients that share a file start at different points in it,
  so they don't speak in unison.
- `--audio flite` needs no file. GStreamer's speech test source (`flitetestsrc`) gives each client its own voice,
  saying its number and a channel name: `bot-0` says "zero, front left", `bot-1` "one, front right", and so on. They
  take turns, like a conversation. `flite-together` has them all speak at once. There are 8 distinct voices; from
  `bot-8` on they repeat. It needs the flite plugin: check with `gst-inspect-1.0 flitetestsrc`. On Ubuntu that plugin
  is in `gstreamer1.0-plugins-bad`.
- Without `--audio`, each client sends a tone (300 Hz, 350 Hz, …).
- `--talkers K` keeps all but the first K silent: they are in the room, but don't speak.
- `--volume` scales what they send. The default is 1.0 for files and 0.1 for tones.
- `--name` sets the display-name prefix. The default is `bot`, which gives `bot-0`, `bot-1`, ….
- `--engine` can't be combined with `--room`: the room already has its engine.

**Reading the output.** "server CPU" is measured on the server over the measuring window. A run passes when:

- every client hears each other client's tone in more than half the frames;
- no client hears its own tone in 2% or more of frames;
- no client hears another room's tone in 2% or more of frames.

If a run fails, send the whole output. Also send the server's console log if it shows GStreamer warnings.
