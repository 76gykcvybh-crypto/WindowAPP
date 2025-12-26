using System;
using System.IO;
using System.Text.Json;
using VfdWpfApp.Models;

namespace VfdWpfApp.Services;

public sealed class JsonlLogger : IDisposable
{
    private readonly object _lock = new();
    private readonly StreamWriter _writer;

    public string FilePath { get; }

    public JsonlLogger(string folderPath)
    {
        Directory.CreateDirectory(folderPath);
        FilePath = Path.Combine(folderPath, $"comm_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
        _writer = new StreamWriter(File.Open(FilePath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };
    }

    public void Log(CommLogRecord record)
    {
        lock (_lock)
        {
            string line = JsonSerializer.Serialize(record);
            _writer.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer.Dispose();
        }
    }
}
