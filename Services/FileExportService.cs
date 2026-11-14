using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Storage;

namespace AiChat.Services;

/// <summary>
/// Saves a file for the user.
/// Windows: the standard "Save as" dialog.
/// Android: the system share sheet (Save to Drive / Files, send to another app, ...).
/// </summary>
public class FileExportService
{
    /// <returns>A short description of where it went, or null if the user cancelled.</returns>
    public async Task<string?> SaveAsync(string suggestedName, byte[] data)
    {
        suggestedName = Sanitize(suggestedName);

#if WINDOWS
        var picker = new Windows.Storage.Pickers.FileSavePicker();

        var window = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView
            as Microsoft.Maui.MauiWinUIWindow;
        if (window == null) throw new InvalidOperationException("The app window was not found.");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, window.WindowHandle);

        var ext = Path.GetExtension(suggestedName);
        if (string.IsNullOrEmpty(ext)) ext = ".txt";

        picker.SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedName);
        picker.FileTypeChoices.Add(ext.TrimStart('.').ToUpperInvariant() + " file", new List<string> { ext });

        var file = await picker.PickSaveFileAsync();
        if (file == null) return null;

        await Windows.Storage.FileIO.WriteBytesAsync(file, data);
        return file.Path;
#else
        var path = Path.Combine(FileSystem.CacheDirectory, suggestedName);
        await File.WriteAllBytesAsync(path, data);
        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = suggestedName,
            File = new ShareFile(path)
        });
        return suggestedName;
#endif
    }

    static string Sanitize(string name)
    {
        name = Path.GetFileName(name);
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "file.txt" : name;
    }

    public static string ExtensionFromMime(string? mime) => (mime ?? "").ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/svg+xml" => ".svg",
        "video/mp4" => ".mp4",
        "video/webm" => ".webm",
        "video/quicktime" => ".mov",
        "audio/mpeg" or "audio/mp3" => ".mp3",
        "audio/wav" or "audio/x-wav" or "audio/wave" => ".wav",
        "audio/ogg" => ".ogg",
        "audio/flac" => ".flac",
        "audio/mp4" or "audio/aac" => ".m4a",
        "model/gltf-binary" => ".glb",
        "model/gltf+json" => ".gltf",
        "model/obj" => ".obj",
        "model/stl" or "application/sla" => ".stl",
        "application/zip" => ".zip",
        "application/json" => ".json",
        "text/plain" => ".txt",
        var m when m.StartsWith("image/") => ".png",
        _ => ".bin"
    };
}
