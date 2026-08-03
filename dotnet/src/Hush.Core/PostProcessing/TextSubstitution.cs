// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

namespace Hush.Core.PostProcessing;

/// <summary>
/// A case-insensitive, whole-word find-and-replace rule applied to
/// committed dictation text before it is typed into the focused app.
/// </summary>
public sealed record TextSubstitution
{
    public required string Match { get; init; }
    public required string Replace { get; init; }
}
