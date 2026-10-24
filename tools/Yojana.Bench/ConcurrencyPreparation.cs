using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Ports;

namespace Yojana.Bench;

/// <summary>
/// Builds the bounded, persistent P6-T18 working set through the same messages as the application. SQL is read-only and
/// only discovers the loaded units or verifies the resulting tag documents.
/// </summary>
public sealed class ConcurrencyPreparation(IServiceProvider services, BenchOptions options)
{
    public const string Actor = "bench.concurrency.prepare";
    public const int RefurbishmentUnits = 200;
    public const int OpenClaims = 300;
    public const int EditableClaims = 50;

    private static readonly DateTime ShipmentAt = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ClaimAt = new(2026, 9, 2, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ReturnAt = new(2026, 9, 3, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Customer = Guid.Parse("b0c00000-0000-4000-8000-000000000001");
    private static readonly Guid ServiceEntity = Guid.Parse("b0c00000-0000-4000-8000-000000000002");
    private static readonly Guid CustomerSite = Guid.Parse("b0c00000-0000-4000-8000-000000000003");
    private static readonly Guid Quarantine = Guid.Parse("b0c00000-0000-4000-8000-000000000004");
    public static readonly Guid ShipmentDocumentId = Guid.Parse("b0c00000-0000-4000-8000-000000000010");
    private static readonly Guid FirstRoundStockSelectionId = Guid.Parse("b0c00000-0000-4000-8000-000000000011");

    /// <summary>The run's working-set round (YOJANA_BENCH_CLAIM_ROUND; unset = 1). Later rounds get fresh claims and a
    /// fresh replacement-stock selection, because a run consumes both. Shared by preparation and the runner.</summary>
    public static string Round => Environment.GetEnvironmentVariable("YOJANA_BENCH_CLAIM_ROUND") is { Length: > 0 } round
        && round.Trim() != "1" ? round.Trim() : "";

    public static Guid StockSelectionId => Round.Length == 0 ? FirstRoundStockSelectionId
        : new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"yojana-bench-concurrency:stock-selection:r{Round}"))[..16]);
    public static string ClaimPoolReference(string numberingMode)
    {
        var mode = numberingMode.ToUpperInvariant().Replace('_', '-');
        var suffix = Round.Length == 0 ? "" : $"-r{Round}";
        return $"BENCH-CONCURRENCY-{mode}{suffix}";
    }
    private static readonly Guid RefurbishmentRmaCommandId = Guid.Parse("b0c00000-0000-4000-8000-000000000012");
    private const string ShipmentDocumentType = "bench_concurrency";
    private const string BasePolicyCode = "BENCH-CONCURRENCY-36";
    private const string ReplacementPolicyCode = "BENCH-CARRY";
    private readonly int approvedClaims = ConcurrencyWorkload.RequiredReplacementCapacity(options);
    private readonly int replacementStockUnits = ConcurrencyWorkload.RequiredReplacementCapacity(options);
    private int customerUnits = ConcurrencyWorkload.RequiredRmaEligibleUnits(options)
        + ConcurrencyWorkload.RequiredReplacementCapacity(options) + RefurbishmentUnits;

    private Guid Tenant => options.ConcurrencyTenantId;
    private string TenantDelivery => Tenant.ToString();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await ConfigureCurrencyAsync(cancellationToken);
        var policyVersionId = await EnsurePoliciesAsync(cancellationToken);
        var existing = await ExistingPreparedUnitsAsync(cancellationToken);
        var requiredRmaUnits = ConcurrencyWorkload.RequiredRmaEligibleUnits(options);
        var missingApprovedSources = Math.Max(0, approvedClaims - existing.CurrentApprovedSources);
        var requiredAvailableUnits = requiredRmaUnits + missingApprovedSources;
        customerUnits = Math.Max(customerUnits,
            existing.Prepared + Math.Max(0, requiredAvailableUnits - existing.RmaEligible));
        var units = await LoadCustomerUnitsAsync(cancellationToken);
        if (units.Count < customerUnits)
            throw new InvalidOperationException($"prepare-concurrency needs {customerUnits:N0} loaded units with current base cover and a business-time-safe history; found {units.Count:N0}.");
        var availableUnits = units.Where(unit => unit.AvailableForRun).ToList();
        if (availableUnits.Count < requiredAvailableUnits)
            throw new InvalidOperationException($"prepare-concurrency needs {requiredAvailableUnits:N0} unused customer/replacement candidates; found {availableUnits.Count:N0} after excluding earlier rounds and open refurbishment work.");

        var validSourceUnits = units.Where(unit => unit.CurrentApprovedSource).Take(approvedClaims)
            .Concat(availableUnits.Take(missingApprovedSources)).ToList();
        if (validSourceUnits.Count < approvedClaims)
            throw new InvalidOperationException($"prepare-concurrency needs {approvedClaims:N0} approved replacement sources for this round; found {validSourceUnits.Count:N0} existing or eligible units.");
        var entitlementChanges = await EnsureCommandEntitlementsAsync(validSourceUnits, policyVersionId, cancellationToken);
        var shipped = await EnsureShipmentAsync(units, cancellationToken);
        var refurbished = await EnsureRefurbishmentsAsync(units.TakeLast(RefurbishmentUnits).ToList(), cancellationToken);
        var claimUnits = validSourceUnits.Concat(availableUnits.Skip(missingApprovedSources).Take(OpenClaims)).ToList();
        var claims = await EnsureClaimsAsync(claimUnits, cancellationToken);
        var documents = await EnsureDocumentsAsync(cancellationToken);
        var stock = await EnsureReplacementStockTagAsync(units.Select(unit => unit.UnitId).ToHashSet(), cancellationToken);
        var consistency = await VerifyWholeUnitConsistencyAsync(cancellationToken);

        Console.WriteLine($"prepared tenant {options.ConcurrencyTenantLabel} ({Tenant})");
        Console.WriteLine($"customer shipment: {shipped:N0}/{customerUnits:N0} units; command-policy entitlements: {entitlementChanges:N0}/{approvedClaims:N0}");
        Console.WriteLine($"RMA capacity: {requiredRmaUnits:N0} eligible units for {options.Engineers} engineers × {options.Duration.TotalMinutes:0.##} min (weighted headroom with a 12,000-unit verification floor)");
        Console.WriteLine($"refurbishment stock: {refurbished:N0}/{RefurbishmentUnits:N0} open work orders; claims: {claims.Open:N0} adjudication + {claims.Editable:N0} editable + {claims.Approved:N0}/{approvedClaims:N0} approved sources");
        Console.WriteLine($"editable documents: {documents.Campaigns:N0} campaigns + {documents.PolicyDrafts:N0} policy drafts; replacement-stock capacity: {stock:N0}/{replacementStockUnits:N0} units");
        Console.WriteLine($"whole-unit check: owners {customerUnits - RefurbishmentUnits:N0} customer + {RefurbishmentUnits:N0} us; holders {consistency.CustomerSite:N0} customer-site + {consistency.Quarantine:N0} us/quarantine + {consistency.InRepair:N0} us/in-repair; live base cover and open internal serial retained on all {customerUnits:N0} units");
    }

    private async Task<(int Prepared, int RmaEligible, int CurrentApprovedSources)> ExistingPreparedUnitsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await Sql.OpenAsync(options.ConnectionString, Tenant);
        var rows = await Sql.RowsAsync(connection, """
            WITH prepared AS (
                SELECT DISTINCT unit_id FROM serialisation.unit_event
                WHERE tenant_id = @tenant AND document_type = @document_type AND document_id = @document_id
                  AND event_type = 'unit.shipped'
            ), refurbished AS (
                SELECT DISTINCT target_unit_id FROM serialisation.refurbishment_work_order
                WHERE tenant_id = @tenant
            ), approved_source AS (
                SELECT DISTINCT unit_id FROM serialisation.claim
                WHERE tenant_id = @tenant AND submitted_by = @actor AND status = 'approved'
            ), current_approved_source AS (
                SELECT DISTINCT c.unit_id FROM serialisation.claim c
                WHERE c.tenant_id = @tenant AND c.submitted_by = @actor AND c.status = 'approved'
                  AND EXISTS (SELECT 1 FROM serialisation.claim_evidence evidence
                      WHERE evidence.tenant_id = c.tenant_id AND evidence.claim_id = c.claim_id
                        AND evidence.reference = @approved_pool)
            )
            SELECT COUNT(*) AS prepared,
                   COALESCE(SUM(CASE WHEN c.location_kind IN ('customer_site','in_transit')
                       AND refurbished.target_unit_id IS NULL
                       AND approved_source.unit_id IS NULL
                       THEN 1 ELSE 0 END), 0) AS rma_eligible,
                   (SELECT COUNT(*) FROM current_approved_source) AS current_approved_sources
            FROM prepared
            JOIN serialisation.unit_current c ON c.tenant_id = @tenant AND c.unit_id = prepared.unit_id
            LEFT JOIN refurbished ON refurbished.target_unit_id = prepared.unit_id
            LEFT JOIN approved_source ON approved_source.unit_id = prepared.unit_id;
            """, 0, ("@tenant", Tenant), ("@document_type", ShipmentDocumentType), ("@document_id", ShipmentDocumentId),
            ("@actor", Actor), ("@approved_pool", ClaimPoolReference(options.NumberingMode)));
        var row = rows.Single();
        return (Convert.ToInt32(row["prepared"]), Convert.ToInt32(row["rma_eligible"]),
            Convert.ToInt32(row["current_approved_sources"]));
    }

    private async Task ConfigureCurrencyAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(new TenantId(Tenant));
        var currency = scope.ServiceProvider.GetRequiredService<ITenantCurrency>();
        await currency.SetAsync("EUR", cancellationToken);
    }

    private async Task<Guid> EnsurePoliciesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await Sql.OpenAsync(options.ConnectionString, Tenant);
        if (await Sql.ScalarAsync<int>(connection, "SELECT COUNT(*) FROM serialisation.coverage_family WHERE tenant_id = @tenant AND family_code = @family", 0,
                ("@tenant", Tenant), ("@family", CoverageFamilyCode.Base)) == 0)
            await InvokeAsync<CoverageFamilyRow>(new DefineCoverageFamily(CoverageFamilyCode.Base, "Benchmark base warranty", false,
                AssuranceOrService.Assurance, ActorRef: Actor), cancellationToken);

        if (await Sql.ScalarAsync<int>(connection, "SELECT COUNT(*) FROM serialisation.replacement_policy WHERE tenant_id = @tenant AND code = @code", 0,
                ("@tenant", Tenant), ("@code", ReplacementPolicyCode)) == 0)
            await InvokeAsync<ReplacementPolicyRow>(new DefineReplacementPolicy(ReplacementPolicyCode,
                ReplacementRule.CarryRemaining, null, null, "Benchmark carry-remaining rule", Actor), cancellationToken);

        var policy = await Sql.RowsAsync(connection, """
            SELECT TOP (1) policy_version_id, status FROM serialisation.warranty_policy_version
            WHERE tenant_id = @tenant AND policy_code = @code ORDER BY version DESC;
            """, 0, ("@tenant", Tenant), ("@code", BasePolicyCode));
        PolicyDraftResult draft;
        if (policy.Count == 0)
        {
            draft = await InvokeAsync<PolicyDraftResult>(new CreatePolicyDraft(Stable("base-policy"), BasePolicyCode,
                "Benchmark concurrency cover", CoverageFamilyCode.Base, EntitlementType.Manufacturer,
                new DateOnly(2026, 1, 1), Terms(), Actor), cancellationToken);
        }
        else
        {
            var id = (Guid)policy[0]["policy_version_id"]!;
            if ((string)policy[0]["status"]! != PolicyVersionStatus.Draft) return id;
            draft = new PolicyDraftResult(id, BasePolicyCode, 1, PolicyVersionStatus.Draft, new PolicyValidation([]));
        }

        var published = await InvokeAsync<PolicyDraftResult>(new PublishPolicyVersion(draft.PolicyVersionId, Actor), cancellationToken);
        return published.PolicyVersionId;
    }

    private async Task<List<WorkingUnit>> LoadCustomerUnitsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await Sql.OpenAsync(options.ConnectionString, Tenant);
        var rows = await Sql.RowsAsync(connection, """
            WITH prepared AS (
                SELECT DISTINCT e.unit_id
                FROM serialisation.unit_event e
                WHERE e.tenant_id = @tenant AND e.document_type = @document_type AND e.document_id = @document_id
                  AND e.event_type = 'unit.shipped'
            ), approved_source AS (
                SELECT DISTINCT unit_id FROM serialisation.claim
                WHERE tenant_id = @tenant AND submitted_by = @actor AND status = 'approved'
            ), current_approved_source AS (
                SELECT DISTINCT c.unit_id FROM serialisation.claim c
                WHERE c.tenant_id = @tenant AND c.submitted_by = @actor AND c.status = 'approved'
                  AND EXISTS (SELECT 1 FROM serialisation.claim_evidence evidence
                      WHERE evidence.tenant_id = c.tenant_id AND evidence.claim_id = c.claim_id
                        AND evidence.reference = @approved_pool)
            ), candidates AS (
                SELECT c.unit_id, c.item_id, i.normalised_value, e.entitlement_id,
                       CASE WHEN p.unit_id IS NULL THEN 1 ELSE 0 END AS fresh,
                       CASE WHEN r.target_unit_id IS NULL THEN 0 ELSE 1 END AS refurbishment,
                       CASE WHEN approved_source.unit_id IS NULL AND r.target_unit_id IS NULL
                                      AND (p.unit_id IS NULL OR c.location_kind IN ('customer_site','in_transit'))
                            THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS available_for_run,
                       CASE WHEN current_approved_source.unit_id IS NOT NULL
                            THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS current_approved_source
                FROM serialisation.unit_current c
                JOIN serialisation.entitlement e ON e.tenant_id = c.tenant_id AND e.unit_id = c.unit_id
                    AND e.coverage_family = 'base_warranty' AND e.direction = 'outbound' AND e.status = 'active'
                    AND e.start_date <= @on AND (e.end_date IS NULL OR e.end_date >= @on)
                OUTER APPLY (
                    SELECT TOP (1) x.normalised_value
                    FROM serialisation.unit_identifier x
                    WHERE x.tenant_id = c.tenant_id AND x.unit_id = c.unit_id AND x.valid_to IS NULL
                      AND x.type = 'internal_serial'
                    ORDER BY x.valid_from, x.identifier_id
                ) i
                LEFT JOIN prepared p ON p.unit_id = c.unit_id
                LEFT JOIN (
                    SELECT DISTINCT target_unit_id FROM serialisation.refurbishment_work_order
                    WHERE tenant_id = @tenant AND source_document_type = 'rma'
                ) r ON r.target_unit_id = c.unit_id
                LEFT JOIN approved_source ON approved_source.unit_id = c.unit_id
                LEFT JOIN current_approved_source ON current_approved_source.unit_id = c.unit_id
                WHERE c.tenant_id = @tenant AND i.normalised_value IS NOT NULL
                  AND (p.unit_id IS NOT NULL OR c.last_event_time < @shipment_at)
            )
            SELECT TOP (@take) unit_id, item_id, normalised_value, entitlement_id, available_for_run, current_approved_source
            FROM candidates ORDER BY refurbishment, fresh, unit_id;
            """, 120, ("@tenant", Tenant), ("@document_type", ShipmentDocumentType), ("@document_id", ShipmentDocumentId),
            ("@on", DateOnly.FromDateTime(ShipmentAt)), ("@shipment_at", ShipmentAt), ("@take", customerUnits),
            ("@actor", Actor), ("@approved_pool", ClaimPoolReference(options.NumberingMode)));
        return rows.Select(row => new WorkingUnit((Guid)row["unit_id"]!, (Guid)row["item_id"]!,
            (string)row["normalised_value"]!, (Guid)row["entitlement_id"]!, (bool)row["available_for_run"]!,
            (bool)row["current_approved_source"]!)).ToList();
    }

    private async Task<int> EnsureCommandEntitlementsAsync(IReadOnlyList<WorkingUnit> units, Guid policyVersionId,
        CancellationToken cancellationToken)
    {
        var changed = 0;
        var runIdentity = $"{options.NumberingMode}:{(Round.Length == 0 ? "1" : Round)}";
        await using var connection = await Sql.OpenAsync(options.ConnectionString, Tenant);
        foreach (var unit in units)
        {
            var active = await Sql.RowsAsync(connection, """
                SELECT TOP (1) entitlement_id, policy_version_id FROM serialisation.entitlement
                WHERE tenant_id = @tenant AND unit_id = @unit AND coverage_family = 'base_warranty' AND status <> 'closed'
                ORDER BY created_at DESC;
                """, 0, ("@tenant", Tenant), ("@unit", unit.UnitId));
            if (active.Count == 1 && (Guid)active[0]["policy_version_id"]! == policyVersionId) continue;
            if (active.Count == 1)
                await InvokeAsync<EntitlementChangeResult>(new CloseEntitlement(Stable($"source-close:{runIdentity}:{unit.UnitId:N}"),
                    (Guid)active[0]["entitlement_id"]!, EntitlementClosedReason.SourceDocumentCorrected,
                    new DateOnly(2026, 9, 1), ShipmentAt.AddMinutes(-2), Actor,
                    new DocumentRef(ShipmentDocumentType, ShipmentDocumentId), "Benchmark command-policy replacement"), cancellationToken);
            await InvokeAsync<CreateEntitlementResult>(new CreateEntitlement(Stable($"source-entitlement:{runIdentity}:{unit.UnitId:N}"), unit.UnitId,
                CoverageFamilyCode.Base, new DateOnly(2026, 9, 1), BenchOptions.LegalEntity, ShipmentAt.AddMinutes(-1), Actor,
                new Dictionary<string, DateTime> { [StartTrigger.Ship] = ShipmentAt }, policyVersionId,
                Customer, PartyType.Customer, Country: "AQ", SourceDocument: new DocumentRef(ShipmentDocumentType, ShipmentDocumentId)), cancellationToken);
            changed++;
        }

        return changed;
    }

    private async Task<int> EnsureShipmentAsync(IReadOnlyList<WorkingUnit> units, CancellationToken cancellationToken)
    {
        var document = new DocumentRef(ShipmentDocumentType, ShipmentDocumentId);
        var lines = units.GroupBy(unit => unit.ItemId).Select((group, index) => new DocumentLine(index + 1, group.Key,
            group.Count(), group.Select(unit => new DocumentSerial(unit.Serial, IdentifierType: IdentifierType.InternalSerial)).ToList())).ToList();
        services.GetRequiredService<InMemoryDocumentPorts>().Add(new ShipmentDocument(document, Customer, ShipmentAt, lines,
            CustomerSite, null, IsDropShip: true, CustomerPoRef: "BENCH-CONCURRENCY"));
        var result = await InvokeAsync<ConsumedEventResult>(new SalesShipmentPosted(
            new ThinEnvelope(Stable("shipment", customerUnits), BenchOptions.LegalEntity, ShipmentAt, Actor), document), cancellationToken);
        if (result.Exceptions.Count > 0)
            throw new InvalidOperationException("Benchmark shipment refused units: " + string.Join("; ", result.Exceptions.Take(10)));
        return result.UnitsAffected == 0 && result.Duplicate ? units.Count : result.UnitsAffected;
    }

    private async Task<int> EnsureRefurbishmentsAsync(IReadOnlyList<WorkingUnit> units, CancellationToken cancellationToken)
    {
        var requested = await InvokeAsync<RequestRmaResult>(new RequestRma(RefurbishmentRmaCommandId,
            new DateOnly(2026, 9, 2), Customer, Customer, ServiceEntity, BenchOptions.LegalEntity, "AQ", "EUR",
            units.Select(unit => new RmaRequestLineInput(unit.UnitId, CoverageFamily: CoverageFamilyCode.Base,
                RequestedAmount: 10m, ClaimLineKind: ClaimLineKind.Parts, Description: "Benchmark refurbishment return")).ToList(),
            ReturnAt, Actor), cancellationToken);
        var authorised = await InvokeAsync<AuthoriseRmaResult>(new AuthoriseRma(Stable("refurb-authorise"),
            requested.RmaId, ReturnAt.AddMinutes(1), Actor), cancellationToken);
        var lineByUnit = authorised.Lines.ToDictionary(line => line.AuthorisedUnitId);
        // The refurbishment return uses a fixed command id, so on a database prepared before it replays the earlier
        // return, whose lines may name a different unit set (the working set is sized per run). That return's work
        // orders stay open across runs (the harness only appends labour and cost), so keep them as they are.
        if (units.Any(unit => !lineByUnit.ContainsKey(unit.UnitId)))
            return await OpenRefurbishmentWorkOrdersAsync(requested.RmaId);
        await InvokeAsync<ReceiveReturnResult>(new ReceiveReturn(Stable("refurb-receive"), requested.RmaId, Quarantine,
            units.Select(unit => new ReturnReceiptLineInput(lineByUnit[unit.UnitId].RmaLineId,
                new DocumentSerial(unit.Serial, IdentifierType: IdentifierType.InternalSerial), ConditionGrades.Used,
                "benchmark_graded_receipt")).ToList(), ReturnAt.AddMinutes(2), ReturnAt.AddMinutes(3), ReturnAt.AddMinutes(3), Actor), cancellationToken);
        await InvokeAsync<DispositionRmaResult>(new DispositionRma(Stable("refurb-disposition"), requested.RmaId,
            authorised.Lines.Select(line => new RmaDispositionLineInput(line.RmaLineId, RmaDisposition.Refurbish)).ToList(),
            new DateOnly(2026, 9, 3), ReturnAt.AddMinutes(4), Actor), cancellationToken);
        return await OpenRefurbishmentWorkOrdersAsync(requested.RmaId);
    }

    private async Task<int> OpenRefurbishmentWorkOrdersAsync(Guid rmaId)
    {
        await using var connection = await Sql.OpenAsync(options.ConnectionString, Tenant);
        return await Sql.ScalarAsync<int>(connection, """
            SELECT COUNT(*) FROM serialisation.refurbishment_work_order
            WHERE tenant_id = @tenant AND source_document_type = 'rma' AND source_document_id = @rma AND status = 'open';
            """, 0, ("@tenant", Tenant), ("@rma", rmaId));
    }

    private async Task<(int Open, int Approved, int Editable)> EnsureClaimsAsync(IReadOnlyList<WorkingUnit> units,
        CancellationToken cancellationToken)
    {
        var claimIds = new List<Guid>(units.Count);
        var claimStatuses = new List<string>(units.Count);
        // A run consumes its approved and open claim pools, so YOJANA_BENCH_CLAIM_ROUND namespaces the next pool.
        // Existing claims are discovered by (unit, pool) before unit-based command ids create only missing rows.
        var roundSuffix = Round.Length == 0 ? "" : $"-r{Round}";
        var approvedPool = options.NumberingMode.ToUpperInvariant().Replace('_', '-');
        await using var existingConnection = await Sql.OpenAsync(options.ConnectionString, Tenant);
        var existingRows = await Sql.RowsAsync(existingConnection, """
            SELECT c.unit_id, c.claim_id, c.status, evidence.reference
            FROM serialisation.claim c
            JOIN serialisation.claim_evidence evidence
              ON evidence.tenant_id = c.tenant_id AND evidence.claim_id = c.claim_id
            WHERE c.tenant_id = @tenant AND c.submitted_by = @actor
              AND evidence.reference IN (@sequence_pool, @gap_free_pool);
            """, 0, ("@tenant", Tenant), ("@actor", Actor),
            ("@sequence_pool", $"BENCH-CONCURRENCY-SEQUENCE{roundSuffix}"),
            ("@gap_free_pool", $"BENCH-CONCURRENCY-GAP-FREE{roundSuffix}"));
        var existing = existingRows.GroupBy(row => ((Guid)row["unit_id"]!, (string)row["reference"]!))
            .ToDictionary(group => group.Key, group => group.Select(row => new PreparedClaim(
                (Guid)row["claim_id"]!, (string)row["status"]!)).ToList());
        foreach (var (unit, index) in units.Select((unit, index) => (unit, index)))
        {
            var pool = index < approvedClaims
                ? approvedPool
                : index < approvedClaims + OpenClaims / 2 ? "SEQUENCE" : "GAP-FREE";
            var poolReference = $"BENCH-CONCURRENCY-{pool}{roundSuffix}";
            var desiredStatus = index < approvedClaims ? ClaimStatus.Approved : ClaimStatus.Submitted;
            var prepared = existing.GetValueOrDefault((unit.UnitId, poolReference))?
                .FirstOrDefault(claim => claim.Status == desiredStatus)
                ?? (desiredStatus == ClaimStatus.Approved
                    ? existing.GetValueOrDefault((unit.UnitId, poolReference))?.FirstOrDefault(claim => claim.Status == ClaimStatus.Submitted)
                    : null);
            if (prepared is null)
            {
                var submitted = await InvokeAsync<SubmitClaimResult>(new SubmitClaim(
                    Stable($"claim:{poolReference}:{unit.UnitId:N}"), unit.UnitId, null,
                    new DateOnly(2026, 9, 2), Customer, Customer, ServiceEntity, BenchOptions.LegalEntity, "AQ", "EUR",
                    [new ClaimLineInput(ClaimLineKind.Parts, CoverageFamilyCode.Base, 10m, Description: "Benchmark open claim")],
                    [new ClaimEvidenceInput(ClaimEvidenceKind.Attachment, poolReference, "Synthetic benchmark evidence")],
                    ClaimRemedy.Replacement, poolReference, ClaimAt, Actor), cancellationToken);
                prepared = new(submitted.ClaimId, ClaimStatus.Submitted);
            }
            claimIds.Add(prepared.ClaimId);
            claimStatuses.Add(prepared.Status);
        }
        for (var index = 0; index < approvedClaims; index++)
        {
            if (claimStatuses[index] == ClaimStatus.Approved) continue;
            await InvokeAsync<AdjudicateClaimResult>(new AdjudicateClaim(
                Stable($"claim-adjudicate:{ClaimPoolReference(options.NumberingMode)}:{units[index].UnitId:N}"), claimIds[index],
                ClaimAt.AddMinutes(1), Actor,
                new ClaimReviewer(Actor, Yojana.Platform.Contracts.Security.YojanaRole.All)), cancellationToken);
        }

        for (var index = 0; index < EditableClaims; index++)
        {
            var unit = units[index % units.Count];
            await InvokeAsync<SubmitClaimResult>(new SubmitClaim(Stable("editable-claim", index), unit.UnitId, null,
                new DateOnly(2026, 9, 2), Customer, Customer, ServiceEntity, BenchOptions.LegalEntity, "AQ", "EUR",
                [new ClaimLineInput(ClaimLineKind.Parts, CoverageFamilyCode.Base, 10m, Description: "Benchmark editable claim")],
                [new ClaimEvidenceInput(ClaimEvidenceKind.Attachment, "BENCH-CONCURRENCY-DOCUMENT", "Editable benchmark document")],
                ClaimRemedy.Repair, "BENCH-CONCURRENCY-DOCUMENT", ClaimAt, Actor), cancellationToken);
        }

        await using var connection = await Sql.OpenAsync(options.ConnectionString, Tenant);
        var rows = await Sql.RowsAsync(connection, """
            SELECT
                SUM(CASE WHEN c.status = 'submitted' AND d.claim_id IS NULL THEN 1 ELSE 0 END) AS open_total,
                SUM(CASE WHEN c.status = 'submitted' AND d.claim_id IS NOT NULL THEN 1 ELSE 0 END) AS editable_total,
                SUM(CASE WHEN c.status = 'approved' AND EXISTS (
                    SELECT 1 FROM serialisation.claim_evidence approved_pool
                    WHERE approved_pool.tenant_id = c.tenant_id AND approved_pool.claim_id = c.claim_id
                      AND approved_pool.reference = @approved_pool)
                    THEN 1 ELSE 0 END) AS approved_total
            FROM serialisation.claim c
            LEFT JOIN (
                SELECT DISTINCT tenant_id, claim_id FROM serialisation.claim_evidence
                WHERE tenant_id = @tenant AND reference = 'BENCH-CONCURRENCY-DOCUMENT'
            ) d ON d.tenant_id = c.tenant_id AND d.claim_id = c.claim_id
            WHERE c.tenant_id = @tenant AND c.submitted_by = @actor
              AND (d.claim_id IS NOT NULL OR EXISTS (
                  SELECT 1 FROM serialisation.claim_evidence pool
                  WHERE pool.tenant_id = c.tenant_id AND pool.claim_id = c.claim_id
                    AND pool.reference IN (@sequence_pool, @gap_free_pool)))
            GROUP BY c.tenant_id;
            """, 0, ("@tenant", Tenant), ("@actor", Actor),
            ("@sequence_pool", $"BENCH-CONCURRENCY-SEQUENCE{roundSuffix}"),
            ("@gap_free_pool", $"BENCH-CONCURRENCY-GAP-FREE{roundSuffix}"),
            ("@approved_pool", ClaimPoolReference(options.NumberingMode)));
        var counts = rows.Single();
        var open = Convert.ToInt32(counts["open_total"]);
        var editable = Convert.ToInt32(counts["editable_total"]);
        var approved = Convert.ToInt32(counts["approved_total"]);
        return (open, approved, editable);
    }

    private async Task<(int Campaigns, int PolicyDrafts)> EnsureDocumentsAsync(CancellationToken cancellationToken)
    {
        var campaigns = (options.Documents + 1) / 2;
        var policyDrafts = options.Documents / 2;
        for (var index = 0; index < campaigns; index++)
            await InvokeAsync<CampaignResult>(new CreateCampaign(Stable("campaign", index), $"BENCH-EDIT-{index:D3}",
                $"Benchmark editable campaign {index + 1}", CampaignCoverageSource.Goodwill, "BENCH-CONCURRENCY", [],
                ClaimAt, Actor, CampaignReason.Reliability, "Benchmark version-contention document", null), cancellationToken);

        await using var connection = await Sql.OpenAsync(options.ConnectionString, Tenant);
        for (var index = 0; index < policyDrafts; index++)
        {
            var code = $"BENCH-EDIT-POL-{index:D3}";
            if (await Sql.ScalarAsync<int>(connection, "SELECT COUNT(*) FROM serialisation.warranty_policy_version WHERE tenant_id = @tenant AND policy_code = @code", 0,
                    ("@tenant", Tenant), ("@code", code)) > 0) continue;
            await InvokeAsync<PolicyDraftResult>(new CreatePolicyDraft(Stable("policy-draft", index), code,
                $"Benchmark editable policy {index + 1}", CoverageFamilyCode.Base, EntitlementType.Manufacturer,
                new DateOnly(2026, 1, 1), Terms(), Actor), cancellationToken);
        }

        return (campaigns, policyDrafts);
    }

    private async Task<int> EnsureReplacementStockTagAsync(IReadOnlySet<Guid> excluded, CancellationToken cancellationToken)
    {
        await using var connection = await Sql.OpenAsync(options.ConnectionString, Tenant);
        var existing = await Sql.ScalarAsync<int>(connection,
            "SELECT COUNT(*) FROM serialisation.selection_set_member WHERE tenant_id = @tenant AND selection_id = @selection", 0,
            ("@tenant", Tenant), ("@selection", StockSelectionId));
        if (existing > 0)
        {
            if (existing < replacementStockUnits)
                throw new InvalidOperationException($"The round's replacement-stock selection has {existing:N0} units but this run needs {replacementStockUnits:N0}; use a fresh YOJANA_BENCH_CLAIM_ROUND.");
            return existing;
        }

        var rows = await Sql.RowsAsync(connection, """
            SELECT TOP (@take) c.unit_id, i.normalised_value
            FROM serialisation.unit_current c
            CROSS APPLY (
                SELECT TOP (1) x.normalised_value FROM serialisation.unit_identifier x
                WHERE x.tenant_id = c.tenant_id AND x.unit_id = c.unit_id AND x.valid_to IS NULL
                  AND x.type = 'internal_serial' ORDER BY x.valid_from, x.identifier_id
            ) i
            WHERE c.tenant_id = @tenant AND NOT EXISTS (
                SELECT 1 FROM serialisation.unit_event e
                WHERE e.tenant_id = c.tenant_id AND e.unit_id = c.unit_id
                  AND e.document_type = @document_type AND e.document_id = @document_id)
              AND NOT EXISTS (
                SELECT 1 FROM serialisation.replacement_link r
                WHERE r.tenant_id = c.tenant_id AND r.replacement_unit_id = c.unit_id)
            ORDER BY i.normalised_value;
            """, 120, ("@tenant", Tenant), ("@take", replacementStockUnits), ("@document_type", ShipmentDocumentType),
            ("@document_id", ShipmentDocumentId));
        var selected = rows.Where(row => !excluded.Contains((Guid)row["unit_id"]!)).ToList();
        if (selected.Count < replacementStockUnits)
            throw new InvalidOperationException($"prepare-concurrency needs {replacementStockUnits:N0} replacement-stock units; found {selected.Count:N0} outside the customer working set.");
        var serials = selected.Select(row => (string)row["normalised_value"]!).ToList();
        var tagged = await InvokeAsync<ResolveSelectionSetResult>(new ResolveSelectionSet(StockSelectionId,
            SelectionSource.ByPastedList(serials), Actor), cancellationToken);
        return tagged.Resolved;
    }

    private async Task<(int CustomerSite, int Quarantine, int InRepair)> VerifyWholeUnitConsistencyAsync(CancellationToken cancellationToken)
    {
        await using var connection = await Sql.OpenAsync(options.ConnectionString, Tenant);
        var rows = await Sql.RowsAsync(connection, """
            WITH prepared AS (
                SELECT DISTINCT unit_id FROM serialisation.unit_event
                WHERE tenant_id = @tenant AND document_type = @document_type AND document_id = @document_id
                  AND event_type = 'unit.shipped'
            ), refurbished AS (
                SELECT DISTINCT w.target_unit_id AS unit_id
                FROM serialisation.refurbishment_work_order w
                WHERE w.tenant_id = @tenant AND w.source_document_type = 'rma' AND w.status = 'open'
            )
            SELECT
                (SELECT COUNT(*) FROM prepared) AS prepared,
                (SELECT COUNT(*) FROM prepared p
                 WHERE NOT EXISTS (SELECT 1 FROM refurbished r WHERE r.unit_id = p.unit_id)
                   AND EXISTS (SELECT 1 FROM serialisation.custody_episode e WHERE e.tenant_id = @tenant AND e.unit_id = p.unit_id
                       AND e.kind = 'ownership' AND e.valid_to IS NULL AND e.party_type = 'customer' AND e.party_ref = @customer)
                   AND EXISTS (SELECT 1 FROM serialisation.custody_episode e WHERE e.tenant_id = @tenant AND e.unit_id = p.unit_id
                       AND e.kind = 'custody' AND e.valid_to IS NULL AND (
                           (e.party_type = 'customer' AND e.party_ref = @customer
                               AND e.location_id = @customer_site AND e.location_kind = 'customer_site')
                           OR (e.party_type = 'us' AND e.location_kind = 'quarantine')))) AS customer_consistent,
                (SELECT COUNT(*) FROM prepared p WHERE EXISTS (SELECT 1 FROM serialisation.custody_episode e
                    WHERE e.tenant_id = @tenant AND e.unit_id = p.unit_id AND e.kind = 'custody' AND e.valid_to IS NULL
                      AND e.party_type = 'customer' AND e.party_ref = @customer AND e.location_kind = 'customer_site')) AS customer_site,
                (SELECT COUNT(*) FROM prepared p WHERE EXISTS (SELECT 1 FROM serialisation.custody_episode e
                    WHERE e.tenant_id = @tenant AND e.unit_id = p.unit_id AND e.kind = 'custody' AND e.valid_to IS NULL
                      AND e.party_type = 'us' AND e.location_kind = 'quarantine')) AS quarantine,
                (SELECT COUNT(*) FROM prepared p
                 WHERE EXISTS (SELECT 1 FROM refurbished r WHERE r.unit_id = p.unit_id)
                   AND EXISTS (SELECT 1 FROM serialisation.custody_episode e WHERE e.tenant_id = @tenant AND e.unit_id = p.unit_id
                       AND e.kind = 'ownership' AND e.valid_to IS NULL AND e.party_type = 'us' AND e.party_ref = @service_entity)
                   AND EXISTS (SELECT 1 FROM serialisation.custody_episode e WHERE e.tenant_id = @tenant AND e.unit_id = p.unit_id
                       AND e.kind = 'custody' AND e.valid_to IS NULL AND e.party_type = 'us'
                       AND e.location_id = @quarantine AND e.location_kind = 'in_repair')) AS refurbishment_consistent,
                (SELECT COUNT(*) FROM prepared p WHERE EXISTS (
                    SELECT 1 FROM serialisation.entitlement e WHERE e.tenant_id = @tenant AND e.unit_id = p.unit_id
                      AND e.coverage_family = 'base_warranty' AND e.direction = 'outbound' AND e.status = 'active'
                      AND e.start_date <= @on AND (e.end_date IS NULL OR e.end_date >= @on))) AS live_base,
                (SELECT COUNT(*) FROM prepared p WHERE 1 = (
                    SELECT COUNT(*) FROM serialisation.unit_identifier i WHERE i.tenant_id = @tenant AND i.unit_id = p.unit_id
                      AND i.type = 'internal_serial' AND i.valid_to IS NULL)) AS open_serial,
                (SELECT COUNT(*) FROM serialisation.unit_event e WHERE e.tenant_id = @tenant
                    AND e.document_type = @document_type AND e.document_id = @document_id AND e.event_type = 'entitlement.closed') AS entitlement_closed,
                (SELECT COUNT(*) FROM serialisation.unit_event e WHERE e.tenant_id = @tenant
                    AND e.document_type = @document_type AND e.document_id = @document_id AND e.event_type = 'entitlement.created') AS entitlement_created,
                (SELECT COUNT(DISTINCT c.unit_id) FROM serialisation.claim c
                 WHERE c.tenant_id = @tenant AND c.submitted_by = @actor AND c.status = 'approved'
                   AND EXISTS (SELECT 1 FROM serialisation.claim_evidence evidence
                       WHERE evidence.tenant_id = c.tenant_id AND evidence.claim_id = c.claim_id
                         AND evidence.reference = @approved_pool)
                   AND EXISTS (SELECT 1 FROM serialisation.entitlement e
                       JOIN serialisation.warranty_policy_version p
                         ON p.tenant_id = e.tenant_id AND p.policy_version_id = e.policy_version_id
                       WHERE e.tenant_id = c.tenant_id AND e.unit_id = c.unit_id
                         AND e.coverage_family = 'base_warranty' AND e.direction = 'outbound'
                         AND e.status = 'active' AND p.policy_code = @policy_code)) AS replacement_source_policy,
                (SELECT COUNT(*) FROM prepared p WHERE EXISTS (SELECT 1 FROM refurbished r WHERE r.unit_id = p.unit_id)
                    AND EXISTS (SELECT 1 FROM serialisation.unit_event e WHERE e.tenant_id = @tenant AND e.unit_id = p.unit_id
                        AND e.event_time >= @return_at AND e.event_type = 'ownership.transferred')
                    AND EXISTS (SELECT 1 FROM serialisation.unit_event e WHERE e.tenant_id = @tenant AND e.unit_id = p.unit_id
                        AND e.event_time >= @return_at AND e.event_type = 'rma.dispositioned')
                    AND EXISTS (SELECT 1 FROM serialisation.unit_event e WHERE e.tenant_id = @tenant AND e.unit_id = p.unit_id
                        AND e.event_time >= @return_at AND e.event_type = 'refurbishment.opened')) AS refurbishment_ledger,
                (SELECT COUNT(*) FROM serialisation.unit_event e WHERE e.tenant_id = @tenant
                    AND e.document_type = @document_type AND e.document_id = @document_id
                    AND e.event_type = 'unit.identifier_assigned') AS identifiers_changed;
            """, 120, ("@tenant", Tenant), ("@document_type", ShipmentDocumentType),
            ("@document_id", ShipmentDocumentId), ("@customer", Customer), ("@customer_site", CustomerSite),
            ("@service_entity", ServiceEntity), ("@quarantine", Quarantine), ("@on", DateOnly.FromDateTime(ShipmentAt)),
            ("@return_at", ReturnAt), ("@actor", Actor),
            ("@approved_pool", ClaimPoolReference(options.NumberingMode)), ("@policy_code", BasePolicyCode));
        var row = rows.Single();
        int Count(string name) => Convert.ToInt32(row[name]);
        var failures = new List<string>();
        // Every prepared unit must be consistent. The prepared count itself is compared with what the run needs, not
        // with this run's target: topping up a database prepared earlier (with a different working-set size) reuses the
        // earlier refurbishment set, so the target arithmetic and the tagged set legitimately differ.
        var prepared = Count("prepared");
        var requiredForRun = ConcurrencyWorkload.RequiredRmaEligibleUnits(options)
            + ConcurrencyWorkload.RequiredReplacementCapacity(options)
            + RefurbishmentUnits;
        if (prepared < requiredForRun) failures.Add($"prepared={prepared} < required {requiredForRun}");
        if (Count("customer_consistent") != prepared - RefurbishmentUnits) failures.Add($"customer_consistent={Count("customer_consistent")}");
        if (Count("refurbishment_consistent") != RefurbishmentUnits) failures.Add($"refurbishment_consistent={Count("refurbishment_consistent")}");
        if (Count("live_base") != prepared) failures.Add($"live_base={Count("live_base")}");
        if (Count("open_serial") != prepared) failures.Add($"open_serial={Count("open_serial")}");
        if (Count("entitlement_closed") < approvedClaims) failures.Add($"entitlement_closed={Count("entitlement_closed")} < {approvedClaims}");
        if (Count("entitlement_created") < approvedClaims) failures.Add($"entitlement_created={Count("entitlement_created")} < {approvedClaims}");
        if (Count("replacement_source_policy") < approvedClaims) failures.Add($"replacement_source_policy={Count("replacement_source_policy")} < {approvedClaims}");
        if (Count("refurbishment_ledger") != RefurbishmentUnits) failures.Add($"refurbishment_ledger={Count("refurbishment_ledger")}");
        if (Count("identifiers_changed") != 0) failures.Add($"identifiers_changed={Count("identifiers_changed")}");
        if (failures.Count > 0)
            throw new InvalidOperationException("Prepared units failed the whole-unit consistency check: " + string.Join(", ", failures));
        return (Count("customer_site"), Count("quarantine"), Count("refurbishment_consistent"));
    }

    private async Task<TResult> InvokeAsync<TResult>(object command, CancellationToken cancellationToken) =>
        await services.GetRequiredService<IMessageBus>().InvokeAsync<TResult>(command,
            new DeliveryOptions { TenantId = TenantDelivery }, cancellationToken);

    private Guid Stable(string purpose, int index = 0)
    {
        var input = Encoding.UTF8.GetBytes($"yojana-bench-concurrency:{Tenant:N}:{purpose}:{index}");
        var bytes = SHA256.HashData(input)[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    private static PolicyTerms Terms() => new(
        new DurationRule(36, DurationRule.Months), DurationBasis.Time, [],
        new StartRule(StartTrigger.Ship, [new StartFallback(StartTrigger.Invoice)], 30),
        new CoverageTerms(100, 100, 100, "return_to_base"), [], ["WORLD"], Transferability.Yes, "full",
        ReplacementPolicyCode, [], null, AssuranceOrService.Assurance, PolicyTerms.TimeZoneUtc);

    private sealed record WorkingUnit(Guid UnitId, Guid ItemId, string Serial, Guid EntitlementId,
        bool AvailableForRun, bool CurrentApprovedSource);
    private sealed record PreparedClaim(Guid ClaimId, string Status);
}
