using System.Collections.Concurrent;
using System.Text;
using UglyToad.PdfPig;

namespace AiChat.Services;

/// <summary>Extracts the text layer of PDF files (scanned PDFs without text are not supported).</summary>
public static class PdfHelper
{
    public const int MaxChars = 300_000;
    static readonly ConcurrentDictionary<string, string> Cache = new();

    public static bool LooksLikePdf(byte[] data) =>
        data.Length > 4 && data[0] == '%' && data[1] == 'P' && data[2] == 'D' && data[3] == 'F';

    /// <summary>Returns the text of the PDF ("" when it has none). Cached per attachment id.</summary>
    public static string ExtractText(byte[] data, string? cacheKey = null)
    {
        if (cacheKey != null && Cache.TryGetValue(cacheKey, out var hit)) return hit;

        var sb = new StringBuilder();
        try
        {
            using var doc = PdfDocument.Open(data);
            foreach (var page in doc.GetPages())
            {
                var text = page.Text;
                if (string.IsNullOrWhiteSpace(text)) continue;
                sb.Append("\n[Page ").Append(page.Number).Append("]\n").Append(text.Trim()).Append('\n');
                if (sb.Length > MaxChars)
                {
                    sb.Length = MaxChars;
                    sb.Append("\n[... text truncated ...]");
                    break;
                }
            }
        }
        catch
        {
            return "";
        }

        var result = sb.ToString().Trim();
        if (cacheKey != null) Cache[cacheKey] = result;
        return result;
    }
}
