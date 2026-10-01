using System.Globalization;
using AltiumWorkspaceMCP.Soap.Vault;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>
/// The numeric representation of typed parameters — what Altium stores in the
/// <c>ParameterRealValue</c> field next to the displayed value.
/// </summary>
/// <remarks>
/// Altium Designer works with a number, not with the displayed string, for all types
/// except text. If there is no number, Single Component Editor considers the value
/// invalid and writes an empty string when the component is saved — this is how, on a manual
/// save, temperatures and voltages set without a number were zeroed.
///
/// The converter itself is native in Altium, so
/// the rules here are restored from the values Altium wrote to the vault:
/// the type's unit, SI prefixes and the format <c>d.ddddddddddddddE±dddd</c>. Correctness
/// is checked by the <c>codec-check</c> command over the whole vault history.
/// </remarks>
public static class ParameterValueCodec
{
    /// <summary>The text type: it has no numeric representation.</summary>
    public const string TextTypeGuid = "4884412E-AAD1-4E69-922A-23C1C75250B1";

    /// <summary>The string Altium writes for zero.</summary>
    public const string Zero = "0.00000000000000E+0000";

    private sealed record Unit(string Name, string Symbol, bool AllowsPrefixes, string[] Aliases);

    private static readonly Dictionary<string, Unit> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0629FA77-BB3E-4045-91F0-CC1164D3D0AA"] = new("Capacitance", "F", true, []),
        ["DE23CE7F-95E4-475E-85A6-1F1B632FCE41"] = new("Charge", "C", true, []),
        ["082973D3-88CB-4C6D-8CC2-0F859D1DFF8C"] = new("Conductance", "S", true, []),
        ["BC2897DA-2143-4E76-8318-50A159276C56"] = new("Current", "A", true, []),
        ["5A9430B6-0CEE-4421-9B4D-C4D85138E548"] = new("Decibels", "dB", false, []),
        ["F4CAEC49-EE33-46C9-AF4A-260721F7AA26"] = new("Frequency", "Hz", true, []),
        ["E8BEA88F-C77E-4170-AA20-C18311195A8F"] = new("Impedance", string.Empty, true, ["Ω", "Ohm", "ohm"]),
        ["DDF49AC0-734C-4C3E-82A9-0D5109AF02FB"] = new("Inductance", "H", true, []),
        ["B0C104C5-1A99-4DE5-A539-D5FC817D4B3E"] = new("Length", "m", true, []),
        ["53176ED9-426D-47B6-A321-17B6CBE8A274"] = new("Mass", "g", true, []),
        ["935791AE-95D8-4D5E-B810-E9B66A56E5A5"] = new("Percent", "%", false, []),
        ["A40E567D-1324-4823-8379-FB7E896E18B9"] = new("Power", "W", true, []),
        // For resistance Altium does not write the unit: "4.7k", "55.6696m".
        ["B90F0DAE-B695-41F5-BCB0-0DE5F75C9E50"] = new("Resistance", string.Empty, true, ["Ω", "Ohm", "ohm"]),
        ["AB1C0C5E-0234-4A88-9ADD-184638343C79"] = new("Temperature", "°C", false, ["C", "°"]),
        ["9235072D-7541-4D79-BF65-C15ECBB62700"] = new("Time", "s", true, []),
        ["28379228-D94F-4F3B-8038-3FE85C36E292"] = new("Voltage", "V", true, []),
    };

    private static readonly Dictionary<char, decimal> Prefixes = new()
    {
        ['f'] = 0.000000000000001m,
        ['p'] = 0.000000000001m,
        ['n'] = 0.000000001m,
        ['u'] = 0.000001m,
        ['µ'] = 0.000001m, // U+00B5, as Altium writes it
        ['μ'] = 0.000001m, // U+03BC, the Greek letter
        ['m'] = 0.001m,
        ['k'] = 1000m,
        ['K'] = 1000m,
        ['M'] = 1000000m,
        ['G'] = 1000000000m,
        ['T'] = 1000000000000m,
    };

    public static bool IsText(string? typeGuid) =>
        string.IsNullOrEmpty(typeGuid) || string.Equals(typeGuid, TextTypeGuid, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the type is known, that is, whether the codec can get a number for it.</summary>
    public static bool IsKnown(string? typeGuid) => typeGuid is not null && Units.ContainsKey(typeGuid);

    /// <summary>The type name for messages: Voltage, Temperature, etc.</summary>
    public static string DescribeType(string? typeGuid) =>
        IsText(typeGuid) ? "Text" : typeGuid is not null && Units.TryGetValue(typeGuid, out Unit? unit) ? unit.Name : typeGuid!;

    /// <summary>The unit in which Altium shows the value: V, °C, % etc.</summary>
    public static string? UnitOf(string? typeGuid) =>
        typeGuid is not null && Units.TryGetValue(typeGuid, out Unit? unit) ? unit.Symbol : null;

    /// <summary>
    /// Parses a displayed value into a number in the type's base units.
    /// Strictly: extraneous text or a foreign unit make the value unparsed,
    /// so as not to write a wrong number to the vault.
    /// </summary>
    public static bool TryParse(string? display, string? typeGuid, out double value)
    {
        value = 0;

        if (string.IsNullOrWhiteSpace(display) || typeGuid is null || !Units.TryGetValue(typeGuid, out Unit? unit))
        {
            return false;
        }

        string text = display.Trim().Replace(" ", string.Empty).Replace(" ", string.Empty);

        // A decimal comma is allowed only if there is no point at all.
        if (text.Contains(',') && !text.Contains('.'))
        {
            text = text.Replace(',', '.');
        }

        foreach (string suffix in new[] { unit.Symbol }.Concat(unit.Aliases)
                     .Where(candidate => candidate.Length > 0)
                     .OrderByDescending(candidate => candidate.Length))
        {
            if (text.EndsWith(suffix, StringComparison.Ordinal))
            {
                text = text[..^suffix.Length];
                break;
            }
        }

        decimal multiplier = 1m;
        if (unit.AllowsPrefixes && text.Length > 1 && Prefixes.TryGetValue(text[^1], out decimal prefix))
        {
            multiplier = prefix;
            text = text[..^1];
        }

        if (text.Length == 0 || !char.IsAsciiDigit(text[^1]))
        {
            return false;
        }

        if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal exact))
        {
            try
            {
                value = (double)(exact * multiplier);
                return true;
            }
            catch (OverflowException)
            {
                // Out of the decimal range — below we count in double.
            }
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double approximate)
            && double.IsFinite(approximate))
        {
            value = approximate * (double)multiplier;
            return true;
        }

        return false;
    }

    /// <summary>The number in the form in which Altium writes it: <c>7.00000000000000E+0001</c>.</summary>
    public static string Format(double value) =>
        value == 0 || !double.IsFinite(value)
            ? Zero
            : value.ToString("0.00000000000000E+0000", CultureInfo.InvariantCulture);

    /// <summary>
    /// The value of the <c>ParameterRealValue</c> field for writing.
    /// </summary>
    /// <returns>
    /// An empty string for text and unfilled parameters — as Altium does;
    /// a number — for parsed values; <see langword="null"/> if the value
    /// of a typed parameter could not be parsed.
    /// </returns>
    public static string? RealValueFor(string? display, string? typeGuid)
    {
        if (string.IsNullOrWhiteSpace(display) || IsText(typeGuid))
        {
            return string.Empty;
        }

        return TryParse(display, typeGuid, out double value) ? Format(value) : null;
    }

    /// <summary>
    /// Brings an entered value to the form in which Altium stores numbers: without edge
    /// spaces and with a decimal point. Text values are not touched.
    /// </summary>
    public static string NormalizeDisplay(string? value, string? typeGuid)
    {
        if (value is null || IsText(typeGuid))
        {
            return value ?? string.Empty;
        }

        string trimmed = value.Trim();

        return trimmed.Contains(',') && !trimmed.Contains('.') && TryParse(trimmed, typeGuid, out _)
            ? trimmed.Replace(',', '.')
            : trimmed;
    }

    /// <summary>
    /// Explanation of why a value cannot be written to a parameter of this type;
    /// <see langword="null"/> if it can be written.
    /// </summary>
    public static string? Explain(string name, string? value, string? typeGuid, string? example = null)
    {
        if (!IsKnown(typeGuid) || RealValueFor(value, typeGuid) is not null)
        {
            return null;
        }

        string unit = UnitOf(typeGuid) is { Length: > 0 } symbol ? $" ({symbol})" : string.Empty;
        string hint = string.IsNullOrWhiteSpace(example) ? string.Empty : $"; a sample value in the vault: '{example}'";

        return $"'{name}' = '{value}' does not parse as {DescribeType(typeGuid)}{unit}{hint}. "
            + "Altium stores a number for such a parameter and on saving the component would clear a value without it.";
    }

    /// <summary>
    /// The <c>ParameterRealValue</c> value for a parameter being written.
    /// </summary>
    /// <remarks>
    /// A parsed value gets a number — including an unchanged one that had no number:
    /// this is how any edit of a component also fixes the values written
    /// earlier without a number. An unchanged unparsable value keeps the old field as is,
    /// while a new unparsable value of a typed parameter is rejected: Altium would not
    /// accept it anyway.
    /// </remarks>
    public static string? RealValueForWrite(
        string name,
        string? value,
        string? typeGuid,
        ALU_ItemRevisionParameter? previous)
    {
        string? real = RealValueFor(value, typeGuid);
        if (real is not null)
        {
            return real;
        }

        if (previous is not null && string.Equals(previous.ParameterValue, value, StringComparison.Ordinal))
        {
            return previous.ParameterRealValue;
        }

        return IsKnown(typeGuid)
            ? throw new InvalidOperationException(Explain(name, value, typeGuid))
            : string.Empty;
    }
}
