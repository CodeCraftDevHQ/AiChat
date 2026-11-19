using System.Text;

namespace AiChat.Services;

/// <summary>Settings of the automatic summary of long chats (JSON in the preferences).</summary>
public class SummaryOptions
{
    /// <summary>Summarize older messages automatically when the chat gets long.</summary>
    public bool Enabled { get; set; }
    /// <summary>Start summarizing when the chat is longer than this many (estimated) tokens.</summary>
    public int TriggerTokens { get; set; } = 6000;
    /// <summary>The newest messages that are always kept word for word.</summary>
    public int KeepLast { get; set; } = 6;
    /// <summary>The summary should be at most this many words.</summary>
    public int MaxWords { get; set; } = 300;
    /// <summary>Own instructions for the summary (empty = the built-in ones).</summary>
    public string Prompt { get; set; } = "";
    /// <summary>API / model used for summarizing (empty = the one of the chat).</summary>
    public string ProviderId { get; set; } = "";
    public string Model { get; set; } = "";
}

public static class SummaryHelper
{
    /// <summary>A rough token count (about 3 characters per token works for English and Persian alike).</summary>
    public static int EstimateTokens(string? text) => string.IsNullOrEmpty(text) ? 0 : (text.Length + 2) / 3;

    /// <summary>
    /// Where the next summary ends: messages [upTo, cut) are summarized, the rest stays. The cut is moved back so the
    /// kept part starts with a user message. Returns upTo when there is nothing worth summarizing.
    /// </summary>
    public static int NextCut(IReadOnlyList<string> roles, int upTo, int keepLast)
    {
        upTo = Math.Clamp(upTo, 0, roles.Count);
        var cut = roles.Count - Math.Max(2, keepLast);
        while (cut > upTo && roles[cut] != "user") cut--;
        return cut - upTo >= 2 ? cut : upTo;
    }

    /// <summary>Is the part that would be sent (summary + messages after it) longer than the limit?</summary>
    public static bool IsTooLong(string? summary, IEnumerable<string> textsAfterSummary, int triggerTokens) =>
        EstimateTokens(summary) + textsAfterSummary.Sum(EstimateTokens) > Math.Max(500, triggerTokens);

    public const string DefaultInstructions =
        "You keep a running summary of a conversation between a user and an AI assistant. " +
        "Update the summary so it also covers the new messages. Keep: the user's goals, decisions, facts, names, numbers, " +
        "important code, commands and identifiers, open questions, and the user's preferences. " +
        "Drop small talk and repetition. Write compact notes in the language of the conversation.";

    public static string BuildPrompt(string? existingSummary, string newMessages, int maxWords, string? custom)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.IsNullOrWhiteSpace(custom) ? DefaultInstructions : custom.Trim());
        sb.AppendLine($"Use at most {Math.Clamp(maxWords, 50, 2000)} words. Output ONLY the updated summary, nothing else.");
        sb.AppendLine();
        sb.AppendLine("Current summary:");
        sb.AppendLine(string.IsNullOrWhiteSpace(existingSummary) ? "(none yet)" : existingSummary.Trim());
        sb.AppendLine();
        sb.AppendLine("New messages:");
        sb.Append(newMessages);
        return sb.ToString();
    }

    /// <summary>One message as plain text for the summary request.</summary>
    public static string MessageText(string role, string text, IEnumerable<string>? attachmentNames = null)
    {
        var sb = new StringBuilder(role == "user" ? "User: " : "Assistant: ");
        sb.Append(text.Trim());
        var names = attachmentNames?.ToList();
        if (names is { Count: > 0 }) sb.Append(" [attachments: ").Append(string.Join(", ", names)).Append(']');
        return sb.ToString();
    }

    /// <summary>The note added to the system prompt (empty when there is no summary).</summary>
    public static string SystemNote(string? summary) =>
        string.IsNullOrWhiteSpace(summary)
            ? ""
            : "Summary of the earlier part of this conversation (those messages are not repeated below):\n" + summary.Trim();
}
