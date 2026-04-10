// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

namespace Hush.Core.Session;

public interface ITranscriptBuffer
{
    /// <summary>Stores a completed session transcript. Thread-safe.</summary>
    void Push(string transcript);
    /// <summary>Returns the most recent transcript, or null if the buffer is empty.</summary>
    string? GetLatest();
    /// <summary>Returns up to <see cref="Capacity"/> transcripts, newest first.</summary>
    IReadOnlyList<string> GetAll();
    int Capacity { get; }
}
