using System.Collections.ObjectModel;

namespace TemperatureControllerAPP.Models;

/// <summary>
/// NT Series register map (Protocol Base 0), arranged like the manual's 3-column sheet.
/// </summary>
public static class NtRegisterMap
{
    public const ushort Pv = 0x0041;
    public const ushort Sv = 0x0023;
    public const ushort ControllerOnOff = 0x0024;
    public const ushort OutputAlarmStatus = 0x0044;
    public const ushort AlarmErrorStatus = 0x0048;
    public const ushort DecimalPoint = 0x0019;
    public const ushort Ct = 0x000F;
    public const ushort Hys = 0x0010;
    public const ushort Int = 0x0017;
    public const ushort HeatingCooling = 0x001B;

    public static readonly (ushort Address, ushort Value, string Code)[] SiteDefaults =
    [
        (Ct, 0, "CT"),
        (Hys, 1, "HYS"),
        (Int, 1, "Int"),
        (HeatingCooling, 1, "H_C")
    ];

    /// <summary>規格書左欄：0x0001–0x000E</summary>
    public static ObservableCollection<RegisterParameter> CreateColumn1() =>
    [
        P(0x0001, "Lck", "Lock setting", "0~3"),
        P(0x0002, "AL1", "#1 alarm", "-0999~9999", signed: true),
        P(0x0003, "AL2", "#2 alarm", "-0999~9999", signed: true),
        P(0x0004, "tnr", "Process timer", "Read only / Alarm mode", readOnly: true),
        P(0x0005, "ALH", "Hysteresis", "0~999"),
        P(0x0006, "t", "Flicker timer", "0~99 S"),
        P(0x0007, "SLH", "High limit of set", "0000~9999"),
        P(0x0008, "Out", "Limit of out", "0~100"),
        P(0x0009, "nUn", "Manual output volume", "0~100"),
        P(0x000A, "Hb", "Current setting / dSPH", "0~99.99 / -1999~9999"),
        P(0x000B, "CtL", "Min CT / dSPL", "-9.99~99.99 / -1999~9999", signed: true),
        P(0x000C, "Cth", "Max CT value", "0~99.99"),
        P(0x000D, "rAP", "Ramp control", "0~9999 °C/min"),
        P(0x000E, "Lot", "Min output volume", "0~100")
    ];

    /// <summary>規格書中欄：0x000F–0x001E（含站點預設）</summary>
    public static ObservableCollection<RegisterParameter> CreateColumn2() =>
    [
        P(Ct, "CT", "Cycle time", "0000–0099 S · 站點預設=0", preferred: 0, site: true, edit: "0"),
        P(Hys, "HYS", "Hysteresis", "0000–9999 · 站點預設=1", preferred: 1, site: true, edit: "1"),
        P(0x0011, "At", "Auto-tuning", "0=Controlling, 1=Auto-tuning", kind: ValueDisplayKind.EnumMap),
        P(0x0012, "Tu", "Auto-tuning bias", "0000–0999"),
        P(0x0013, "P", "Proportion band", "0000–0999"),
        P(0x0014, "I", "Integral time", "0000–3999"),
        P(0x0015, "D", "Derivative time", "0000–3999"),
        P(0x0016, "GAin", "Gain", "0.0–9.9"),
        P(Int, "Int", "Input type", "0=Pt,1=K,2=J,3=R,4=S,5=T,6=B,7=E,8=N,9=L · 預設=1(K)",
            preferred: 1, site: true, edit: "1", kind: ValueDisplayKind.EnumMap),
        P(0x0018, "Unt", "Unit", "0=°C, 1=°F", kind: ValueDisplayKind.EnumMap),
        P(0x0019, "dP", "Decimal point", "0=None, 1=One decimal", kind: ValueDisplayKind.EnumMap),
        P(0x001A, "Sht", "Input shift", "-0999~0099", signed: true),
        P(HeatingCooling, "H_C", "Heating/Cooling", "0=Heating, 1=Cooling · 預設=1",
            preferred: 1, site: true, edit: "1", kind: ValueDisplayKind.EnumMap),
        P(0x001C, "ALT", "Alarm mode", "00–18"),
        P(0x001D, "Id", "Station No.", "01H–FFH"),
        P(0x001E, "RS", "Communication mode", "Refer to manual")
    ];

    /// <summary>規格書右欄：通訊 / SV / PV / 狀態</summary>
    public static ObservableCollection<RegisterParameter> CreateColumn3() =>
    [
        P(0x001F, "bPS", "Baud rate", "Refer to manual"),
        P(0x0020, "bit", "Data configuration", "Parity / stop bits"),
        P(0x0021, "Ft", "Filter / Deadband", "Ft 1–50; db 0–9999"),
        P(Sv, "SV", "Setting value", "-0999~9999", signed: true, kind: ValueDisplayKind.DecimalPointAware),
        P(ControllerOnOff, "ON OFF", "Controller ON/OFF", "0=ON, 1=OFF", kind: ValueDisplayKind.EnumMap),
        P(0x0025, "M A", "Auto/Manual", "0=Auto, 1=Manual", kind: ValueDisplayKind.EnumMap),
        P(0x0027, "SV2", "Soft start", "0–9999"),
        P(Pv, "PV", "Process value", "Read only -0999~9999", readOnly: true, signed: true, kind: ValueDisplayKind.DecimalPointAware),
        P(0x0042, "Un", "Output volume", "Read only 0–100", readOnly: true),
        P(0x0043, "Ctu", "Process current", "Read only 0–99.99", readOnly: true),
        P(OutputAlarmStatus, "Out/AL", "Out1/Out2/AL1/AL2 status", "Bit0–3", readOnly: true),
        P(0x0045, "AL1", "Alarm1 (read)", "-999~9999", signed: true),
        P(0x0046, "AL2", "Alarm2 (read)", "-999~9999", signed: true),
        P(AlarmErrorStatus, "AL Err", "Alarm error status", "Bit0 FFF / Bit1 --- / Bit2 HtEr / Bit3 OhEr", readOnly: true)
    ];

    public static string FormatRaw(ushort raw, RegisterParameter p, int decimalPoint = 0)
    {
        return p.DisplayKind switch
        {
            ValueDisplayKind.Signed => FormatSigned(raw, decimalPoint),
            ValueDisplayKind.DecimalPointAware => FormatSigned(raw, decimalPoint),
            ValueDisplayKind.EnumMap => FormatEnum(p.Address, raw),
            _ => raw.ToString()
        };
    }

    public static string FormatSigned(ushort raw, int decimalPoint = 0)
    {
        short signed = unchecked((short)raw);
        if (decimalPoint <= 0)
            return signed.ToString();

        double scaled = signed / Math.Pow(10, decimalPoint);
        return scaled.ToString($"F{decimalPoint}");
    }

    public static bool TryParseWriteValue(string text, RegisterParameter p, int decimalPoint, out ushort value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        text = text.Trim();

        if (int.TryParse(text, out var intVal))
        {
            if (p.IsSigned || p.DisplayKind is ValueDisplayKind.Signed or ValueDisplayKind.DecimalPointAware)
            {
                if (intVal is < short.MinValue or > short.MaxValue)
                    return false;
                value = unchecked((ushort)(short)intVal);
                return true;
            }

            if (intVal is < 0 or > ushort.MaxValue)
                return false;
            value = (ushort)intVal;
            return true;
        }

        if ((p.IsSigned || p.DisplayKind is ValueDisplayKind.DecimalPointAware)
            && double.TryParse(text, out var dbl))
        {
            var scaled = (int)Math.Round(dbl * Math.Pow(10, Math.Max(0, decimalPoint)));
            if (scaled is < short.MinValue or > short.MaxValue)
                return false;
            value = unchecked((ushort)(short)scaled);
            return true;
        }

        return false;
    }

    private static RegisterParameter P(
        ushort address,
        string code,
        string description,
        string range,
        bool readOnly = false,
        bool signed = false,
        ValueDisplayKind kind = ValueDisplayKind.Raw,
        ushort? preferred = null,
        bool site = false,
        string? edit = null)
    {
        var p = new RegisterParameter
        {
            Address = address,
            Code = code,
            Description = description,
            RangeOrOptions = range,
            IsReadOnly = readOnly,
            IsSigned = signed,
            DisplayKind = kind,
            PreferredDefault = preferred,
            IsSiteDefault = site
        };
        if (edit is not null)
            p.EditValue = edit;
        return p;
    }

    private static string FormatEnum(ushort address, ushort raw) => address switch
    {
        0x0011 => raw switch { 0 => "0 — Controlling", 1 => "1 — Auto-tuning", _ => raw.ToString() },
        0x001B => raw switch { 0 => "0 — Heating", 1 => "1 — Cooling", _ => raw.ToString() },
        0x0017 => raw switch
        {
            0 => "0 — Pt", 1 => "1 — K", 2 => "2 — J", 3 => "3 — R", 4 => "4 — S",
            5 => "5 — T", 6 => "6 — B", 7 => "7 — E", 8 => "8 — N", 9 => "9 — L",
            _ => raw.ToString()
        },
        0x0018 => raw switch { 0 => "0 — °C", 1 => "1 — °F", _ => raw.ToString() },
        0x0019 => raw switch { 0 => "0 — None", 1 => "1 — One decimal", _ => raw.ToString() },
        0x0024 => raw switch { 0 => "0 — ON", 1 => "1 — OFF", _ => raw.ToString() },
        0x0025 => raw switch { 0 => "0 — Auto", 1 => "1 — Manual", _ => raw.ToString() },
        _ => raw.ToString()
    };
}
