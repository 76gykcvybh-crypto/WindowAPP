using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using VfdWpfApp.Core;

namespace VfdWpfApp.Services;

public sealed class CommandClient : IDisposable
{
    private readonly SerialTransport _transport;
    private readonly FrameDecoder _decoder = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private readonly object _pendingLock = new();
    private PendingRequest? _pending;

    private long _lastSendTick = 0;

    public int InterCommandDelayMs { get; set; } = 10;
    public int ResponseTimeoutMs { get; set; } = 20;

    public event Action<byte[], string, double?>? TxLogged; // frame, parsed, rtt
    public event Action<byte[], string, double?>? RxLogged;

    public CommandClient(SerialTransport transport)
    {
        _transport = transport;
        _transport.BytesReceived += OnBytesReceived;
        _transport.TransportError += msg => FailPending($"TransportError: {msg}");
    }

    public bool IsConnected => _transport.IsOpen;

    public async Task<ReadResult> ReadAsync(ushort startAddress, ushort wordCount, CancellationToken ct)
    {
        if (wordCount < 1 || wordCount > 16)
            throw new ArgumentOutOfRangeException(nameof(wordCount), "wordCount must be 1..16");

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnforceGapAsync(ct).ConfigureAwait(false);

            _transport.DiscardInBuffer();
            _decoder.Clear();

            byte[] req = VfdProtocol.BuildReadRequest(startAddress, wordCount);
            var sw = Stopwatch.StartNew();

            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            SetPending(new PendingRequest(RequestType.Read, startAddress, wordCount, 0, tcs, sw));

            TxLogged?.Invoke(req, $"Read 0x{startAddress:X4} x{wordCount}", null);
            await _transport.WriteAsync(req, ct).ConfigureAwait(false);
            MarkSentNow();

            byte[] resp = await WaitResponseAsync(tcs, ct).ConfigureAwait(false);
            double rtt = sw.Elapsed.TotalMilliseconds;

            // Parse read response: [Addr][0x03][ByteCount][Data...][CrcLo][CrcHi]
            int byteCount = resp[2];
            if (byteCount != wordCount * 2)
                throw new InvalidOperationException($"ByteCount mismatch. Expected {wordCount*2}, got {byteCount}");

            var data = resp.AsSpan(3, byteCount).ToArray();
            string parsed = $"Read OK 0x{startAddress:X4} x{wordCount} (bytes={byteCount})";
            RxLogged?.Invoke(resp, parsed, rtt);

            return new ReadResult(startAddress, wordCount, data, rtt);
        }
        finally
        {
            ClearPending();
            _sendLock.Release();
        }
    }

    public async Task<WriteResult> WriteSingleAsync(ushort address, ushort value, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnforceGapAsync(ct).ConfigureAwait(false);

            _transport.DiscardInBuffer();
            _decoder.Clear();

            byte[] req = VfdProtocol.BuildWriteSingleRequest(address, value);
            var sw = Stopwatch.StartNew();

            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            SetPending(new PendingRequest(RequestType.WriteSingle, address, 1, value, tcs, sw));

            TxLogged?.Invoke(req, $"Write 0x{address:X4} = 0x{value:X4} ({value})", null);
            await _transport.WriteAsync(req, ct).ConfigureAwait(false);
            MarkSentNow();

            byte[] resp = await WaitResponseAsync(tcs, ct).ConfigureAwait(false);
            double rtt = sw.Elapsed.TotalMilliseconds;

            // Validate echo
            if (resp.Length != 8 || resp[1] != 0x06)
                throw new InvalidOperationException("Invalid write response.");

            ushort addrEcho = (ushort)((resp[2] << 8) | resp[3]);
            ushort valEcho = (ushort)((resp[4] << 8) | resp[5]);

            if (addrEcho != address || valEcho != value)
                throw new InvalidOperationException($"Write echo mismatch. Got addr=0x{addrEcho:X4}, val=0x{valEcho:X4}");

            string parsed = $"Write OK 0x{address:X4} = {value}";
            RxLogged?.Invoke(resp, parsed, rtt);

            return new WriteResult(address, value, rtt);
        }
        finally
        {
            ClearPending();
            _sendLock.Release();
        }
    }

    private async Task<byte[]> WaitResponseAsync(TaskCompletionSource<byte[]> tcs, CancellationToken outerCt)
    {
        using var timeoutCts = new CancellationTokenSource(ResponseTimeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(outerCt, timeoutCts.Token);

        try
        {
            return await tcs.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // If outer cancelled, propagate; else treat as timeout
            if (outerCt.IsCancellationRequested) throw;
            throw new TimeoutException($"No response within {ResponseTimeoutMs} ms (device may drop commands or silent on error).");
        }
    }

    private void OnBytesReceived(byte[] chunk)
    {
        _decoder.Append(chunk);

        while (_decoder.TryExtractFrame(out var frame))
        {
            // Only deliver to pending request. Ignore stray frames otherwise.
            PendingRequest? p = GetPending();
            if (p is null) return;

            // Basic FC match
            byte fc = frame[1];
            if (p.Type == RequestType.Read && fc != 0x03) continue;
            if (p.Type == RequestType.WriteSingle && fc != 0x06) continue;

            // Complete
            p.Tcs.TrySetResult(frame);
            return;
        }
    }

    private async Task EnforceGapAsync(CancellationToken ct)
    {
        int gap = InterCommandDelayMs;
        if (gap <= 0) return;

        long last = Interlocked.Read(ref _lastSendTick);
        if (last == 0) return;

        double elapsedMs = (Stopwatch.GetTimestamp() - last) * 1000.0 / Stopwatch.Frequency;
        int delay = (int)Math.Ceiling(gap - elapsedMs);
        if (delay > 0)
            await Task.Delay(delay, ct).ConfigureAwait(false);
    }

    private void MarkSentNow() => Interlocked.Exchange(ref _lastSendTick, Stopwatch.GetTimestamp());

    private void SetPending(PendingRequest p)
    {
        lock (_pendingLock) { _pending = p; }
    }

    private PendingRequest? GetPending()
    {
        lock (_pendingLock) { return _pending; }
    }

    private void ClearPending()
    {
        lock (_pendingLock) { _pending = null; }
    }

    private void FailPending(string reason)
    {
        PendingRequest? p = GetPending();
        p?.Tcs.TrySetException(new InvalidOperationException(reason));
    }

    public void Dispose()
    {
        _transport.BytesReceived -= OnBytesReceived;
    }

    private enum RequestType { Read, WriteSingle }

    private sealed record PendingRequest(
        RequestType Type,
        ushort StartAddress,
        ushort WordCount,
        ushort WriteValue,
        TaskCompletionSource<byte[]> Tcs,
        Stopwatch Stopwatch
    );

    public sealed record ReadResult(ushort StartAddress, ushort WordCount, byte[] Data, double RttMs)
    {
        public ushort GetWord(int index)
        {
            int i = index * 2;
            return (ushort)((Data[i] << 8) | Data[i + 1]);
        }
    }

    public sealed record WriteResult(ushort Address, ushort Value, double RttMs);
}
