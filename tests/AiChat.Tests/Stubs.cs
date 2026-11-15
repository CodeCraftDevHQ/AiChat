using AiChat.Models;

namespace AiChat.Services;

// Test doubles for the MAUI-coupled services that the compiled sources reference.

public static class StorageService
{
    /// <summary>Attachment id -> bytes (filled by tests).</summary>
    public static readonly Dictionary<string, byte[]> Files = new();

    public static byte[]? ReadAttachmentSync(Attachment a) => Files.TryGetValue(a.Id, out var b) ? b : null;
}

public static class FileExportService
{
    public static string ExtensionFromMime(string? mime) => (mime ?? "").ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "video/mp4" => ".mp4",
        _ => ".bin"
    };
}
