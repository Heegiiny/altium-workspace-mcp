using AltiumWorkspaceMCP.Vault;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>Typed parameter numbers: without them Altium clears the values on save.</summary>
public sealed class ParameterValueCodecTests
{
    private const string Voltage = "28379228-D94F-4F3B-8038-3FE85C36E292";
    private const string Current = "BC2897DA-2143-4E76-8318-50A159276C56";
    private const string Temperature = "AB1C0C5E-0234-4A88-9ADD-184638343C79";
    private const string Capacitance = "0629FA77-BB3E-4045-91F0-CC1164D3D0AA";
    private const string Resistance = "B90F0DAE-B695-41F5-BCB0-0DE5F75C9E50";
    private const string Percent = "935791AE-95D8-4D5E-B810-E9B66A56E5A5";
    private const string Frequency = "F4CAEC49-EE33-46C9-AF4A-260721F7AA26";

    [Theory]
    [InlineData("5.5V", Voltage, "5.50000000000000E+0000")]
    [InlineData("3.3 V", Voltage, "3.30000000000000E+0000")]
    [InlineData("100mV", Voltage, "1.00000000000000E-0001")]
    [InlineData("100µA", Current, "1.00000000000000E-0004")] // U+00B5, as Altium writes it
    [InlineData("100μA", Current, "1.00000000000000E-0004")] // the Greek mu
    [InlineData("70°C", Temperature, "7.00000000000000E+0001")]
    [InlineData("-40°C", Temperature, "-4.00000000000000E+0001")]
    [InlineData("100nF", Capacitance, "1.00000000000000E-0007")]
    [InlineData("4.7k", Resistance, "4.70000000000000E+0003")]
    [InlineData("4.7kΩ", Resistance, "4.70000000000000E+0003")]
    [InlineData("55.6696m", Resistance, "5.56696000000000E-0002")]
    [InlineData("1%", Percent, "1.00000000000000E+0000")]
    [InlineData("35MHz", Frequency, "3.50000000000000E+0007")]
    public void RealValueForKnownDisplay(string display, string type, string expected) =>
        Assert.Equal(expected, ParameterValueCodec.RealValueFor(display, type));

    [Fact]
    public void DecimalCommaIsAcceptedOnlyWithoutPoint()
    {
        Assert.Equal("4.70000000000000E+0003", ParameterValueCodec.RealValueFor("4,7k", Resistance));
        Assert.Equal("4.7k", ParameterValueCodec.NormalizeDisplay("4,7k", Resistance));

        // A comma together with a point is not decimal, the value does not parse.
        Assert.Null(ParameterValueCodec.RealValueFor("1,000.5V", Voltage));
    }

    [Theory]
    [InlineData("abc", Voltage)]
    [InlineData("5V", Current)] // a foreign unit
    [InlineData("5m%", Percent)] // percent has no prefixes
    [InlineData("V", Voltage)]
    [InlineData("5.5.5V", Voltage)]
    public void UnparsableValueGivesNoNumber(string display, string type)
    {
        Assert.False(ParameterValueCodec.TryParse(display, type, out _));
        Assert.Null(ParameterValueCodec.RealValueFor(display, type));
    }

    [Fact]
    public void TextAndEmptyValuesHaveEmptyRealValue()
    {
        Assert.Equal(string.Empty, ParameterValueCodec.RealValueFor("anything", ParameterValueCodec.TextTypeGuid));
        Assert.Equal(string.Empty, ParameterValueCodec.RealValueFor("anything", null));
        Assert.Equal(string.Empty, ParameterValueCodec.RealValueFor("", Voltage));
        Assert.Equal(string.Empty, ParameterValueCodec.RealValueFor("  ", Voltage));
    }

    [Fact]
    public void ZeroUsesAltiumZeroString()
    {
        Assert.Equal(ParameterValueCodec.Zero, ParameterValueCodec.Format(0));
        Assert.Equal(ParameterValueCodec.Zero, ParameterValueCodec.RealValueFor("0V", Voltage));
    }

    [Fact]
    public void ExplainSaysWhyAndStaysSilentForGoodValues()
    {
        string? problem = ParameterValueCodec.Explain("Vmax", "many", Voltage, "5.5V");

        Assert.NotNull(problem);
        Assert.Contains("Vmax", problem);
        Assert.Contains("5.5V", problem);

        Assert.Null(ParameterValueCodec.Explain("Vmax", "5.5V", Voltage));
        Assert.Null(ParameterValueCodec.Explain("Note", "any text", ParameterValueCodec.TextTypeGuid));
    }

    [Fact]
    public void TypeIntrospection()
    {
        Assert.True(ParameterValueCodec.IsKnown(Voltage));
        Assert.False(ParameterValueCodec.IsKnown(ParameterValueCodec.TextTypeGuid));
        Assert.Equal("Voltage", ParameterValueCodec.DescribeType(Voltage));
        Assert.Equal("Text", ParameterValueCodec.DescribeType(null));
        Assert.Equal("V", ParameterValueCodec.UnitOf(Voltage));
    }
}
