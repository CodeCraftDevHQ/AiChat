using System.Text;

namespace AiChat.Services;

/// <summary>
/// Persian/Arabic-friendly text matching, the same rules as the in-chat search in app.js:
/// ignores case, diacritics, ZWNJ and tatweel, unifies ي/ی and ك/ک, and treats Persian/Arabic digits as Latin digits.
/// </summary>
public static class SearchText
{
    /// <summary>Normalized text plus, for every normalized char, the index of the original char.</summary>
    public static (string Text, int[] Map) Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return ("", Array.Empty<int>());

        var sb = new StringBuilder(s.Length);
        var map = new List<int>(s.Length);

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];

            // ZWNJ, ZWJ, LRM, RLM, tatweel, Arabic diacritics
            if (c is '‌' or '‍' or '‎' or '‏' or 'ـ' or 'ٰ') continue;
            if (c >= 'ً' && c <= 'ٟ') continue;

            if (c is 'ي' or 'ى') c = 'ی';
            else if (c == 'ك') c = 'ک';
            else if (c == 'ة') c = 'ه';
            else if (c is 'أ' or 'إ' or 'ٱ') c = 'ا';
            else if (c >= '۰' && c <= '۹') c = (char)('0' + (c - '۰'));
            else if (c >= '٠' && c <= '٩') c = (char)('0' + (c - '٠'));

            sb.Append(char.ToLowerInvariant(c));
            map.Add(i);
        }

        return (sb.ToString(), map.ToArray());
    }

    public static string NormalizeQuery(string? query) => Normalize(query).Text.Trim();

    /// <summary>Number of non-overlapping occurrences of an already normalized query.</summary>
    public static int Count(string? text, string normQuery)
    {
        if (string.IsNullOrEmpty(normQuery)) return 0;
        var (norm, _) = Normalize(text);

        var count = 0;
        var from = 0;
        int idx;
        while ((idx = norm.IndexOf(normQuery, from, StringComparison.Ordinal)) >= 0)
        {
            count++;
            from = idx + normQuery.Length;
        }
        return count;
    }

    /// <summary>The first match with some text around it, split in (before, match, after) for highlighting.</summary>
    public static (string Before, string Match, string After)? Snippet(string? text, string normQuery)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(normQuery)) return null;

        var (norm, map) = Normalize(text);
        var idx = norm.IndexOf(normQuery, StringComparison.Ordinal);
        if (idx < 0) return null;

        var start = map[idx];
        var end = map[idx + normQuery.Length - 1] + 1;

        var from = Math.Max(0, start - 28);
        var to = Math.Min(text.Length, end + 56);

        string Flat(string part) => part.Replace('\r', ' ').Replace('\n', ' ');

        var before = (from > 0 ? "…" : "") + Flat(text[from..start]);
        var match = Flat(text[start..end]);
        var after = Flat(text[end..to]) + (to < text.Length ? "…" : "");
        return (before, match, after);
    }
}
