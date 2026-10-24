using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Platform.Contracts.Records;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Knowledge.DatabaseTests;

[Collection("knowledge host")]
public sealed class RecordReferenceDatabaseTests(KnowledgeHostFixture fixture)
{
    public static TheoryData<string, string> Kinds => new()
    {
        { RecordKind.Unit, "SRV-44001" },
        { RecordKind.Claim, "CLM-2026-0041" },
        { RecordKind.Rma, "RMA-2026-0031" },
        { RecordKind.RefurbishmentWorkOrder, "RFW-2026-0012" },
        { RecordKind.WarrantyPolicy, "Fictional server care v3" },
        { RecordKind.Certificate, "CERT-2026-0081" },
        { RecordKind.ImportJob, "fictional-network-switches.csv" },
        { RecordKind.IntegrationMessage, "ServiceOrderReceived · SO-2042" },
    };

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Resolver_reports_exists_missing_readable_name_and_not_visible(string kind, string expectedLabel)
    {
        if (KnowledgeHostFixture.ConnectionString is null) return;
        var setup = await fixture.NewTenantAsync("ref");
        var id = Guid.CreateVersion7();
        await SeedAsync(setup.Tenant, kind, id);
        using var scope = fixture.ScopeFor(setup.Tenant);
        var resolver = scope.ServiceProvider.GetRequiredKeyedService<IRecordReferenceResolver>(kind);
        var allowedRoles = kind == RecordKind.IntegrationMessage
            ? new[] { YojanaRole.IntegrationOperator }
            : new[] { YojanaRole.ServiceDesk };
        var resolved = await resolver.ResolveAsync([id, Guid.CreateVersion7()],
            new RecordReferenceViewer(setup.Staff, allowedRoles), default);
        Assert.True(resolved[id].Exists);
        Assert.True(resolved[id].MayView);
        Assert.Equal(expectedLabel, resolved[id].DisplayName);
        Assert.Contains(resolved.Values, item => !item.Exists && item.DisplayName is null && !item.MayView);
        var hidden = await resolver.ResolveAsync([id], new RecordReferenceViewer(setup.Staff, []), default);
        Assert.True(hidden[id].Exists);
        Assert.False(hidden[id].MayView);
        Assert.DoesNotContain(id.ToString("D"), hidden[id].DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    private async Task SeedAsync(TenantId tenant, string kind, Guid id)
    {
        if (kind == RecordKind.IntegrationMessage)
        {
            using var scope = fixture.ScopeFor(tenant);
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            db.InboundMessages.Add(new InboundMessage
            {
                TenantId = tenant, InboundMessageId = id, MessageId = Guid.CreateVersion7(), ConnectionId = Guid.CreateVersion7(),
                ConnectorCode = "FX01", ErpEntity = "service_order", ErpExternalId = "SO-2042", ErpChangeMarker = "4",
                MarkerKind = "sequence", ContractVersion = "1", MessageType = "ServiceOrderReceived", RawPayload = "{}",
                CanonicalMessage = "{}", OccurredAt = fixture.Clock.UtcNow, ObservedAt = fixture.Clock.UtcNow,
                AcceptedAt = fixture.Clock.UtcNow, IsLatest = true, TranslationStatus = InboundTranslationStatus.NeedsAttention,
            });
            await db.SaveChangesAsync();
            return;
        }

        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            switch (kind)
            {
                case RecordKind.Unit:
                    db.Units.Add(Unit(tenant, id));
                    db.Identifiers.Add(new UnitIdentifier
                    {
                        TenantId = tenant, IdentifierId = Guid.CreateVersion7(), UnitId = id, Type = "vendor_serial",
                        Scheme = "plain", RawValue = "SRV-44001", NormalisedValue = "SRV-44001", ScopeKey = "TENANT",
                        ValidFrom = fixture.Clock.UtcNow, ActorRef = "fixture", RecordedTime = fixture.Clock.UtcNow,
                        EventId = Guid.CreateVersion7(),
                    });
                    break;
                case RecordKind.Claim:
                    db.Claims.Add(new Claim
                    {
                        TenantId = tenant, ClaimId = id, ClaimNumber = "CLM-2026-0041", CommandId = Guid.CreateVersion7(),
                        UnitId = Guid.CreateVersion7(), ClaimDate = new DateOnly(2026, 8, 20), ClaimantPartyRef = Guid.CreateVersion7(),
                        SettlementPartyRef = Guid.CreateVersion7(), ServiceEntityId = Guid.CreateVersion7(), ObligorEntityId = Guid.CreateVersion7(),
                        ServiceCountry = "GB", Currency = "GBP", CoverageSource = "chargeable", Remedy = "repair", Status = "submitted",
                        SubmittedAt = fixture.Clock.UtcNow, SubmittedBy = "fixture", LastEventId = Guid.CreateVersion7(),
                    });
                    break;
                case RecordKind.Rma:
                    db.Rmas.Add(new Rma
                    {
                        TenantId = tenant, RmaId = id, CommandId = Guid.CreateVersion7(), RmaNumber = "RMA-2026-0031",
                        Stage = "requested", RequestDate = new DateOnly(2026, 8, 20), ReturningPartyRef = Guid.CreateVersion7(),
                        SettlementPartyRef = Guid.CreateVersion7(), ServiceEntityId = Guid.CreateVersion7(), ObligorEntityId = Guid.CreateVersion7(),
                        ServiceCountry = "GB", Currency = "GBP", RequestedAt = fixture.Clock.UtcNow,
                        RequestedBy = "fixture", LastEventId = Guid.CreateVersion7(),
                    });
                    break;
                case RecordKind.RefurbishmentWorkOrder:
                    db.RefurbishmentWorkOrders.Add(new RefurbishmentWorkOrder
                    {
                        TenantId = tenant, WorkOrderId = id, OpenCommandId = Guid.CreateVersion7(), WorkOrderNumber = "RFW-2026-0012",
                        Kind = "refurbish", TargetUnitId = Guid.CreateVersion7(), ItemId = Guid.CreateVersion7(), Status = "open",
                        CycleNumber = 1, IdentityPolicy = "same_unit_new_serial", MaxCycles = 3, LifeEpisodeId = Guid.CreateVersion7(),
                        OpenedAt = fixture.Clock.UtcNow, OpenedBy = "fixture", OpeningEventId = Guid.CreateVersion7(),
                    });
                    break;
                case RecordKind.WarrantyPolicy:
                    db.PolicyVersions.Add(new WarrantyPolicyVersion
                    {
                        TenantId = tenant, PolicyVersionId = id, PolicyCode = "SRV-CARE", Version = 3,
                        Name = "Fictional server care", CoverageFamily = "base", EntitlementType = "manufacturer",
                        AppliesToContractsFrom = new DateOnly(2026, 1, 1), Status = "draft", RuleVersion = "3",
                        DurationValue = 36, DurationUnit = "months", EndOfMonthRule = "clamp", DurationBasis = "calendar",
                        StartTrigger = "delivery", Transferable = "yes", SettlementFormula = "repair", AssuranceOrService = "assurance",
                        ReferenceTimeZoneRule = "utc", CreatedAt = fixture.Clock.UtcNow, CreatedBy = "fixture",
                    });
                    break;
                case RecordKind.Certificate:
                    var templateId = Guid.CreateVersion7();
                    var templateVersionId = Guid.CreateVersion7();
                    db.CertificateTemplates.Add(new CertificateTemplate
                    {
                        TenantId = tenant, TemplateId = templateId, Code = "DEVICE", Name = "Device certificate",
                        DocumentKind = "release_record", FormType = "generic_release", CreatedAt = fixture.Clock.UtcNow, CreatedBy = "fixture",
                    });
                    db.CertificateTemplateVersions.Add(new CertificateTemplateVersion
                    {
                        TenantId = tenant, TemplateVersionId = templateVersionId, TemplateId = templateId, Version = 1,
                        Language = "en", PayloadSchemaVersion = "1", Status = "active", LayoutKind = "html", LayoutBody = "<p>Certificate</p>",
                        PageSize = "A4", Orientation = "portrait", CreatedAt = fixture.Clock.UtcNow, CreatedBy = "fixture",
                        UpdatedAt = fixture.Clock.UtcNow, UpdatedBy = "fixture",
                    });
                    db.CertificateInstances.Add(new CertificateInstance
                    {
                        TenantId = tenant, CertificateId = id, UnitId = Guid.CreateVersion7(), DocumentKind = "release_record",
                        FormType = "generic_release", Version = 1, TrackingNumber = "CERT-2026-0081", TemplateVersionId = templateVersionId,
                        Payload = "{}", PayloadHash = new string('a', 64), FactsHash = new string('b', 64),
                        TriggeringEventId = Guid.CreateVersion7(), IssuerEntityId = Guid.CreateVersion7(), IssuerSnapshot = "{}",
                        BrandingSnapshot = "{}", Language = "en", Provenance = "system_generated", IssuedAt = fixture.Clock.UtcNow, Status = "issued",
                    });
                    break;
                case RecordKind.ImportJob:
                    db.ImportJobs.Add(new ImportJob
                    {
                        TenantId = tenant, JobId = id, CommandId = Guid.CreateVersion7(), Source = "network inventory",
                        FileName = "fictional-network-switches.csv", OriginalHash = new string('c', 64), Format = "csv",
                        Mode = "apply", Status = "applied", ActorRef = "fixture", RecordedTime = fixture.Clock.UtcNow,
                    });
                    break;
                default:
                    throw new InvalidOperationException(kind);
            }
            await db.SaveChangesAsync();
        }
    }

    private static Unit Unit(TenantId tenant, Guid id) => new()
    {
        TenantId = tenant, UnitId = id, LifecycleStatus = LifecycleStatus.Released, Origin = "manufactured", SerialisationPoint = "production",
        RetentionClass = "standard", CreatedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
        CreatedEventId = Guid.CreateVersion7(), CreatedEventTime = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
    };
}
