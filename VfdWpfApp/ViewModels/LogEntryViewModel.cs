using System;
using VfdWpfApp.Models;

namespace VfdWpfApp.ViewModels;

public sealed class LogEntryViewModel
{
    public string TimeLocal { get; }
    public string Direction { get; }
    public string Hex { get; }
    public string Parsed { get; }
    public string Result { get; }
    public string RttMs { get; }

    public LogEntryViewModel(CommLogRecord r)
    {
        TimeLocal = r.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff");
        Direction = r.Direction.ToString();
        Hex = r.Hex;
        Parsed = r.Parsed;
        Result = r.Result;
        RttMs = r.RttMs is null ? "" : r.RttMs.Value.ToString("0.0");
    }
}
