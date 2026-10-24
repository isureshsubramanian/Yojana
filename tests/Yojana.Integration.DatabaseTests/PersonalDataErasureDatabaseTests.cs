using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.SqlClient;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Gateway;
using Yojana.Integration.Privacy;
using Yojana.Platform.Contracts.Privacy;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;
using Yojana.Platform.Privacy;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class PersonalDataErasureDatabaseTests(IntegrationHostFixture fixture)
{
    [DatabaseFact]
    public async Task Refused_request_is_not_a_gateway_tombstone()
    {
        var tenant = await fixture.NewTenantAsync("erref");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.CreateVersion7());
        const string externalCode = "FIC-REFUSED-9901";
        Guid party;
        using (var initial = fixture.ScopeFor(tenant))
        {
            var gateway = initial.ServiceProvider.GetRequiredService<GatewayReferenceStore>();
            party = await gateway.UpsertPartyAsync(connection,
                new(externalCode, OpenEnum.Known("person"), "Fictional Initial Customer", "TAX-1", "GB"), default);
            await initial.ServiceProvider.GetRequiredService<IntegrationDbContext>().SaveChangesAsync();
        }
        using (var refusal = fixture.ScopeFor(tenant))
        {
            var platform = refusal.ServiceProvider.GetRequiredService<PlatformDbContext>();
            platform.ErasureRequests.Add(new ErasureRequest
            {
                TenantId = tenant, ErasureId = Guid.CreateVersion7(), SubjectKind = ErasureSubjectKind.Party,
                SubjectId = party, RequestReference = "DSR-2026-REFUSED", Reason = "open return",
                RequestReceivedOn = new DateOnly(2026, 9, 1), RequestedBy = Guid.CreateVersion7(),
                RequestedAt = fixture.Clock.UtcNow, Status = ErasureRequestStatus.Refused,
                RetentionBasis = PersonalDataErasureService.RetentionBasis, FailureRule = "erasure_open_obligations",
            });
            await platform.SaveChangesAsync();
        }

        using (var update = fixture.ScopeFor(tenant))
        {
            var gateway = update.ServiceProvider.GetRequiredService<GatewayReferenceStore>();
            Assert.Equal(party, await gateway.UpsertPartyAsync(connection,
                new(externalCode, OpenEnum.Known("person"), "Fictional Updated Customer", "TAX-2", "GB"), default));
            await update.ServiceProvider.GetRequiredService<IntegrationDbContext>().SaveChangesAsync();
        }

        using var verify = fixture.ScopeFor(tenant);
        var projection = await verify.ServiceProvider.GetRequiredService<IntegrationDbContext>()
            .ReferenceProjections.AsNoTracking().SingleAsync(x => x.ModuleId == party);
        Assert.Equal("Fictional Updated Customer", projection.DisplayName);
        Assert.Contains("TAX-2", projection.AttributesJson, StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task Stored_payload_erasure_crosses_a_batch_boundary_and_a_rerun_is_a_no_op()
    {
        var tenant = await fixture.NewTenantAsync("erase-batch");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.CreateVersion7());
        var party = Guid.CreateVersion7();
        const string name = "Fictional Batch Customer";
        const int rowCount = 502;
        using (var seed = fixture.ScopeFor(tenant))
        {
            var db = seed.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            db.CanonicalDocuments.AddRange(Enumerable.Range(0, rowCount).Select(index => new CanonicalDocument
            {
                TenantId = tenant, DocumentId = Guid.CreateVersion7(), ConnectionId = connection,
                ErpEntity = "document", ErpExternalId = $"BATCH-{index:D4}", MessageType = "InvoicePosted",
                Payload = $$"""{"customer_name":"{{name}}","sequence":{{index}}}""",
                OccurredAt = fixture.Clock.UtcNow, UpdatedAt = fixture.Clock.UtcNow,
                SourceInboundMessageId = Guid.CreateVersion7(),
            }));
            await db.SaveChangesAsync();
        }

        var subject = new ErasureSubject(ErasureSubjectKind.Party, party, [name], []);
        using (var preview = fixture.ScopeFor(tenant))
        {
            var result = await preview.ServiceProvider.GetRequiredService<IntegrationPersonalDataHolder>()
                .PreviewAsync(subject, default);
            Assert.Equal(rowCount, result.Counts.Single(x => x.Kind == "stored_messages").Count);
        }
        using (var erase = fixture.ScopeFor(tenant))
        {
            var result = await erase.ServiceProvider.GetRequiredService<IntegrationPersonalDataHolder>()
                .EraseAsync(subject, Guid.CreateVersion7(), default);
            Assert.Equal(rowCount, result.Counts.Single(x => x.Kind == "stored_messages").Count);
        }
        using (var rerun = fixture.ScopeFor(tenant))
        {
            var result = await rerun.ServiceProvider.GetRequiredService<IntegrationPersonalDataHolder>()
                .EraseAsync(subject, Guid.CreateVersion7(), default);
            Assert.Equal(0, result.Counts.Single(x => x.Kind == "stored_messages").Count);
        }
        using var verify = fixture.ScopeFor(tenant);
        var payloads = await verify.ServiceProvider.GetRequiredService<IntegrationDbContext>()
            .CanonicalDocuments.AsNoTracking().Where(x => x.ConnectionId == connection)
            .Select(x => x.Payload).ToListAsync();
        Assert.Equal(rowCount, payloads.Count);
        Assert.All(payloads, payload =>
        {
            Assert.DoesNotContain(name, payload, StringComparison.Ordinal);
            using var document = JsonDocument.Parse(payload);
            Assert.Equal(subject.Token, document.RootElement.GetProperty("customer_name").GetString());
        });
    }

    [DatabaseFact]
    public async Task Erased_party_mapping_is_one_way_and_a_later_upsert_stores_only_the_token()
    {
        var tenant = await fixture.NewTenantAsync("erase-party");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.CreateVersion7());
        var externalCode = "FIC-SOLE-9901";
        var legalName = "Fictional Network Services";
        var taxId = "TAX-FIC-9901";
        Guid party;
        using (var initial = fixture.ScopeFor(tenant))
        {
            var gateway = initial.ServiceProvider.GetRequiredService<GatewayReferenceStore>();
            party = await gateway.UpsertPartyAsync(connection,
                new(externalCode, OpenEnum.Known("person"), legalName, taxId, "GB"), default);
            var db = initial.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            var inboundId = Guid.CreateVersion7();
            var payload = $$"""{"customer_name":"{{legalName}}","customer_code":"{{externalCode}}","tax_id":"{{taxId}}","email":"person@example.invalid"}""";
            db.InboundMessages.Add(new InboundMessage
            {
                TenantId = tenant, InboundMessageId = inboundId, MessageId = Guid.CreateVersion7(),
                ConnectionId = connection, ConnectorCode = IntegrationHostFixture.FakeConnectorCode,
                ErpEntity = "party", ErpExternalId = "PARTY-MESSAGE-9901", ErpChangeMarker = "1",
                MarkerKind = "sequence", ContractVersion = "1.0", MessageType = "PartyUpserted",
                RawPayload = payload, CanonicalMessage = payload, OccurredAt = fixture.Clock.UtcNow,
                ObservedAt = fixture.Clock.UtcNow, AcceptedAt = fixture.Clock.UtcNow, IsLatest = true,
                TranslationStatus = InboundTranslationStatus.Completed,
            });
            db.CanonicalDocuments.Add(new CanonicalDocument
            {
                TenantId = tenant, DocumentId = Guid.CreateVersion7(), ConnectionId = connection,
                ErpEntity = "document", ErpExternalId = "DOC-ERASE-9901", MessageType = "InvoicePosted",
                Payload = payload, OccurredAt = fixture.Clock.UtcNow, UpdatedAt = fixture.Clock.UtcNow,
                SourceInboundMessageId = inboundId,
            });
            db.DeadLetterItems.Add(new DeadLetterItem
            {
                TenantId = tenant, DeadLetterItemId = Guid.CreateVersion7(), ConnectionId = connection,
                Direction = DeadLetterDirection.Inbound, RecordType = "InvoicePosted",
                SourceKind = DeadLetterSourceKind.InboundMessage, SourceId = inboundId,
                ReasonCode = "fixture", RawPayload = payload, CanonicalAttempt = payload,
                Failure = "fixture", Status = DeadLetterStatus.NeedsAttention,
                CreatedAt = fixture.Clock.UtcNow, UpdatedAt = fixture.Clock.UtcNow,
            });
            db.OutboundCommands.Add(new OutboundCommandRow
            {
                TenantId = tenant, OutboundCommandId = Guid.CreateVersion7(), ConnectionId = connection,
                CommandType = "AttachDocument", IdempotencyKey = Guid.CreateVersion7(),
                ModuleDocumentRef = "fixture", ErpReferenceText = "fixture", Payload = payload,
                Status = OutboundCommandStatus.Pending, CreatedAt = fixture.Clock.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var subject = new ErasureSubject(ErasureSubjectKind.Party, party, [], []);
        using (var preview = fixture.ScopeFor(tenant))
        {
            var result = await preview.ServiceProvider.GetRequiredService<IntegrationPersonalDataHolder>()
                .PreviewAsync(subject, default);
            Assert.NotEmpty(result.Counts);
            Assert.Contains("person@example.invalid", result.KnownValues);
            subject = subject with { KnownValues = result.KnownValues };
            var unchanged = await preview.ServiceProvider.GetRequiredService<IntegrationDbContext>()
                .ReferenceProjections.AsNoTracking().SingleAsync(x => x.ModuleId == party);
            Assert.Equal(legalName, unchanged.DisplayName);
            Assert.Contains(taxId, unchanged.AttributesJson, StringComparison.Ordinal);
        }

        var erasure = Guid.CreateVersion7();
        using (var tombstone = fixture.ScopeFor(tenant))
        {
            var platform = tombstone.ServiceProvider.GetRequiredService<PlatformDbContext>();
            platform.ErasureRequests.Add(new ErasureRequest
            {
                TenantId = tenant, ErasureId = erasure, SubjectKind = ErasureSubjectKind.Party,
                SubjectId = party, RequestReference = "DSR-2026-9801", Reason = "verified request",
                RequestReceivedOn = new DateOnly(2026, 9, 1),
                RequestedBy = Guid.CreateVersion7(), RequestedAt = fixture.Clock.UtcNow,
                StartedAt = fixture.Clock.UtcNow, Status = ErasureRequestStatus.Running,
                RetentionBasis = PersonalDataErasureService.RetentionBasis,
            });
            await platform.SaveChangesAsync();
        }

        using (var erase = fixture.ScopeFor(tenant))
            await erase.ServiceProvider.GetRequiredService<IntegrationPersonalDataHolder>()
                .EraseAsync(subject, erasure, default);

        using (var replay = fixture.ScopeFor(tenant))
        {
            var gateway = replay.ServiceProvider.GetRequiredService<GatewayReferenceStore>();
            Assert.Equal(party, await gateway.UpsertPartyAsync(connection,
                new(externalCode, OpenEnum.Known("person"), legalName, taxId, "GB"), default));
            await replay.ServiceProvider.GetRequiredService<IntegrationDbContext>().SaveChangesAsync();
        }

        using (var rerun = fixture.ScopeFor(tenant))
        {
            var rerunDb = rerun.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            var before = await rerunDb.ExternalIdMaps.AsNoTracking().Where(x => x.ModuleId == party)
                .Select(x => x.ExternalId).SingleAsync();
            await rerun.ServiceProvider.GetRequiredService<IntegrationPersonalDataHolder>()
                .EraseAsync(subject, erasure, default);
            var after = await rerunDb.ExternalIdMaps.AsNoTracking().Where(x => x.ModuleId == party)
                .Select(x => x.ExternalId).SingleAsync();
            Assert.Equal(before, after);
        }

        using var verify = fixture.ScopeFor(tenant);
        var verifyDb = verify.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var map = await verifyDb.ExternalIdMaps.AsNoTracking().SingleAsync(x => x.ModuleId == party);
        Assert.StartsWith("hmac256:", map.ExternalId, StringComparison.Ordinal);
        Assert.DoesNotContain(externalCode, map.ExternalId, StringComparison.Ordinal);
        var projection = await verifyDb.ReferenceProjections.AsNoTracking().SingleAsync(x => x.ModuleId == party);
        Assert.Equal(subject.Token, projection.DisplayName);
        Assert.Equal(subject.Code, projection.Code);
        Assert.Equal("{}", projection.AttributesJson);
        var labels = await verify.ServiceProvider.GetRequiredService<IPartyNamePort>()
            .NamesAsync([party], default);
        Assert.Equal(subject.Token, labels[party].Name);
        Assert.Equal(subject.Code, labels[party].Code);
        foreach (var value in new[] { externalCode, legalName, taxId, "person@example.invalid" })
            Assert.Empty(await StringOccurrencesAsync(verifyDb, tenant.Value, value));
    }

    private static async Task<IReadOnlyList<string>> StringOccurrencesAsync(IntegrationDbContext db, Guid tenant,
        string value)
    {
        await db.Database.OpenConnectionAsync();
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            var columns = new List<(string Schema, string Table, string Column)>();
            await using (var metadata = connection.CreateCommand())
            {
                metadata.CommandText = """
                    SELECT s.name, t.name, c.name
                    FROM sys.tables t
                    JOIN sys.schemas s ON s.schema_id = t.schema_id
                    JOIN sys.columns c ON c.object_id = t.object_id
                    JOIN sys.types ty ON ty.user_type_id = c.user_type_id
                    WHERE s.name IN ('platform','serialisation','integration','knowledge')
                      AND ty.name IN ('char','nchar','varchar','nvarchar','text','ntext')
                      AND EXISTS (SELECT 1 FROM sys.columns tc
                                  WHERE tc.object_id = t.object_id AND tc.name = 'tenant_id');
                    """;
                await using var reader = await metadata.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    columns.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
            var found = new List<string>();
            foreach (var column in columns)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT COUNT_BIG(*) FROM {Name(column.Schema)}.{Name(column.Table)} "
                    + $"WHERE [tenant_id] = @tenant AND CONVERT(nvarchar(max), {Name(column.Column)}) LIKE @value";
                command.Parameters.Add(new SqlParameter("@tenant", tenant));
                command.Parameters.Add(new SqlParameter("@value", "%" + value + "%"));
                if (Convert.ToInt64(await command.ExecuteScalarAsync()) > 0)
                    found.Add($"{column.Schema}.{column.Table}.{column.Column}");
            }
            return found;
        }
        finally { await db.Database.CloseConnectionAsync(); }

        static string Name(string value) => $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";
    }
}
