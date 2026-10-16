using Xunit;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Identity;

namespace Yojana.Serialisation.Tests;

public sealed class SerialSchemeTests
{
    private static readonly SerialSchemeDefinition Vx = new("VX", IdentifierType.InternalSerial, "", "yyMM", "-", 6);

    [Fact]
    public void Formats_and_parses_a_blocked_scheme()
    {
        Assert.Equal("2610-000121", Vx.Format(2610, 121));
        var parsed = Vx.TryParse("2610-000121");
        Assert.NotNull(parsed);
        Assert.Equal((2610L, 121L, 2610L * 1_000_000_000 + 121), parsed.Value);
        Assert.Null(Vx.TryParse("2610-00012"));
        Assert.Equal(121, Vx.TryParse("2610000121")!.Value.Counter); // normalised form, separator stripped
        Assert.Null(Vx.TryParse("VX-2610-000121"));
        Assert.Equal(2610, Vx.BlockFor(new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(999_999, Vx.MaxCounter);
    }

    [Fact]
    public void Default_scheme_is_a_plain_counter()
    {
        var s = InMemorySerialSchemes.Default;
        Assert.Equal("00000042", s.Format(0, 42));
        Assert.Equal(42, s.TryParse("00000042")!.Value.Counter);
        Assert.Null(s.TryParse("0000042"));
    }

    [Fact]
    public void Recognise_returns_a_scheme_only_when_exactly_one_matches()
    {
        var source = new InMemorySerialSchemes();
        source.Register(Vx);
        source.Register(new SerialSchemeDefinition("PM", IdentifierType.InternalSerial, "PM", "yyMM", "-", 6));
        Assert.Equal("VX", source.Recognise("2610-000121")!.Code);
        Assert.Equal("PM", source.Recognise("PM2610-000001")!.Code);
        Assert.Null(source.Recognise("ABC"));
    }
}
