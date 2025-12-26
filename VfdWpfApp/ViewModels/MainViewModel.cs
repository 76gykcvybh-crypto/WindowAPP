using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
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
    private bool _isLoadingUsages;
    private bool _isUpdatingChartSelection;

    private static readonly string UsageConfigPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "usage_entries.json");

    public ObservableCollection<string> ComPorts { get; } = new();
    public ObservableCollection<ParameterRowViewModel> Parameters { get; } = new();
    public ObservableCollection<ParameterUsageViewModel> ParameterUsages { get; } = new();
    public ObservableCollection<LogEntryViewModel> LogEntries { get; } = new();
    public ObservableCollection<ChartSeriesViewModel> ChartSeries { get; } = new();
    public bool HasChartSeries => ChartSeries.Count > 0;
    public bool NoChartSeries => ChartSeries.Count == 0;

    private readonly CollectionViewSource _pollingReadUsagesView = new();
    public ICollectionView PollingReadUsagesView => _pollingReadUsagesView.View;

    private readonly CollectionViewSource _pollingWriteUsagesView = new();
    public ICollectionView PollingWriteUsagesView => _pollingWriteUsagesView.View;

    private readonly CollectionViewSource _singleUsagesView = new();
    public ICollectionView SingleUsagesView => _singleUsagesView.View;

    private readonly Dictionary<ParameterUsageViewModel, ChartSeriesViewModel> _chartSeriesMap = new();
    private readonly Dictionary<ParameterUsageViewModel, List<double>> _chartSamplesMap = new();
    private readonly DispatcherTimer _chartTimer = new();

    private const int ChartMaxSamples = 200;
    private const double ChartWidth = 1000;
    private const double ChartHeight = 500;
    private const int ChartRefreshIntervalMs = 100;

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

    private bool _isOperationPage;
    public bool IsOperationPage
    {
        get => _isOperationPage;
        set
        {
            if (Set(ref _isOperationPage, value))
                Raise(nameof(IsSettingsPage));
        }
    }

    public bool IsSettingsPage => !_isOperationPage;

    private string _currentLogFileHint = "";
    public string CurrentLogFileHint { get => _currentLogFileHint; set => Set(ref _currentLogFileHint, value); }

    private string _speedRawU16 = "0";
    public string SpeedRawU16 { get => _speedRawU16; set => Set(ref _speedRawU16, value); }

    private ParameterRowViewModel? _selectedParameter;
    public ParameterRowViewModel? SelectedParameter
    {
        get => _selectedParameter;
        set
        {
            if (Set(ref _selectedParameter, value))
                AddUsageCommand.RaiseCanExecuteChanged();
        }
    }

    private string _selectedWriteValueU16 = "0";
    public string SelectedWriteValueU16 { get => _selectedWriteValueU16; set => Set(ref _selectedWriteValueU16, value); }

    public RelayCommand RefreshPortsCommand { get; }
    public AsyncRelayCommand ConnectCommand { get; }
    public RelayCommand DisconnectCommand { get; }

    public RelayCommand EnterOperationCommand { get; }
    public RelayCommand ReturnToSettingsCommand { get; }
    public RelayCommand AddUsageCommand { get; }
    public RelayCommand<ParameterUsageViewModel> RemoveUsageCommand { get; }

    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand ShutdownCommand { get; }
    public AsyncRelayCommand SetSpeedCommand { get; }

    public AsyncRelayCommand ReadSelectedCommand { get; }
    public AsyncRelayCommand WriteSelectedCommand { get; }

    public RelayCommand<ParameterUsageViewModel> ExecuteSingleCommand { get; }

    public RelayCommand OpenLogFolderCommand { get; }

    public MainViewModel()
    {
        RefreshPortsCommand = new RelayCommand(RefreshPorts);
        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !IsConnected);
        DisconnectCommand = new RelayCommand(Disconnect, () => IsConnected);

        EnterOperationCommand = new RelayCommand(() => IsOperationPage = true, () => IsConnected);
        ReturnToSettingsCommand = new RelayCommand(() => IsOperationPage = false);
        AddUsageCommand = new RelayCommand(AddUsageForSelected, () => SelectedParameter != null);
        RemoveUsageCommand = new RelayCommand<ParameterUsageViewModel>(RemoveUsage, usage => usage != null);

        StartCommand = new AsyncRelayCommand(() => WriteCmdAsync(0x0001, 1, "Start motor"), () => IsConnected);
        StopCommand = new AsyncRelayCommand(() => WriteCmdAsync(0x0000, 1, "Stop motor"), () => IsConnected);
        ShutdownCommand = new AsyncRelayCommand(() => WriteCmdAsync(0x000B, 1, "Shutdown"), () => IsConnected);
        SetSpeedCommand = new AsyncRelayCommand(SetSpeedAsync, () => IsConnected);

        ReadSelectedCommand = new AsyncRelayCommand(ReadSelectedAsync, () => IsConnected && SelectedParameter != null);
        WriteSelectedCommand = new AsyncRelayCommand(WriteSelectedAsync, () => IsConnected && SelectedParameter != null);

        ExecuteSingleCommand = new RelayCommand<ParameterUsageViewModel>(usage => _ = ExecuteSingleAsync(usage), usage => IsConnected);

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

        _pollingReadUsagesView.Source = ParameterUsages;
        _pollingReadUsagesView.Filter += (_, e) =>
            e.Accepted = e.Item is ParameterUsageViewModel usage
                         && usage.Mode == ParameterUsageMode.Polling
                         && usage.Action == ParameterActionType.Read;

        _pollingWriteUsagesView.Source = ParameterUsages;
        _pollingWriteUsagesView.Filter += (_, e) =>
            e.Accepted = e.Item is ParameterUsageViewModel usage
                         && usage.Mode == ParameterUsageMode.Polling
                         && usage.Action == ParameterActionType.Write;

        _singleUsagesView.Source = ParameterUsages;
        _singleUsagesView.Filter += (_, e) =>
            e.Accepted = e.Item is ParameterUsageViewModel usage && usage.Mode == ParameterUsageMode.Single;

        LoadUsageEntries();

        _chartTimer.Interval = TimeSpan.FromMilliseconds(ChartRefreshIntervalMs);
        _chartTimer.Tick += (_, _) => UpdateChartFromCurrentValues();
        _chartTimer.Start();

        ChartSeries.CollectionChanged += OnChartSeriesChanged;
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
        EnterOperationCommand.RaiseCanExecuteChanged();
        AddUsageCommand.RaiseCanExecuteChanged();
    }

    private void LoadParameterCatalog()
    {
        try
        {
            string jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tac_cac3x_parameters.json");
            _catalog = ParameterCatalog.LoadFromJson(jsonPath);

            Parameters.Clear();
            ParameterUsages.Clear();
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
            EnterOperationCommand.RaiseCanExecuteChanged();
            ExecuteSingleCommand.RaiseCanExecuteChanged();
            AddUsageCommand.RaiseCanExecuteChanged();
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
        PollEnabled = false;
        IsOperationPage = false;

        ConnectCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
        EnterOperationCommand.RaiseCanExecuteChanged();
        ExecuteSingleCommand.RaiseCanExecuteChanged();
        AddUsageCommand.RaiseCanExecuteChanged();
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

    private async Task ExecuteSingleAsync(ParameterUsageViewModel? usage)
    {
        if (_client is null || usage is null) return;

        if (usage.Mode != ParameterUsageMode.Single)
        {
            FooterStatus = "Selected usage is not Single.";
            return;
        }

        if (usage.Action == ParameterActionType.Read)
        {
            await ExecuteReadAsync(usage, "Single read").ConfigureAwait(false);
            return;
        }

        await ExecuteWriteAsync(usage, "Single write").ConfigureAwait(false);
    }

    private async Task ExecuteReadAsync(ParameterUsageViewModel usage, string label)
    {
        if (_client is null) return;

        try
        {
            ushort addr = (ushort)usage.Parameter.Def.Address;
            var res = await _client.ReadAsync(addr, 1, CancellationToken.None).ConfigureAwait(false);
            ushort v = res.GetWord(0);

            Application.Current.Dispatcher.Invoke(() => usage.Parameter.SetLastValue(v));

            string parsed = BuildParsedParameter(addr, v);
            AddLog(LogDirection.RX, Array.Empty<byte>(), parsed, $"{label} OK RTT={res.RttMs:0.0}ms", res.RttMs);
            FooterStatus = $"{label} {usage.AddressHex} OK, value={v} (0x{v:X4})";
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

    private async Task ExecuteWriteAsync(ParameterUsageViewModel usage, string label)
    {
        if (_client is null) return;

        if (usage.Access.Equals("RO", StringComparison.OrdinalIgnoreCase))
        {
            FooterStatus = "Selected parameter is Read-Only.";
            return;
        }

        if (!TryGetWriteValue(usage, out ushort raw, out string error))
        {
            FooterStatus = error;
            return;
        }

        try
        {
            ushort addr = (ushort)usage.Parameter.Def.Address;
            var res = await _client.WriteSingleAsync(addr, raw, CancellationToken.None).ConfigureAwait(false);
            FooterStatus = $"{label} {usage.AddressHex} OK, RTT={res.RttMs:0.0}ms";
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

    private bool TryGetWriteValue(ParameterUsageViewModel usage, out ushort value, out string error)
    {
        if (!ushort.TryParse(usage.WriteValueU16, out value))
        {
            error = "Write U16 must be 0..65535";
            return false;
        }

        error = "";
        return true;
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
        _pollTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
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
                var now = Stopwatch.GetTimestamp();
                var polling = ParameterUsages.Where(p => p.Mode == ParameterUsageMode.Polling).ToList();
                foreach (var usage in polling)
                {
                    int interval = Math.Max(20, usage.PollIntervalMs);
                    if (usage.LastPollTick != 0)
                    {
                        double elapsedMs = (now - usage.LastPollTick) * 1000.0 / Stopwatch.Frequency;
                        if (elapsedMs < interval) continue;
                    }

                    usage.LastPollTick = now;
                    await PollParameterAsync(usage, ct).ConfigureAwait(false);
                }
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

    private async Task PollParameterAsync(ParameterUsageViewModel usage, CancellationToken ct)
    {
        if (_client is null) return;

        try
        {
            ushort addr = (ushort)usage.Parameter.Def.Address;
            if (usage.Action == ParameterActionType.Read)
            {
                var res = await _client.ReadAsync(addr, 1, ct).ConfigureAwait(false);
                ushort v = res.GetWord(0);

                Application.Current.Dispatcher.Invoke(() => usage.Parameter.SetLastValue(v));

                string parsed = BuildParsedParameter(addr, v);
                AddLog(LogDirection.RX, Array.Empty<byte>(), $"Poll {parsed}", "OK", res.RttMs);
            }
            else
            {
                if (usage.Access.Equals("RO", StringComparison.OrdinalIgnoreCase))
                {
                    AddLog(LogDirection.RX, Array.Empty<byte>(), $"Poll {addr:X4}", "SKIP (RO)", null);
                    return;
                }

                if (!TryGetWriteValue(usage, out ushort raw, out _))
                {
                    AddLog(LogDirection.RX, Array.Empty<byte>(), $"Poll {addr:X4}", "INVALID WRITE", null);
                    return;
                }

                var res = await _client.WriteSingleAsync(addr, raw, ct).ConfigureAwait(false);
                AddLog(LogDirection.RX, Array.Empty<byte>(), $"Poll write {usage.AddressHex} = {raw}", "OK", res.RttMs);
            }
        }
        catch (TimeoutException)
        {
            AddLog(LogDirection.RX, Array.Empty<byte>(), $"Poll {usage.AddressHex}", "TIMEOUT", null);
        }
    }

    private void AddUsageForSelected()
    {
        if (SelectedParameter is null)
        {
            FooterStatus = "Select a parameter first.";
            return;
        }

        var usage = new ParameterUsageViewModel(SelectedParameter);
        RegisterUsage(usage);
        SelectedParameter.Usages.Add(usage);
        SaveUsageEntries();
        FooterStatus = $"Added usage for {SelectedParameter.AddressHex}.";
    }

    private void RemoveUsage(ParameterUsageViewModel? usage)
    {
        if (usage is null) return;

        usage.Parameter.Usages.Remove(usage);
        UnregisterUsage(usage);
        SaveUsageEntries();
    }

    private void RegisterUsage(ParameterUsageViewModel usage)
    {
        usage.PropertyChanged += OnUsagePropertyChanged;
        ParameterUsages.Add(usage);

        if (usage.IsChartSelected)
            HandleChartSelectionChange(usage);
    }

    private void UnregisterUsage(ParameterUsageViewModel usage)
    {
        usage.PropertyChanged -= OnUsagePropertyChanged;
        ParameterUsages.Remove(usage);
        RemoveChartSeries(usage);
    }

    private void OnUsagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ParameterUsageViewModel.Mode)
            || e.PropertyName == nameof(ParameterUsageViewModel.Action))
        {
            _pollingReadUsagesView.View?.Refresh();
            _pollingWriteUsagesView.View?.Refresh();
            _singleUsagesView.View?.Refresh();

            if (sender is ParameterUsageViewModel usage && usage.IsChartSelected)
                HandleChartSelectionChange(usage);
        }

        if (e.PropertyName == nameof(ParameterUsageViewModel.IsChartSelected))
        {
            if (sender is ParameterUsageViewModel usage)
                HandleChartSelectionChange(usage);
        }

        if (!_isLoadingUsages)
            SaveUsageEntries();
    }

    private void HandleChartSelectionChange(ParameterUsageViewModel usage)
    {
        if (_isUpdatingChartSelection) return;

        if (usage.Mode != ParameterUsageMode.Polling || usage.Action != ParameterActionType.Read)
        {
            if (usage.IsChartSelected)
            {
                _isUpdatingChartSelection = true;
                usage.IsChartSelected = false;
                _isUpdatingChartSelection = false;
            }
            return;
        }

        if (usage.IsChartSelected)
        {
            if (_chartSeriesMap.Count >= 5)
            {
                _isUpdatingChartSelection = true;
                usage.IsChartSelected = false;
                _isUpdatingChartSelection = false;
                FooterStatus = "Chart selection limited to 5 polling read items.";
                return;
            }

            AddChartSeries(usage);
        }
        else
        {
            RemoveChartSeries(usage);
        }
    }

    private void AddChartSeries(ParameterUsageViewModel usage)
    {
        if (_chartSeriesMap.ContainsKey(usage)) return;

        var color = GetNextChartColor();
        var series = new ChartSeriesViewModel($"{usage.AddressHex} {usage.Name}", color);
        _chartSeriesMap[usage] = series;
        var samples = new List<double>(ChartMaxSamples);
        double seedValue = usage.Parameter.TryGetLastValue(out ushort value) ? value : 0;
        for (int i = 0; i < 20; i++)
            samples.Add(seedValue);
        _chartSamplesMap[usage] = samples;
        ChartSeries.Add(series);
        UpdateChartSeries();
    }

    private void RemoveChartSeries(ParameterUsageViewModel usage)
    {
        if (!_chartSeriesMap.Remove(usage, out var series)) return;

        _chartSamplesMap.Remove(usage);
        ChartSeries.Remove(series);
        UpdateChartSeries();
    }

    private void OnChartSeriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Raise(nameof(HasChartSeries));
        Raise(nameof(NoChartSeries));
    }

    private void UpdateChartFromCurrentValues()
    {
        if (_chartSeriesMap.Count == 0) return;

        foreach (var usage in _chartSeriesMap.Keys)
        {
            if (usage.Parameter.TryGetLastValue(out ushort value))
                AddChartSampleValue(usage, value);
        }

        UpdateChartSeries();
    }

    private void AddChartSampleValue(ParameterUsageViewModel usage, double value)
    {
        if (!_chartSamplesMap.TryGetValue(usage, out var samples)) return;

        samples.Add(value);
        if (samples.Count > ChartMaxSamples)
            samples.RemoveAt(0);
    }

    private void UpdateChartSeries()
    {
        if (ChartSeries.Count == 0) return;

        double min = double.MaxValue;
        double max = double.MinValue;

        foreach (var samples in _chartSamplesMap.Values)
        {
            foreach (double v in samples)
            {
                if (v < min) min = v;
                if (v > max) max = v;
            }
        }

        if (min == double.MaxValue || max == double.MinValue)
        {
            min = 0;
            max = 1;
        }

        foreach (var (usage, series) in _chartSeriesMap)
        {
            if (_chartSamplesMap.TryGetValue(usage, out var samples))
                series.UpdatePoints(samples, min, max, ChartWidth, ChartHeight);
        }
    }

    private Brush GetNextChartColor()
    {
        Brush[] colors =
        {
            Brushes.DeepSkyBlue,
            Brushes.LimeGreen,
            Brushes.OrangeRed,
            Brushes.Goldenrod,
            Brushes.MediumPurple
        };

        return colors[_chartSeriesMap.Count % colors.Length];
    }

    private void LoadUsageEntries()
    {
        if (!File.Exists(UsageConfigPath)) return;

        try
        {
            _isLoadingUsages = true;
            var json = File.ReadAllText(UsageConfigPath);
            var entries = JsonSerializer.Deserialize<List<UsageEntryConfig>>(json);
            if (entries is null) return;

            ParameterUsages.Clear();
            ChartSeries.Clear();
            _chartSeriesMap.Clear();
            _chartSamplesMap.Clear();
            foreach (var param in Parameters)
                param.Usages.Clear();

            foreach (var entry in entries)
            {
                var param = Parameters.FirstOrDefault(p => p.Def.Address == entry.Address);
                if (param is null) continue;

                var usage = new ParameterUsageViewModel(param)
                {
                    Mode = entry.Mode,
                    Action = entry.Action,
                    PollIntervalMs = entry.PollIntervalMs,
                    WriteValueU16 = entry.WriteValueU16,
                    IsChartSelected = entry.IsChartSelected
                };
                RegisterUsage(usage);
                param.Usages.Add(usage);
            }
        }
        catch (Exception ex)
        {
            FooterStatus = $"Failed to load usage entries: {ex.Message}";
        }
        finally
        {
            _isLoadingUsages = false;
        }
    }

    private void SaveUsageEntries()
    {
        try
        {
            var entries = ParameterUsages.Select(usage => new UsageEntryConfig(
                usage.Parameter.Def.Address,
                usage.Mode,
                usage.Action,
                usage.PollIntervalMs,
                usage.WriteValueU16,
                usage.IsChartSelected
            )).ToList();

            var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(UsageConfigPath, json);
        }
        catch (Exception ex)
        {
            FooterStatus = $"Failed to save usage entries: {ex.Message}";
        }
    }

    private sealed record UsageEntryConfig(
        int Address,
        ParameterUsageMode Mode,
        ParameterActionType Action,
        int PollIntervalMs,
        string WriteValueU16,
        bool IsChartSelected
    );

    public void Dispose()
    {
        StopPolling();
        _client?.Dispose();
        _logger?.Dispose();
        _transport.Dispose();
    }
}
