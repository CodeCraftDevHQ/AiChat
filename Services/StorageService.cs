using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiChat.Models;
using Microsoft.Maui.Storage;

namespace AiChat.Services;

/// <summary>
/// Providers and conversations are saved as JSON in the app data folder.
/// API keys go to SecureStorage (Android Keystore / Windows data protection);
/// if SecureStorage is unavailable on a device, it falls back to Preferences.
/// </summary>
public class StorageService
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    readonly SemaphoreSlim _lock = new(1, 1);
    readonly LogService _log;

    public StorageService(LogService log) => _log = log;

    static string PathOf(string name) => Path.Combine(FileSystem.AppDataDirectory, name);

    // ---- providers ----

    public async Task<List<ProviderConfig>> LoadProvidersAsync()
    {
        var list = await ReadAsync<List<ProviderConfig>>("providers.json") ?? new();
        foreach (var p in list)
            p.ApiKey = await GetSecretAsync(p.Id);
        return list;
    }

    public async Task SaveProvidersAsync(List<ProviderConfig> list)
    {
        foreach (var p in list)
            await SetSecretAsync(p.Id, p.ApiKey);
        await WriteAsync("providers.json", JsonSerializer.Serialize(list, Json));
    }

    // the web-search API key lives in secure storage too
    public Task SaveSearchKeyAsync(string key) => SetSecretAsync("websearch", key);
    public Task<string> LoadSearchKeyAsync() => GetSecretAsync("websearch");

    // MCP servers: the list is plain JSON in the preferences, the headers (tokens) are secrets
    public async Task<List<McpServerConfig>> LoadMcpServersAsync()
    {
        List<McpServerConfig> list;
        try { list = JsonSerializer.Deserialize<List<McpServerConfig>>(Preferences.Default.Get("mcp_servers", "")) ?? new(); }
        catch { list = new(); }
        foreach (var m in list) m.Headers = await GetSecretAsync("mcp-" + m.Id);
        return list;
    }

    public async Task SaveMcpServersAsync(List<McpServerConfig> list)
    {
        foreach (var m in list) await SetSecretAsync("mcp-" + m.Id, m.Headers);
        Preferences.Default.Set("mcp_servers", JsonSerializer.Serialize(list));
    }

    public Task DeleteMcpSecretAsync(string id) => SetSecretAsync("mcp-" + id, "");

    public void DeleteSecret(string providerId)
    {
        var key = "apikey-" + providerId;
        try { SecureStorage.Default.Remove(key); } catch { }
        Preferences.Default.Remove(key);
    }

    // ---- conversations ----

    public Task<List<Conversation>?> LoadConversationsAsync() => ReadAsync<List<Conversation>>("conversations.json");

    // Not async on purpose: the list is serialized right now (on the caller's thread),
    // so later changes during streaming cannot corrupt the snapshot.
    public Task SaveConversationsAsync(List<Conversation> list) =>
        WriteAsync("conversations.json", JsonSerializer.Serialize(list, Json));

    // ---- projects ----

    public async Task<List<Project>> LoadProjectsAsync() =>
        await ReadAsync<List<Project>>("projects.json") ?? new();

    public Task SaveProjectsAsync(List<Project> list) =>
        WriteAsync("projects.json", JsonSerializer.Serialize(list, Json));

    // ---- model test results ----

    public Task<Dictionary<string, ModelTestResult>?> LoadModelTestsAsync() =>
        ReadAsync<Dictionary<string, ModelTestResult>>("model_tests.json");

    public Task SaveModelTestsAsync(Dictionary<string, ModelTestResult> snapshot) =>
        WriteAsync("model_tests.json", JsonSerializer.Serialize(snapshot, Json));

    // ---- user presets (saved API templates) ----

    public async Task<List<ProviderConfig>> LoadUserPresetsAsync() =>
        await ReadAsync<List<ProviderConfig>>("user_presets.json") ?? new();

    public Task SaveUserPresetsAsync(List<ProviderConfig> list) =>
        WriteAsync("user_presets.json", JsonSerializer.Serialize(list, Json));

    // ---- attachments ----

    static string AttachDir => Path.Combine(FileSystem.AppDataDirectory, "attachments");

    static string AttachPath(Attachment a) => Path.Combine(AttachDir, a.StoredName);

    public async Task<Attachment> SaveAttachmentAsync(string name, string mime, string kind, byte[] data)
    {
        Directory.CreateDirectory(AttachDir);
        var att = new Attachment
        {
            Name = name,
            Kind = kind,
            MimeType = mime,
            Size = data.LongLength
        };
        att.StoredName = att.Id + Path.GetExtension(name).ToLowerInvariant();
        await File.WriteAllBytesAsync(AttachPath(att), data);
        return att;
    }

    public static byte[]? ReadAttachmentSync(Attachment a)
    {
        try
        {
            var path = AttachPath(a);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<byte[]?> ReadAttachmentAsync(Attachment a)
    {
        try
        {
            var path = AttachPath(a);
            return File.Exists(path) ? await File.ReadAllBytesAsync(path) : null;
        }
        catch
        {
            return null;
        }
    }

    public void DeleteAttachments(IEnumerable<Attachment> list)
    {
        foreach (var a in list)
        {
            try { File.Delete(AttachPath(a)); } catch { }
        }
    }

    // ---- backup / restore ----

    static readonly string[] BackupFiles =
        { "providers.json", "conversations.json", "projects.json", "user_presets.json", "model_tests.json" };

    /// <summary>A zip with all data (providers, chats, projects, templates, attachments). API keys only when asked.</summary>
    public async Task<byte[]> CreateBackupAsync(bool includeKeys)
    {
        Dictionary<string, string>? keys = null;
        if (includeKeys)
        {
            keys = new();
            foreach (var p in await LoadProvidersAsync())
                if (!string.IsNullOrEmpty(p.ApiKey)) keys[p.Id] = p.ApiKey;
        }

        await _lock.WaitAsync();
        try
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                void AddText(string name, string text)
                {
                    using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open());
                    w.Write(text);
                }
                AddText("backup.json", JsonSerializer.Serialize(new { app = "AiChat", format = 1, created = DateTime.UtcNow }));
                foreach (var f in BackupFiles)
                    if (File.Exists(PathOf(f))) zip.CreateEntryFromFile(PathOf(f), f, CompressionLevel.Optimal);
                if (Directory.Exists(AttachDir))
                    foreach (var f in Directory.GetFiles(AttachDir))
                        zip.CreateEntryFromFile(f, "attachments/" + Path.GetFileName(f), CompressionLevel.Fastest);
                if (keys is { Count: > 0 }) AddText("keys.json", JsonSerializer.Serialize(keys));
            }
            return ms.ToArray();
        }
        finally { _lock.Release(); }
    }

    /// <summary>Replaces the current data with the backup. Returns an error text, or null on success.</summary>
    public async Task<string?> RestoreBackupAsync(Stream input)
    {
        try
        {
            using var buffer = new MemoryStream();
            await input.CopyToAsync(buffer);
            buffer.Position = 0;
            using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
            if (zip.GetEntry("backup.json") == null) return "NOT_BACKUP";

            Dictionary<string, string>? keys = null;
            await _lock.WaitAsync();
            try
            {
                Directory.CreateDirectory(AttachDir);
                foreach (var e in zip.Entries)
                {
                    if (e.Length == 0 && e.FullName.EndsWith('/')) continue;
                    var name = e.FullName.Replace('\\', '/');
                    if (name == "keys.json")
                    {
                        using var r = new StreamReader(e.Open());
                        keys = JsonSerializer.Deserialize<Dictionary<string, string>>(await r.ReadToEndAsync());
                    }
                    else if (BackupFiles.Contains(name))
                        await Extract(e, PathOf(name));
                    else if (name.StartsWith("attachments/"))
                    {
                        var file = Path.GetFileName(name); // never trust folders from the zip
                        if (file.Length > 0) await Extract(e, Path.Combine(AttachDir, file));
                    }
                }
            }
            finally { _lock.Release(); }

            if (keys != null)
                foreach (var (id, key) in keys) await SetSecretAsync(id, key);
            return null;
        }
        catch (InvalidDataException) { return "NOT_BACKUP"; }
        catch (Exception ex)
        {
            _log.Error("Restoring the backup failed", ex);
            return ex.Message;
        }

        static async Task Extract(ZipArchiveEntry e, string dest)
        {
            var tmp = dest + ".tmp";
            await using (var src = e.Open())
            await using (var dst = File.Create(tmp))
                await src.CopyToAsync(dst);
            File.Move(tmp, dest, true);
        }
    }

    // ---- helpers ----

    async Task<T?> ReadAsync<T>(string file)
    {
        try
        {
            var path = PathOf(file);
            if (!File.Exists(path)) return default;
            var text = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<T>(text, Json);
        }
        catch (Exception ex)
        {
            _log.Error($"Could not read {file}", ex);
            return default;
        }
    }

    async Task WriteAsync(string file, string json)
    {
        await _lock.WaitAsync();
        try
        {
            var path = PathOf(file);
            var tmp = path + ".tmp";
            await File.WriteAllTextAsync(tmp, json);
            File.Move(tmp, path, true);
        }
        catch (Exception ex)
        {
            _log.Error($"Could not save {file}", ex);
        }
        finally
        {
            _lock.Release();
        }
    }

    static async Task SetSecretAsync(string id, string value)
    {
        var key = "apikey-" + id;
        try
        {
            if (string.IsNullOrEmpty(value)) SecureStorage.Default.Remove(key);
            else await SecureStorage.Default.SetAsync(key, value);
            Preferences.Default.Remove(key);
        }
        catch
        {
            if (string.IsNullOrEmpty(value)) Preferences.Default.Remove(key);
            else Preferences.Default.Set(key, value);
        }
    }

    static async Task<string> GetSecretAsync(string id)
    {
        var key = "apikey-" + id;
        try
        {
            var v = await SecureStorage.Default.GetAsync(key);
            if (!string.IsNullOrEmpty(v)) return v;
        }
        catch
        {
            // fall through to Preferences
        }
        return Preferences.Default.Get(key, "");
    }
}
