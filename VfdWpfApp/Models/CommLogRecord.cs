namespace VfdWpfApp.Models;

public enum LogDirection
{
    TX,
    RX
}

public sealed record CommLogRecord(
    DateTimeOffset TimestampUtc,
    LogDirection Direction,
    string Hex,
    string Parsed,
    string Result,
    double? RttMs
);
