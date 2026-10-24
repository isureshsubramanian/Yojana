using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Platform.Contracts.Privacy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Privacy;
using Xunit;

namespace Yojana.Serialisation.DatabaseTests;

[Collection("serialisation host")]
public sealed class PersonalDataErasureTests(SerialisationHostFixture fixture)
{
    private static readonly DateTime At = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);

    [DatabaseFact]
    public async Task Open_rma_refuses_party_erasure_and_closed_rma_becomes_reviewable()
    {
        var tenant = await fixture.NewTenantAsync("erase-rma");
        var party = Guid.CreateVersion7();
        var rma = Guid.CreateVersion7();
        using (var seed = fixture.ScopeFor(tenant))
        {
            var db = seed.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            db.Rmas.Add(new Rma
            {
                TenantId = tenant, RmaId = rma, CommandId = Guid.CreateVersion7(), RmaNumber = "RMA-2026-9901",
                Stage = RmaStage.Requested, RequestDate = DateOnly.FromDateTime(At), ReturningPartyRef = party,
                SettlementPartyRef = party, ServiceEntityId = Guid.CreateVersion7(),
                ObligorEntityId = Guid.CreateVersion7(), ServiceCountry = "GB", Currency = "GBP",
                RequestedAt = At, RequestedBy = "service.user", LastEventId = Guid.CreateVersion7(),
                LastModifiedBy = "service.user", LastModifiedAt = At,
            });
            await db.SaveChangesAsync();
        }

        var subject = new ErasureSubject(ErasureSubjectKind.Party, party, [], []);
        using (var refusedScope = fixture.ScopeFor(tenant))
        {
            var refused = await Assert.ThrowsAsync<PersonalDataErasureRefusedException>(
                () => refusedScope.ServiceProvider
                .GetRequiredService<SerialisationPersonalDataHolder>().PreviewAsync(subject, default));
            Assert.Equal("erasure_open_obligations", refused.Rule);
            var blocker = Assert.Single(refused.Blockers);
            Assert.Equal(("rma", rma, "RMA-2026-9901"),
                (blocker.Kind, blocker.RecordId, blocker.Reference));
        }

        using (var close = fixture.ScopeFor(tenant))
        {
            var row = await close.ServiceProvider.GetRequiredService<SerialisationDbContext>()
                .Rmas.SingleAsync(x => x.RmaId == rma);
            row.Stage = RmaStage.Closed;
            row.ClosedAt = At.AddDays(1);
            await close.ServiceProvider.GetRequiredService<SerialisationDbContext>().SaveChangesAsync();
        }
        using var allowedScope = fixture.ScopeFor(tenant);
        await allowedScope.ServiceProvider.GetRequiredService<SerialisationPersonalDataHolder>()
            .PreviewAsync(subject, default);
    }
}
