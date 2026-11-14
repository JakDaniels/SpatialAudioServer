# Benchmarks and load-test results

Measured results for SpatialAudioServer, oldest first. The decisions taken from them are recorded in
[PLAN.md](../PLAN.md); how to run the server is in [running.md](running.md).

## How the numbers are made

- **Load tool:** `src/SpatialAudioServer.LoadTest` (a self-contained Linux build is in `scripts/loadtest-linux-x64/`).
  Each fake client is a real WebRTC peer that sends a tone (300 Hz + 50 Hz × i) and measures, with a Goertzel tone
  meter on 20 ms frames, which tones it hears. A run passes when every listener hears every other talker in its room
  in more than half the frames, hears itself in almost none (mix-minus) and hears no other room.
- **CPU** is the server process's own CPU time over the measuring window (from `GET /api/status`), as **% of one
  core**. "% of N cores" divides by the logical processor count, so on machines with hyperthreading it flatters.
- `--talkers K`: only K clients per room send a tone; the rest send silence. Most runs use 3, a typical meeting.
- Since 2026-11-13 the tool warms up for 10 s (`--warmup`) before a 20 s window, so start-up work (JIT, building
  mixes and clusters) is not counted.
- **Phase 2 gate:** a full 32-participant room runs in < 60% of a 4-core server.

## Machines

| name | role | hardware |
|---|---|---|
| N6005 | dev box (server + tool, early runs) | Pentium Silver N6005, 4 cores, 5 GB, Ubuntu 26.04 |
| DevTest26 | test VM server | Xen guest, 4 vCPUs of an N6005, Ubuntu 26.04 |
| 22.04 machine | load-tool client | Ubuntu 22.04, GStreamer 1.20.3 |
| Windows | server | Xeon W-10855M (6 cores / 12 threads, 2.8–5.1 GHz), 32 GB, Windows |

## Summary

| server | 32 people, 3 talkers | spatial | more rooms |
|---|---|---|---|
| DevTest26 (4 vCPU) | matrix-shared **196%** (49% of 4: meets the gate), matrix 257% | matrix 232% (58% of 4) | not measured |
| Windows (6 cores) | matrix-shared **56%**, matrix 88%, pairchains 377% | matrix 86–98%, matrix-shared 110–113% | linear to 6 × 32 (337%); 8 × 32 776% |

Working figure for the Windows machine: about 8 full non-spatial rooms or 4 full spatial rooms.

## 2026-11-12: first numbers (N6005, server and tool on one box)

`pairchains` only (one queue → audioamplify → audiopanorama chain per source/listener pair). The tool's own CPU
competes, so the numbers are indicative.

| participants | chains | server CPU | server RSS | server threads | heard others (min / mean) | self |
|---|---|---|---|---|---|---|
| 4 | 12 | 25% of 1 core (6% of 4) | 143 MB | 85 | 100% / 100% | 0% |
| 8 | 56 | 63% of 1 core (16% of 4) | 164 MB | 174 | 100% / 100% | 0% |
| 16 | 240 | 214% of 1 core (53% of 4) | 207 MB | 444 | 100% / 90% | 1.2%* |

\*The tool used another 107% of a core, so the box was ~80% busy; the dropouts and the 1.2% "self" (tone
detection at 50 Hz spacing, not a routing fault: no self chain exists) came from that.

Finding: cost is ~0.9–1% of a core per chain and chains grow as N(N−1): 32 people = 992 chains ≈ 9 cores. Threads
also grow as N² (each chain's queue is a streaming thread: ~1,500 for one 32-person room).

## 2026-11-12: engine comparison (N6005, server and tool on one box)

`--engine pairchains,pairchains-lite`, 15 s (`pairchains-lite`: no per-chain queue/audioamplify; since removed):

| participants | engine | server CPU | threads | heard min |
|---|---|---|---|---|
| 8 | pairchains | 54% of 1 core | 173 | 100% |
| 8 | pairchains-lite | 57% of 1 core | 117 | 100% |
| 16 | pairchains | 204% of 1 core (51% of 4) | 445 | 100% |
| 16 | pairchains-lite | 121% of 1 core (30% of 4) | 204 | 100% |

Dropping the per-chain queue threads saves ~40% at 16, but the cost is still per pair (lite at 32 ≈ 5 cores).

The `matrix` engine on the same box, 16 people, 15–20 s: matrix 90–123% of one core / ~190 threads; pairchains-lite
114–127% / ~204; pairchains ~205–212% / 445. 24 people on matrix: 186% (with the tool using another 173%, so
inflated). The remaining cost looks per participant (decode, encode, SRTP, jitter buffer), not per pair. One
cold-server 16-person matrix run failed the 2% self/leak check (later runs 0.0–0.2%).

## 2026-11-12: second machine (server DevTest26, tool on the 22.04 machine)

20 s windows, everyone talking:

| people | engine | server CPU | threads | result |
|---|---|---|---|---|
| 16 | matrix | 85% of 1 core (21% of 4) | 192 | PASS |
| 16 | pairchains-lite | 116% (29%) | 207 | PASS |
| 16 | pairchains | 176% (44%) | 446 | PASS |
| 32 | matrix | **310% (78% of 4)** | 352 | PASS (self max 0.7%) |
| 32 | pairchains-lite | 359% (90%) | 383 | FAIL: nothing heard (overloaded) |

Per-person cost rose from 5.3% of a core at 16 to 9.7% at 32; the matrix arithmetic is negligible, so probably
contention (VM, ~80% busy, cache). Per-thread CPU at 16 (matrix, from /proc thread names): listener threads (matrix
+ Opus encode + RTP/SRTP send) 56–59%, decode-side queues 26%, nicesrc (receive + SRTP) 10%, interleave 3.5%.
Opus complexity 5 instead of 10 cut the listener threads to 41% and the total by ~17%.

`matrix-shared` (talkers get personal mixes, everyone else shares one encoder), first numbers with the tool on
DevTest26, 3 talkers, complexity 5 / 40 ms: 8 people matrix 50% / matrix-shared 32% of one core; 16 people
75% / 64%.

## 2026-11-12: the 32-person gate (server DevTest26, tool on the 22.04 machine)

3 talkers, Opus complexity 5 / 40 ms:

| engine | server CPU | memory | threads | heard min | self max |
|---|---|---|---|---|---|
| matrix-shared | **196% of 1 core = 49% of 4** | 194 MB | 356 | 100% | 0.1% |
| matrix | 257% = 64% of 4 | 219 MB | 353 | 100% | 0.3% |

matrix-shared meets the gate. (The tool's "992 pairs" label counts n(n−1) regardless of `--talkers`; with 3
talkers 93 pairs are actually mixed.)

## 2026-11-13: spatial rooms (server DevTest26, tool on the 22.04 machine)

32 people, 3 talkers, everyone at random in a 30 m square facing a random way:

- Without warm-up: matrix-shared 74% of 4 cores (29 clusters, 32 encoders), matrix 65%. Part of the gap was
  start-up work; with warm-up a steady ~10% gap remained (85% vs 75% of a core at 16 people locally), mostly each
  listener's link queue thread. Hence spatial rooms default to `matrix`.
- Clustering saves little in a scattered crowd (offline simulation, 29 listeners): clusters ≈ 29 at the default
  grid, 26 at 6 dB / 2 pan steps, 21 even at 10 dB / left-centre-right; facing the same way helps somewhat (13–28).

Encoder experiments (warm-up on, matrix):

| Opus settings | server CPU | result |
|---|---|---|
| complexity 5, 40 ms (defaults) | **58%** of 4 cores | PASS |
| complexity 2, 40 ms | 54% | PASS (sounds worse with speech, still intelligible) |
| complexity 2, 60 ms | 46% | FAIL: 500 Hz intermodulation heard by a silent client in 45% of frames |

## 2026-11-14: Windows server (tool on the 22.04 machine)

Xeon W-10855M, 3 talkers unless noted, CPU as % of one core:

| load | matrix-shared | matrix | pairchains |
|---|---|---|---|
| 32 people | **56%** (303 MB) | 88% | 377% (1387 threads) |
| 32 people, spatial | 110–113% (29 clusters) | 86–98% | 383–515% |
| 2 × 32 | 109% | | |
| 4 × 32 = 128 | 224% (452 MB) | | |
| 6 × 32 = 192 | 337% (563 MB) | | |
| 8 × 32 = 256 | **776%** (637 MB, 2644 threads), PASS | | |
| 4 × 32, spatial | | 373% (586 MB) | |
| 32 people, 8 talkers | 51% (4 mixed, by design) | 88% | 528% |

- About 3.5× the test VM per core (matrix-shared 56% vs 196%).
- Linear in rooms up to 6 (~56% of a core per 32-person room); 8 rooms passes but costs 97% per room. That is past
  the 6 physical cores, so probably hyperthreads plus the mobile CPU's lower all-core clock. Not yet confirmed (watch
  the clock speed during the run; run 6 rooms again while hot).
- 16 × 32 (the design maximum) would need a bigger machine.
- With 8 talkers matrix-shared mixes only the 4 loudest (`Media:ActiveSpeakers`), so the "hears every talker" check
  fails by design; its CPU does not grow with talkers.

## Measurement artefacts found on the way

- **Codec harmonics:** a listener whose tone is a multiple of a talker's (600 Hz = 2 × 300 Hz) hears that talker's
  faint harmonics above the low spatial threshold. The leak check ignores such tones.
- **Intermodulation:** two talkers a and b produce a faint 2a − b (2 × 400 − 300 = 500 Hz), up to ~volume/12 when a
  talker is near in a spatial room, more at 60 ms frames. Ignored like harmonics since 2026-11-14.
- **Load tool and rate limits:** the tool once polled `/signal/{id}/ice` without pause after the server's candidates
  were complete, and connected clients polled every 500 ms; with 16 bots against a remote server, then 8 × 32 from
  one machine, that hit the signalling rate limits (429). It now pauses, and connected clients poll every 2 s.
- **One-sided ICE:** with server and tool on one machine ICE could connect from one side's candidates only, which
  hid a signalling bug until the first cross-machine run; the tests now check trickle ICE in both directions.
