using System.Buffers.Binary;
using System.Text;

namespace Hush.E2E.Tests;

internal sealed record WavPcmFile(
    string FilePath,
    int AudioFormat,
    int Channels,
    int SampleRate,
    int BitsPerSample,
    byte[] PcmData)
{
    public static WavPcmFile Read(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        if (bytes.Length < 44)
            throw new InvalidDataException($"WAV file is too short: {filePath}");

        AssertChunk(bytes, 0, "RIFF", filePath);
        AssertChunk(bytes, 8, "WAVE", filePath);

        int offset = 12;
        int? audioFormat = null;
        int? channels = null;
        int? sampleRate = null;
        int? bitsPerSample = null;
        byte[]? pcmData = null;

        while (offset + 8 <= bytes.Length)
        {
            var chunkId = Encoding.ASCII.GetString(bytes, offset, 4);
            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            if (chunkSize < 0 || offset + 8 + chunkSize > bytes.Length)
                throw new InvalidDataException($"Invalid WAV chunk '{chunkId}' in {filePath}.");

            var chunkStart = offset + 8;
            if (chunkId == "fmt ")
            {
                if (chunkSize < 16)
                    throw new InvalidDataException($"Invalid WAV fmt chunk in {filePath}.");

                audioFormat = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(chunkStart, 2));
                channels = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(chunkStart + 2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(chunkStart + 4, 4));
                bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(chunkStart + 14, 2));
            }
            else if (chunkId == "data")
            {
                pcmData = bytes.AsSpan(chunkStart, chunkSize).ToArray();
            }

            offset = chunkStart + chunkSize + (chunkSize % 2);
        }

        return new WavPcmFile(
            filePath,
            audioFormat ?? throw new InvalidDataException($"WAV fmt chunk missing in {filePath}."),
            channels ?? throw new InvalidDataException($"WAV channel count missing in {filePath}."),
            sampleRate ?? throw new InvalidDataException($"WAV sample rate missing in {filePath}."),
            bitsPerSample ?? throw new InvalidDataException($"WAV bit depth missing in {filePath}."),
            pcmData ?? throw new InvalidDataException($"WAV data chunk missing in {filePath}."));
    }

    private static void AssertChunk(byte[] bytes, int offset, string expected, string filePath)
    {
        var actual = Encoding.ASCII.GetString(bytes, offset, expected.Length);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidDataException($"Expected WAV chunk '{expected}' at offset {offset} in {filePath}, found '{actual}'.");
    }
}
