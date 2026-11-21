using AiChat.Models;

namespace AiChat.Services;

/// <summary>One side of a "compare two models" run.</summary>
public class CompareSide
{
    public string ProviderId = "";
    public string Model = "";
    public string Content = "";
    public string? Error;
    public bool Running;
    public bool Finished;
    public DateTime Started;
    public TimeSpan Elapsed;
    /// <summary>Time until the first piece of text arrived.</summary>
    public TimeSpan? FirstToken;
    public List<Attachment> Attachments = new();
}

public static class CompareHelper
{
    /// <summary>The history both models get: the messages of the chat (if any) + the new prompt.</summary>
    public static List<ChatMessage> BuildHistory(IEnumerable<ChatMessage>? chat, string prompt, List<Attachment>? attachments = null)
    {
        var list = new List<ChatMessage>();
        if (chat != null)
            foreach (var m in chat)
                if (!string.IsNullOrEmpty(m.Content) || m.Attachments.Count > 0)
                    list.Add(m);
        list.Add(new ChatMessage { Role = "user", Content = prompt, Attachments = attachments ?? new() });
        return list;
    }

    /// <summary>"3.2 s" / "1:05" style duration.</summary>
    public static string FormatTime(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{t.TotalSeconds:0.0} s" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    /// <summary>Estimated cost of one answer (null when no price row matches).</summary>
    public static double? Cost(CostOptions o, string model, int inputTokens, int outputTokens)
    {
        var row = TokenCost.FindPrice(o, model);
        return row == null ? null : (inputTokens * row.InputPerM + outputTokens * row.OutputPerM) / 1_000_000.0;
    }

    /// <summary>Output tokens per second (0 when unknown).</summary>
    public static double TokensPerSecond(int tokens, TimeSpan elapsed) =>
        elapsed.TotalSeconds < 0.05 ? 0 : tokens / elapsed.TotalSeconds;
}
