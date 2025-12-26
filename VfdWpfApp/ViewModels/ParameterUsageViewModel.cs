namespace VfdWpfApp.ViewModels;

public enum ParameterUsageMode
{
    Polling,
    Single
}

public enum ParameterActionType
{
    Read,
    Write
}

public sealed class ParameterUsageViewModel : NotifyBase
{
    public ParameterRowViewModel Parameter { get; }

    public string AddressHex => Parameter.AddressHex;
    public string Name => Parameter.Name;
    public string Access => Parameter.Access;
    public string LastValueDec => Parameter.LastValueDec;
    public string LastValueHex => Parameter.LastValueHex;

    private ParameterUsageMode _mode = ParameterUsageMode.Polling;
    public ParameterUsageMode Mode { get => _mode; set => Set(ref _mode, value); }

    private ParameterActionType _action = ParameterActionType.Read;
    public ParameterActionType Action { get => _action; set => Set(ref _action, value); }

    private int _pollIntervalMs = 200;
    public int PollIntervalMs { get => _pollIntervalMs; set => Set(ref _pollIntervalMs, Math.Max(20, value)); }

    private string _writeValueU16 = "0";
    public string WriteValueU16 { get => _writeValueU16; set => Set(ref _writeValueU16, value); }

    private double _chartScale = 1.0;
    public double ChartScale { get => _chartScale; set => Set(ref _chartScale, Math.Max(0.0001, value)); }

    private bool _isChartSelected;
    public bool IsChartSelected { get => _isChartSelected; set => Set(ref _isChartSelected, value); }

    public long LastPollTick { get; set; }

    public ParameterUsageViewModel(ParameterRowViewModel parameter)
    {
        Parameter = parameter;
        Parameter.PropertyChanged += (_, _) =>
        {
            Raise(nameof(LastValueDec));
            Raise(nameof(LastValueHex));
        };
    }
}
