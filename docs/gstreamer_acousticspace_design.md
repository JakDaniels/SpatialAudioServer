# Custom GStreamer Acoustic Space DSP Plugin

## Overview

The goal is to build a custom GStreamer DSP element that goes beyond traditional conferencing and creates the illusion of participants occupying a shared virtual acoustic space.

Target environments include:

- Small reflective rooms
- Offices
- Hallways
- Warehouses
- Churches
- Caves
- Outdoor spaces
- Forests

The design prioritises immersion, positional awareness, and scalability while keeping CPU requirements practical for an MCU architecture.

---

# Why a Custom Plugin?

A conventional WebRTC SFU forwards streams and lets clients perform spatialisation.

This project requires an MCU architecture where:

- Audio is mixed centrally.
- Each listener receives a personalised mix.
- Participants can occupy positions in a simulated environment.
- Acoustic effects are generated server-side.

A native GStreamer plugin is a better fit than LV2/LADSPA because the problem is fundamentally a many-input / many-output routing and acoustic simulation engine rather than a simple audio effect.

---

# Proposed GStreamer Element

## Element Name

```text
acousticspace
```

## Responsibilities

- Multiple audio inputs
- Multiple audio outputs
- Participant position tracking
- Listener orientation tracking
- Environmental modelling
- N-minus-1 mixing
- Spatial positioning
- Distance modelling
- Early reflections
- Shared reverb bus

---

# High-Level Architecture

```text
 Alice ----\
 Bob -------\
 Charlie ----> acousticspace ---> Mix for Alice
 Dave ------/                  ---> Mix for Bob
 Eve -------/                  ---> Mix for Charlie
                                ---> Mix for Dave
                                ---> Mix for Eve
```

Internally the element generates unique mixes for each participant.

---

# GStreamer Structure

The element would likely derive directly from:

```c
GstElement
```

rather than:

```c
GstAudioFilter
```

because it requires multiple sink and source pads.

## Request Pads

```text
sink_0
sink_1
sink_2
...
sink_N

src_0
src_1
src_2
...
src_N
```

Each sink pad represents one participant's microphone stream.

Each source pad delivers that participant's personalised output mix.

---

# Participant Model

Each participant maintains spatial state.

```c
struct Participant
{
    float x;
    float y;
    float z;

    float yaw;

    float gain;

    bool active;
};
```

Properties:

- Position in 3D space
- Orientation
- Gain adjustment
- Activity state

---

# Control Interface

Rather than exposing hundreds of GObject properties, participant movement should be delivered via custom events.

Example:

```text
participant-update
{
    id = 5
    x = 12.3
    y = 4.5
    z = 0.0
}
```

Typical updates:

- Position changes
- Orientation changes
- Environment changes
- Gain changes

---

# Environment Model

## Presets

Examples:

```text
small-room
office
hallway
warehouse
church
cave
outdoors
forest
```

A preset contains:

```c
struct RoomPreset
{
    float rt60;
    float hf_absorption;
    float reflection_density;
};
```

---

# DSP Pipeline

For every speaker/listener pair:

```text
Distance
   |
ILD / ITD
   |
Early Reflections
   |
Contribution Buffer
```

After all contributions:

```text
Mix
 |
Shared Late Reverb
 |
Output
```

---

# Stage 1 - Distance Modelling

Distance determines:

- Gain reduction
- High frequency attenuation
- Direct-to-reflection ratio

Possible attenuation:

```c
gain = 1 / (1 + k * distance);
```

---

# Stage 2 - Localisation

Use inexpensive binaural cues.

## ITD

Interaural Time Difference.

Typical range:

```text
0 to 0.6 ms
```

## ILD

Interaural Level Difference.

Example:

```text
Speaker on left

Left  = 0 dB
Right = -8 dB
```

This provides convincing localisation with very little CPU cost.

---

# Stage 3 - Early Reflections

The key design conclusion was that early reflections contribute more realism than expensive HRTF processing.

A low-CPU approach uses several delayed taps.

Example:

```text
Tap 1 = 4 ms   -10 dB
Tap 2 = 9 ms   -14 dB
Tap 3 = 16 ms  -18 dB
Tap 4 = 24 ms  -22 dB
```

Implementation:

```c
out = direct
    + tap4ms  * 0.32
    + tap9ms  * 0.20
    + tap16ms * 0.13
    + tap24ms * 0.08;
```

Advantages:

- Very low CPU usage
- No convolution
- No feedback loops
- Strong perception of room size

---

# Reflection Positioning

Reflections should become progressively less directional.

Example:

Direct:

```text
L = 0.8
R = 0.4
```

Reflections:

```text
L = 0.6 R = 0.4
L = 0.55 R = 0.45
L = 0.52 R = 0.48
L = 0.50 R = 0.50
```

This increases realism and room perception.

---

# Shared Late Reverb

Late reverb is largely diffuse and difficult for listeners to localise.

Therefore one shared reverb engine should be used.

```text
Participant Contributions
           |
        Mix Bus
           |
      Late Reverb
           |
        Outputs
```

Benefits:

- Very large CPU saving
- Consistent acoustic space
- Scales well with participant count

---

# Room Presets

## Office

```text
4 ms  -8 dB
7 ms  -12 dB
11 ms -15 dB
```

## Cave

```text
80 ms  -8 dB
140 ms -12 dB
220 ms -15 dB
430 ms -20 dB
```

## Outdoors

```text
Very weak reflections
Minimal reverb
Distance attenuation only
```

The timing and density of reflections create most of the perceptual difference between spaces.

---

# Future Geometry Support

Future versions could support explicit room geometry.

Example:

```json
{
  "walls": [
    {"x1":0,"y1":0,"x2":10,"y2":0},
    {"x1":10,"y1":0,"x2":10,"y2":8},
    {"x1":10,"y1":8,"x2":0,"y2":8},
    {"x1":0,"y1":8,"x2":0,"y2":0}
  ]
}
```

Using the image-source method, first-order reflections could be generated dynamically.

---

# HRTF Strategy

The conclusion was not to start with HRTF.

Version 1 should focus on:

- Positioning
- Distance
- ITD
- ILD
- Early reflections
- Room presets
- Shared reverb

Later enhancements may use:

- libmysofa
- OpenAL Soft techniques
- SOFA datasets

for premium spatial rendering.

---

# Recommended MVP

## Features

- Multiple participants
- X/Y/Z positioning
- Listener orientation
- Distance attenuation
- ITD localisation
- ILD localisation
- Four-tap early reflection engine
- Shared late reverb
- Environment presets
- N-minus-1 mixing

## Explicitly Deferred

- Full HRTF convolution
- Occlusion modelling
- Wall diffraction
- Ray tracing
- Per-participant reverb instances

This MVP should already provide a convincing illusion of participants occupying a common acoustic environment while remaining computationally practical for a server-side MCU.

---

# DSP Core Architecture and Computational Complexity

## Design Goals

The DSP engine should:

- Scale to tens or hundreds of participants.
- Support personalised listener mixes.
- Support virtual spaces and environmental acoustics.
- Minimise duplicated calculations.
- Remain CPU-efficient enough for server-side MCU deployment.

The primary challenge is that every listener hears a different version of every speaker.

---

# Naïve Approach

A naïve implementation performs complete processing for every speaker/listener pair:

```text
Speaker A -> Listener A
Speaker A -> Listener B
Speaker A -> Listener C

Speaker B -> Listener A
Speaker B -> Listener B
Speaker B -> Listener C

...
```

Complexity:

```text
O(S × L)
```

where:

- S = speakers
- L = listeners

For 50 participants:

```text
50 × 49

= 2450 signal paths
```

This quickly becomes expensive if each path includes:

- Position calculations
- Reflection generation
- HRTF
- EQ
- Reverb

---

# Recommended Processing Pipeline

The recommended architecture separates calculations into:

## Stage 1: Speaker Processing

Performed once per speaker.

```text
Input Audio
    |
Distance EQ
    |
Voice Processing
    |
Delay Buffer
```

Output:

```text
speaker_buffer[n]
```

stored for later reuse.

Complexity:

```text
O(S)
```

---

## Stage 2: Acoustic Path Generation

For each speaker/listener pair:

```text
Geometry
    |
Gain
ILD
ITD
Reflection Taps
```

This stage generates coefficients rather than processing audio repeatedly.

Example:

```c
struct AcousticPath
{
    float gain;

    float left_gain;
    float right_gain;

    int itd_samples;

    Reflection taps[4];
};
```

These coefficients change only when:

- Listener moves
- Speaker moves
- Room changes

NOT every audio block.

---

# Coefficient Caching

Most participant positions change much more slowly than the audio stream.

Audio:

```text
48000 samples/sec
```

Position updates:

```text
10-50 updates/sec
```

Therefore:

```text
Calculate acoustics
Store coefficients
Reuse during mixing
```

This dramatically reduces CPU usage.

---

# Audio Mixing Pass

Processing block:

```c
for each listener
{
    clear_output();

    for each speaker
    {
        if (speaker == listener)
            continue;

        apply_cached_path();
        accumulate();
    }
}
```

No geometry calculations occur during the audio pass.

Only:

```text
gain
delay taps
mixing
```

---

# Delay Buffer Design

Each speaker owns a circular buffer:

```c
float delay_buffer[MAX_DELAY];
```

Typical size:

```text
100 ms
```

at 48 kHz:

```text
4800 samples
```

Reflections simply read older samples:

```c
direct = buffer[now];

r1 = buffer[now - 192];
r2 = buffer[now - 432];
r3 = buffer[now - 768];
r4 = buffer[now - 1152];
```

No FFTs.

No convolution.

Just memory reads and multiply-adds.

---

# Reflection Engine

Each environment supplies a reflection preset.

Example:

```c
struct Reflection
{
    int delay_samples;
    float gain;

    float left_pan;
    float right_pan;
};
```

Office:

```text
4ms
7ms
11ms
```

Hall:

```text
15ms
25ms
40ms
70ms
```

Cave:

```text
80ms
140ms
220ms
430ms
```

These are loaded once and reused.

---

# Shared Reverb Bus

The most expensive component should be shared.

```text
Direct Paths
      |
      +----> Listener Mix
      |
      +----> Reverb Send
```

One reverb instance:

```text
all_speakers_mix
       |
   reverb
       |
 diffuse_tail
```

This avoids:

```text
participants × reverb_instances
```

which would be prohibitively expensive.

---

# Listener Orientation

Each listener stores:

```c
position
yaw
pitch
roll
```

Incoming speaker locations are transformed into listener-relative coordinates.

Example:

```text
World:
Alice  (10,0)
Bob    (20,0)

Alice turns 180°
```

No audio processing changes.

Only:

```text
pan coefficients
ITD coefficients
```

are recalculated.

---

# Optional HRTF Layer

Future versions may support:

```text
libmysofa
SOFA datasets
```

Architecture:

```text
Direct Path
     |
Simple ILD/ITD
     |
Optional HRTF
     |
Mix
```

Possible optimisation:

Apply HRTF only to:

```text
nearest speakers
or
loudest active speakers
```

while distant participants use simple stereo positioning.

This keeps CPU cost bounded.

---

# Approximate Scaling

For N participants:

Geometry updates:

```text
O(N²)
```

but only on movement events.

Audio mixing:

```text
O(N²)
```

which is unavoidable for personalised mixes.

Reflection generation:

```text
O(1)
```

per path using fixed tap counts.

Reverb:

```text
O(1)
```

using a single shared engine.

This provides practical scalability while preserving convincing spatial acoustics.

---

# Version 1 DSP Roadmap

Phase 1:

- N-minus-1 mixing
- Distance attenuation
- Stereo panning
- ILD
- ITD

Phase 2:

- Early reflection engine
- Environment presets
- Shared reverb

Phase 3:

- Room geometry
- Image-source reflections

Phase 4:

- HRTF
- Occlusion
- Material modelling

The key design principle is:

> Perform expensive acoustic calculations only when geometry changes, and keep the real-time audio path limited to gain, delay, interpolation and accumulation.
