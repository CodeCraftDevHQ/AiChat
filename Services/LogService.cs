using Microsoft.Maui.Storage;

namespace AiChat.Services;

/// <summary>
/// Small in-app log. Lines are kept in memory and mirrored to app.log so they survive a crash.
/// When the log would pass 1000 lines it is cleared automatically.
/// API keys and message contents are never written here.
/// </summary>
public class LogService
{
    public const int MaxLines = 1000;

    readonly object _gate = new();
    readonly List<string> _lines = new();
    readonly string _path;

    public event Action? Changed;

    public LogService()
    {
        _path = Path.Combine(FileSystem.AppDataDirectory, "app.log");
        try
        {
            if (File.Exists(_path))
                _lines.AddRange(File.ReadAllLines(_path).TakeLast(MaxLines - 1));
        }
        catch
        {
            // ignore: the log must never break the app
        }
    }

    public int Count
    {
        get { lock (_gate) return _lines.Count; }
    }

    public void Info(string message) => Add("INF", message);
    public void Warn(string message) => Add("WRN", message);

    public void Error(string message, Exception? ex = null) =>
        Add("ERR", ex == null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}");

    public string GetText()
    {
        lock (_gate) return string.Join("\n", _lines);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _lines.Clear();
            try { File.Delete(_path); } catch { }
        }
        Changed?.Invoke();
    }

    void Add(string level, string message)
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var parts = (message ?? string.Empty).Replace("\r", "").Split('\n');

        var batch = new List<string>();
        for (var i = 0; i < parts.Length; i++)
            batch.Add(i == 0 ? $"{stamp} [{level}] {parts[i]}" : "    " + parts[i]);

        lock (_gate)
        {
            if (_lines.Count + batch.Count > MaxLines)
            {
                _lines.Clear();
                try { File.Delete(_path); } catch { }
                var note = $"{stamp} [INF] Log reached {MaxLines} lines and was cleared automatically.";
                _lines.Add(note);
                try { File.AppendAllLines(_path, new[] { note }); } catch { }
            }

            _lines.AddRange(batch);
            try { File.AppendAllLines(_path, batch); } catch { }
        }

        Changed?.Invoke();
    }
}
