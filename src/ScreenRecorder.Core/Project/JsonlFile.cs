using System.Text.Json;

namespace ScreenRecorder.Core.Project;

/// <summary>Thread-safe append-only writer for JSON-lines track files.</summary>
public sealed class JsonlWriter<T> : IDisposable
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly StreamWriter _writer;
    private readonly object _gate = new();
    private bool _disposed;

    public JsonlWriter(string path)
    {
        _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024));
    }

    public void Write(T item)
    {
        string line = JsonSerializer.Serialize(item, Options);
        lock (_gate)
        {
            if (_disposed) return;
            _writer.WriteLine(line);
        }
    }

    public void Flush() { lock (_gate) if (!_disposed) _writer.Flush(); }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _writer.Dispose();
        }
    }
}

public static class JsonlReader
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Reads every complete line; a half-written last line (after a crash) is skipped.</summary>
    public static List<T> ReadAll<T>(string path)
    {
        var list = new List<T>();
        if (!File.Exists(path)) return list;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            try
            {
                if (JsonSerializer.Deserialize<T>(line, Options) is { } item) list.Add(item);
            }
            catch (JsonException) { }
        }
        return list;
    }
}
