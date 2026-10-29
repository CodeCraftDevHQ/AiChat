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
        catch
        {
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
        catch
        {
            // best effort
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
