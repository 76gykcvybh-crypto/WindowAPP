using System;

namespace VfdWpfApp.Core;

/// <summary>
/// Modbus CRC16 (poly 0xA001), returns (lo, hi) order for RTU.
/// </summary>
public static class Crc16Modbus
{
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                bool lsb = (crc & 0x0001) != 0;
                crc >>= 1;
                if (lsb) crc ^= 0xA001;
            }
        }
        return crc;
    }

    public static bool Validate(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 4) return false;
        ushort expected = Compute(frame[..^2]);
        byte lo = (byte)(expected & 0xFF);
        byte hi = (byte)((expected >> 8) & 0xFF);
        return frame[^2] == lo && frame[^1] == hi;
    }

    public static void AppendCrc(Span<byte> buffer, int lengthWithoutCrc)
    {
        ushort crc = Compute(buffer[..lengthWithoutCrc]);
        buffer[lengthWithoutCrc] = (byte)(crc & 0xFF);           // CRC Lo
        buffer[lengthWithoutCrc + 1] = (byte)((crc >> 8) & 0xFF); // CRC Hi
    }
}
