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
