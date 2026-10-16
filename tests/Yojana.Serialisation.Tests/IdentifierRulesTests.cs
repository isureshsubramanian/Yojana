using Xunit;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Identity;

namespace Yojana.Serialisation.Tests;

/// <summary>P1-T07: normalisation, validators and symbology rules (ADR-0001 Identity rules 1–4, AI 41, cases 8, 9, 139).</summary>
public sealed class IdentifierRulesTests
{
    private static readonly IdentifierPolicySet Policies = new();

    [Theory]
    [InlineData(IdentifierType.InternalSerial, " 2506-493445 ", "2506493445")]
    [InlineData(IdentifierType.ManufacturerSerial, "hgi 2506 493445", "HGI2506493445")]
    [InlineData(IdentifierType.Imei, "35-693803-564380-9", "356938035643809")]
    [InlineData(IdentifierType.Mac, "00:1a:2b:3c:4d:5e", "001A2B3C4D5E")]
    [InlineData(IdentifierType.Sgtin, "(01)09506000134352(21)abcDEF", "(01)09506000134352(21)abcDEF")]
    public void Normalisation_follows_the_type_policy(string type, string raw, string expected) =>
        Assert.Equal(expected, IdentifierNormaliser.Normalise(raw, Policies.For(type)).Value);

    [Fact]
    public void Code39_start_stop_asterisks_are_scanner_artefacts_but_an_inner_asterisk_is_refused()
    {
        var policy = Policies.For(IdentifierType.Vin);
        var normalised = IdentifierNormaliser.Normalise("*1HGCM82633A004352*", policy);
        Assert.Equal("1HGCM82633A004352", normalised.Value);
        Assert.Equal(["leading_asterisk", "trailing_asterisk"], normalised.ArtefactsStripped);
        Assert.True(IdentifierValidator.Validate(normalised.Value, policy).IsValid);

        var inner = IdentifierNormaliser.Normalise("1HGCM8*633A004352", policy);
        var outcome = IdentifierValidator.Validate(inner.Value, policy);
        Assert.False(outcome.IsValid);
        Assert.Contains("asterisk_not_encodable_in_code39", outcome.Reasons);
        Assert.True(outcome.Blocks);
    }

    [Fact]
    public void Leading_zero_policy_strips_only_when_asked()
    {
        var keep = Policies.For(IdentifierType.InternalSerial);
        Assert.Equal("000123", IdentifierNormaliser.Normalise("000123", keep).Value);
        var strip = keep with { LeadingZeros = LeadingZeroPolicy.Strip };
        Assert.Equal("123", IdentifierNormaliser.Normalise("000123", strip).Value);
        Assert.Equal("0", IdentifierNormaliser.Normalise("0000", strip).Value);
        Assert.Equal("00A1", IdentifierNormaliser.Normalise("00A1", strip).Value); // not all digits: untouched
    }

    [Theory]
    [InlineData("1HGCM82633A004352", true)]
    [InlineData("1HGCM82633A004353", false)]
    [InlineData("1HGCM8263IA004352", false)] // letter I never appears in a VIN
    [InlineData("1HGCM82633A00435", false)]
    public void Vin_position_nine_check_digit(string vin, bool valid) =>
        Assert.Equal(valid, IdentifierValidator.Validate(vin, Policies.For(IdentifierType.Vin)).IsValid);

    [Theory]
    [InlineData("356938035643809", true)]
    [InlineData("356938035643808", false)]
    [InlineData("35693803564380", false)]
    public void Imei_luhn(string imei, bool valid) =>
        Assert.Equal(valid, IdentifierValidator.Validate(imei, Policies.For(IdentifierType.Imei)).IsValid);

    [Theory]
    [InlineData("09506000134352", true)]   // GTIN-14
    [InlineData("4006381333931", true)]    // GTIN-13
    [InlineData("4006381333932", false)]
    [InlineData("96385074", true)]         // GTIN-8
    [InlineData("012345678905", true)]     // GTIN-12
    public void Gtin_mod_10(string gtin, bool valid) => Assert.Equal(valid, IdentifierValidator.Gtin(gtin).Count == 0);

    [Theory]
    [InlineData("003400000000000018", false)]
    [InlineData("106141411234567897", true)]
    public void Sscc_mod_10(string sscc, bool valid) => Assert.Equal(valid, IdentifierValidator.Sscc(sscc).Count == 0);

    [Fact]
    public void Gs1_check_digit_helper_matches_the_validator()
    {
        var body = "0950600013435";
        var full = body + IdentifierValidator.Gs1CheckDigit(body);
        Assert.Equal("09506000134352", full);
        Assert.True(IdentifierValidator.Gs1Mod10(full));
    }

    [Fact]
    public void Gs1_element_strings_parse_in_hri_and_fnc1_form_and_report_bad_check_digits()
    {
        var hri = Gs1ElementStrings.Parse("(01)09506000134352(21)2506493445(10)L-2610-03");
        Assert.Equal(["01", "21", "10"], hri.Select(e => e.ApplicationIdentifier));
        Assert.Equal("09506000134352", hri[0].Value);
        Assert.Equal("2506493445", hri[1].Value);
        Assert.All(hri, e => Assert.Empty(e.Errors));

        var fnc1 = Gs1ElementStrings.Parse("0109506000134352212506493445\u001d10L-2610-03\u001d11261009");
        Assert.Equal(["01", "21", "10", "11"], fnc1.Select(e => e.ApplicationIdentifier));
        Assert.Equal("261009", fnc1[3].Value);

        var bad = Gs1ElementStrings.Validate("(01)09506000134353(21)X");
        Assert.NotEmpty(bad);
        Assert.Contains(bad, r => r.StartsWith("gs1_ai_01", StringComparison.Ordinal));

        var sgtin = IdentifierValidator.Validate("(01)09506000134352(21)2506493445", Policies.For(IdentifierType.Sgtin));
        Assert.True(sgtin.IsValid);
    }

    [Theory]
    [InlineData("09506000134352", true)]                     // GS1 DI
    [InlineData("(01)09506000134352(17)281231(10)L1", true)]  // GS1 DI inside a full UDI carrier
    [InlineData("+A99912345/$$52001510X3C", false)]           // HIBC with a wrong check character
    [InlineData("=/A9999XYZ100T0944", true)]                  // ICCBBA structural
    [InlineData("notaudi", false)]
    public void Udi_device_identifier(string di, bool valid) =>
        Assert.Equal(valid, IdentifierValidator.Validate(di, Policies.For(IdentifierType.UdiDi)).IsValid);

    [Fact]
    public void Hibc_primary_code_check_character()
    {
        // Compute a valid HIBC primary code and confirm the check, then break it.
        const string body = "+A99912345/$$52001510X3";
        const string set = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-. $/+%";
        var check = set[body.Sum(c => set.IndexOf(c, StringComparison.Ordinal)) % 43];
        Assert.Empty(IdentifierValidator.HibcPrimary(body + check));
        Assert.Equal(["hibc_check_character"], IdentifierValidator.HibcPrimary(body + (check == 'A' ? 'B' : 'A')));
    }

    [Theory]
    [InlineData("D12345ABC98765432", true)]     // concatenated construct 1 (CAGE + serial)
    [InlineData("25SD123450001234567", true)]   // DI form start without separators is treated as concatenated
    [InlineData("25SD12345\u001dS0001234567", true)]
    [InlineData("99XD12345\u001dS0001234567", false)]
    [InlineData("short", false)]
    public void Uii_structure(string uii, bool valid) => Assert.Equal(valid, IdentifierValidator.Uii(uii).Count == 0);

    [Theory]
    [InlineData("001A2B3C4D5E", true)]
    [InlineData("001A2B3C4D5", false)]
    [InlineData("001A2B3C4D5G", false)]
    public void Mac_address(string mac, bool valid) => Assert.Equal(valid, IdentifierValidator.Mac(mac).Count == 0);

    [Fact]
    public void Scope_key_follows_identity_rule_1()
    {
        var item = Guid.NewGuid();
        var owner = Guid.NewGuid();
        Assert.Equal(item.ToString("N"), Policies.For(IdentifierType.InternalSerial).ScopeKeyFor(item, owner));
        Assert.Equal(ScopeKeys.Tenant, Policies.For(IdentifierType.Imei).ScopeKeyFor(item, owner));
        Assert.Equal("owner:" + owner.ToString("N"), Policies.For(IdentifierType.CustomerAssetTag).ScopeKeyFor(item, owner));
        var global = Policies.For(IdentifierType.InternalSerial) with { GlobalUniqueness = true };
        Assert.Equal(ScopeKeys.Tenant, global.ScopeKeyFor(item, owner));
    }

    [Fact]
    public void Regulated_types_block_and_unregulated_types_warn()
    {
        Assert.True(IdentifierValidator.Validate("bad", Policies.For(IdentifierType.Imei)).Blocks);
        var warn = IdentifierValidator.Validate("ÄÖ", Policies.For(IdentifierType.ManufacturerSerial)); // Code 128 cannot carry non-ASCII
        Assert.False(warn.IsValid);
        Assert.False(warn.Blocks);
    }
}
