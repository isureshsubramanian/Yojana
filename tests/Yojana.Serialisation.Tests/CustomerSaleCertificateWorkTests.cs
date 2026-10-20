using System.Text.Json;
using Xunit;
using Yojana.Serialisation.Certificates;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Tests;

public sealed class CustomerSaleCertificateWorkTests
{
    private static readonly DateTime SoldAt = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(500, 0, 1)]
    [InlineData(1000, 0, 1)]
    [InlineData(1001, 0, 2)]
    [InlineData(12_500, 0, 13)]
    [InlineData(0, 2500, 3)]
    [InlineData(1500, 400, 2)]
    [InlineData(1500, 600, 3)]
    public void A_large_append_splits_into_bounded_envelopes_carrying_every_item_once(int issueCount,
        int reversalCount, int expectedEnvelopes)
    {
        var issues = Enumerable.Range(0, issueCount).Select(_ => Issue()).ToList();
        var reversals = Enumerable.Range(0, reversalCount).Select(_ => Reversal()).ToList();

        var work = CustomerSaleCertificateWork.Split(issues, reversals);

        Assert.Equal(expectedEnvelopes, work.Count);
        Assert.All(work, envelope =>
        {
            Assert.InRange(envelope.Issues.Count + envelope.Reversals.Count, 1, CustomerSaleCertificateQueue.MaximumWorkPerEnvelope);
        });
        Assert.Equal(issues, work.SelectMany(envelope => envelope.Issues));
        Assert.Equal(reversals, work.SelectMany(envelope => envelope.Reversals));
    }

    [Fact]
    public void A_full_envelope_stays_far_below_the_durable_inbox_read_limit()
    {
        var evidence = Enumerable.Range(0, 4).Select(index => $"evidence-{index}-{new string('x', 40)}").ToList();
        var issues = Enumerable.Range(0, CustomerSaleCertificateQueue.MaximumWorkPerEnvelope)
            .Select(_ => Issue() with { Unknowns = evidence, Evidence = evidence })
            .ToList();

        var work = Assert.Single(CustomerSaleCertificateWork.Split(issues, []));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(work).Length;

        // The inbox refuses envelopes over 4 MB when it reads them back; keep a full one under a quarter of that.
        Assert.True(bytes < 1024 * 1024, $"A full envelope serialised to {bytes:N0} bytes.");
    }

    private static IssueCertificateVersion Issue() => new(Guid.CreateVersion7(), Guid.CreateVersion7(),
        CertificateDocumentKind.ReleaseRecord, CertificateFormType.GenericRelease, CertificateSupersedeReason.Reassembly,
        SoldAt, "system:certificates", Guid.CreateVersion7(), TriggerSchema: CustodyChanged.Schema);

    private static CertificateSaleReversalWork Reversal() => new(Guid.CreateVersion7(), Guid.CreateVersion7(),
        Guid.CreateVersion7(), SoldAt, Guid.CreateVersion7(), "sales_delivery", Guid.CreateVersion7(), 1);
}
