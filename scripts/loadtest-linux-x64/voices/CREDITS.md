# Bot voices: credits and licences

Rendered with [Piper](https://github.com/OHF-Voice/piper1-gpl) by `scripts/render-voices.py`. Piper's own licence
(GPL-3.0) does not cover audio it produces; each voice's training data sets the terms for these clips:

| Folder | Piper voice | Data and licence |
|---|---|---|
| `jenny` | en_GB-jenny_dioco-medium | Voice: **Jenny (Dioco)** ([dataset](https://github.com/dioco-group/jenny-tts-dataset)): commercial use permitted; attribution not required for distributed clips (given here anyway). Fine-tuned from the Hi-Fi TTS reader 92 model (CC BY 4.0). |
| `cori` | en_GB-cori-high | Trained on public-domain [LibriVox](https://librivox.org) recordings. |
| `libritts-*` | en_US-libritts_r-medium, speakers 0, 40, 120, 300, 550, 850 | [LibriTTS-R](http://www.openslr.org/141/) (CC BY 4.0), derived from public-domain LibriVox audiobooks. |

Voices whose data do not allow redistribution were not used (e.g. alan, lessac, ryan, hfc_male/hfc_female).
