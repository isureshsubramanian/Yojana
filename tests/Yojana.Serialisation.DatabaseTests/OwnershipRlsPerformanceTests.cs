using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Certificates;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Queries;
using Yojana.Serialisation.Rmas;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>Back-office read-shape guard for the portal ownership RLS startup branch.</summary>
[Collection("serialisation host")]
public sealed class OwnershipRlsPerformanceTests(SerialisationHostFixture fixture)
{
    private static readonly DateTime At = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly Regex LogicalReads = new(
        @"logical reads (?<reads>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex OwnershipTableReads = new(
        @"Table '(?:custody_episode|assembly_link)'.*?logical reads (?<reads>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [DatabaseFact]
    public async Task Back_office_read_shapes_keep_their_statement_and_logical_read_budgets()
    {
        var tenant = await fixture.NewTenantAsync("p7rlsperf");
        var item = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var serial = "RLS-PERF-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var commissioned = await InvokeAsync<CommissionUnitsResult>(tenant,
            new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Manufactured,
                [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, serial)])], At,
                "test:production", Guid.NewGuid(), LocationId: Guid.NewGuid(), LocationKind: LocationKind.Warehouse));
        var unitId = commissioned.Lines.Single().UnitId!.Value;
        var owned = await InvokeAsync<OpenOwnershipResult>(tenant,
            new OpenOwnership(Guid.NewGuid(), unitId, owner, PartyType.Customer, EpisodeReason.Sale,
                At.AddDays(1), "test:sales", Guid.NewGuid()));

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var backOfficeParty = await db.Database.SqlQueryRaw<Guid?>(
            "SELECT TRY_CAST(SESSION_CONTEXT(N'portal_party_ref') AS uniqueidentifier) AS [Value]")
            .SingleAsync();
        Assert.Null(backOfficeParty);
        var rmaId = Guid.NewGuid();
        db.Rmas.Add(new Rma
        {
            TenantId = tenant, RmaId = rmaId, CommandId = Guid.NewGuid(), RmaNumber = "RMA-RLS-PERF",
            Stage = RmaStage.Requested, RequestDate = DateOnly.FromDateTime(At), ReturningPartyRef = owner,
            SettlementPartyRef = owner, ServiceEntityId = Guid.NewGuid(), ObligorEntityId = Guid.NewGuid(),
            ServiceCountry = "GB", Currency = "GBP", RequestedAt = At.AddDays(2), RequestedBy = "test:service",
            LastEventId = Guid.NewGuid(), LastModifiedBy = "test:service", LastModifiedAt = At.AddDays(2),
        });
        db.RmaLines.Add(new RmaLine
        {
            TenantId = tenant, RmaLineId = Guid.NewGuid(), RmaId = rmaId, Sequence = 1,
            RequestedUnitId = unitId, OwnershipEpisodeId = owned.EpisodeId,
            OriginalSaleDocumentType = "delivery", OriginalSaleDocumentId = Guid.NewGuid(),
            CoverageFamily = CoverageFamilyCode.Base, ClaimLineKind = ClaimLineKind.Parts,
        });
        await scope.ServiceProvider.GetRequiredService<CertificateTemplateService>()
            .EnsureStartersAsync(CancellationToken.None);
        var templateVersion = await db.CertificateTemplateVersions.AsNoTracking()
            .Select(row => row.TemplateVersionId).FirstAsync();
        db.CertificateInstances.Add(new CertificateInstance
        {
            TenantId = tenant, CertificateId = Guid.NewGuid(), UnitId = unitId,
            DocumentKind = CertificateDocumentKind.ReleaseRecord, FormType = CertificateFormType.GenericRelease,
            Version = 1, TrackingNumber = "CERT-RLS-PERF", TemplateVersionId = templateVersion,
            Payload = "{}", PayloadHash = "payload", FactsHash = "facts", TriggeringEventId = Guid.NewGuid(),
            IssuerEntityId = Guid.NewGuid(), IssuerSnapshot = "{}", BrandingSnapshot = "{}", Language = "en",
            Provenance = CertificateProvenance.SystemGenerated, IssuedAt = At.AddDays(2), Status = CertificateStatus.Issued,
        });
        await db.SaveChangesAsync();

        var (_, lookupStatements) = await StatementCounter.CountAsync(() => scope.ServiceProvider
            .GetRequiredService<UnitQueries>().LookupAsync(serial, IdentifierType.InternalSerial,
                CancellationToken.None));
        var (_, rmaStatements) = await StatementCounter.CountAsync(() => scope.ServiceProvider
            .GetRequiredService<RmaQueries>().ListAsync(new RmaListFilter(null, null, null, serial[..8]),
                At.AddDays(10), null, 25, CancellationToken.None));
        var (_, certificateStatements) = await StatementCounter.CountAsync(() => scope.ServiceProvider
            .GetRequiredService<CertificateQueries>().PageAsync(null, null, null, serial[..8], null, null,
                0, 25, CancellationToken.None));

        await db.Database.OpenConnectionAsync();
        var connection = (SqlConnection)db.Database.GetDbConnection();
        var lookupIo = await ReadsAsync(connection, """
            SELECT TOP (3) identifier.unit_id, current_state.item_id
            FROM serialisation.unit_identifier AS identifier
            INNER JOIN serialisation.unit_current AS current_state
                ON current_state.tenant_id = identifier.tenant_id AND current_state.unit_id = identifier.unit_id
            WHERE identifier.tenant_id = @tenant AND identifier.normalised_value = @serial
                AND identifier.type = @type;
            """, ("@tenant", tenant.Value), ("@serial", serial.Replace("-", "", StringComparison.Ordinal)),
            ("@type", IdentifierType.InternalSerial));
        var ledgerIo = await ReadsAsync(connection, """
            SELECT event_id, event_type, event_time
            FROM serialisation.unit_event
            WHERE tenant_id = @tenant AND unit_id = @unit
                AND event_time >= @from AND event_time < @to
            ORDER BY event_time DESC, seq DESC;
            """, ("@tenant", tenant.Value), ("@unit", unitId), ("@from", At), ("@to", At.AddMonths(1)));
        var rmaIo = await ReadsAsync(connection, """
            SELECT TOP (25) rma.rma_id, line.requested_unit_id, current_state.item_id
            FROM serialisation.rma AS rma
            INNER JOIN serialisation.rma_line AS line
                ON line.tenant_id = rma.tenant_id AND line.rma_id = rma.rma_id
            INNER JOIN serialisation.unit_current AS current_state
                ON current_state.tenant_id = line.tenant_id AND current_state.unit_id = line.requested_unit_id
            WHERE rma.tenant_id = @tenant
            ORDER BY rma.requested_at, rma.rma_id;
            """, ("@tenant", tenant.Value));
        var certificateIo = await ReadsAsync(connection, """
            SELECT certificate_id, tracking_number, issued_at
            FROM serialisation.certificate_instance
            WHERE tenant_id = @tenant AND unit_id = @unit
            ORDER BY issued_at DESC, certificate_id DESC;
            """, ("@tenant", tenant.Value), ("@unit", unitId));

        // Pre-P7T08 baseline on the same database: lookup 2 statements/6 reads; RMA 18/8;
        // certificate 10/5; ledger 40 reads. The paired post-migration run was 2/6, 18/8,
        // 10/4 and 40 respectively. Absolute page reads can rise as this shared suite grows its
        // B-trees; the stable regression signal is that the NULL-key startup branch performs no
        // ownership-table reads at all.
        // Ceilings, not exact counts: other work may lower a read shape's statements without touching row security.
        Assert.InRange(lookupStatements, 1, 2);
        Assert.InRange(rmaStatements, 1, 18);
        // 12 since P6 polish 4, which added a batched issued-by name read per page (not per row).
        Assert.InRange(certificateStatements, 1, 12);
        AssertNoOwnershipReads("current-state lookup", lookupIo);
        AssertNoOwnershipReads("RMA list", rmaIo);
        AssertNoOwnershipReads("certificate read", certificateIo);
        AssertNoOwnershipReads("ledger read", ledgerIo);

        using var portalScope = fixture.ScopeForPortal(tenant, owner);
        var portalDb = portalScope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        await portalDb.Database.OpenConnectionAsync();
        var portalLookupIo = await ReadsAsync((SqlConnection)portalDb.Database.GetDbConnection(), """
            SELECT TOP (3) identifier.unit_id, current_state.item_id
            FROM serialisation.unit_identifier AS identifier
            INNER JOIN serialisation.unit_current AS current_state
                ON current_state.tenant_id = identifier.tenant_id AND current_state.unit_id = identifier.unit_id
            WHERE identifier.tenant_id = @tenant AND identifier.normalised_value = @serial
                AND identifier.type = @type;
            """, ("@tenant", tenant.Value), ("@serial", serial.Replace("-", "", StringComparison.Ordinal)),
            ("@type", IdentifierType.InternalSerial));
        Assert.True(portalLookupIo.OwnershipLogicalReads > 0,
            "The portal control query must touch an ownership table so the zero-read back-office assertions are meaningful. " +
            portalLookupIo.Trace);
    }

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(command,
            new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private static void AssertNoOwnershipReads(string query, IoMeasurement measurement) =>
        Assert.True(measurement.OwnershipLogicalReads == 0,
            $"{query} executed {measurement.OwnershipLogicalReads} ownership-table logical reads " +
            $"({measurement.TotalLogicalReads} total); the back-office startup branch was not removed.");

    private static async Task<IoMeasurement> ReadsAsync(SqlConnection connection, string sql,
        params (string Name, object Value)[] parameters)
    {
        var messages = new List<string>();
        SqlInfoMessageEventHandler handler = (_, args) => messages.AddRange(args.Errors.Cast<SqlError>()
            .Select(error => error.Message));
        connection.InfoMessage += handler;
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SET STATISTICS IO ON;\n" + sql + "\nSET STATISTICS IO OFF;";
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            await using var reader = await command.ExecuteReaderAsync();
            do
            {
                while (await reader.ReadAsync()) { }
            } while (await reader.NextResultAsync());
        }
        finally
        {
            connection.InfoMessage -= handler;
        }

        var total = messages.SelectMany(message => LogicalReads.Matches(message).Cast<Match>())
            .Sum(ReadCount);
        var ownership = messages.SelectMany(message => OwnershipTableReads.Matches(message).Cast<Match>())
            .Sum(ReadCount);
        return new IoMeasurement(total, ownership, string.Join(" | ", messages));
    }

    private static int ReadCount(Match match) => int.Parse(match.Groups["reads"].Value,
        System.Globalization.CultureInfo.InvariantCulture);

    private sealed record IoMeasurement(int TotalLogicalReads, int OwnershipLogicalReads, string Trace);
}
