using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
    public ObservableCollection<AxisLabelViewModel> TimeAxisLabels { get; } = new();
    public ObservableCollection<AxisLabelViewModel> YAxisLabels { get; } = new();
    public ObservableCollection<AxisLineViewModel> XAxisGridLines { get; } = new();
    public ObservableCollection<AxisLineViewModel> YAxisGridLines { get; } = new();
    public ObservableCollection<SeriesScaleLabelViewModel> SeriesScaleLabels { get; } = new();

    private readonly CollectionViewSource _pollingReadUsagesView = new();
    public ICollectionView PollingReadUsagesView => _pollingReadUsagesView.View;

    private readonly CollectionViewSource _pollingWriteUsagesView = new();
    public ICollectionView PollingWriteUsagesView => _pollingWriteUsagesView.View;

    private readonly CollectionViewSource _singleUsagesView = new();
    public ICollectionView SingleUsagesView => _singleUsagesView.View;

    private readonly CollectionViewSource _chartSelectedUsagesView = new();
    public ICollectionView ChartSelectedUsagesView => _chartSelectedUsagesView.View;

    private readonly Dictionary<ParameterUsageViewModel, ChartSeriesViewModel> _chartSeriesMap = new();
    private readonly Dictionary<ParameterUsageViewModel, List<double>> _chartSamplesMap = new();
    private readonly DispatcherTimer _chartTimer = new();
    private Window? _oscilloscopeWindow;
    private double _chartMin;
    private double _chartMax = 1;

    private const double ChartWidth = 1000;
    private const double ChartHeight = 500;
    private const int ChartRefreshIntervalMs = 100;
    private const int ChartSeedSamples = 20;

    private double _chartTimeWindowSeconds = 20;
    public double ChartTimeWindowSeconds
    {
        get => _chartTimeWindowSeconds;
        set
        {
            if (Set(ref _chartTimeWindowSeconds, Math.Max(1, value)))
            {
                UpdateChartWindow();
                UpdateTimeAxisLabels();
            }
        }
    }

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
    public RelayCommand OpenOscilloscopeCommand { get; }

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
        OpenOscilloscopeCommand = new RelayCommand(OpenOscilloscopeWindow);

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

        _chartSelectedUsagesView.Source = ParameterUsages;
        _chartSelectedUsagesView.Filter += (_, e) =>
            e.Accepted = e.Item is ParameterUsageViewModel usage && usage.IsChartSelected;

        LoadUsageEntries();

        _chartTimer.Interval = TimeSpan.FromMilliseconds(ChartRefreshIntervalMs);
        _chartTimer.Tick += (_, _) => UpdateChartFromCurrentValues();
        _chartTimer.Start();

        ChartSeries.CollectionChanged += OnChartSeriesChanged;
        UpdateAxisGrid();
        UpdateTimeAxisLabels();
        UpdateYAxisLabels();
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

        if (e.PropertyName == nameof(ParameterUsageViewModel.ChartScale))
        {
            if (sender is ParameterUsageViewModel usage
                && _chartSeriesMap.TryGetValue(usage, out var series))
            {
                series.Scale = usage.ChartScale;
                UpdateChartSeries();
            }
        }

        if (e.PropertyName == nameof(ParameterUsageViewModel.ChartOffset))
        {
            if (sender is ParameterUsageViewModel usage
                && _chartSeriesMap.TryGetValue(usage, out var series))
            {
                series.Offset = usage.ChartOffset;
                UpdateChartSeries();
            }
        }

        if (e.PropertyName == nameof(ParameterUsageViewModel.IsChartSelected))
            _chartSelectedUsagesView.View?.Refresh();

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
        var series = new ChartSeriesViewModel($"{usage.AddressHex} {usage.Name}", color)
        {
            Scale = usage.ChartScale,
            Offset = usage.ChartOffset
        };
        _chartSeriesMap[usage] = series;
        var samples = new List<double>(ChartMaxSamples);
        double seedValue = usage.Parameter.TryGetLastValue(out ushort value) ? value : 0;
        int seedCount = Math.Min(ChartMaxSamples, ChartSeedSamples);
        for (int i = 0; i < seedCount; i++)
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

        foreach (var (usage, samples) in _chartSamplesMap)
        {
            foreach (double v in samples)
            {
                double scaled = v * usage.ChartScale + usage.ChartOffset;
                if (scaled < min) min = scaled;
                if (scaled > max) max = scaled;
            }
        }

        if (min == double.MaxValue || max == double.MinValue)
        {
            min = 0;
            max = 1;
        }

        _chartMin = min;
        _chartMax = max;

        foreach (var (usage, series) in _chartSeriesMap)
        {
            if (_chartSamplesMap.TryGetValue(usage, out var samples))
                series.UpdatePoints(samples, min, max, ChartWidth, ChartHeight);
        }

        UpdateSeriesScaleLabels();
        UpdateYAxisLabels();
        UpdateTimeAxisLabels();
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

    private int ChartMaxSamples
        => Math.Max(2, (int)Math.Ceiling((ChartTimeWindowSeconds * 1000) / ChartRefreshIntervalMs));

    private void UpdateChartWindow()
    {
        foreach (var (usage, samples) in _chartSamplesMap)
        {
            int max = ChartMaxSamples;
            if (samples.Count > max)
            {
                samples.RemoveRange(0, samples.Count - max);
            }
            else if (samples.Count < max)
            {
                double fillValue = usage.Parameter.TryGetLastValue(out ushort value) ? value : 0;
                while (samples.Count < max)
                    samples.Insert(0, fillValue);
            }
        }

        UpdateChartSeries();
    }

    private void UpdateTimeAxisLabels()
    {
        TimeAxisLabels.Clear();

        double window = ChartTimeWindowSeconds;
        if (window <= 0) return;

        int ticks = 10;
        for (int i = 0; i <= ticks; i++)
        {
            double t = -window + (window * i / ticks);
            double x = (ChartWidth - 1) * i / ticks;
            double top = ChartHeight - 18;
            TimeAxisLabels.Add(new AxisLabelViewModel(x, top, $"{t:0.#}s"));
        }
    }

    private void UpdateSeriesScaleLabels()
    {
        SeriesScaleLabels.Clear();

        foreach (var (usage, series) in _chartSeriesMap)
        {
            if (!_chartSamplesMap.TryGetValue(usage, out var samples)) continue;

            double min = double.MaxValue;
            double max = double.MinValue;
            foreach (double v in samples)
            {
                double scaled = v * usage.ChartScale + usage.ChartOffset;
                if (scaled < min) min = scaled;
                if (scaled > max) max = scaled;
            }

            if (min == double.MaxValue || max == double.MinValue)
            {
                min = 0;
                max = 0;
            }

            SeriesScaleLabels.Add(new SeriesScaleLabelViewModel(
                series.Label,
                series.Stroke,
                usage.ChartScale,
                usage.ChartOffset,
                min,
                max
            ));
        }
    }

    private void UpdateYAxisLabels()
    {
        YAxisLabels.Clear();

        double min = _chartMin;
        double max = _chartMax;
        if (max <= min)
        {
            min = 0;
            max = 1;
        }

        int ticks = 20;
        for (int i = 0; i <= ticks; i++)
        {
            double value = max - ((max - min) * i / ticks);
            double y = (ChartHeight - 1) * i / ticks;
            double top = Math.Max(0, y - 8);
            YAxisLabels.Add(new AxisLabelViewModel(6, top, $"{value:0.##}"));
        }
    }

    private void UpdateAxisGrid()
    {
        XAxisGridLines.Clear();
        YAxisGridLines.Clear();

        int xDivisions = 10;
        for (int i = 0; i <= xDivisions; i++)
        {
            double x = (ChartWidth - 1) * i / xDivisions;
            XAxisGridLines.Add(new AxisLineViewModel(x, 0, x, ChartHeight - 1));
        }

        int yDivisions = 20;
        for (int i = 0; i <= yDivisions; i++)
        {
            double y = (ChartHeight - 1) * i / yDivisions;
            YAxisGridLines.Add(new AxisLineViewModel(0, y, ChartWidth - 1, y));
        }
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
                IsChartSelected = entry.IsChartSelected,
                ChartScale = entry.ChartScale,
                ChartOffset = entry.ChartOffset
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
                usage.IsChartSelected,
                usage.ChartScale,
                usage.ChartOffset
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
        bool IsChartSelected,
        double ChartScale,
        double ChartOffset
    );

    private void OpenOscilloscopeWindow()
    {
        if (_oscilloscopeWindow != null)
        {
            _oscilloscopeWindow.Activate();
            return;
        }

        var window = new VfdWpfApp.OscilloscopeWindow
        {
            DataContext = this
        };
        window.Closed += (_, _) => _oscilloscopeWindow = null;
        _oscilloscopeWindow = window;
        window.Show();
    }

    public void Dispose()
    {
        StopPolling();
        _client?.Dispose();
        _logger?.Dispose();
        _transport.Dispose();
    }
}
