using Xunit;
using Yojana.Serialisation.Identity;

namespace Yojana.Serialisation.Tests;

/// <summary>P1-T08: GS1 and MH10.8.2 scans parse on the server after wedge separator recovery (Identity rule 11).</summary>
public sealed class ScanParserTests
{
    [Fact]
    public void A_gs1_data_matrix_scan_with_aim_prefix_and_real_separators_parses()
    {
        var scan = ScanParser.Parse("]d20109506000134352212506493661\u001d10L-2610-03\u001d11261009");
        Assert.Equal(ScanFormat.Gs1, scan.Format);
        Assert.Equal("]d2", scan.SymbologyIdentifier);
        Assert.Equal("09506000134352", scan["01"]);
        Assert.Equal("2506493661", scan["21"]);
        Assert.Equal("L-2610-03", scan["10"]);
        Assert.Equal("261009", scan["11"]);
        Assert.False(scan.HasErrors);
        Assert.Equal(0, scan.SeparatorsRecovered);
    }

    [Theory]
    [InlineData("0109506000134352212506493661<GS>10L-2610-03<GS>11261009", 2)]
    [InlineData("0109506000134352212506493661{GS}10L-2610-03{GS}11261009", 2)]
    [InlineData("0109506000134352212506493661^]10L-2610-03^]11261009", 2)]
    [InlineData("0109506000134352212506493661\\x1d10L-2610-03\\x1d11261009", 2)]
    public void Wedge_renderings_of_fnc1_are_recovered(string raw, int recovered)
    {
        var scan = ScanParser.Parse(raw);
        Assert.Equal(ScanFormat.Gs1, scan.Format);
        Assert.Equal(recovered, scan.SeparatorsRecovered);
        Assert.Equal("2506493661", scan["21"]);
        Assert.Equal("L-2610-03", scan["10"]);
        Assert.Equal("261009", scan["11"]);
    }

    [Fact]
    public void Hri_form_parses_without_separators()
    {
        var scan = ScanParser.Parse("(01)09506000134352(21)2506493445");
        Assert.Equal(ScanFormat.Gs1, scan.Format);
        Assert.Equal("2506493445", scan["21"]);
    }

    [Fact]
    public void An_iso_15434_envelope_with_mh10_data_identifiers_parses()
    {
        var scan = ScanParser.Parse("[)>\u001e06\u001d1PVX-220\u001dS2506493445\u001d1TL-2610-03\u001dQ1\u001e\u0004");
        Assert.Equal(ScanFormat.Mh10, scan.Format);
        Assert.Equal("VX-220", scan["1P"]);
        Assert.Equal("2506493445", scan["S"]);
        Assert.Equal("L-2610-03", scan["1T"]);
        Assert.Equal("1", scan["Q"]);
        Assert.False(scan.HasErrors);
    }

    [Fact]
    public void A_bare_mh10_string_and_a_uii_parse()
    {
        var scan = ScanParser.Parse("25SD12345\u001dS0001234567");
        Assert.Equal(ScanFormat.Mh10, scan.Format);
        Assert.Equal("D12345", scan["25S"]);
        Assert.Equal("0001234567", scan["S"]);
        Assert.Equal("UII construct 2 (IAC + EID + serial)", scan.Fields[0].Description);
    }

    [Fact]
    public void A_plain_serial_is_plain()
    {
        var scan = ScanParser.Parse("  2506-493445 ");
        Assert.Equal(ScanFormat.Plain, scan.Format);
        Assert.Equal("2506-493445", scan.Cleaned);
        Assert.Empty(scan.Fields);
        Assert.Null(scan.SymbologyIdentifier);
    }

    [Theory]
    [InlineData("1721 0000007724188-P001291")]
    [InlineData("1721 0000007724188-P001297")]
    public void A_memory_module_serial_that_starts_with_ai_like_digits_stays_plain(string raw)
    {
        // Real laptop memory module QR payloads (owner's DS2208 scans, 2026-10-25).
        var scan = ScanParser.Parse(raw);
        Assert.Equal(ScanFormat.Plain, scan.Format);
        Assert.Equal(raw, scan.Cleaned);
        Assert.False(scan.HasErrors);
    }

    [Fact]
    public void A_carton_code_carrying_only_a_gtin_parses_as_gs1()
    {
        var scan = ScanParser.Parse("0104043752177374");
        Assert.Equal(ScanFormat.Gs1, scan.Format);
        Assert.Equal("04043752177374", scan["01"]);
        Assert.False(scan.HasErrors);
    }

    [Fact]
    public void Sixteen_digits_starting_01_with_a_wrong_gtin_check_digit_stay_plain()
    {
        var scan = ScanParser.Parse("0104043752177375");
        Assert.Equal(ScanFormat.Plain, scan.Format);
    }

    [Fact]
    public void A_gs1_scan_with_a_bad_check_digit_reports_the_error_on_that_element()
    {
        var scan = ScanParser.Parse("]C10109506000134353212506493661");
        Assert.Equal(ScanFormat.Gs1, scan.Format);
        Assert.True(scan.HasErrors);
        Assert.Null(scan["01"]);
        Assert.NotEmpty(scan.Fields.First(f => f.Key == "01").Errors);
    }
}
