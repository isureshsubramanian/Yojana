using Xunit;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.Tests;

public sealed class UnavailablePortTests
{
    [Fact]
    public async Task Stock_quantity_never_proves_that_open_stock_is_empty_when_the_source_is_unavailable()
    {
        var port = new UnavailableStockQuantityPort();

        var refusal = await Assert.ThrowsAsync<CommandRefusedException>(() =>
            port.GetOpenStockAsync(Guid.CreateVersion7(), new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc),
                CancellationToken.None));

        Assert.Equal(SerialisationUnavailableCodes.StockQuantityUnavailable, refusal.Rule);
    }

    [Fact]
    public async Task Party_names_are_renderable_but_explicitly_marked_unavailable()
    {
        var party = Guid.CreateVersion7();

        var labels = await new UnavailablePartyNamePort().NamesAsync([party], CancellationToken.None);

        Assert.Equal(SerialisationUnavailableCodes.UnavailableLabel, labels[party].Name);
        Assert.Equal(SerialisationUnavailableCodes.PartyNameUnavailable, labels[party].AvailabilityCode);
        Assert.DoesNotContain(party.ToString("N"), labels[party].Code, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Label_printer_refuses_instead_of_confirming_a_print()
    {
        var port = new UnavailableLabelPrinterPort();

        var refusal = await Assert.ThrowsAsync<CommandRefusedException>(() => port.SendAsync(
            new LabelPrinterRequest(Guid.CreateVersion7(), "site-printer", "zpl", "payload", "hash"),
            CancellationToken.None));

        Assert.Equal(SerialisationUnavailableCodes.LabelPrinterNotConfigured, refusal.Rule);
        Assert.Equal("Label printing is not set up for this site", refusal.Message);
    }
}
