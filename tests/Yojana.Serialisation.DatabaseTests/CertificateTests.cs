using System.Text.Json;
using System.Text;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Runtime;
using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;
using Yojana.Serialisation.Certificates;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Corrections;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Entitlements;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Ports;
using Yojana.Serialisation.Refurbishments;
using Yojana.Serialisation.TimeZones;
using Yojana.Serialisation.Privacy;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P6-T02..T06: immutable certificate chains, payload boundaries, subscriptions and tenant templates.</summary>
[Collection("serialisation host")]
public sealed class CertificateDatabaseTests(SerialisationHostFixture fixture)
{
    private static readonly DateTime At = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Entity = Guid.Parse("c0000000-0000-0000-0000-000000000009");
    // Sale-shaped ledger facts now add legitimate certificate work to the shared durable queue. Keep the assertion
    // window above the observed full-suite queue tail while retaining a bounded failure signal.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    [DatabaseFact]
    public void Published_certificate_facts_use_the_shared_durable_endpoint()
    {
        var runtime = fixture.Services.GetRequiredService<IWolverineRuntime>();
        foreach (var messageType in new[] { typeof(UnitReleased), typeof(EntitlementCoverageChanged) })
        {
            var routing = runtime.ExplainRoutingFor(messageType).ToText();
            Assert.Contains("local://serialisation-published-events", routing, StringComparison.Ordinal);
        }
        Assert.Contains("local://serialisation-certificate-triggers",
            runtime.ExplainRoutingFor(typeof(CertificateTriggerWork)).ToText(), StringComparison.Ordinal);
        Assert.Contains("local://serialisation-certificate-sale-triggers",
            runtime.ExplainRoutingFor(typeof(CustomerSaleCertificateWork)).ToText(), StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task Personal_data_erasure_redacts_the_owner_through_the_narrow_trigger_exception()
    {
        var setup = await CommissionAsync("cert-erase");
        const string ownerName = "Fictional Sole Trader";
        const string ownerCode = "SOLE-ERASE-01";
        var party = Guid.CreateVersion7();
        var erasure = Guid.CreateVersion7();
        var certificate = Guid.CreateVersion7();
        string originalHash;
        using (var seed = fixture.ScopeFor(setup.Tenant))
        {
            var serial = seed.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            await seed.ServiceProvider.GetRequiredService<CertificateTemplateService>()
                .EnsureStartersAsync(CancellationToken.None);
            var template = await (from version in serial.CertificateTemplateVersions.AsNoTracking()
                    join definition in serial.CertificateTemplates.AsNoTracking()
                        on new { version.TenantId, version.TemplateId }
                        equals new { definition.TenantId, definition.TemplateId }
                    where definition.DocumentKind == CertificateDocumentKind.WarrantyCertificate
                          && version.Status == CertificateTemplateStatus.Active
                    select version.TemplateVersionId).FirstAsync();
            var payload = JsonSerializer.Serialize(new
            {
                owner = new { code = ownerCode, name = ownerName },
                presentation = new { time_zone = "UTC", date_pattern = "yyyy-MM-dd", time_pattern = "HH:mm" },
            }, CanonicalJson.Options);
            originalHash = CanonicalJson.Hash(payload);
            serial.CertificateInstances.Add(new Yojana.Serialisation.Data.Entities.CertificateInstance
            {
                TenantId = setup.Tenant, CertificateId = certificate, UnitId = setup.UnitId,
                DocumentKind = CertificateDocumentKind.WarrantyCertificate,
                FormType = CertificateFormType.WarrantyCertificate, Version = 1,
                TrackingNumber = "CRT-2026-990001", TemplateVersionId = template, Payload = payload,
                PayloadHash = originalHash, FactsHash = originalHash, TriggeringEventId = Guid.CreateVersion7(),
                IssuerEntityId = Entity,
                IssuerSnapshot = JsonSerializer.Serialize(new CertificateIssuerSnapshot("Fictional Hardware Ltd", null),
                    CanonicalJson.Options),
                OwnerSnapshot = JsonSerializer.Serialize(new CertificateOwnerSnapshot(ownerCode, ownerName,
                    PartyType.Customer, At), CanonicalJson.Options),
                BrandingSnapshot = JsonSerializer.Serialize(new CertificateBrandingSnapshot("default", null, null),
                    CanonicalJson.Options),
                Language = "en", Provenance = CertificateProvenance.SystemGenerated, IssuedAt = At,
                Status = CertificateStatus.Issued,
            });
            serial.Episodes.Add(new Yojana.Serialisation.Data.Entities.CustodyEpisode
            {
                TenantId = setup.Tenant, EpisodeId = Guid.CreateVersion7(), UnitId = setup.UnitId,
                Kind = EpisodeKind.Ownership, PartyRef = party, PartyType = PartyType.Customer,
                LegalEntityId = Entity, Reason = EpisodeReason.Sale, Source = EpisodeSource.Delivery,
                Confidence = Confidence.High, ValidFrom = At, OpeningEventId = Guid.CreateVersion7(),
            });
            serial.CertificateSignatures.Add(new Yojana.Serialisation.Data.Entities.CertificateSignature
            {
                TenantId = setup.Tenant, SignatureId = Guid.CreateVersion7(), CertificateId = certificate,
                SignerIdentity = "user:test", PrintedName = "Fictional Quality Signer",
                Meaning = CertificateSignatureMeaning.Approval, SignedAt = At,
                HashSigned = originalHash, SignatureMethod = "authenticated_user",
            });
            await serial.SaveChangesAsync();

            var platform = seed.ServiceProvider.GetRequiredService<PlatformDbContext>();
            platform.ErasureRequests.Add(new ErasureRequest
            {
                TenantId = setup.Tenant, ErasureId = erasure,
                SubjectKind = Yojana.Platform.Contracts.Privacy.ErasureSubjectKind.Party,
                SubjectId = party, RequestReference = "DSR-2026-9901", Reason = "verified request",
                RequestedBy = Guid.CreateVersion7(), RequestedAt = At, StartedAt = At,
                Status = ErasureRequestStatus.Running,
                RetentionBasis = Yojana.Platform.Privacy.PersonalDataErasureService.RetentionBasis,
            });
            await platform.SaveChangesAsync();
        }

        var subject = new Yojana.Platform.Contracts.Privacy.ErasureSubject(
            Yojana.Platform.Contracts.Privacy.ErasureSubjectKind.Party, party, [ownerName, ownerCode], []);
        using (var erase = fixture.ScopeFor(setup.Tenant))
            await erase.ServiceProvider.GetRequiredService<SerialisationPersonalDataHolder>()
                .EraseAsync(subject, erasure, CancellationToken.None);

        using (var verify = fixture.ScopeFor(setup.Tenant))
        {
            var db = verify.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var stored = await db.CertificateInstances.AsNoTracking().SingleAsync(x => x.CertificateId == certificate);
            Assert.Equal(originalHash, stored.PayloadHash);
            Assert.DoesNotContain(ownerName, stored.Payload, StringComparison.Ordinal);
            Assert.DoesNotContain(ownerCode, stored.OwnerSnapshot!, StringComparison.Ordinal);
            var redaction = await db.CertificateRedactions.AsNoTracking().SingleAsync(x => x.CertificateId == certificate);
            Assert.Equal(originalHash, redaction.OriginalPayloadHash);
            Assert.Equal("Fictional Quality Signer", await db.CertificateSignatures.AsNoTracking()
                .Where(x => x.CertificateId == certificate).Select(x => x.PrintedName).SingleAsync());
            var detail = await verify.ServiceProvider.GetRequiredService<CertificateQueries>()
                .DetailAsync(certificate, CancellationToken.None);
            Assert.Contains("owner details removed", detail!.Redaction!.Note, StringComparison.Ordinal);
            Assert.Equal(originalHash, detail.Redaction.OriginalPayloadHash);
            var timeline = await verify.ServiceProvider.GetRequiredService<Yojana.Serialisation.Queries.UnitQueries>()
                .HistoryAsync(setup.UnitId, 0, 50, CancellationToken.None);
            Assert.NotEmpty(timeline.Items);
            Assert.Equal(setup.LocationId, await db.Current.AsNoTracking().Where(x => x.UnitId == setup.UnitId)
                .Select(x => x.LocationId).SingleAsync());
        }

        using (var ordinary = fixture.ScopeFor(setup.Tenant))
        {
            var db = ordinary.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var stored = await db.CertificateInstances.SingleAsync(x => x.CertificateId == certificate);
            stored.Payload = "{}";
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [DatabaseFact]
    public async Task Same_name_on_another_partys_unit_does_not_redact_its_certificate_event_or_correction()
    {
        var setup = await CommissionAsync("cert-erase-scope");
        var second = await InvokeAsync<CommissionUnitsResult>(setup.Tenant, new CommissionUnits(
            Guid.NewGuid(), setup.ItemId, UnitOrigin.Manufactured,
            [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, setup.Serial + "-OTHER")])],
            At, "production.user", Entity, LocationId: setup.LocationId, LocationKind: LocationKind.Warehouse));
        var otherUnit = second.Lines.Single().UnitId!.Value;
        var subjectParty = Guid.CreateVersion7();
        var otherParty = Guid.CreateVersion7();
        var erasure = Guid.CreateVersion7();
        const string sharedName = "Fictional Shared Customer";
        const string subjectCode = "SHARED-A";
        const string otherCode = "SHARED-B";
        Guid subjectCertificate;
        Guid otherCertificate;
        Guid subjectEvent;
        Guid otherEvent;
        Guid subjectCorrection;
        Guid otherCorrection;
        string otherPayload;
        string otherSnapshot;
        const string sharedNote = sharedName;

        using (var seed = fixture.ScopeFor(setup.Tenant))
        {
            var serial = seed.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            await seed.ServiceProvider.GetRequiredService<CertificateTemplateService>()
                .EnsureStartersAsync(CancellationToken.None);
            var template = await (from version in serial.CertificateTemplateVersions.AsNoTracking()
                    join definition in serial.CertificateTemplates.AsNoTracking()
                        on new { version.TenantId, version.TemplateId }
                        equals new { definition.TenantId, definition.TemplateId }
                    where definition.DocumentKind == CertificateDocumentKind.WarrantyCertificate
                          && version.Status == CertificateTemplateStatus.Active
                    select version.TemplateVersionId).FirstAsync();

            serial.Episodes.AddRange(Ownership(setup.UnitId, subjectParty), Ownership(otherUnit, otherParty));
            var subjectCertificateRow = Certificate(setup.UnitId, subjectCode, "CRT-2026-990101", template);
            var otherCertificateRow = Certificate(otherUnit, otherCode, "CRT-2026-990102", template);
            subjectCertificate = subjectCertificateRow.CertificateId;
            otherCertificate = otherCertificateRow.CertificateId;
            otherPayload = otherCertificateRow.Payload;
            otherSnapshot = otherCertificateRow.OwnerSnapshot!;
            serial.CertificateInstances.AddRange(subjectCertificateRow, otherCertificateRow);

            subjectEvent = Guid.CreateVersion7();
            otherEvent = Guid.CreateVersion7();
            serial.Events.AddRange(Ledger(setup.UnitId, subjectEvent, subjectCode, 900001),
                Ledger(otherUnit, otherEvent, otherCode, 900002));
            subjectCorrection = Guid.CreateVersion7();
            otherCorrection = Guid.CreateVersion7();
            serial.Corrections.AddRange(Correction(subjectCorrection), Correction(otherCorrection));
            serial.CorrectionUnits.AddRange(
                new Yojana.Serialisation.Data.Entities.CorrectionRecordUnit
                {
                    TenantId = setup.Tenant, CorrectionId = subjectCorrection,
                    UnitId = setup.UnitId, ItemId = setup.ItemId,
                },
                new Yojana.Serialisation.Data.Entities.CorrectionRecordUnit
                {
                    TenantId = setup.Tenant, CorrectionId = otherCorrection,
                    UnitId = otherUnit, ItemId = setup.ItemId,
                });
            await serial.SaveChangesAsync();

            var platform = seed.ServiceProvider.GetRequiredService<PlatformDbContext>();
            platform.ErasureRequests.Add(new ErasureRequest
            {
                TenantId = setup.Tenant, ErasureId = erasure,
                SubjectKind = Yojana.Platform.Contracts.Privacy.ErasureSubjectKind.Party,
                SubjectId = subjectParty, RequestReference = "DSR-2026-9902", Reason = "verified request",
                RequestedBy = Guid.CreateVersion7(), RequestedAt = At, StartedAt = At,
                Status = ErasureRequestStatus.Running,
                RetentionBasis = Yojana.Platform.Privacy.PersonalDataErasureService.RetentionBasis,
            });
            await platform.SaveChangesAsync();

            Yojana.Serialisation.Data.Entities.CustodyEpisode Ownership(Guid unitId, Guid partyRef) => new()
            {
                TenantId = setup.Tenant, EpisodeId = Guid.CreateVersion7(), UnitId = unitId,
                Kind = EpisodeKind.Ownership, PartyRef = partyRef, PartyType = PartyType.Customer,
                LegalEntityId = Entity, Reason = EpisodeReason.Sale, Source = EpisodeSource.Delivery,
                Confidence = Confidence.High, ValidFrom = At, OpeningEventId = Guid.CreateVersion7(),
            };

            Yojana.Serialisation.Data.Entities.CertificateInstance Certificate(Guid unitId, string code,
                string tracking, Guid templateVersionId)
            {
                var payload = JsonSerializer.Serialize(new { owner = new { code, name = sharedName } },
                    CanonicalJson.Options);
                var hash = CanonicalJson.Hash(payload);
                return new()
                {
                    TenantId = setup.Tenant, CertificateId = Guid.CreateVersion7(), UnitId = unitId,
                    DocumentKind = CertificateDocumentKind.WarrantyCertificate,
                    FormType = CertificateFormType.WarrantyCertificate, Version = 1,
                    TrackingNumber = tracking, TemplateVersionId = templateVersionId, Payload = payload,
                    PayloadHash = hash, FactsHash = hash, TriggeringEventId = Guid.CreateVersion7(),
                    IssuerEntityId = Entity, IssuerSnapshot = "{}",
                    OwnerSnapshot = JsonSerializer.Serialize(new CertificateOwnerSnapshot(code, sharedName,
                        PartyType.Customer, At), CanonicalJson.Options), BrandingSnapshot = "{}", Language = "en",
                    Provenance = CertificateProvenance.SystemGenerated, IssuedAt = At,
                    Status = CertificateStatus.Issued,
                };
            }

            Yojana.Serialisation.Data.Entities.UnitEvent Ledger(Guid unitId, Guid eventId, string code, long seq) =>
                new()
                {
                    TenantId = setup.Tenant, EventTime = At.AddMinutes(seq - 900000), EventId = eventId,
                    UnitId = unitId, Seq = seq, EventType = "fixture.personal_data", LegalEntityId = Entity,
                    ActorRef = "fixture", RecordedTime = At, IdempotencyKey = Guid.NewGuid().ToString("N"),
                    Attributes = JsonSerializer.Serialize(new { party_code = code, note = sharedNote }),
                };

            Yojana.Serialisation.Data.Entities.CorrectionRecord Correction(Guid id) => new()
            {
                TenantId = setup.Tenant, CorrectionId = id, Command = "CorrectIdentifier", ReasonCode = "typo",
                InitiatedVia = "fixture", InitiatingRole = "serial_administrator", ActorRef = "fixture",
                EventTime = At, RecordedTime = At, UnitCount = 1, Note = sharedNote,
            };
        }

        var subject = new Yojana.Platform.Contracts.Privacy.ErasureSubject(
            Yojana.Platform.Contracts.Privacy.ErasureSubjectKind.Party, subjectParty,
            [sharedName, subjectCode], []);
        using (var erase = fixture.ScopeFor(setup.Tenant))
            await erase.ServiceProvider.GetRequiredService<SerialisationPersonalDataHolder>()
                .EraseAsync(subject, erasure, CancellationToken.None);

        using var verify = fixture.ScopeFor(setup.Tenant);
        var db = verify.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var untouchedCertificate = await db.CertificateInstances.AsNoTracking()
            .SingleAsync(x => x.CertificateId == otherCertificate);
        Assert.Equal(otherPayload, untouchedCertificate.Payload);
        Assert.Equal(otherSnapshot, untouchedCertificate.OwnerSnapshot);
        Assert.Equal(JsonSerializer.Serialize(new { party_code = otherCode, note = sharedNote }),
            await db.Events.AsNoTracking().Where(x => x.EventId == otherEvent).Select(x => x.Attributes).SingleAsync());
        Assert.Equal(sharedNote, await db.Corrections.AsNoTracking()
            .Where(x => x.CorrectionId == otherCorrection).Select(x => x.Note).SingleAsync());
        Assert.DoesNotContain(sharedName, await db.CertificateInstances.AsNoTracking()
            .Where(x => x.CertificateId == subjectCertificate).Select(x => x.OwnerSnapshot!).SingleAsync(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(sharedName, await db.Events.AsNoTracking()
            .Where(x => x.EventId == subjectEvent).Select(x => x.Attributes!).SingleAsync(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(sharedName, await db.Corrections.AsNoTracking()
            .Where(x => x.CorrectionId == subjectCorrection).Select(x => x.Note!).SingleAsync(),
            StringComparison.Ordinal);
        var redaction = Assert.Single(await db.CertificateRedactions.AsNoTracking().ToListAsync());
        Assert.Equal(subjectCertificate, redaction.CertificateId);
    }

    [DatabaseFact]
    public async Task Release_versions_are_immutable_signed_snapshots_and_preserve_the_whole_unit()
    {
        var setup = await CommissionAsync("cert-chain", missingRequiredComponent: true);
        await ReleaseAsync(setup);
        await WaitForCountAsync(setup.Tenant, setup.UnitId, CertificateDocumentKind.ReleaseRecord, 1);

        var correctedSerial = setup.Serial + "-FIX";
        await InvokeAsync<CorrectIdentifierResult>(setup.Tenant, new CorrectIdentifier(Guid.NewGuid(), setup.UnitId,
            IdentifierType.InternalSerial, correctedSerial, IdentifierCorrectionReason.Typo, At.AddHours(3),
            "quality.user", Entity));
        await WaitForCountAsync(setup.Tenant, setup.UnitId, CertificateDocumentKind.ReleaseRecord, 2);
        var correctedEvent = Assert.Single(await PublishedEventSink.WaitForAsync<UnitIdentifierCorrected>(
            row => row.Header.UnitId == setup.UnitId && row.AfterValue == correctedSerial.Replace("-", "", StringComparison.Ordinal), 1, Timeout));
        // A duplicate delivery has the same event id and must not allocate another version or tracking number.
        await InvokeEventAsync(setup.Tenant, correctedEvent);
        var published = await PublishedEventSink.WaitForAsync<CertificateIssued>(row => row.Header.UnitId == setup.UnitId, 2, Timeout);
        Assert.Equal(2, published.Count);
        Assert.All(published, row => Assert.Equal((CertificateIssued.Schema, "1.0"), (row.Header.Schema, row.Header.Version)));
        var supersededPublication = Assert.Single(await PublishedEventSink.WaitForAsync<CertificateSuperseded>(
            row => row.Header.UnitId == setup.UnitId, 1, Timeout));
        Assert.Equal(CertificateSuperseded.Schema, supersededPublication.Header.Schema);

        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var versions = await db.CertificateInstances.AsNoTracking().Where(row => row.UnitId == setup.UnitId
                    && row.DocumentKind == CertificateDocumentKind.ReleaseRecord).OrderBy(row => row.Version).ToListAsync();
            Assert.Equal([1, 2], versions.Select(row => row.Version));
            Assert.Equal([CertificateStatus.Superseded, CertificateStatus.Issued], versions.Select(row => row.Status));
            Assert.Equal(versions[0].CertificateId, versions[1].SupersedesCertificateId);
            Assert.Equal(CertificateSupersedeReason.IdentifierChange, versions[1].SupersedeReason);
            Assert.NotEqual(versions[0].TrackingNumber, versions[1].TrackingNumber);
            Assert.All(versions, row => Assert.Matches("^CRT-2026-[0-9]{6}$", row.TrackingNumber));
            Assert.All(versions, row => Assert.Equal(CanonicalJson.Hash(row.Payload), row.PayloadHash));
            Assert.All(versions, row => Assert.Contains("\"status\":\"MISSING\"", row.Payload, StringComparison.Ordinal));
            Assert.All(versions, row => Assert.DoesNotContain("\"owner\"", row.Payload, StringComparison.OrdinalIgnoreCase));
            Assert.Contains("\"condition_reverification_required\":false", versions[1].Payload, StringComparison.Ordinal);
            Assert.Contains("unit condition was not re-verified", versions[1].Payload, StringComparison.Ordinal);
            Assert.Contains(UnitIdentifierCorrected.Schema, versions[1].Payload, StringComparison.Ordinal);
            var signatures = await db.CertificateSignatures.AsNoTracking().Where(row => versions.Select(v => v.CertificateId).Contains(row.CertificateId)).ToListAsync();
            Assert.Equal(2, signatures.Count);
            Assert.All(signatures, signature => Assert.Equal(versions.Single(row => row.CertificateId == signature.CertificateId).PayloadHash, signature.HashSigned));

            var queries = scope.ServiceProvider.GetRequiredService<CertificateQueries>();
            var chain = await queries.ChainAsync(setup.UnitId, CancellationToken.None);
            Assert.Equal([1, 2], chain.ReleaseRecords.Select(row => row.Version));
            Assert.Empty(chain.WarrantyCertificates);
            var filtered = await queries.PageAsync(CertificateDocumentKind.ReleaseRecord, null, null,
                correctedSerial[..6], null, null, 0, 20, CancellationToken.None);
            Assert.Contains(filtered.Items, row => row.UnitId == setup.UnitId);

            var current = await db.Current.AsNoTracking().SingleAsync(row => row.UnitId == setup.UnitId);
            Assert.Equal(setup.LocationId, current.LocationId);
            Assert.Equal(LocationKind.Warehouse, current.LocationKind);
            Assert.Null(current.OwnerPartyRef);
            Assert.Equal("[]", current.LiveEntitlementIds);
            Assert.Single(await db.Identifiers.AsNoTracking().Where(row => row.UnitId == setup.UnitId && row.ValidTo == null).ToListAsync());
            Assert.Equal(2, await db.Events.CountAsync(row => row.UnitId == setup.UnitId && row.EventType == CertificateEventType.Issued));
            Assert.Equal(1, await db.Events.CountAsync(row => row.UnitId == setup.UnitId && row.EventType == CertificateEventType.Superseded));
        }

        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var issued = await db.CertificateInstances.SingleAsync(row => row.UnitId == setup.UnitId
                && row.DocumentKind == CertificateDocumentKind.ReleaseRecord && row.Version == 2);
            issued.Payload = "{}";
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var unchanged = await db.CertificateInstances.AsNoTracking().SingleAsync(row => row.UnitId == setup.UnitId
                && row.DocumentKind == CertificateDocumentKind.ReleaseRecord && row.Version == 2);
            Assert.NotEqual("{}", unchanged.Payload);
            Assert.Equal(CanonicalJson.Hash(unchanged.Payload), unchanged.PayloadHash);
            Assert.Equal(CanonicalJson.HashExcludingRootProperties(unchanged.Payload, "trigger"), unchanged.FactsHash);
        }

        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var issued = await db.CertificateInstances.SingleAsync(row => row.UnitId == setup.UnitId
                && row.DocumentKind == CertificateDocumentKind.ReleaseRecord && row.Version == 2);
            issued.FactsHash = new string('0', 64);
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var signature = await db.CertificateSignatures.FirstAsync(row => row.CertificateId != Guid.Empty);
            signature.PrintedName = "Transferred signer";
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [DatabaseFact]
    public async Task Release_and_warranty_triggers_never_cross_chains_and_warranty_keeps_owner_snapshot()
    {
        var setup = await CommissionAsync("cert-cross");
        await ReleaseAsync(setup);
        await WaitForCountAsync(setup.Tenant, setup.UnitId, CertificateDocumentKind.ReleaseRecord, 1);
        var owner = Guid.NewGuid();
        fixture.Services.GetRequiredService<InMemoryPartyNamePort>().Register(new PartyLabel(owner, "NORTH-01", "Northwind Field Services", PartyType.Customer));
        await InvokeAsync<OpenOwnershipResult>(setup.Tenant, new OpenOwnership(Guid.NewGuid(), setup.UnitId, owner,
            PartyType.Customer, EpisodeReason.Sale, At.AddHours(2), "sales.user", Entity));

        var firstWarranty = await InvokeAsync<IssueCertificateVersionResult>(setup.Tenant,
            new IssueCertificateVersion(Guid.NewGuid(), setup.UnitId, CertificateDocumentKind.WarrantyCertificate,
                CertificateFormType.WarrantyCertificate, null, At.AddHours(2), "system:certificates", Entity, Language: "fr"));
        Assert.True(firstWarranty.Issued);

        var entitlementEventId = Guid.NewGuid();
        var coverage = new EntitlementCoverageChanged(
            Header(EntitlementCoverageChanged.Schema, EntitlementCoverageChanged.Version, setup.UnitId, entitlementEventId, At.AddHours(3)),
            [], [], null, null, EntitlementEventType.Amended);
        await InvokeEventAsync(setup.Tenant, coverage);
        await InvokeEventAsync(setup.Tenant, coverage);
        await Task.Delay(500);

        await AssertCountsAsync(setup.Tenant, setup.UnitId, release: 1, warranty: 1);
        await InvokeAsync<CorrectIdentifierResult>(setup.Tenant, new CorrectIdentifier(Guid.NewGuid(), setup.UnitId,
            IdentifierType.InternalSerial, setup.Serial + "-NEW", IdentifierCorrectionReason.Typo, At.AddHours(4),
            "quality.user", Entity));
        await WaitForCountAsync(setup.Tenant, setup.UnitId, CertificateDocumentKind.ReleaseRecord, 2);
        await AssertCountsAsync(setup.Tenant, setup.UnitId, release: 2, warranty: 1);

        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var warranty = await db.CertificateInstances.AsNoTracking().Where(row => row.UnitId == setup.UnitId
                && row.DocumentKind == CertificateDocumentKind.WarrantyCertificate).OrderByDescending(row => row.Version).FirstAsync();
        var snapshot = JsonSerializer.Deserialize<CertificateOwnerSnapshot>(warranty.OwnerSnapshot!, CanonicalJson.Options)!;
        Assert.Equal(("NORTH-01", "Northwind Field Services", PartyType.Customer), (snapshot.Code, snapshot.Name, snapshot.Kind));
        var issuer = JsonSerializer.Deserialize<CertificateIssuerSnapshot>(warranty.IssuerSnapshot, CanonicalJson.Options)!;
        Assert.False(string.IsNullOrWhiteSpace(issuer.Name));
        Assert.NotEqual("Issuing entity", issuer.Name);
        Assert.Contains("\"profile\":\"default\"", warranty.BrandingSnapshot, StringComparison.Ordinal);
        Assert.Equal((Entity, "fr"), (warranty.IssuerEntityId, warranty.Language));
        var current = await db.Current.AsNoTracking().SingleAsync(row => row.UnitId == setup.UnitId);
        Assert.Equal(owner, current.OwnerPartyRef);
        Assert.Equal((setup.LocationId, LocationKind.Warehouse), (current.LocationId, current.LocationKind));
        Assert.Equal("[]", current.LiveEntitlementIds);
        Assert.Single(await db.Identifiers.AsNoTracking().Where(row => row.UnitId == setup.UnitId && row.ValidTo == null).ToListAsync());
        Assert.Equal(3, await db.Events.CountAsync(row => row.UnitId == setup.UnitId && row.EventType == CertificateEventType.Issued));
        Assert.Equal(1, await db.Events.CountAsync(row => row.UnitId == setup.UnitId && row.EventType == CertificateEventType.Superseded));
    }

    [DatabaseFact]
    public async Task Unreleased_units_and_incomplete_reconstruction_provenance_are_typed_refusals()
    {
        var setup = await CommissionAsync("cert-refuse");
        var beforeRelease = await InvokeAsync<IssueCertificateVersionResult>(setup.Tenant,
            new IssueCertificateVersion(Guid.NewGuid(), setup.UnitId, CertificateDocumentKind.ReleaseRecord,
                CertificateFormType.GenericRelease, null, At, "system:certificates", Entity));
        Assert.False(beforeRelease.Issued);
        Assert.Equal("certificate_unit_not_released", beforeRelease.RefusalRule);

        await ReleaseAsync(setup);
        await WaitForCountAsync(setup.Tenant, setup.UnitId, CertificateDocumentKind.ReleaseRecord, 1);
        var incomplete = await InvokeAsync<IssueCertificateVersionResult>(setup.Tenant,
            new IssueCertificateVersion(Guid.NewGuid(), setup.UnitId, CertificateDocumentKind.ReleaseRecord,
                CertificateFormType.GenericRelease, CertificateSupersedeReason.Correction, At.AddHours(4),
                "system:certificates", Entity, CertificateProvenance.Reconstructed, ["unknown inspection"], []));
        Assert.False(incomplete.Issued);
        Assert.Equal("certificate_provenance_evidence_required", incomplete.RefusalRule);
    }

    [DatabaseFact]
    public async Task Tenant_templates_validate_payload_only_fields_and_activated_versions_cannot_be_edited()
    {
        var setup = await CommissionAsync("cert-template", itemClass: "telemetry");
        await ReleaseAsync(setup);
        await WaitForCountAsync(setup.Tenant, setup.UnitId, CertificateDocumentKind.ReleaseRecord, 1);
        using var scope = fixture.ScopeFor(setup.Tenant);
        var service = scope.ServiceProvider.GetRequiredService<CertificateTemplateService>();
        var starters = await service.ListAsync(CancellationToken.None);
        Assert.Contains(starters, row => row.BuiltIn && row.FormType == CertificateFormType.GenericRelease);
        Assert.Contains(starters, row => row.BuiltIn && row.FormType == CertificateFormType.CertificateOfConformity);
        Assert.Contains(starters, row => row.BuiltIn && row.DocumentKind == CertificateDocumentKind.WarrantyCertificate);
        Assert.Contains(starters, row => row.BuiltIn && row.FormType == CertificateFormType.DeviceHistoryExtract);
        Assert.Contains(starters, row => row.BuiltIn && row.FormType == CertificateFormType.Release81303Style);
        Assert.Contains(starters, row => row.BuiltIn && row.FormType == CertificateFormType.EasaForm1Style);
        Assert.Contains(starters, row => row.BuiltIn && row.FormType == CertificateFormType.BatteryPassport);
        Assert.DoesNotContain(service.Catalogue(CertificateDocumentKind.ReleaseRecord), row =>
            row.Path.Contains("party", StringComparison.OrdinalIgnoreCase) || row.Path.Contains("owner", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(service.Catalogue(CertificateDocumentKind.ReleaseRecord), row =>
            row.Path.EndsWith("_id", StringComparison.Ordinal) || row.Path is "source_lots" or "as_maintained.lot_reference");
        Assert.DoesNotContain(service.Catalogue(CertificateDocumentKind.WarrantyCertificate), row =>
            row.Path.EndsWith("_id", StringComparison.Ordinal) || row.Path.EndsWith("superseded_lineage", StringComparison.Ordinal));
        foreach (var starter in starters.Where(row => row.BuiltIn && row.DocumentKind == CertificateDocumentKind.ReleaseRecord))
        {
            var detail = (await service.GetAsync(starter.TemplateId, CancellationToken.None))!;
            var version = detail.Versions.OrderByDescending(row => row.Version).First();
            var validation = service.Validate(detail.DocumentKind, version.LayoutBody, version.HeaderBody,
                version.FooterBody, version.Fields);
            Assert.True(validation.Valid, $"{starter.Code}: {string.Join("; ", validation.Errors)}");
            Assert.Empty(validation.PartyFieldViolations);
        }

        var invalid = service.Validate(CertificateDocumentKind.ReleaseRecord,
            "<p>{{owner.name}} {{live.table.value}}</p>", null, null, ["owner.name", "live.table.value"]);
        Assert.False(invalid.Valid);
        Assert.Contains("owner.name", invalid.PartyFieldViolations);
        Assert.Contains("live.table.value", invalid.UnknownPlaceholders);

        var source = starters.Single(row => row.FormType == CertificateFormType.GenericRelease);
        var copy = await service.CreateAsync(new CreateCertificateTemplateRequest("tenant-release", "Tenant release",
            CertificateDocumentKind.ReleaseRecord, CertificateFormType.GenericRelease, "telemetry", source.TemplateId), "template.admin", CancellationToken.None);
        var draft = Assert.Single(copy.Versions);
        var activated = await service.ActivateAsync(copy.TemplateId, draft.TemplateVersionId, "template.approver", CancellationToken.None);
        Assert.Equal(CertificateTemplateStatus.Active, activated.Status);
        var preview = await service.PreviewAsync(copy.TemplateId, activated.TemplateVersionId, setup.UnitId,
            "template.admin", CancellationToken.None);
        Assert.True(preview.Validation.Valid);
        Assert.Equal("text/html; charset=utf-8", preview.MediaType);
        var previewHtml = System.Text.Encoding.UTF8.GetString(preview.Bytes);
        Assert.Contains("YT-100", previewHtml, StringComparison.Ordinal);
        Assert.Contains("Serial number", previewHtml, StringComparison.Ordinal);
        Assert.Contains("No tests recorded", previewHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("internal_serial", previewHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Supersedes</span>", previewHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(".0000000Z", previewHtml, StringComparison.Ordinal);

        await InvokeAsync<CorrectIdentifierResult>(setup.Tenant, new CorrectIdentifier(Guid.NewGuid(), setup.UnitId,
            IdentifierType.InternalSerial, setup.Serial + "-LAYOUT", IdentifierCorrectionReason.Typo, At.AddHours(5),
            "quality.user", Entity));
        await WaitForCountAsync(setup.Tenant, setup.UnitId, CertificateDocumentKind.ReleaseRecord, 2);

        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        db.ChangeTracker.Clear();
        var selected = await db.CertificateInstances.AsNoTracking().SingleAsync(row => row.UnitId == setup.UnitId
            && row.DocumentKind == CertificateDocumentKind.ReleaseRecord && row.Version == 2);
        Assert.Equal(activated.TemplateVersionId, selected.TemplateVersionId);
        var entity = await db.CertificateTemplateVersions.SingleAsync(row => row.TemplateVersionId == activated.TemplateVersionId);
        entity.LayoutBody = "<p>changed after approval</p>";
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [DatabaseFact]
    public async Task Date_format_settings_offer_active_layout_languages_and_keep_saved_retired_languages()
    {
        var tenant = await fixture.NewTenantAsync("cert-date-languages", importJurisdictionStarter: false);
        (Guid TemplateId, CertificateTemplateVersionRow Version) spanish;
        (Guid TemplateId, CertificateTemplateVersionRow Version) italian;
        using (var templateScope = fixture.ScopeFor(tenant))
        {
            var templates = templateScope.ServiceProvider.GetRequiredService<CertificateTemplateService>();
            var starter = (await templates.ListAsync(CancellationToken.None))
                .Single(row => row.Code == "builtin-generic-release");
            spanish = await ActiveCopyAsync("tenant-release-es", "Tenant release ES", "es-MX");
            italian = await ActiveCopyAsync("tenant-release-it", "Tenant release IT", "it");

            async Task<(Guid TemplateId, CertificateTemplateVersionRow Version)> ActiveCopyAsync(
                string code, string name, string language)
            {
                var copy = await templates.CreateAsync(new CreateCertificateTemplateRequest(code, name,
                    CertificateDocumentKind.ReleaseRecord, CertificateFormType.GenericRelease, null, starter.TemplateId),
                    "template.admin", CancellationToken.None);
                var draft = Assert.Single(copy.Versions);
                var savedDraft = await templates.SaveDraftAsync(copy.TemplateId,
                    new SaveCertificateTemplateDraftRequest(draft.TemplateVersionId, language,
                        draft.PayloadSchemaVersion, draft.LayoutBody, draft.PageSize, draft.Orientation,
                        draft.MarginTopMm, draft.MarginRightMm, draft.MarginBottomMm, draft.MarginLeftMm,
                        draft.HeaderBody, draft.FooterBody, draft.BrandingAssetId, draft.BrandingAssets,
                        draft.StaticText, draft.Fields), "template.admin", CancellationToken.None);
                var active = await templates.ActivateAsync(copy.TemplateId,
                    savedDraft.Version!.TemplateVersionId, "template.admin", CancellationToken.None);
                return (copy.TemplateId, active);
            }
        }

        using (var settingsScope = fixture.ScopeFor(tenant))
        {
            var settings = settingsScope.ServiceProvider.GetRequiredService<CertificateDateFormatSettings>();
            var initial = await settings.GetAsync(CancellationToken.None);
            Assert.Equal(["en", "es-mx", "it"], initial.Languages.Select(row => row.Language));
            Assert.Equal(CertificateDateFormatSettings.DefaultFor("es-MX"),
                initial.Languages.Single(row => row.Language == "es-mx"));

            var saved = await settings.SetAsync(new UpdateCertificateDateFormatSettings(
                [
                    new("es-MX", CertificateDatePattern.YearMonthDay, CertificateTimePattern.TwentyFourHour),
                    new("fr", CertificateDatePattern.DayMonthYear, CertificateTimePattern.TwentyFourHour),
                ], initial.Version), CancellationToken.None);
            Assert.Equal(["en", "es-mx", "fr", "it"], saved.Languages.Select(row => row.Language));
        }

        using (var retirementScope = fixture.ScopeFor(tenant))
        {
            var templates = retirementScope.ServiceProvider.GetRequiredService<CertificateTemplateService>();
            await templates.RetireAsync(spanish.TemplateId, spanish.Version.TemplateVersionId,
                "template.admin", CancellationToken.None);
            await templates.RetireAsync(italian.TemplateId, italian.Version.TemplateVersionId,
                "template.admin", CancellationToken.None);
        }

        using (var assertionScope = fixture.ScopeFor(tenant))
        {
            var settings = assertionScope.ServiceProvider.GetRequiredService<CertificateDateFormatSettings>();
            var afterRetirement = await settings.GetAsync(CancellationToken.None);
            Assert.Equal(["en", "es-mx", "fr"], afterRetirement.Languages.Select(row => row.Language));
            Assert.Equal(CertificateDatePattern.YearMonthDay,
                afterRetirement.Languages.Single(row => row.Language == "es-mx").DatePattern);
            Assert.Equal(CertificateDateFormatSettings.DefaultFor("it"),
                await settings.ResolveAsync("it", CancellationToken.None));
        }
    }

    [DatabaseFact]
    public async Task Logo_assets_are_tenant_private_sanitised_and_snapshotted_by_certificate_version()
    {
        var setup = await CommissionAsync("cert-logo");
        Guid firstAssetId;
        string firstHash;
        Guid templateId;
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var assets = scope.ServiceProvider.GetRequiredService<TenantAssetService>();
            var hostile = Encoding.UTF8.GetBytes("""
                <svg xmlns="http://www.w3.org/2000/svg" width="120" height="40" onload="alert(1)">
                  <script>alert(2)</script><foreignObject><div>unsafe</div></foreignObject>
                  <image href="https://outside.invalid/logo.png" width="10" height="10"/>
                  <image href="data:text/html;base64,PHNjcmlwdD4=" width="10" height="10"/>
                  <rect width="120" height="40" fill="url(https://outside.invalid/paint)"/>
                  <rect width="120" height="40" stroke="url(#safe) url(https://outside.invalid/bypass)"/>
                  <text x="4" y="24">Fictional Systems</text>
                </svg>
                """);
            var uploadCommand = new UploadTenantLogo(Guid.NewGuid(), Entity, hostile, "template.admin", "fictional-systems-logo.svg");
            var uploadedResult = await InvokeAsync<TenantAssetUploadResult>(setup.Tenant, uploadCommand);
            var uploaded = uploadedResult.Asset;
            Assert.False(uploadedResult.Replayed);
            var replayed = await InvokeAsync<TenantAssetUploadResult>(setup.Tenant, uploadCommand);
            Assert.True(replayed.Replayed);
            Assert.Equal(uploaded.AssetId, replayed.Asset.AssetId);
            firstAssetId = uploaded.AssetId;
            firstHash = uploaded.Sha256;
            Assert.Equal(("image/svg+xml", 120, 40), (uploaded.MediaType, uploaded.Width, uploaded.Height));
            Assert.Equal("fictional-systems-logo.svg", uploaded.OriginalFileName);
            var stored = await assets.ContentAsync(firstAssetId, CancellationToken.None);
            var clean = Encoding.UTF8.GetString(stored!.Bytes);
            Assert.DoesNotContain("script", clean, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("foreignObject", clean, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("onload", clean, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("outside.invalid", clean, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("data:text", clean, StringComparison.OrdinalIgnoreCase);

            var templates = scope.ServiceProvider.GetRequiredService<CertificateTemplateService>();
            var starter = (await templates.ListAsync(CancellationToken.None)).Single(row => row.Code == "builtin-generic-release");
            var created = await templates.CreateAsync(new CreateCertificateTemplateRequest("logo-release", "Logo release",
                CertificateDocumentKind.ReleaseRecord, CertificateFormType.GenericRelease, null, starter.TemplateId),
                "template.admin", CancellationToken.None);
            templateId = created.TemplateId;
            var draft = Assert.Single(created.Versions);
            var saved = await templates.SaveDraftAsync(templateId,
                Request(draft, firstAssetId) with { HeaderBody = "<header>{{model.code}}</header>" },
                "template.admin", CancellationToken.None);
            await templates.ActivateAsync(templateId, saved.Version!.TemplateVersionId, "template.admin", CancellationToken.None);
        }

        await ReleaseAsync(setup);
        await WaitForCountAsync(setup.Tenant, setup.UnitId, CertificateDocumentKind.ReleaseRecord, 1);

        Guid secondAssetId;
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var replacement = (await InvokeAsync<TenantAssetUploadResult>(setup.Tenant,
                new UploadTenantLogo(Guid.NewGuid(), Entity, Encoding.UTF8.GetBytes("""
                <svg xmlns="http://www.w3.org/2000/svg" width="120" height="40"><text x="4" y="24">New mark</text></svg>
                """), "template.admin"))).Asset;
            secondAssetId = replacement.AssetId;
            var templates = scope.ServiceProvider.GetRequiredService<CertificateTemplateService>();
            var detail = (await templates.GetAsync(templateId, CancellationToken.None))!;
            var active = detail.Versions.Single(row => row.Status == CertificateTemplateStatus.Active);
            var next = await templates.SaveDraftAsync(templateId, Request(active, secondAssetId) with { TemplateVersionId = null },
                "template.admin", CancellationToken.None);
            await templates.ActivateAsync(templateId, next.Version!.TemplateVersionId, "template.admin", CancellationToken.None);
        }

        await InvokeAsync<CorrectIdentifierResult>(setup.Tenant, new CorrectIdentifier(Guid.NewGuid(), setup.UnitId,
            IdentifierType.InternalSerial, setup.Serial + "-BRAND", IdentifierCorrectionReason.Typo, At.AddHours(3),
            "quality.user", Entity));
        await WaitForCountAsync(setup.Tenant, setup.UnitId, CertificateDocumentKind.ReleaseRecord, 2);

        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var versions = await db.CertificateInstances.AsNoTracking().Where(row => row.UnitId == setup.UnitId
                    && row.DocumentKind == CertificateDocumentKind.ReleaseRecord).OrderBy(row => row.Version).ToListAsync();
            var firstBranding = JsonSerializer.Deserialize<CertificateBrandingSnapshot>(versions[0].BrandingSnapshot, CanonicalJson.Options)!;
            var secondBranding = JsonSerializer.Deserialize<CertificateBrandingSnapshot>(versions[1].BrandingSnapshot, CanonicalJson.Options)!;
            Assert.Equal((firstAssetId, firstHash), (firstBranding.LogoAssetId, firstBranding.LogoSha256));
            Assert.Equal(secondAssetId, secondBranding.LogoAssetId);
            Assert.NotEqual(firstBranding.LogoSha256, secondBranding.LogoSha256);
            var rendered = await scope.ServiceProvider.GetRequiredService<CertificateQueries>()
                .RenderAsync(versions[0].CertificateId, CancellationToken.None);
            var renderedHtml = Encoding.UTF8.GetString(rendered!.Bytes);
            Assert.Contains("<strong>Released by</strong>", renderedHtml, StringComparison.Ordinal);
            Assert.Contains(Convert.ToBase64String((await db.TenantAssets.AsNoTracking().SingleAsync(row => row.AssetId == firstAssetId)).Bytes),
                renderedHtml, StringComparison.Ordinal);
            Assert.DoesNotContain("{{model.code}}", renderedHtml, StringComparison.Ordinal);
            var current = await db.Current.AsNoTracking().SingleAsync(row => row.UnitId == setup.UnitId);
            Assert.Null(current.OwnerPartyRef);
            Assert.Equal((setup.LocationId, LocationKind.Warehouse, "[]"),
                (current.LocationId, current.LocationKind, current.LiveEntitlementIds));
            var openEpisodes = await db.Episodes.AsNoTracking().Where(row => row.UnitId == setup.UnitId && row.ValidTo == null).ToListAsync();
            Assert.DoesNotContain(openEpisodes, row => row.Kind == EpisodeKind.Ownership);
            var custody = Assert.Single(openEpisodes, row => row.Kind == EpisodeKind.Custody);
            Assert.Equal((setup.LocationId, LocationKind.Warehouse), (custody.LocationId, custody.LocationKind));
            Assert.Empty(await db.Entitlements.AsNoTracking().Where(row => row.UnitId == setup.UnitId).ToListAsync());
            var identifier = Assert.Single(await db.Identifiers.AsNoTracking()
                .Where(row => row.UnitId == setup.UnitId && row.ValidTo == null).ToListAsync());
            Assert.Equal(setup.Serial + "-BRAND", identifier.RawValue);
            Assert.Equal(1, await db.Events.CountAsync(row => row.UnitId == setup.UnitId && row.EventType == UnitEventType.IdentifierCorrected));
            Assert.Equal(2, await db.Events.CountAsync(row => row.UnitId == setup.UnitId && row.EventType == CertificateEventType.Issued));
            Assert.Equal(1, await db.Events.CountAsync(row => row.UnitId == setup.UnitId && row.EventType == CertificateEventType.Superseded));
        }

        var otherTenant = await fixture.NewTenantAsync("cert-logo-other");
        using var otherScope = fixture.ScopeFor(otherTenant);
        Assert.Null(await otherScope.ServiceProvider.GetRequiredService<TenantAssetService>()
            .ContentAsync(firstAssetId, CancellationToken.None));

        static SaveCertificateTemplateDraftRequest Request(CertificateTemplateVersionRow row, Guid assetId) => new(
            row.TemplateVersionId, row.Language, row.PayloadSchemaVersion, row.LayoutBody, row.PageSize, row.Orientation,
            row.MarginTopMm, row.MarginRightMm, row.MarginBottomMm, row.MarginLeftMm, row.HeaderBody, row.FooterBody,
            null, new Dictionary<string, Guid> { [Entity.ToString()] = assetId }, row.StaticText, row.Fields);
    }

    [DatabaseFact]
    public async Task Logo_upload_reads_raster_dimensions_from_magic_bytes_and_refuses_unknown_or_oversized_content()
    {
        var tenant = await fixture.NewTenantAsync("cert-logo-raster");
        byte[] png = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82,
            0, 0, 1, 64, 0, 0, 0, 48];
        byte[] jpeg = [0xff, 0xd8, 0xff, 0xc0, 0, 17, 8, 0, 64, 1, 32, 3, 1, 17, 0, 2, 17, 0, 3, 17, 0];

        var pngResult = await InvokeAsync<TenantAssetUploadResult>(tenant,
            new UploadTenantLogo(Guid.NewGuid(), null, png, "template.admin"));
        var jpegResult = await InvokeAsync<TenantAssetUploadResult>(tenant,
            new UploadTenantLogo(Guid.NewGuid(), null, jpeg, "template.admin"));

        Assert.Equal(("image/png", 320, 48), (pngResult.Asset.MediaType, pngResult.Asset.Width, pngResult.Asset.Height));
        Assert.Equal(("image/jpeg", 288, 64), (jpegResult.Asset.MediaType, jpegResult.Asset.Width, jpegResult.Asset.Height));
        var unknown = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<TenantAssetUploadResult>(tenant,
            new UploadTenantLogo(Guid.NewGuid(), null, "not an image"u8.ToArray(), "template.admin")));
        Assert.Equal("tenant_asset_media_type_unknown", unknown.Rule);
        var oversized = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<TenantAssetUploadResult>(tenant,
            new UploadTenantLogo(Guid.NewGuid(), null, new byte[TenantAssetService.MaximumBytes + 1], "template.admin")));
        Assert.Equal("tenant_asset_too_large", oversized.Rule);
    }

    [DatabaseFact]
    public async Task Issued_certificate_keeps_facility_time_zone_and_date_format_snapshots_while_a_new_version_uses_changes()
    {
        var setup = await CommissionAsync("cert-zone");
        var issuer = Guid.NewGuid();
        const string facilityName = "Fictional North Works";
        fixture.Services.GetRequiredService<InMemoryLocationNameRegistry>().Register(
            new LocationLabel(setup.LocationId, "FAC-21", facilityName, LocationKind.Warehouse));
        using (var settingsScope = fixture.ScopeFor(setup.Tenant))
        {
            var timeZones = settingsScope.ServiceProvider.GetRequiredService<TenantTimeZoneSettings>();
            var timeZoneView = await timeZones.GetAsync(CancellationToken.None);
            await timeZones.SetAsync(new UpdateTenantTimeZoneSettings("Asia/Kolkata", [], timeZoneView.Version),
                "tenant.admin", CancellationToken.None);
            var settings = settingsScope.ServiceProvider.GetRequiredService<CertificateDateFormatSettings>();
            var settingsView = await settings.GetAsync(CancellationToken.None);
            await settings.SetAsync(new UpdateCertificateDateFormatSettings(
                [new("en", CertificateDatePattern.DayMonthYear, CertificateTimePattern.TwelveHour)], settingsView.Version),
                CancellationToken.None);
        }

        await InvokeAsync<ReleaseUnitResult>(setup.Tenant,
            new ReleaseUnit(Guid.NewGuid(), setup.UnitId, At.AddHours(1), "quality.user", issuer, "sig:quality",
                FacilityId: setup.LocationId));
        await WaitForCountAsync(setup.Tenant, setup.UnitId, CertificateDocumentKind.ReleaseRecord, 1);

        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var certificate = await db.CertificateInstances.AsNoTracking().SingleAsync(row => row.UnitId == setup.UnitId
            && row.DocumentKind == CertificateDocumentKind.ReleaseRecord);
        Assert.Contains($"\"facility\":\"{facilityName}\"", certificate.Payload, StringComparison.Ordinal);
        Assert.Contains("\"presentation\":{\"date_pattern\":\"dd/MM/yyyy\",\"time_pattern\":\"h:mm tt\",\"time_zone\":\"Asia/Kolkata\"}",
            certificate.Payload, StringComparison.Ordinal);
        var queries = scope.ServiceProvider.GetRequiredService<CertificateQueries>();
        var before = await queries.RenderAsync(certificate.CertificateId, CancellationToken.None);
        var outbound = await scope.ServiceProvider.GetRequiredService<IRenderedOutboundDocumentPort>()
            .ReadAsync(certificate.CertificateId, CancellationToken.None);
        Assert.NotNull(outbound);
        Assert.Equal(before!.Bytes, outbound.Bytes);
        Assert.Equal(before.MediaType, outbound.ContentType);
        Assert.EndsWith(".html", outbound.FileName);
        Assert.DoesNotContain(certificate.CertificateId.ToString(), outbound.FileName);
        var beforeHtml = Encoding.UTF8.GetString(before!.Bytes);
        Assert.Contains(facilityName, beforeHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("FAC-21", beforeHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(setup.LocationId.ToString("D"), beforeHtml, StringComparison.OrdinalIgnoreCase);
        Assert.True(beforeHtml.Split("IST", StringSplitOptions.None).Length >= 3,
            "The issued and signed lines should both carry the snapshotted zone abbreviation.");
        db.ReprintLogs.Add(new Yojana.Serialisation.Data.Entities.ReprintLog
        {
            TenantId = setup.Tenant, ReprintId = Guid.NewGuid(), CertificateId = certificate.CertificateId,
            Version = certificate.Version, RequestedBy = "quality.user", At = At.AddHours(1).AddMinutes(30),
        });
        await db.SaveChangesAsync();
        var beforeDetail = await queries.DetailAsync(certificate.CertificateId, CancellationToken.None);
        Assert.EndsWith("IST", beforeDetail!.IssuedAtDisplay, StringComparison.Ordinal);
        Assert.All(beforeDetail.Signatures, row => Assert.EndsWith("IST", row.SignedAtDisplay, StringComparison.Ordinal));
        Assert.EndsWith("IST", Assert.Single(beforeDetail.Reprints).AtDisplay, StringComparison.Ordinal);

        fixture.Services.GetRequiredService<InMemoryLocationNameRegistry>().Register(
            new LocationLabel(setup.LocationId, "FAC-22", "Fictional South Works", LocationKind.Warehouse));
        using (var settingsScope = fixture.ScopeFor(setup.Tenant))
        {
            var timeZones = settingsScope.ServiceProvider.GetRequiredService<TenantTimeZoneSettings>();
            var timeZoneView = await timeZones.GetAsync(CancellationToken.None);
            await timeZones.SetAsync(new UpdateTenantTimeZoneSettings("America/New_York", [], timeZoneView.Version),
                "tenant.admin", CancellationToken.None);
            var settings = settingsScope.ServiceProvider.GetRequiredService<CertificateDateFormatSettings>();
            var settingsView = await settings.GetAsync(CancellationToken.None);
            await settings.SetAsync(new UpdateCertificateDateFormatSettings(
                [new("en", CertificateDatePattern.YearMonthDay, CertificateTimePattern.TwentyFourHour)], settingsView.Version),
                CancellationToken.None);
        }
        var after = await queries.RenderAsync(certificate.CertificateId, CancellationToken.None);

        Assert.Equal(before.Bytes, after!.Bytes);
        var afterDetail = await queries.DetailAsync(certificate.CertificateId, CancellationToken.None);
        Assert.Equal(beforeDetail.IssuedAtDisplay, afterDetail!.IssuedAtDisplay);
        Assert.Equal(beforeDetail.Reprints.Single().AtDisplay, afterDetail.Reprints.Single().AtDisplay);
        await InvokeAsync<CorrectIdentifierResult>(setup.Tenant, new CorrectIdentifier(Guid.NewGuid(), setup.UnitId,
            IdentifierType.InternalSerial, setup.Serial + "-FORMAT", IdentifierCorrectionReason.Typo, At.AddHours(2),
            "quality.user", issuer));
        await WaitForCountAsync(setup.Tenant, setup.UnitId, CertificateDocumentKind.ReleaseRecord, 2);

        db.ChangeTracker.Clear();
        var replacement = await db.CertificateInstances.AsNoTracking().SingleAsync(row => row.UnitId == setup.UnitId
            && row.DocumentKind == CertificateDocumentKind.ReleaseRecord && row.Version == 2);
        Assert.Contains("\"presentation\":{\"date_pattern\":\"yyyy-MM-dd\",\"time_pattern\":\"HH:mm\",\"time_zone\":\"America/New_York\"}",
            replacement.Payload, StringComparison.Ordinal);
        var replacementHtml = Encoding.UTF8.GetString((await queries.RenderAsync(replacement.CertificateId,
            CancellationToken.None))!.Bytes);
        Assert.Contains("Fictional South Works", replacementHtml, StringComparison.Ordinal);
        Assert.Contains("EDT", replacementHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(facilityName, replacementHtml, StringComparison.Ordinal);
        Assert.NotEqual(beforeHtml, replacementHtml);
        var current = await db.Current.AsNoTracking().SingleAsync(row => row.UnitId == setup.UnitId);
        Assert.Null(current.OwnerPartyRef);
        Assert.Equal((setup.LocationId, LocationKind.Warehouse, "[]"),
            (current.LocationId, current.LocationKind, current.LiveEntitlementIds));
        Assert.Empty(await db.Episodes.AsNoTracking().Where(row => row.UnitId == setup.UnitId
            && row.Kind == EpisodeKind.Ownership && row.ValidTo == null).ToListAsync());
        var custody = await db.Episodes.AsNoTracking().SingleAsync(row => row.UnitId == setup.UnitId
            && row.Kind == EpisodeKind.Custody && row.ValidTo == null);
        Assert.Equal((setup.LocationId, LocationKind.Warehouse), (custody.LocationId, custody.LocationKind));
        Assert.Empty(await db.Entitlements.AsNoTracking().Where(row => row.UnitId == setup.UnitId).ToListAsync());
        Assert.Single(await db.Identifiers.AsNoTracking().Where(row => row.UnitId == setup.UnitId && row.ValidTo == null).ToListAsync());
        Assert.Equal(1, await db.Events.CountAsync(row => row.UnitId == setup.UnitId && row.EventType == UnitEventType.Released));
        Assert.Equal(2, await db.Events.CountAsync(row => row.UnitId == setup.UnitId && row.EventType == CertificateEventType.Issued));
        Assert.Equal(1, await db.Events.CountAsync(row => row.UnitId == setup.UnitId && row.EventType == CertificateEventType.Superseded));
        Assert.Equal(1, await db.Events.CountAsync(row => row.UnitId == setup.UnitId && row.EventType == UnitEventType.IdentifierCorrected));
    }

    [DatabaseFact]
    public async Task Refurbishment_release_absorbs_overlapping_release_triggers_and_redelivery()
    {
        var setup = await CommissionAsync("cert-refurb");
        await InvokeAsync<OpenOwnershipResult>(setup.Tenant, new OpenOwnership(Guid.NewGuid(), setup.UnitId, Entity,
            PartyType.Us, "company_stock", At.AddMinutes(5), "receiving", Entity));
        await ReleaseAsync(setup);
        await WaitForCountAsync(setup.Tenant, setup.UnitId, CertificateDocumentKind.ReleaseRecord, 1);
        using (var scope = fixture.ScopeFor(setup.Tenant))
            await scope.ServiceProvider.GetRequiredService<RefurbishmentPolicySettings>()
                .SetAsync(new RefurbishmentPolicy(RefurbishmentIdentity.SameUnitSameSerial, 3, []), CancellationToken.None);
        var repair = Guid.NewGuid();
        var opened = await InvokeAsync<OpenRefurbishmentResult>(setup.Tenant,
            new OpenRefurbishment(Guid.NewGuid(), setup.UnitId, RefurbishmentKind.Refurbish, At.AddHours(2), "quality", Entity, repair));
        await InvokeAsync<RecordTestResult>(setup.Tenant, new RecordTest(Guid.NewGuid(), setup.UnitId,
            RefurbishmentTestKind.KeyFunctions, UnitTestResult.Pass, "{}", "fixture-fictional-cert-1",
            At.AddHours(2.5), "quality", Entity));
        await InvokeAsync<ReleaseRefurbishmentResult>(setup.Tenant, new ReleaseRefurbishment(Guid.NewGuid(),
            opened.WorkOrderId, ConditionGrades.RefurbishedA, "graded_receipt_plus_postings", At.AddHours(3),
            "quality", Entity, setup.LocationId, null, "F2", "C2"));

        await WaitForCountAsync(setup.Tenant, setup.UnitId, CertificateDocumentKind.ReleaseRecord, 2);
        var released = Assert.Single(await PublishedEventSink.WaitForAsync<RefurbishmentReleased>(
            row => row.WorkOrderId == opened.WorkOrderId, 1, Timeout));
        await InvokeEventAsync(setup.Tenant, released);
        await Task.Delay(500);

        using var finalScope = fixture.ScopeFor(setup.Tenant);
        var db = finalScope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var versions = await db.CertificateInstances.AsNoTracking().Where(row => row.UnitId == setup.UnitId
            && row.DocumentKind == CertificateDocumentKind.ReleaseRecord).OrderBy(row => row.Version).ToListAsync();
        Assert.Equal([1, 2], versions.Select(row => row.Version));
        Assert.Equal(2, await db.CertificateTriggers.CountAsync(row => row.UnitId == setup.UnitId
            && row.DocumentKind == CertificateDocumentKind.ReleaseRecord && row.Outcome == "issued"));
        Assert.Equal(0, await db.CertificateTriggers.CountAsync(row => row.UnitId == setup.UnitId
            && row.DocumentKind == CertificateDocumentKind.ReleaseRecord && row.Outcome == "absorbed"));
        await AssertWholeUnitAsync(db, setup.UnitId, Entity, setup.LocationId);
    }

    [DatabaseFact]
    public async Task Sale_entitlement_events_share_one_warranty_version_when_the_final_facts_are_identical()
    {
        var setup = await CommissionAsync("cert-sale");
        await ReleaseAsync(setup);
        var owner = Guid.NewGuid();
        fixture.Services.GetRequiredService<InMemoryPartyNamePort>().Register(new PartyLabel(owner, "BUYER-01", "Fictional Survey Works", PartyType.Customer));
        await InvokeAsync<OpenOwnershipResult>(setup.Tenant, new OpenOwnership(Guid.NewGuid(), setup.UnitId, owner,
            PartyType.Customer, EpisodeReason.Sale, At.AddHours(2), "sales", Entity));
        var initial = await InvokeAsync<IssueCertificateVersionResult>(setup.Tenant,
            new IssueCertificateVersion(Guid.NewGuid(), setup.UnitId, CertificateDocumentKind.WarrantyCertificate,
                CertificateFormType.WarrantyCertificate, null, At.AddHours(2), "system:certificates", Entity));
        Assert.True(initial.Issued);
        var policies = new List<Guid>();
        var policyCodes = new List<string>();
        var policyNames = new List<string>();
        fixture.Services.GetRequiredService<InMemoryWarrantyPorts>().RegisterZone(setup.LocationId, "UTC");
        foreach (var family in new[] { "sale_parts", "sale_service" })
        {
            await InvokeAsync<CoverageFamilyRow>(setup.Tenant,
                new DefineCoverageFamily(family, family, false, AssuranceOrService.Service));
            var code = family.ToUpperInvariant() + "-" + Guid.NewGuid().ToString("N")[..6];
            var name = family == "sale_parts" ? "Fictional parts protection" : "Fictional service protection";
            var draft = await InvokeAsync<PolicyDraftResult>(setup.Tenant, new CreatePolicyDraft(Guid.NewGuid(), code,
                name, family, EntitlementType.Contract, new DateOnly(2026, 1, 1),
                PolicyValidatorTestsTerms.Standard24() with { AssuranceOrService = AssuranceOrService.Service }, "policy.admin"));
            policyCodes.Add(code);
            policyNames.Add(name);
            policies.Add((await InvokeAsync<PolicyDraftResult>(setup.Tenant,
                new PublishPolicyVersion(draft.PolicyVersionId, "policy.admin"))).PolicyVersionId);
        }
        await InvokeEventAsync(setup.Tenant, new CreateSaleEntitlements(setup.UnitId, owner, policies, At.AddHours(3), Entity));
        await WaitForCountAsync(setup.Tenant, setup.UnitId, CertificateDocumentKind.WarrantyCertificate, 2);
        var coverage = await PublishedEventSink.WaitForAsync<EntitlementCoverageChanged>(
            row => row.Header.UnitId == setup.UnitId && row.Header.EventTime == At.AddHours(3), 2, Timeout);
        await WaitForTriggerCountAsync(setup.Tenant, coverage.Select(row => row.Header.EventId).ToList(), 2);

        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(2, await db.Entitlements.CountAsync(row => row.UnitId == setup.UnitId));
        Assert.Equal(2, await db.CertificateInstances.CountAsync(row => row.UnitId == setup.UnitId
            && row.DocumentKind == CertificateDocumentKind.WarrantyCertificate));
        var latest = await db.CertificateInstances.AsNoTracking().Where(row => row.UnitId == setup.UnitId
                && row.DocumentKind == CertificateDocumentKind.WarrantyCertificate)
            .OrderByDescending(row => row.Version).FirstAsync();
        Assert.All(policyNames, name => Assert.Contains($"\"policy_name\":\"{name}\"", latest.Payload, StringComparison.Ordinal));
        var templates = scope.ServiceProvider.GetRequiredService<CertificateTemplateService>();
        Assert.Contains(templates.Catalogue(CertificateDocumentKind.WarrantyCertificate), row => row.Path == "entitlements.policy_name");
        var rendered = await scope.ServiceProvider.GetRequiredService<CertificateQueries>()
            .RenderAsync(latest.CertificateId, CancellationToken.None);
        var html = Encoding.UTF8.GetString(rendered!.Bytes);
        Assert.All(policyNames, name => Assert.Contains(name, html, StringComparison.Ordinal));
        Assert.All(policyCodes, code => Assert.DoesNotContain(code, html, StringComparison.Ordinal));
        Assert.Contains("<strong>Issued by</strong>", html, StringComparison.Ordinal);
        Assert.Contains("body{margin:0;padding:15mm 15mm 15mm 15mm}", html, StringComparison.Ordinal);
        var outcomes = await db.CertificateTriggers.Where(row => coverage.Select(message => message.Header.EventId)
                .Contains(row.TriggeringEventId)).Select(row => row.Outcome).OrderBy(row => row).ToListAsync();
        Assert.Equal(["absorbed", "issued"], outcomes);
        await AssertWholeUnitAsync(db, setup.UnitId, owner, setup.LocationId, expectedEntitlements: 2);
    }

    [DatabaseFact]
    public async Task Concurrent_chain_triggers_complete_without_duplicate_versions_or_gaps()
    {
        var setup = await CommissionAsync("cert-concurrent");
        await ReleaseAsync(setup);
        await WaitForCountAsync(setup.Tenant, setup.UnitId, CertificateDocumentKind.ReleaseRecord, 1);
        var corrected = setup.Serial + "-CONCURRENT";
        await CorrectWithoutPublicationAsync(setup, corrected);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var before = setup.Serial.Replace("-", "", StringComparison.Ordinal);
        var after = corrected.Replace("-", "", StringComparison.Ordinal);
        var first = new UnitIdentifierCorrected(Header(UnitIdentifierCorrected.Schema, UnitIdentifierCorrected.Version,
            setup.UnitId, firstId, At.AddHours(3)), IdentifierType.InternalSerial, IdentifierCorrectionReason.Typo,
            before, after, Guid.NewGuid(), Guid.NewGuid(), null);
        var second = first with { Header = Header(UnitIdentifierCorrected.Schema, UnitIdentifierCorrected.Version,
            setup.UnitId, secondId, At.AddHours(3).AddSeconds(1)) };

        await Task.WhenAll(InvokeEventAsync(setup.Tenant, first), InvokeEventAsync(setup.Tenant, second));
        await WaitForTriggerCountAsync(setup.Tenant, [firstId, secondId], 2);

        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var versions = await db.CertificateInstances.AsNoTracking().Where(row => row.UnitId == setup.UnitId
                && row.DocumentKind == CertificateDocumentKind.ReleaseRecord).OrderBy(row => row.Version)
            .Select(row => row.Version).ToListAsync();
        Assert.Equal([1, 2], versions);
        var outcomes = await db.CertificateTriggers.Where(row => row.TriggeringEventId == firstId || row.TriggeringEventId == secondId)
            .Select(row => row.Outcome).OrderBy(row => row).ToListAsync();
        Assert.Equal(["absorbed", "issued"], outcomes);
    }

    [DatabaseFact]
    public async Task Published_release_and_warranty_triggers_use_isolated_state_neutral_work()
    {
        var setup = await CommissionAsync("cert-publish-race");
        await ReleaseAsync(setup);
        await WaitForCountAsync(setup.Tenant, setup.UnitId, CertificateDocumentKind.ReleaseRecord, 1);
        var owner = Guid.NewGuid();
        fixture.Services.GetRequiredService<InMemoryPartyNamePort>().Register(new PartyLabel(owner,
            "RACE-BUYER", "Fictional Parallel Systems", PartyType.Customer));
        await InvokeAsync<OpenOwnershipResult>(setup.Tenant, new OpenOwnership(Guid.NewGuid(), setup.UnitId, owner,
            PartyType.Customer, EpisodeReason.Sale, At.AddHours(2), "sales", Entity));
        long projectedSeq;
        DateTime projectedAt;
        using (var currentScope = fixture.ScopeFor(setup.Tenant))
        {
            var current = await currentScope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Current.AsNoTracking()
                .SingleAsync(row => row.UnitId == setup.UnitId);
            (projectedSeq, projectedAt) = (current.LastEventSeq, current.LastEventTime);
        }

        var ids = Enumerable.Range(0, 24).Select(_ => Guid.NewGuid()).ToList();
        var published = ids.Select((eventId, index) => index % 2 == 0
            ? (object)new UnitReleased(Header(UnitReleased.Schema, UnitReleased.Version, setup.UnitId, eventId,
                At.AddHours(3).AddMilliseconds(index)), setup.ItemId, Guid.NewGuid(), 1, new string('a', 64), [])
            : new EntitlementCoverageChanged(Header(EntitlementCoverageChanged.Schema,
                EntitlementCoverageChanged.Version, setup.UnitId, eventId, At.AddHours(3).AddMilliseconds(index)),
                [], [], null, null, EntitlementEventType.Created)).ToList();

        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
            await Task.WhenAll(published.Select(message => bus.PublishAsync(message,
                new DeliveryOptions { TenantId = setup.Tenant.ToString() }).AsTask()));
        }
        await WaitForTriggerCountAsync(setup.Tenant, ids, ids.Count);

        using var finalScope = fixture.ScopeFor(setup.Tenant);
        var db = finalScope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(ids.Count, await db.CertificateTriggers.CountAsync(row => ids.Contains(row.TriggeringEventId)));
        Assert.False(await db.CertificateTriggers.AnyAsync(row => ids.Contains(row.TriggeringEventId)
            && row.Outcome == "refused"));
        Assert.Equal(1, await db.CertificateInstances.CountAsync(row => row.UnitId == setup.UnitId
            && row.DocumentKind == CertificateDocumentKind.ReleaseRecord));
        Assert.Equal(1, await db.CertificateInstances.CountAsync(row => row.UnitId == setup.UnitId
            && row.DocumentKind == CertificateDocumentKind.WarrantyCertificate));
        var finalCurrent = await db.Current.AsNoTracking().SingleAsync(row => row.UnitId == setup.UnitId);
        Assert.Equal((projectedSeq, projectedAt), (finalCurrent.LastEventSeq, finalCurrent.LastEventTime));
        await AssertWholeUnitAsync(db, setup.UnitId, owner, setup.LocationId);
    }

    [DatabaseFact]
    public async Task Concurrent_first_template_reads_create_each_starter_once()
    {
        var tenant = await fixture.NewTenantAsync("cert-starter-race");
        async Task<IReadOnlyList<CertificateTemplateSummary>> LoadAsync()
        {
            using var scope = fixture.ScopeFor(tenant);
            return await scope.ServiceProvider.GetRequiredService<CertificateTemplateService>().ListAsync(CancellationToken.None);
        }

        var lists = await Task.WhenAll(LoadAsync(), LoadAsync());
        Assert.All(lists, list => Assert.Equal(7, list.Count(row => row.BuiltIn)));
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(7, await db.CertificateTemplates.CountAsync(row => row.BuiltIn));
        Assert.Equal(7, await db.CertificateTemplateVersions.CountAsync());
    }

    [DatabaseFact]
    public async Task Starter_upgrade_adds_a_new_builtin_version_without_changing_tenant_copies()
    {
        var tenant = await fixture.NewTenantAsync("cert-starter-upgrade");
        var builtInId = Guid.NewGuid();
        var copyId = Guid.NewGuid();
        const string legacyLayout = "<article><h1>Legacy release</h1><p>{{model.code}}</p></article>";
        using (var seed = fixture.ScopeFor(tenant))
        {
            var db = seed.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            db.CertificateTemplates.AddRange(
                new Yojana.Serialisation.Data.Entities.CertificateTemplate { TenantId = tenant, TemplateId = builtInId, Code = "builtin-generic-release", Name = "Generic release record", DocumentKind = CertificateDocumentKind.ReleaseRecord, FormType = CertificateFormType.GenericRelease, BuiltIn = true, CreatedAt = At, CreatedBy = "system:certificate-starters" },
                new Yojana.Serialisation.Data.Entities.CertificateTemplate { TenantId = tenant, TemplateId = copyId, Code = "tenant-copy", Name = "Tenant copy", DocumentKind = CertificateDocumentKind.ReleaseRecord, FormType = CertificateFormType.GenericRelease, BuiltIn = false, CreatedAt = At, CreatedBy = "template.admin" });
            db.CertificateTemplateVersions.AddRange(
                LegacyVersion(tenant, builtInId, legacyLayout, "system:certificate-starters"),
                LegacyVersion(tenant, copyId, legacyLayout, "template.admin"));
            await db.SaveChangesAsync();
        }

        using var scope = fixture.ScopeFor(tenant);
        var service = scope.ServiceProvider.GetRequiredService<CertificateTemplateService>();
        await service.ListAsync(CancellationToken.None);
        var dbAfter = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var builtIn = await dbAfter.CertificateTemplateVersions.AsNoTracking().Where(row => row.TemplateId == builtInId)
            .OrderBy(row => row.Version).ToListAsync();
        Assert.Equal([1, 4], builtIn.Select(row => row.Version));
        Assert.Equal(CertificateTemplateStatus.Retired, builtIn[0].Status);
        Assert.Equal(CertificateTemplateStatus.Active, builtIn[1].Status);
        Assert.Contains("As-built at release", builtIn[1].LayoutBody, StringComparison.Ordinal);
        var tenantCopy = await dbAfter.CertificateTemplateVersions.AsNoTracking().SingleAsync(row => row.TemplateId == copyId);
        Assert.Equal((1, CertificateTemplateStatus.Active, legacyLayout), (tenantCopy.Version, tenantCopy.Status, tenantCopy.LayoutBody));
    }

    [DatabaseFact]
    public async Task Certificate_template_policy_accepts_certificate_admin_and_rejects_label_admin()
    {
        var tenant = await fixture.NewTenantAsync("cert-role");
        using var scope = fixture.ScopeFor(tenant);
        var authorisation = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        ClaimsPrincipal Actor(string role) => new(new ClaimsIdentity(
            [new Claim(YojanaClaim.TenantId, tenant.ToString()), new Claim(YojanaClaim.Role, role)], "test"));

        Assert.True((await authorisation.AuthorizeAsync(Actor(YojanaRole.CertificateAdministrator), null,
            YojanaPolicy.CertificateAdministrator)).Succeeded);
        Assert.False((await authorisation.AuthorizeAsync(Actor(YojanaRole.LabelAdministrator), null,
            YojanaPolicy.CertificateAdministrator)).Succeeded);
        Assert.True((await authorisation.AuthorizeAsync(Actor(YojanaRole.CertificateAdministrator), null,
            YojanaPolicy.CertificateIssuer)).Succeeded);
        Assert.True((await authorisation.AuthorizeAsync(Actor(YojanaRole.Quality), null,
            YojanaPolicy.CertificateIssuer)).Succeeded);
        Assert.False((await authorisation.AuthorizeAsync(Actor(YojanaRole.LabelAdministrator), null,
            YojanaPolicy.CertificateIssuer)).Succeeded);
    }

    private async Task CorrectWithoutPublicationAsync(Setup setup, string correctedSerial)
    {
        using var scope = fixture.ScopeFor(setup.Tenant);
        scope.ServiceProvider.GetRequiredService<LedgerPublication>().Suppressed = true;
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await CorrectIdentifierHandler.Handle(new CorrectIdentifier(Guid.NewGuid(), setup.UnitId,
                IdentifierType.InternalSerial, correctedSerial, IdentifierCorrectionReason.Typo, At.AddHours(2.5),
                "quality.user", Entity), db, scope.ServiceProvider.GetRequiredService<IUnitEventWriter>(),
            scope.ServiceProvider.GetRequiredService<IdentifierService>(),
            scope.ServiceProvider.GetRequiredService<EffectiveItemProfiles>(),
            scope.ServiceProvider.GetRequiredService<CorrectionLedger>(),
            scope.ServiceProvider.GetRequiredService<ITenantContext>(),
            scope.ServiceProvider.GetRequiredService<IClock>(), CancellationToken.None);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private async Task WaitForTriggerCountAsync(TenantId tenant, IReadOnlyList<Guid> eventIds, int count)
    {
        var until = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < until)
        {
            using var scope = fixture.ScopeFor(tenant);
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            if (await db.CertificateTriggers.CountAsync(row => eventIds.Contains(row.TriggeringEventId)) >= count) return;
            await Task.Delay(100);
        }
        using var finalScope = fixture.ScopeFor(tenant);
        var finalDb = finalScope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(count, await finalDb.CertificateTriggers.CountAsync(row => eventIds.Contains(row.TriggeringEventId)));
    }

    private static async Task AssertWholeUnitAsync(SerialisationDbContext db, Guid unitId, Guid owner,
        Guid location, int expectedEntitlements = 0)
    {
        var ownership = await db.Episodes.AsNoTracking().SingleAsync(row => row.UnitId == unitId
            && row.Kind == EpisodeKind.Ownership && row.ValidTo == null);
        var custody = await db.Episodes.AsNoTracking().SingleAsync(row => row.UnitId == unitId
            && row.Kind == EpisodeKind.Custody && row.ValidTo == null);
        Assert.Equal(owner, ownership.PartyRef);
        Assert.Equal((location, LocationKind.Warehouse), (custody.LocationId, custody.LocationKind));
        Assert.Equal(expectedEntitlements, await db.Entitlements.CountAsync(row => row.UnitId == unitId
            && row.Status != EntitlementStatus.Closed));
        Assert.Single(await db.Identifiers.Where(row => row.UnitId == unitId && row.ValidTo == null).ToListAsync());
        Assert.True(await db.Events.AnyAsync(row => row.UnitId == unitId && row.EventType == CertificateEventType.Issued));
    }

    private async Task<Setup> CommissionAsync(string prefix, bool missingRequiredComponent = false, string? itemClass = null)
    {
        var tenant = await fixture.NewTenantAsync(prefix);
        var item = Guid.NewGuid();
        var location = Guid.NewGuid();
        var serial = prefix.ToUpperInvariant()[..Math.Min(prefix.Length, 10)] + "-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var items = fixture.Services.GetRequiredService<InMemoryItemPorts>();
        items.Register(InMemoryItemPorts.DefaultProfile(item) with
        {
            AsBuiltCompleteness = missingRequiredComponent ? ItemSerialisationProfile.Completeness.Warn : ItemSerialisationProfile.Completeness.Block,
            ItemClass = itemClass,
        });
        fixture.Services.GetRequiredService<InMemoryItemNamePort>().Register(new ItemLabel(item, "YT-100", "Telemetry controller"));
        if (missingRequiredComponent)
            items.RegisterBom(item, [new ExpectedComponent("BAT", Guid.NewGuid(), "BAT-1", 1)]);
        var command = new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Manufactured,
            [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, serial)])], At,
            "production.user", Entity, LocationId: location, LocationKind: LocationKind.Warehouse);
        var result = await InvokeAsync<CommissionUnitsResult>(tenant, command);
        return new Setup(tenant, result.Lines.Single().UnitId!.Value, item, location, serial);
    }

    private Task<ReleaseUnitResult> ReleaseAsync(Setup setup) => InvokeAsync<ReleaseUnitResult>(setup.Tenant,
        new ReleaseUnit(Guid.NewGuid(), setup.UnitId, At.AddHours(1), "quality.user", Entity, "sig:quality"));

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>()
            .InvokeAsync<T>(message, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private async Task InvokeEventAsync(TenantId tenant, object message)
    {
        using var scope = fixture.ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<IMessageBus>()
            .InvokeAsync(message, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None, Timeout);
    }

    private async Task WaitForCountAsync(TenantId tenant, Guid unitId, string kind, int count)
    {
        var until = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < until)
        {
            using var scope = fixture.ScopeFor(tenant);
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            if (await db.CertificateInstances.CountAsync(row => row.UnitId == unitId && row.DocumentKind == kind) >= count) return;
            await Task.Delay(100);
        }
        using var finalScope = fixture.ScopeFor(tenant);
        var finalDb = finalScope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(count, await finalDb.CertificateInstances.CountAsync(row => row.UnitId == unitId && row.DocumentKind == kind));
    }

    private async Task AssertCountsAsync(TenantId tenant, Guid unitId, int release, int warranty)
    {
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(release, await db.CertificateInstances.CountAsync(row => row.UnitId == unitId && row.DocumentKind == CertificateDocumentKind.ReleaseRecord));
        Assert.Equal(warranty, await db.CertificateInstances.CountAsync(row => row.UnitId == unitId && row.DocumentKind == CertificateDocumentKind.WarrantyCertificate));
    }

    private static PublishedEventHeader Header(string schema, string version, Guid unitId, Guid eventId, DateTime at) =>
        new(schema, version, eventId, unitId, at, at, 1, Entity, "system:test-event", null);

    private static Yojana.Serialisation.Data.Entities.CertificateTemplateVersion LegacyVersion(TenantId tenant, Guid templateId, string layout, string actor) => new()
    {
        TenantId = tenant, TemplateVersionId = Guid.NewGuid(), TemplateId = templateId, Version = 1,
        Language = "en", PayloadSchemaVersion = "1.0", Status = CertificateTemplateStatus.Active,
        EffectiveFrom = At, LayoutKind = CertificateLayoutKind.Html, LayoutBody = layout, PageSize = "A4",
        Orientation = "portrait", MarginTopMm = 15, MarginRightMm = 15, MarginBottomMm = 15, MarginLeftMm = 15,
        Fields = "[\"model.code\"]", ApprovedBy = actor, ApprovedAt = At, CreatedAt = At, CreatedBy = actor,
        UpdatedAt = At, UpdatedBy = actor,
    };

    private sealed record Setup(TenantId Tenant, Guid UnitId, Guid ItemId, Guid LocationId, string Serial);
}

public sealed record CreateSaleEntitlements(Guid UnitId, Guid OwnerId, IReadOnlyList<Guid> PolicyVersionIds,
    DateTime EventTime, Guid LegalEntityId);

public static class CreateSaleEntitlementsHandler
{
    public static async Task Handle(CreateSaleEntitlements message, SerialisationDbContext db, EntitlementLedger ledger,
        PolicyResolver resolver, PolicyStore store, ITimeZonePort zones, RefurbishedGoodsSettings refurbishedSettings,
        IJurisdictionRegionPort regions, IJurisdictionRuleResolver jurisdictionRules, IItemQueryPort items,
        ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        for (var index = 0; index < message.PolicyVersionIds.Count; index++)
        {
            var family = index == 0 ? "sale_parts" : "sale_service";
            await CreateEntitlementHandler.Handle(new CreateEntitlement(Guid.NewGuid(), message.UnitId, family,
                DateOnly.FromDateTime(message.EventTime), message.LegalEntityId, message.EventTime, "sales",
                new Dictionary<string, DateTime> { [StartTrigger.Invoice] = message.EventTime },
                PolicyVersionId: message.PolicyVersionIds[index], OwnerPartyRef: message.OwnerId,
                OwnerPartyType: PartyType.Customer, SourceDocument: new DocumentRef("invoice", Guid.NewGuid(), index + 1)),
                db, ledger, resolver, store, zones, refurbishedSettings, regions, jurisdictionRules, tenant, clock, items, cancellationToken);
        }
    }
}
