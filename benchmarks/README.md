# Hush benchmarks

This directory contains shared benchmark entry points for comparing the .NET and Rust implementations against the same audio inputs.

The benchmark harness does not commit audio binaries. Pass an explicit WAV file path so both implementations measure the same input:

```powershell
.\benchmarks\run-comparison.ps1 -AudioFile C:\path\to\sample.wav
```

The intended comparison metrics are runtime initialization, model lookup/cache/download, model load, transcription latency, streaming latency, transcript length, and artifact size.
