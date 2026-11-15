using System.Globalization;
using AiChat.Models;

namespace AiChat.Services;

/// <summary>One line of the user's price table. The first row whose pattern is contained in the model name wins.</summary>
public class PriceRow
{
    /// <summary>Part of the model name (case-insensitive). Empty = matches every model.</summary>
    public string Pattern { get; set; } = "";
    /// <summary>Price per 1,000,000 input tokens.</summary>
    public double InputPerM { get; set; }
    /// <summary>Price per 1,000,000 output tokens.</summary>
    public double OutputPerM { get; set; }
    /// <summary>Context window in thousands of tokens (0 = unknown).</summary>
    public int ContextK { get; set; }
}

public class CostOptions
{
    /// <summary>off | message | total | both</summary>
    public string Mode { get; set; } = "total";
    public string Currency { get; set; } = "$";
    /// <summary>Tokens counted for one image.</summary>
    public int ImageTokens { get; set; } = 800;
    /// <summary>Characters per token for Latin text.</summary>
    public double LatinCharsPerToken { get; set; } = 4;
    /// <summary>Characters per token for Persian / Arabic text.</summary>
    public double ArabicCharsPerToken { get; set; } = 2.2;
    public List<PriceRow> Prices { get; set; } = new();
}

public record MessageStat(int Tokens, int InputTokens, double? Cost);

public record ConversationStats(int ContextTokens, int TotalInput, int TotalOutput, double? Cost, int? ContextLimit,
    Dictionary<ChatMessage, MessageStat> PerMessage);

/// <summary>Rough token counting and cost (an estimate: the real tokenizer of each model differs).</summary>
public static class TokenCost
{
    public static int Estimate(string? text, CostOptions o)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        double arabic = 0, cjk = 0, other = 0;
        foreach (var ch in text)
        {
            if ((ch >= 0x0600 && ch <= 0x06FF) || (ch >= 0x0750 && ch <= 0x077F) || (ch >= 0xFB50 && ch <= 0xFDFF) || (ch >= 0xFE70 && ch <= 0xFEFF))
                arabic++;
            else if ((ch >= 0x3040 && ch <= 0x30FF) || (ch >= 0x4E00 && ch <= 0x9FFF) || (ch >= 0xAC00 && ch <= 0xD7AF))
                cjk++;
            else other++;
        }
        var t = arabic / Math.Max(0.5, o.ArabicCharsPerToken) + cjk + other / Math.Max(0.5, o.LatinCharsPerToken);
        return (int)Math.Ceiling(t);
    }

    public static int ForAttachment(Attachment a, CostOptions o)
    {
        if (a.Excluded) return 0;
        return a.Kind switch
        {
            "image" => Math.Max(0, o.ImageTokens),
            "text" => (int)Math.Ceiling(a.Size / Math.Max(0.5, o.LatinCharsPerToken)),
            "pdf" => (int)Math.Ceiling(a.Size / 8.0),
            _ => 0   // video, audio and other files are not sent to chat models
        };
    }

    public static int ForMessage(ChatMessage m, CostOptions o) =>
        Estimate(m.Role == "assistant" ? m.Content : m.Content, o) + m.Attachments.Sum(a => ForAttachment(a, o)) + 4; // + role/format overhead

    public static PriceRow? FindPrice(CostOptions o, string? model)
    {
        model ??= "";
        return o.Prices.FirstOrDefault(p => p.Pattern.Trim().Length > 0 && model.Contains(p.Pattern.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? o.Prices.FirstOrDefault(p => p.Pattern.Trim().Length == 0);
    }

    static double? Price(PriceRow? row, int inTokens, int outTokens) =>
        row == null ? null : (inTokens * row.InputPerM + outTokens * row.OutputPerM) / 1_000_000.0;

    /// <param name="baseTokens">System prompt, project instructions and project files (sent with every request).</param>
    /// <param name="currentModel">Used for the context window and when a message has no model name.</param>
    public static ConversationStats Analyze(IReadOnlyList<ChatMessage> messages, int baseTokens, string? currentModel, CostOptions o)
    {
        var per = new Dictionary<ChatMessage, MessageStat>();
        var history = baseTokens;
        int totalIn = 0, totalOut = 0;
        double? cost = null;

        foreach (var m in messages)
        {
            var t = ForMessage(m, o);
            if (m.Role == "assistant" && (!string.IsNullOrEmpty(m.Content) || m.Attachments.Count > 0))
            {
                var c = Price(FindPrice(o, string.IsNullOrEmpty(m.Model) ? currentModel : m.Model), history, t);
                per[m] = new MessageStat(t, history, c);
                totalIn += history;
                totalOut += t;
                if (c != null) cost = (cost ?? 0) + c;
            }
            else per[m] = new MessageStat(t, 0, null);
            history += t;
        }

        int? limit = FindPrice(o, currentModel) is { ContextK: > 0 } row ? row.ContextK * 1000 : null;
        return new ConversationStats(history, totalIn, totalOut, cost, limit, per);
    }

    public static string FormatTokens(int n) =>
        n < 1000 ? n.ToString(CultureInfo.InvariantCulture)
        : n < 100_000 ? (n / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k"
        : (n / 1000).ToString(CultureInfo.InvariantCulture) + "k";

    public static string FormatMoney(double v, string currency) =>
        currency + (v >= 1 ? v.ToString("0.00", CultureInfo.InvariantCulture)
                  : v >= 0.01 ? v.ToString("0.000", CultureInfo.InvariantCulture)
                  : v.ToString("0.0000", CultureInfo.InvariantCulture));
}
