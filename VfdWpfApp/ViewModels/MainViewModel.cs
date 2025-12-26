using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using VfdWpfApp.Core;
using VfdWpfApp.Models;
using VfdWpfApp.Services;

namespace VfdWpfApp.ViewModels;

public sealed class MainViewModel : NotifyBase, IDisposable
{
    private readonly SerialTransport _transport = new();
    private CommandClient? _client;
    private JsonlLogger? _logger;
    private ParameterCatalog? _catalog;
    private PeriodicTimer? _pollTimer;
    private CancellationTokenSource? _pollCts;

    public ObservableCollection<string> ComPorts { get; } = new();
    public ObservableCollection<ParameterRowViewModel> Parameters { get; } = new();
    public ObservableCollection<LogEntryViewModel> LogEntries { get; } = new();

    public string[] ParityOptions { get; } = Enum.GetNames(typeof(Parity));
    public string[] StopBitOptions { get; } = Enum.GetNames(typeof(StopBits));

    private string? _selectedComPort;
    public string? SelectedComPort { get => _selectedComPort; set => Set(ref _selectedComPort, value); }

    private int _baudRate = 115200;
    public int BaudRate { get => _baudRate; set => Set(ref _baudRate, value); }

    private string _selectedParity = Parity.None.ToString();
    public string SelectedParity { get => _selectedParity; set => Set(ref _selectedParity, value); }

    private int _dataBits = 8;
    public int DataBits { get => _dataBits; set => Set(ref _dataBits, value); }

    private string _selectedStopBits = StopBits.One.ToString();
    public string SelectedStopBits { get => _selectedStopBits; set => Set(ref _selectedStopBits, value); }

    private int _responseTimeoutMs = 20;
    public int ResponseTimeoutMs
    {
        get => _responseTimeoutMs;
        set
        {
            if (Set(ref _responseTimeoutMs, value))
                if (_client != null) _client.ResponseTimeoutMs = _responseTimeoutMs;
        }
    }

    private int _interCommandDelayMs = 10;
    public int InterCommandDelayMs
    {
        get => _interCommandDelayMs;
        set
        {
            if (Set(ref _interCommandDelayMs, value))
                if (_client != null) _client.InterCommandDelayMs = _interCommandDelayMs;
        }
    }

    private int _pollIntervalMs = 200;
    public int PollIntervalMs { get => _pollIntervalMs; set => Set(ref _pollIntervalMs, Math.Max(20, value)); }

    private bool _pollEnabled;
    public bool PollEnabled
    {
        get => _pollEnabled;
        set
        {
            if (Set(ref _pollEnabled, value))
            {
                if (value) StartPolling();
                else StopPolling();
            }
        }
    }

    private string _statusText = "Disconnected";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    private string _footerStatus = "Ready";
    public string FooterStatus { get => _footerStatus; set => Set(ref _footerStatus, value); }

    private string _currentLogFileHint = "";
    public string CurrentLogFileHint { get => _currentLogFileHint; set => Set(ref _currentLogFileHint, value); }

    private string _speedRawU16 = "0";
    public string SpeedRawU16 { get => _speedRawU16; set => Set(ref _speedRawU16, value); }

    private ParameterRowViewModel? _selectedParameter;
    public ParameterRowViewModel? SelectedParameter { get => _selectedParameter; set => Set(ref _selectedParameter, value); }

    private string _selectedWriteValueU16 = "0";
    public string SelectedWriteValueU16 { get => _selectedWriteValueU16; set => Set(ref _selectedWriteValueU16, value); }

    public RelayCommand RefreshPortsCommand { get; }
    public AsyncRelayCommand ConnectCommand { get; }
    public RelayCommand DisconnectCommand { get; }

    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand ShutdownCommand { get; }
    public AsyncRelayCommand SetSpeedCommand { get; }

    public AsyncRelayCommand ReadSelectedCommand { get; }
    public AsyncRelayCommand WriteSelectedCommand { get; }

    public RelayCommand OpenLogFolderCommand { get; }

    public MainViewModel()
    {
        RefreshPortsCommand = new RelayCommand(RefreshPorts);
        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !IsConnected);
        DisconnectCommand = new RelayCommand(Disconnect, () => IsConnected);

        StartCommand = new AsyncRelayCommand(() => WriteCmdAsync(0x0001, 1, "Start motor"), () => IsConnected);
        StopCommand = new AsyncRelayCommand(() => WriteCmdAsync(0x0000, 1, "Stop motor"), () => IsConnected);
        ShutdownCommand = new AsyncRelayCommand(() => WriteCmdAsync(0x000B, 1, "Shutdown"), () => IsConnected);
        SetSpeedCommand = new AsyncRelayCommand(SetSpeedAsync, () => IsConnected);

        ReadSelectedCommand = new AsyncRelayCommand(ReadSelectedAsync, () => IsConnected && SelectedParameter != null);
        WriteSelectedCommand = new AsyncRelayCommand(WriteSelectedAsync, () => IsConnected && SelectedParameter != null);

        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);

        _transport.TransportError += msg =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                StatusText = $"Transport error: {msg}";
                FooterStatus = "Disconnected due to transport error. Please reconnect manually.";
            });
        };

        LoadParameterCatalog();
        RefreshPorts();
    }

    private bool IsConnected => _transport.IsOpen;

    private void RefreshPorts()
    {
        ComPorts.Clear();
        foreach (string p in SerialPort.GetPortNames().OrderBy(x => x))
            ComPorts.Add(p);

        SelectedComPort ??= ComPorts.FirstOrDefault();
        FooterStatus = $"Found {ComPorts.Count} COM ports.";
        ConnectCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
    }

    private void LoadParameterCatalog()
    {
        try
        {
            string jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tac_cac3x_parameters.json");
            _catalog = ParameterCatalog.LoadFromJson(jsonPath);

            Parameters.Clear();
            foreach (var p in _catalog.Parameters)
                Parameters.Add(new ParameterRowViewModel(p));

            FooterStatus = $"Loaded parameters: {Parameters.Count}";
        }
        catch (Exception ex)
        {
            FooterStatus = $"Failed to load parameter catalog: {ex.Message}";
        }
    }

    private async Task ConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedComPort))
        {
            FooterStatus = "Select a COM port first.";
            return;
        }

        try
        {
            var parity = Enum.Parse<Parity>(SelectedParity);
            var stopBits = Enum.Parse<StopBits>(SelectedStopBits);

            _transport.Connect(SelectedComPort, BaudRate, parity, DataBits, stopBits);

            // start logger
            _logger?.Dispose();
            string logFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
            _logger = new JsonlLogger(logFolder);
            CurrentLogFileHint = $"Logging: {Path.GetFileName(_logger.FilePath)}";

            _client?.Dispose();
            _client = new CommandClient(_transport)
            {
                ResponseTimeoutMs = ResponseTimeoutMs,
                InterCommandDelayMs = InterCommandDelayMs
            };

            _client.TxLogged += (frame, parsed, rtt) => AddLog(LogDirection.TX, frame, parsed, "Sent", rtt);
            _client.RxLogged += (frame, parsed, rtt) => AddLog(LogDirection.RX, frame, parsed, "OK", rtt);

            StatusText = $"Connected: {SelectedComPort}";
            FooterStatus = "Ready (one-request-at-a-time, min-gap enforced).";

            ConnectCommand.RaiseCanExecuteChanged();
            DisconnectCommand.RaiseCanExecuteChanged();
        }
        catch (Exception ex)
        {
            StatusText = $"Connect failed: {ex.Message}";
            FooterStatus = "Check COM port settings and try again.";
        }
    }

    private void Disconnect()
    {
        StopPolling();

        _client?.Dispose();
        _client = null;

        _logger?.Dispose();
        _logger = null;

        _transport.Disconnect();
        StatusText = "Disconnected";
        FooterStatus = "Disconnected. Manual reconnect required.";

        ConnectCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
    }

    private async Task WriteCmdAsync(ushort addr, ushort value, string label)
    {
        if (_client is null) return;

        try
        {
            var result = await _client.WriteSingleAsync(addr, value, CancellationToken.None);
            FooterStatus = $"{label} OK, RTT={result.RttMs:0.0}ms";
        }
        catch (TimeoutException tex)
        {
            AddLog(LogDirection.RX, Array.Empty<byte>(), label, $"TIMEOUT ({ResponseTimeoutMs}ms)", null);
            FooterStatus = tex.Message;
        }
        catch (Exception ex)
        {
            AddLog(LogDirection.RX, Array.Empty<byte>(), label, $"ERROR: {ex.Message}", null);
            FooterStatus = ex.Message;
        }
    }

    private async Task SetSpeedAsync()
    {
        if (!ushort.TryParse(SpeedRawU16, out ushort raw))
        {
            FooterStatus = "SpeedRawU16 must be 0..65535";
            return;
        }

        await WriteCmdAsync(0x000A, raw, "Set speed");
    }

    private async Task ReadSelectedAsync()
    {
        if (_client is null || SelectedParameter is null) return;

        try
        {
            ushort addr = (ushort)SelectedParameter.Def.Address;
            var res = await _client.ReadAsync(addr, 1, CancellationToken.None);
            ushort v = res.GetWord(0);

            SelectedParameter.SetLastValue(v);

            string parsed = BuildParsedParameter(addr, v);
            AddLog(LogDirection.RX, Array.Empty<byte>(), parsed, $"OK RTT={res.RttMs:0.0}ms", res.RttMs);
            FooterStatus = $"Read {SelectedParameter.AddressHex} OK, value={v} (0x{v:X4}), RTT={res.RttMs:0.0}ms";
        }
        catch (TimeoutException tex)
        {
            FooterStatus = tex.Message;
        }
        catch (Exception ex)
        {
            FooterStatus = ex.Message;
        }
    }

    private async Task WriteSelectedAsync()
    {
        if (_client is null || SelectedParameter is null) return;

        if (SelectedParameter.Access.Equals("RO", StringComparison.OrdinalIgnoreCase))
        {
            FooterStatus = "Selected parameter is Read-Only.";
            return;
        }

        if (!ushort.TryParse(SelectedWriteValueU16, out ushort raw))
        {
            FooterStatus = "Write U16 must be 0..65535";
            return;
        }

        try
        {
            ushort addr = (ushort)SelectedParameter.Def.Address;
            var res = await _client.WriteSingleAsync(addr, raw, CancellationToken.None);
            FooterStatus = $"Write {SelectedParameter.AddressHex} OK, RTT={res.RttMs:0.0}ms";
        }
        catch (TimeoutException tex)
        {
            FooterStatus = tex.Message;
        }
        catch (Exception ex)
        {
            FooterStatus = ex.Message;
        }
    }

    private string BuildParsedParameter(ushort addr, ushort value)
    {
        if (_catalog is null) return $"0x{addr:X4} = {value}";

        var def = _catalog.FindByAddress(addr);
        if (def is null) return $"0x{addr:X4} = {value}";

        if (def.Bitfields.Count == 0)
            return $"{def.Name} ({def.AddressHex}) = {value} (0x{value:X4})";

        // Bitfield summary
        var setBits = def.Bitfields
            .OrderBy(b => b.Bit)
            .Select(b => (b.Bit, On: ((value >> b.Bit) & 1) == 1, b.Meaning))
            .ToList();

        string onList = string.Join(", ", setBits.Where(x => x.On).Select(x => $"BIT{x.Bit}:{x.Meaning}"));
        if (string.IsNullOrWhiteSpace(onList)) onList = "(no bits set)";
        return $"{def.Name} ({def.AddressHex}) = 0x{value:X4} => {onList}";
    }

    private void AddLog(LogDirection dir, byte[] frame, string parsed, string result, double? rtt)
    {
        var record = new CommLogRecord(
            TimestampUtc: DateTimeOffset.UtcNow,
            Direction: dir,
            Hex: frame.Length == 0 ? "" : Hex.ToHex(frame),
            Parsed: parsed,
            Result: result,
            RttMs: rtt
        );

        _logger?.Log(record);

        Application.Current.Dispatcher.Invoke(() =>
        {
            LogEntries.Insert(0, new LogEntryViewModel(record));
            while (LogEntries.Count > 800) LogEntries.RemoveAt(LogEntries.Count - 1);
        });
    }

    private void OpenLogFolder()
    {
        try
        {
            string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            FooterStatus = $"Open folder failed: {ex.Message}";
        }
    }

    private void StartPolling()
    {
        if (!IsConnected || _client is null) return;

        StopPolling();

        _pollCts = new CancellationTokenSource();
        _pollTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(PollIntervalMs));
        _ = Task.Run(() => PollLoopAsync(_pollCts.Token));
        FooterStatus = "Polling started.";
    }

    private void StopPolling()
    {
        try { _pollCts?.Cancel(); } catch { /* ignore */ }
        _pollTimer?.Dispose();
        _pollTimer = null;
        _pollCts = null;
        FooterStatus = "Polling stopped.";
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        if (_client is null) return;

        try
        {
            while (_pollTimer != null && await _pollTimer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                // Example: poll 0x0100 (system status) and 0x0101 (protect status)
                await PollOneAsync(0x0100, ct).ConfigureAwait(false);
                await PollOneAsync(0x0101, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // normal
        }
        catch (Exception ex)
        {
            Application.Current.Dispatcher.Invoke(() => FooterStatus = $"Polling error: {ex.Message}");
        }
    }

    private async Task PollOneAsync(ushort addr, CancellationToken ct)
    {
        if (_client is null) return;

        try
        {
            var res = await _client.ReadAsync(addr, 1, ct).ConfigureAwait(false);
            ushort v = res.GetWord(0);

            // Update grid row if exists
            Application.Current.Dispatcher.Invoke(() =>
            {
                var row = Parameters.FirstOrDefault(p => p.Def.Address == addr);
                row?.SetLastValue(v);
            });

            string parsed = BuildParsedParameter(addr, v);
            AddLog(LogDirection.RX, Array.Empty<byte>(), $"Poll {parsed}", "OK", res.RttMs);
        }
        catch (TimeoutException)
        {
            AddLog(LogDirection.RX, Array.Empty<byte>(), $"Poll 0x{addr:X4}", "TIMEOUT", null);
        }
    }

    public void Dispose()
    {
        StopPolling();
        _client?.Dispose();
        _logger?.Dispose();
        _transport.Dispose();
    }
}
