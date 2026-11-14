#!/usr/bin/env python3
"""Renders the speaking bots' voice clips (scripts/loadtest-linux-x64/voices) with Piper.

The load tool joins a bot's name clip ("Bot, 3."), and in spatial rooms a direction clip ("North-west, behind, to
your left.") and a distance clip ("15 metres."), into its line, so no speech engine is needed where the load tool runs (Linux or Windows). Re-run this
only to change the voices or phrases; the clips are committed.

Needs Piper (pip install piper-tts, in a virtual environment) and the voice models, e.g.:
    python -m piper.download_voices --data-dir ~/piper-voices \\
        en_GB-jenny_dioco-medium en_GB-cori-high en_US-libritts_r-medium
    python scripts/render-voices.py --models ~/piper-voices
and GStreamer's gst-launch-1.0 with opusenc (to encode Ogg Opus).

Only voices whose licences allow distributing generated audio are used: see voices/CREDITS.md.
"""

import argparse
import json
import pathlib
import shutil
import subprocess
import tempfile
import wave

from piper import PiperVoice, SynthesisConfig

# (id, model, LibriTTS-R speaker or None, credit). Order matters: bot N gets voice (N - 1) % len(VOICES).
VOICES = [
    ("jenny", "en_GB-jenny_dioco-medium", None, 'Jenny (Dioco), https://github.com/dioco-group/jenny-tts-dataset'),
    ("cori", "en_GB-cori-high", None, "Cori, Piper voice trained on public-domain LibriVox recordings"),
    ("libritts-0", "en_US-libritts_r-medium", 0, "LibriTTS-R speaker 0 (CC BY 4.0)"),
    ("libritts-40", "en_US-libritts_r-medium", 40, "LibriTTS-R speaker 40 (CC BY 4.0)"),
    ("libritts-120", "en_US-libritts_r-medium", 120, "LibriTTS-R speaker 120 (CC BY 4.0)"),
    ("libritts-300", "en_US-libritts_r-medium", 300, "LibriTTS-R speaker 300 (CC BY 4.0)"),
    ("libritts-550", "en_US-libritts_r-medium", 550, "LibriTTS-R speaker 550 (CC BY 4.0)"),
    ("libritts-850", "en_US-libritts_r-medium", 850, "LibriTTS-R speaker 850 (CC BY 4.0)"),
]

NAMES = 32  # a room holds at most 32 participants

# Octants anticlockwise from east, as the load tool numbers them (Program.Spoken): compass, then relative to a
# listener facing east (the browser client's starting direction).
DIRECTIONS = [
    ("east", "East, in front of you."),
    ("north-east", "North-east, in front, to your left."),
    ("north", "North, on your left."),
    ("north-west", "North-west, behind, to your left."),
    ("west", "West, behind you."),
    ("south-west", "South-west, behind, to your right."),
    ("south", "South, on your right."),
    ("south-east", "South-east, in front, to your right."),
]


# Ring distances: bots stand on rings 10 m apart (5, 15, 25, 35 m by default); 5 m steps cover other --radius values.
DISTANCES = list(range(5, 61, 5))


CREDITS = """# Bot voices: credits and licences

Rendered with [Piper](https://github.com/OHF-Voice/piper1-gpl) by `scripts/render-voices.py`. Piper's own licence
(GPL-3.0) does not cover audio it produces; each voice's training data sets the terms for these clips:

| Folder | Piper voice | Data and licence |
|---|---|---|
| `jenny` | en_GB-jenny_dioco-medium | Voice: **Jenny (Dioco)** ([dataset](https://github.com/dioco-group/jenny-tts-dataset)): commercial use permitted; attribution not required for distributed clips (given here anyway). Fine-tuned from the Hi-Fi TTS reader 92 model (CC BY 4.0). |
| `cori` | en_GB-cori-high | Trained on public-domain [LibriVox](https://librivox.org) recordings. |
| `libritts-*` | en_US-libritts_r-medium, speakers 0, 40, 120, 300, 550, 850 | [LibriTTS-R](http://www.openslr.org/141/) (CC BY 4.0), derived from public-domain LibriVox audiobooks. |

Voices whose data do not allow redistribution were not used (e.g. alan, lessac, ryan, hfc_male/hfc_female).
"""


def name_text(number: int) -> str:
    # The comma makes a pause between "Bot" and the number (flite ran them together; kept for the same rhythm).
    return f"Bot, {number}."


def render(voice: PiperVoice, speaker, text: str, out: pathlib.Path, scratch: pathlib.Path) -> None:
    wav_path = scratch / "clip.wav"
    with wave.open(str(wav_path), "wb") as wav:
        voice.synthesize_wav(text, wav, syn_config=SynthesisConfig(speaker_id=speaker))
    # Ogg Opus, mono, 48 kHz: a tenth of the WAV size and decoded by GStreamer everywhere.
    subprocess.run(
        ["gst-launch-1.0", "-q", "filesrc", f"location={wav_path}", "!", "wavparse", "!", "audioconvert", "!",
         "audioresample", "!", "audio/x-raw,rate=48000,channels=1", "!", "opusenc", "bitrate=32000", "!", "oggmux",
         "!", "filesink", f"location={out}"],
        check=True)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--models", required=True, type=pathlib.Path, help="directory with the Piper .onnx voices")
    parser.add_argument("--out", type=pathlib.Path,
                        default=pathlib.Path(__file__).resolve().parent / "loadtest-linux-x64" / "voices")
    parser.add_argument("--only", choices=["names", "directions", "distances"],
                        help="re-render just these clips in the existing set (keeps the rest unchanged in git)")
    args = parser.parse_args()

    if args.only is None:
        if args.out.exists():
            shutil.rmtree(args.out)
        args.out.mkdir(parents=True)
    loaded = {}
    with tempfile.TemporaryDirectory() as scratch_dir:
        scratch = pathlib.Path(scratch_dir)
        for voice_id, model, speaker, _ in VOICES:
            if model not in loaded:
                loaded[model] = PiperVoice.load(args.models / f"{model}.onnx")
            folder = args.out / voice_id
            folder.mkdir(exist_ok=True)
            if args.only in (None, "names"):
                for number in range(1, NAMES + 1):
                    render(loaded[model], speaker, name_text(number), folder / f"name-{number:02}.ogg", scratch)
            if args.only in (None, "directions"):
                for key, text in DIRECTIONS:
                    render(loaded[model], speaker, text, folder / f"dir-{key}.ogg", scratch)
            if args.only in (None, "distances"):
                for metres in DISTANCES:
                    render(loaded[model], speaker, f"{metres} metres.", folder / f"dist-{metres:02}.ogg", scratch)
            print(f"{voice_id}: {args.only or 'all clips'} rendered")

    manifest = {
        "format": 1,
        "names": NAMES,
        "directions": [key for key, _ in DIRECTIONS],
        "distances": DISTANCES,
        "voices": [{"id": v, "model": m, "speaker": s, "credit": c} for v, m, s, c in VOICES],
    }
    (args.out / "voices.json").write_text(json.dumps(manifest, indent=2) + "\n")
    (args.out / "CREDITS.md").write_text(CREDITS)


if __name__ == "__main__":
    main()
