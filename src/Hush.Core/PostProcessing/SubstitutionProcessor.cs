// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using System.Text.RegularExpressions;

namespace Hush.Core.PostProcessing;

public static class SubstitutionProcessor
{
    /// <summary>
    /// Applies all substitutions to <paramref name="text"/> in order.
    /// Matching is whole-word and case-insensitive. Returns processed text.
    /// </summary>
    public static string Apply(string text, IReadOnlyList<TextSubstitution> substitutions)
    {
        if (string.IsNullOrEmpty(text) || substitutions.Count == 0) return text;
        foreach (var sub in substitutions)
        {
            if (string.IsNullOrEmpty(sub.Match)) continue;
            var pattern = $@"\b{Regex.Escape(sub.Match)}\b";
            text = Regex.Replace(text, pattern, sub.Replace, RegexOptions.IgnoreCase);
        }
        return text;
    }
}
