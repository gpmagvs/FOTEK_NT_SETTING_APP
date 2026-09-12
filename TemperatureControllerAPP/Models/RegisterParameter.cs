using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TemperatureControllerAPP.Models;

/// <summary>
/// NT Series holding-register parameter row for DataGrid binding.
/// Addresses follow Modbus RTU Protocol Base 0 (hex value sent as-is).
/// </summary>
public sealed class RegisterParameter : INotifyPropertyChanged
{
    private string _currentValue = "—";
    private string _editValue = string.Empty;

    public ushort Address { get; init; }
    public string HexAddress => $"0x{Address:X4}";
    public int DecAddress => 40000 + Address;
    public string Code { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string CodeAndDescription => $"{Code} — {Description}";
    public string RangeOrOptions { get; init; } = string.Empty;
    public bool IsReadOnly { get; init; }
    public bool CanWrite => !IsReadOnly;
    public bool IsSigned { get; init; }
    public bool IsSiteDefault { get; init; }
    /// <summary>關鍵設定（非站點預設列，如 Lck / Id / bPS）。</summary>
    public bool IsCritical { get; init; }
    public ushort? PreferredDefault { get; init; }
    public ValueDisplayKind DisplayKind { get; init; } = ValueDisplayKind.Raw;

    /// <summary>0=一般, 1=關鍵設定, 2=站點預設（供列樣式綁定）。</summary>
    public int HighlightLevel => IsSiteDefault ? 2 : IsCritical ? 1 : 0;

    public string CurrentValue
    {
        get => _currentValue;
        set
        {
            if (_currentValue == value) return;
            _currentValue = value;
            OnPropertyChanged();
        }
    }

    public string EditValue
    {
        get => _editValue;
        set
        {
            if (_editValue == value) return;
            _editValue = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public enum ValueDisplayKind
{
    Raw,
    Signed,
    DecimalPointAware,
    EnumMap
}
