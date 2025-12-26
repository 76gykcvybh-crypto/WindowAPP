using VfdWpfApp.Models;

namespace VfdWpfApp.ViewModels;

public enum ParameterUsageMode
{
    Unused,
    Polling,
    Single
}

public enum ParameterActionType
{
    Read,
    Write
}

public sealed class ParameterRowViewModel : NotifyBase
{
    public ParameterDefinition Def { get; }

    public string AddressHex => Def.AddressHex;
    public string Name => Def.Name;
    public string Access => Def.Access;
    public string Unit => Def.Unit ?? "";
    public string Description => Def.Description;

    private ParameterUsageMode _usageMode = ParameterUsageMode.Unused;
    public ParameterUsageMode UsageMode { get => _usageMode; set => Set(ref _usageMode, value); }

    private ParameterActionType _pollAction = ParameterActionType.Read;
    public ParameterActionType PollAction { get => _pollAction; set => Set(ref _pollAction, value); }

    private int _pollIntervalMs = 200;
    public int PollIntervalMs { get => _pollIntervalMs; set => Set(ref _pollIntervalMs, Math.Max(20, value)); }

    private ParameterActionType _singleAction = ParameterActionType.Read;
    public ParameterActionType SingleAction { get => _singleAction; set => Set(ref _singleAction, value); }

    private string _writeValueU16 = "0";
    public string WriteValueU16 { get => _writeValueU16; set => Set(ref _writeValueU16, value); }

    public long LastPollTick { get; set; }

    private ushort? _lastValue;
    public string LastValueDec => _lastValue is null ? "" : _lastValue.Value.ToString();
    public string LastValueHex => _lastValue is null ? "" : $"0x{_lastValue.Value:X4}";

    public ParameterRowViewModel(ParameterDefinition def) => Def = def;

    public void SetLastValue(ushort value)
    {
        _lastValue = value;
        Raise(nameof(LastValueDec));
        Raise(nameof(LastValueHex));
    }
}
