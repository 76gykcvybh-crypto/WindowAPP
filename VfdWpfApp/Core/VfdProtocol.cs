using System;

namespace VfdWpfApp.Core;

public static class VfdProtocol
{
    public const byte DeviceAddress = 0x01;

    public static byte[] BuildReadRequest(ushort startAddress, ushort wordCount)
    {
        // [Addr][0x03][StartHi][StartLo][CountHi][CountLo][CrcLo][CrcHi]
        Span<byte> buf = stackalloc byte[8];
        buf[0] = DeviceAddress;
        buf[1] = 0x03;
        buf[2] = (byte)(startAddress >> 8);
        buf[3] = (byte)(startAddress & 0xFF);
        buf[4] = (byte)(wordCount >> 8);
        buf[5] = (byte)(wordCount & 0xFF);
        Crc16Modbus.AppendCrc(buf, 6);
        return buf.ToArray();
    }

    public static byte[] BuildWriteSingleRequest(ushort address, ushort value)
    {
        // [Addr][0x06][AddrHi][AddrLo][ValHi][ValLo][CrcLo][CrcHi]
        Span<byte> buf = stackalloc byte[8];
        buf[0] = DeviceAddress;
        buf[1] = 0x06;
        buf[2] = (byte)(address >> 8);
        buf[3] = (byte)(address & 0xFF);
        buf[4] = (byte)(value >> 8);
        buf[5] = (byte)(value & 0xFF);
        Crc16Modbus.AppendCrc(buf, 6);
        return buf.ToArray();
    }

    public static bool TryParseFrameLength(ReadOnlySpan<byte> buffer, out int frameLen)
    {
        frameLen = 0;
        if (buffer.Length < 2) return false;

        if (buffer[0] != DeviceAddress) return false;

        byte fc = buffer[1];
        if (fc == 0x06)
        {
            frameLen = 8;
            return buffer.Length >= frameLen;
        }

        if (fc == 0x03)
        {
            if (buffer.Length < 3) return false;
            int byteCount = buffer[2];
            frameLen = 3 + byteCount + 2;
            return buffer.Length >= frameLen;
        }

        return false;
    }
}

public sealed class FrameDecoder
{
    private readonly byte[] _buffer = new byte[4096];
    private int _len;

    public int BufferedLength => _len;

    public void Clear() => _len = 0;

    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;

        if (_len + data.Length > _buffer.Length)
        {
            // If overflow risk, drop old data (resync strategy).
            _len = 0;
        }

        data.CopyTo(_buffer.AsSpan(_len));
        _len += data.Length;
    }

    public bool TryExtractFrame(out byte[] frame)
    {
        frame = Array.Empty<byte>();
        if (_len < 5) return false;

        // Resync: find DeviceAddress at buffer[0]
        int start = 0;
        while (start < _len && _buffer[start] != VfdProtocol.DeviceAddress) start++;
        if (start > 0)
        {
            ShiftLeft(start);
            if (_len < 5) return false;
        }

        // Now buffer[0] == DeviceAddress
        if (!VfdProtocol.TryParseFrameLength(_buffer.AsSpan(0, _len), out int frameLen))
        {
            // Not enough or unknown FC -> drop 1 byte to resync
            ShiftLeft(1);
            return false;
        }

        if (_len < frameLen) return false;

        var candidate = _buffer.AsSpan(0, frameLen).ToArray();
        if (!Crc16Modbus.Validate(candidate))
        {
            // CRC error, drop 1 byte and resync
            ShiftLeft(1);
            return false;
        }

        frame = candidate;
        ShiftLeft(frameLen);
        return true;
    }

    private void ShiftLeft(int count)
    {
        if (count <= 0) return;
        if (count >= _len)
        {
            _len = 0;
            return;
        }
        Buffer.BlockCopy(_buffer, count, _buffer, 0, _len - count);
        _len -= count;
    }
}
