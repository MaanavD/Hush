# English Real-Time Transcription Test Set — Synthetic v1

This package contains 30 short English WAV files intended for real-time transcription testing.

## Audio format
- WAV, 16 kHz, mono, 16-bit PCM
- Each file is under 2 minutes
- Generated locally with espeak synthetic voices, then normalized with sox

## Included files
- `audio/*.wav`: audio clips
- `manifest.jsonl`: metadata and expected transcript
- `manifest.csv`: spreadsheet-friendly manifest

## Coverage
The set includes:
- short commands and questions
- filler words and self-corrections
- slow, medium, and fast speech
- technical/product terms
- names, numbers, dates, and measurements
- low-volume speech
- background-noise stress cases
- phone-bandwidth audio
- longer streaming clips

## Important note
These clips are synthetic, not real human recordings. They are useful for automated regression, streaming latency, endpointing, filler/correction handling, product terms, numbers, and noise/telephone stress tests. For final model quality validation, combine these with real human clips from Common Voice, LibriSpeech, TED-LIUM, or licensed conversational corpora.

## Suggested streaming metrics
- first partial latency
- first stable token latency
- finalization latency
- partial revision rate
- endpointing accuracy
- WER / CER
- number/date accuracy
- filler retention
