using System.Globalization;
using System.Text;

namespace ClaudeCodeExplorer.Services;

/// <summary>
/// Normalises text for search: lower-cased and with diacritics stripped, so a query like
/// "citta" matches "città" and case never matters. Used by the in-memory filter and when
/// preparing query terms.
/// </summary>
public static class TextNormalizer
{
    /// <summary>Lower-case and remove combining marks (accents) from <paramref name="s"/>.</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";

        var lowered = s.ToLowerInvariant();
        var decomposed = lowered.Normalize(NormalizationForm.FormD);

        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
