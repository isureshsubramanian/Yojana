using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P3-T02: CorrectIdentifier writes common metadata and links a later correction to the one it corrects.</summary>
[Collection("serialisation host")]
public sealed class CorrectionInfrastructureTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 10, 17, 8, 0, 0, DateTimeKind.Utc);

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(command, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    [DatabaseFact]
    public async Task Correct_identifier_records_origin_and_a_correction_of_a_correction()
    {
        var tenant = await fixture.NewTenantAsync("ci");
        var prefix = "CI" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var commissioned = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), Guid.NewGuid(), UnitOrigin.Manufactured,
            [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, $"{prefix}-001")])], At, "line", Entity));
        var unit = commissioned.Lines[0].UnitId!.Value;
        var firstId = Guid.NewGuid();
        var first = await InvokeAsync<CorrectIdentifierResult>(tenant, new CorrectIdentifier(Guid.NewGuid(), unit, IdentifierType.InternalSerial, $"{prefix}-002",
            IdentifierCorrectionReason.Typo, At.AddHours(1), "serial-admin", Entity, Correction: new(firstId, IdentifierCorrectionReason.Typo,
                "label did not match", ["photo:1"], CorrectionInitiatedVia.Workbench, InitiatingRole: CorrectionInitiatingRole.SerialAdministrator)));
        var secondId = Guid.NewGuid();
        var second = await InvokeAsync<CorrectIdentifierResult>(tenant, new CorrectIdentifier(Guid.NewGuid(), unit, IdentifierType.InternalSerial, $"{prefix}-003",
            IdentifierCorrectionReason.Misread, At.AddHours(2), "serial-admin", Entity, Correction: new(secondId, IdentifierCorrectionReason.Misread,
                EvidenceRefs: ["email:2"], InitiatedVia: CorrectionInitiatedVia.Api, CorrectsCorrectionId: firstId, InitiatingRole: CorrectionInitiatingRole.CustomerService)));

        Assert.Equal(firstId, first.CorrectionId);
        Assert.Equal(secondId, second.CorrectionId);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var records = await db.Corrections.OrderBy(c => c.EventTime).ToListAsync();
        Assert.Equal(2, records.Count);
        Assert.Equal(CorrectionInitiatedVia.Workbench, records[0].InitiatedVia);
        Assert.Null(records[0].CorrectsCorrectionId);
        Assert.Equal(firstId, records[1].CorrectsCorrectionId);
        Assert.Equal(CorrectionRootCause.TypoOrCrossedPair, records[1].RootCauseCategory);

        var facts = await db.Events.Where(e => e.UnitId == unit && e.EventType == UnitEventType.IdentifierCorrected).OrderBy(e => e.EventTime).ToListAsync();
        Assert.Equal(2, facts.Count);
        using var firstJson = JsonDocument.Parse(facts[0].Attributes!);
        Assert.Equal(CorrectionInitiatedVia.Workbench, firstJson.RootElement.GetProperty("initiated_via").GetString());
        Assert.Equal(firstId, firstJson.RootElement.GetProperty("correction_id").GetGuid());
        using var secondJson = JsonDocument.Parse(facts[1].Attributes!);
        Assert.Equal(firstId, secondJson.RootElement.GetProperty("corrects_correction_id").GetGuid());
    }
}
