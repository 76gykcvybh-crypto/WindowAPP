using System;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace VfdWpfApp.Services;

/// <summary>
/// SerialPort wrapper with background read loop using BaseStream.
/// Manual reconnect: caller calls Connect/Disconnect.
/// </summary>
public sealed class SerialTransport : IDisposable
{
    private SerialPort? _port;
    private CancellationTokenSource? _rxCts;
    private Task? _rxTask;

    public bool IsOpen => _port?.IsOpen == true;

    public event Action<byte[]>? BytesReceived;
    public event Action<string>? TransportError;

    public void Connect(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits)
    {
        Disconnect();

        _port = new SerialPort(portName, baudRate, parity, dataBits, stopBits)
        {
            Handshake = Handshake.None,
            ReadTimeout = -1,
            WriteTimeout = 200,
            DtrEnable = true,
            RtsEnable = true
        };

        _port.Open();

        _rxCts = new CancellationTokenSource();
        _rxTask = Task.Run(() => RxLoopAsync(_rxCts.Token));
    }

    public void Disconnect()
    {
        try { _rxCts?.Cancel(); } catch { /* ignore */ }

        try { _port?.Close(); } catch { /* ignore */ }

        _rxCts = null;
        _rxTask = null;
        _port = null;
    }

    public async Task WriteAsync(byte[] data, CancellationToken ct)
    {
        if (_port is null || !_port.IsOpen)
            throw new InvalidOperationException("Serial port is not open.");

        await _port.BaseStream.WriteAsync(data, 0, data.Length, ct).ConfigureAwait(false);
        await _port.BaseStream.FlushAsync(ct).ConfigureAwait(false);
    }

    public void DiscardInBuffer()
    {
        try { _port?.DiscardInBuffer(); } catch { /* ignore */ }
    }

    private async Task RxLoopAsync(CancellationToken ct)
    {
        if (_port is null) return;

        var stream = _port.BaseStream;
        var buf = new byte[256];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false);
                if (n <= 0) continue;

                var chunk = new byte[n];
                Buffer.BlockCopy(buf, 0, chunk, 0, n);
                BytesReceived?.Invoke(chunk);
            }
        }
        catch (OperationCanceledException)
        {
            // normal
        }
        catch (Exception ex)
        {
            TransportError?.Invoke(ex.Message);
        }
    }

    public void Dispose()
    {
        Disconnect();
    }
}
