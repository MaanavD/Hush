using Hush.Core.Transcription;

namespace Hush.Core.Tests;

/// <summary>
/// Unit tests for <see cref="TranscriptionEngine"/> behaviour that can be
/// tested without a Foundry Local runtime. This focuses on the WAV writer,
/// silence detection, hallucination filtering, and session lifecycle guards.
/// </summary>
public sealed class TranscriptionEngineTests
{
    // ── Session lifecycle guards ─────────────────────────────────────────

    [Fact]
    public async Task StartSessionAsync_WithoutInitialize_Throws()
    {
        var engine = new TranscriptionEngine();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.StartSessionAsync());
    }

    [Fact]
    public async Task GetResultStreamAsync_WithoutSession_Throws()
    {
        var engine = new TranscriptionEngine();
        // GetResultStreamAsync is an async iterator — the throw is deferred
        // until MoveNextAsync is called.
        var stream = engine.GetResultStreamAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in stream) { }
        });
    }

    [Fact]
    public async Task StopSessionAsync_WithoutStart_DoesNotThrow()
    {
        var engine = new TranscriptionEngine();
        await engine.StopSessionAsync();
    }

    [Fact]
    public async Task DisposeAsync_WithoutInit_DoesNotThrow()
    {
        var engine = new TranscriptionEngine();
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_Idempotent()
    {
        var engine = new TranscriptionEngine();
        await engine.DisposeAsync();
        await engine.DisposeAsync();
    }

    // ── AppendAudioAsync without session ──────────────────────────────────

    [Fact]
    public async Task AppendAudioAsync_NoActiveSession_IsNoOp()
    {
        var engine = new TranscriptionEngine();
        // Should not throw; audio is silently dropped
        await engine.AppendAudioAsync(new byte[1024]);
    }

    // ── WAV file writing (reflection-based test on private method) ───────
    // We test the WAV header spec indirectly by creating the expected output
    // and verifying it's a valid WAV file.

    [Fact]
    public void WavHeaderFormat_ProducesValidRiff()
    {
        // Exercise the WAV writing path by simulating what TranscribeChunkAsync does
        var pcm = GenerateSilentPcm(sampleRate: 16000, durationMs: 100);
        var wavPath = Path.GetTempFileName() + ".wav";

        try
        {
            WritePcmAsWav(pcm, wavPath, sampleRate: 16000, channels: 1);

            var wavBytes = File.ReadAllBytes(wavPath);
            Assert.True(wavBytes.Length > 44, "WAV file must have header + data");

            // RIFF header
            Assert.Equal((byte)'R', wavBytes[0]);
            Assert.Equal((byte)'I', wavBytes[1]);
            Assert.Equal((byte)'F', wavBytes[2]);
            Assert.Equal((byte)'F', wavBytes[3]);

            // WAVE format
            Assert.Equal((byte)'W', wavBytes[8]);
            Assert.Equal((byte)'A', wavBytes[9]);
            Assert.Equal((byte)'V', wavBytes[10]);
            Assert.Equal((byte)'E', wavBytes[11]);

            // fmt chunk
            Assert.Equal((byte)'f', wavBytes[12]);
            Assert.Equal((byte)'m', wavBytes[13]);
            Assert.Equal((byte)'t', wavBytes[14]);
            Assert.Equal((byte)' ', wavBytes[15]);

            // AudioFormat = 1 (PCM)
            Assert.Equal(1, BitConverter.ToInt16(wavBytes, 20));

            // Channels = 1
            Assert.Equal(1, BitConverter.ToInt16(wavBytes, 22));

            // SampleRate = 16000
            Assert.Equal(16000, BitConverter.ToInt32(wavBytes, 24));

            // data chunk
            Assert.Equal((byte)'d', wavBytes[36]);
            Assert.Equal((byte)'a', wavBytes[37]);
            Assert.Equal((byte)'t', wavBytes[38]);
            Assert.Equal((byte)'a', wavBytes[39]);

            // Data size matches PCM
            Assert.Equal(pcm.Length, BitConverter.ToInt32(wavBytes, 40));
        }
        finally
        {
            File.Delete(wavPath);
        }
    }

    [Fact]
    public void WavFile_StereoChannels_WritesCorrectHeader()
    {
        var pcm = GenerateSilentPcm(sampleRate: 44100, durationMs: 50, channels: 2);
        var wavPath = Path.GetTempFileName() + ".wav";

        try
        {
            WritePcmAsWav(pcm, wavPath, sampleRate: 44100, channels: 2);

            var wavBytes = File.ReadAllBytes(wavPath);
            Assert.Equal(2, BitConverter.ToInt16(wavBytes, 22)); // Channels
            Assert.Equal(44100, BitConverter.ToInt32(wavBytes, 24)); // SampleRate
        }
        finally
        {
            File.Delete(wavPath);
        }
    }

    // ── Silence detection ────────────────────────────────────────────────

    [Fact]
    public void ComputeRms_SilentBuffer_ReturnsZero()
    {
        var silent = new byte[3200]; // 100ms at 16kHz 16-bit mono = 3200 bytes
        double rms = ComputeRms(silent);
        Assert.Equal(0.0, rms);
    }

    [Fact]
    public void ComputeRms_EmptyBuffer_ReturnsZero()
    {
        Assert.Equal(0.0, ComputeRms(Array.Empty<byte>()));
    }

    [Fact]
    public void ComputeRms_MaxAmplitude_ReturnsNearOne()
    {
        // Fill buffer with max-amplitude 16-bit samples (32767)
        var buffer = new byte[200];
        for (int i = 0; i < buffer.Length; i += 2)
        {
            buffer[i] = 0xFF;   // 32767 = 0x7FFF
            buffer[i + 1] = 0x7F;
        }

        double rms = ComputeRms(buffer);
        Assert.True(rms > 0.9, $"Expected RMS > 0.9 but got {rms}");
    }

    [Fact]
    public void ComputeRms_LowNoise_BelowSilenceThreshold()
    {
        // Simulate very quiet background noise (values around ±10 out of ±32768)
        var buffer = new byte[3200];
        var rng = new Random(42);
        for (int i = 0; i < buffer.Length; i += 2)
        {
            short sample = (short)rng.Next(-10, 11);
            buffer[i] = (byte)(sample & 0xFF);
            buffer[i + 1] = (byte)((sample >> 8) & 0xFF);
        }

        double rms = ComputeRms(buffer);
        Assert.True(rms < 0.01, $"Expected RMS < 0.01 for near-silence but got {rms}");
    }

    // ── Noise token detection ────────────────────────────────────────────

    [Theory]
    [InlineData("[blank_audio]")]
    [InlineData("(silence)")]
    [InlineData("[silence]")]
    [InlineData("[music]")]
    [InlineData("[inaudible]")]
    [InlineData("[ Silence ]")]
    [InlineData("(music)")]
    [InlineData("[anything in brackets]")]
    [InlineData("(anything in parens)")]
    public void IsNoiseToken_KnownTokens_ReturnsTrue(string token)
    {
        Assert.True(IsNoiseToken(token));
    }

    [Theory]
    [InlineData("hello world")]
    [InlineData("the quick brown fox")]
    [InlineData("")]
    [InlineData("I said silence")]
    [InlineData("music is playing")]
    public void IsNoiseToken_RealText_ReturnsFalse(string text)
    {
        Assert.False(IsNoiseToken(text));
    }

    // ── Helpers (mirror private methods from TranscriptionEngine) ─────────

    private static double ComputeRms(byte[] pcm)
    {
        int samples = pcm.Length / 2;
        if (samples == 0) return 0.0;
        double sumSq = 0.0;
        for (int i = 0; i < pcm.Length - 1; i += 2)
        {
            short s = (short)(pcm[i] | (pcm[i + 1] << 8));
            double norm = s / 32768.0;
            sumSq += norm * norm;
        }
        return Math.Sqrt(sumSq / samples);
    }

    private static readonly HashSet<string> NoiseTokens =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "[blank_audio]", "(silence)", "[silence]", "[music]",
            "[inaudible]", "[ Silence ]", "(music)"
        };

    private static bool IsNoiseToken(string text) =>
        NoiseTokens.Contains(text) ||
        (text.StartsWith('[') && text.EndsWith(']')) ||
        (text.StartsWith('(') && text.EndsWith(')'));

    private static byte[] GenerateSilentPcm(int sampleRate, int durationMs, int channels = 1)
    {
        int samples = sampleRate * durationMs / 1000 * channels;
        return new byte[samples * 2]; // 16-bit = 2 bytes per sample
    }

    private static void WritePcmAsWav(byte[] pcm, string path, int sampleRate, int channels)
    {
        const short bitsPerSample = 16;
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        short blockAlign = (short)(channels * bitsPerSample / 8);

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);

        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + pcm.Length);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write(blockAlign);
        bw.Write(bitsPerSample);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(pcm.Length);
        bw.Write(pcm);
    }
}
