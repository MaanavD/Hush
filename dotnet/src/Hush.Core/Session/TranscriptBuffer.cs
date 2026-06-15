// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

namespace Hush.Core.Session;

/// <summary>
/// Thread-safe ring buffer that retains the last <see cref="Capacity"/> session transcripts.
/// Newest entries are at the front; oldest are evicted when capacity is exceeded.
/// </summary>
public sealed class TranscriptBuffer : ITranscriptBuffer
{
    private readonly LinkedList<string> _buffer = new();
    private readonly object _lock = new();

    public int Capacity => 5;

    /// <inheritdoc/>
    public void Push(string transcript)
    {
        lock (_lock)
        {
            _buffer.AddFirst(transcript);
            while (_buffer.Count > Capacity)
                _buffer.RemoveLast();
        }
    }

    /// <inheritdoc/>
    public string? GetLatest()
    {
        lock (_lock)
        {
            return _buffer.Count > 0 ? _buffer.First!.Value : null;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> GetAll()
    {
        lock (_lock)
        {
            return [.. _buffer];
        }
    }
}
