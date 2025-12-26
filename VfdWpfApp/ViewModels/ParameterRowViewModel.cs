using VfdWpfApp.Models;

using System.Collections.ObjectModel;

namespace VfdWpfApp.ViewModels;

public sealed class ParameterRowViewModel : NotifyBase
{
    public ParameterDefinition Def { get; }

    public string AddressHex => Def.AddressHex;
    public string Name => Def.Name;
    public string Access => Def.Access;
    public string Unit => Def.Unit ?? "";
    public string Description => Def.Description;

    public ObservableCollection<ParameterUsageViewModel> Usages { get; } = new();

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
