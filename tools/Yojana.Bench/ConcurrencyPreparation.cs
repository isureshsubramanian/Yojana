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
    public const int ApprovedClaims = 10;
    public const int EditableClaims = 50;
    public const int ReplacementStockUnits = 10_000;

    private static readonly DateTime ShipmentAt = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ClaimAt = new(2026, 9, 2, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ReturnAt = new(2026, 9, 3, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Customer = Guid.Parse("b0c00000-0000-4000-8000-000000000001");
    private static readonly Guid ServiceEntity = Guid.Parse("b0c00000-0000-4000-8000-000000000002");
    private static readonly Guid CustomerSite = Guid.Parse("b0c00000-0000-4000-8000-000000000003");
    private static readonly Guid Quarantine = Guid.Parse("b0c00000-0000-4000-8000-000000000004");
    public static readonly Guid ShipmentDocumentId = Guid.Parse("b0c00000-0000-4000-8000-000000000010");
    public static readonly Guid StockSelectionId = Guid.Parse("b0c00000-0000-4000-8000-000000000011");
    private static readonly Guid RefurbishmentRmaCommandId = Guid.Parse("b0c00000-0000-4000-8000-000000000012");
    private const string ShipmentDocumentType = "bench_concurrency";
    private const string BasePolicyCode = "BENCH-CONCURRENCY-36";
    private const string ReplacementPolicyCode = "BENCH-CARRY";
    private int customerUnits = ConcurrencyWorkload.RequiredRmaEligibleUnits(options) + RefurbishmentUnits;

    private Guid Tenant => options.ConcurrencyTenantId;
    private string TenantDelivery => Tenant.ToString();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await ConfigureCurrencyAsync(cancellationToken);
        var policyVersionId = await EnsurePoliciesAsync(cancellationToken);
        var existing = await ExistingPreparedUnitsAsync(cancellationToken);
        var requiredRmaUnits = ConcurrencyWorkload.RequiredRmaEligibleUnits(options);
        customerUnits = Math.Max(customerUnits,
            existing.Prepared + Math.Max(0, requiredRmaUnits - existing.RmaEligible));
        var units = await LoadCustomerUnitsAsync(cancellationToken);
        if (units.Count < customerUnits)
            throw new InvalidOperationException($"prepare-concurrency needs {customerUnits:N0} loaded units with current base cover and a business-time-safe history; found {units.Count:N0}.");

        var validSourceUnits = units.Take(ApprovedClaims).ToList();
        var entitlementChanges = await EnsureCommandEntitlementsAsync(validSourceUnits, policyVersionId, cancellationToken);
        var shipped = await EnsureShipmentAsync(units, cancellationToken);
        var refurbished = await EnsureRefurbishmentsAsync(units.TakeLast(RefurbishmentUnits).ToList(), cancellationToken);
        var claims = await EnsureClaimsAsync(units.Take(OpenClaims + ApprovedClaims).ToList(), cancellationToken);
        var documents = await EnsureDocumentsAsync(cancellationToken);
        var stock = await EnsureReplacementStockTagAsync(units.Select(unit => unit.UnitId).ToHashSet(), cancellationToken);
        var consistency = await VerifyWholeUnitConsistencyAsync(cancellationToken);

        Console.WriteLine($"prepared tenant {options.ConcurrencyTenantLabel} ({Tenant})");
        Console.WriteLine($"customer shipment: {shipped:N0}/{customerUnits:N0} units; command-policy entitlements: {entitlementChanges:N0}/{ApprovedClaims:N0}");
        Console.WriteLine($"RMA capacity: {customerUnits - RefurbishmentUnits:N0} eligible units for {options.Engineers} engineers × {options.Duration.TotalMinutes:0.##} min (weighted headroom with a 12,000-unit verification floor)");
        Console.WriteLine($"refurbishment stock: {refurbished:N0}/{RefurbishmentUnits:N0} open work orders; claims: {claims.Open:N0} adjudication + {claims.Editable:N0} editable + {claims.Approved:N0} approved sources");
        Console.WriteLine($"editable documents: {documents.Campaigns:N0} campaigns + {documents.PolicyDrafts:N0} policy drafts; replacement-stock tag: {stock:N0} units");
        Console.WriteLine($"whole-unit check: owners {customerUnits - RefurbishmentUnits:N0} customer + {RefurbishmentUnits:N0} us; holders {consistency.CustomerSite:N0} customer-site + {consistency.Quarantine:N0} us/quarantine + {consistency.InRepair:N0} us/in-repair; live base cover and open internal serial retained on all {customerUnits:N0} units");
    }

    private async Task<(int Prepared, int RmaEligible)> ExistingPreparedUnitsAsync(CancellationToken cancellationToken)
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
            )
            SELECT COUNT(*) AS prepared,
                   COALESCE(SUM(CASE WHEN c.location_kind IN ('customer_site','in_transit')
                       AND refurbished.target_unit_id IS NULL
                       THEN 1 ELSE 0 END), 0) AS rma_eligible
            FROM prepared
            JOIN serialisation.unit_current c ON c.tenant_id = @tenant AND c.unit_id = prepared.unit_id
            LEFT JOIN refurbished ON refurbished.target_unit_id = prepared.unit_id;
            """, 0, ("@tenant", Tenant), ("@document_type", ShipmentDocumentType), ("@document_id", ShipmentDocumentId));
        var row = rows.Single();
        return (Convert.ToInt32(row["prepared"]), Convert.ToInt32(row["rma_eligible"]));
    }

    private async Task ConfigureCurrencyAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(new TenantId(Tenant));
        var currency = scope.ServiceProvider.GetRequiredService<ITenantCurrency>();
        await currency.SetAsync("EUR", cancellationToken);
        await currency.SetAcceptedAsync(["EUR"], cancellationToken);
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
            ), candidates AS (
                SELECT c.unit_id, c.item_id, i.normalised_value, e.entitlement_id,
                       CASE WHEN p.unit_id IS NULL THEN 1 ELSE 0 END AS fresh,
                       CASE WHEN r.target_unit_id IS NULL THEN 0 ELSE 1 END AS refurbishment
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
                WHERE c.tenant_id = @tenant AND i.normalised_value IS NOT NULL
                  AND (p.unit_id IS NOT NULL OR c.last_event_time < @shipment_at)
            )
            SELECT TOP (@take) unit_id, item_id, normalised_value, entitlement_id
            FROM candidates ORDER BY refurbishment, fresh, unit_id;
            """, 120, ("@tenant", Tenant), ("@document_type", ShipmentDocumentType), ("@document_id", ShipmentDocumentId),
            ("@on", DateOnly.FromDateTime(ShipmentAt)), ("@shipment_at", ShipmentAt), ("@take", customerUnits));
        return rows.Select(row => new WorkingUnit((Guid)row["unit_id"]!, (Guid)row["item_id"]!,
            (string)row["normalised_value"]!, (Guid)row["entitlement_id"]!)).ToList();
    }

    private async Task<int> EnsureCommandEntitlementsAsync(IReadOnlyList<WorkingUnit> units, Guid policyVersionId,
        CancellationToken cancellationToken)
    {
        var changed = 0;
        await using var connection = await Sql.OpenAsync(options.ConnectionString, Tenant);
        foreach (var (unit, index) in units.Select((unit, index) => (unit, index)))
        {
            var active = await Sql.RowsAsync(connection, """
                SELECT TOP (1) entitlement_id, policy_version_id FROM serialisation.entitlement
                WHERE tenant_id = @tenant AND unit_id = @unit AND coverage_family = 'base_warranty' AND status <> 'closed'
                ORDER BY created_at DESC;
                """, 0, ("@tenant", Tenant), ("@unit", unit.UnitId));
            if (active.Count == 1 && (Guid)active[0]["policy_version_id"]! == policyVersionId) continue;
            if (active.Count == 1)
                await InvokeAsync<EntitlementChangeResult>(new CloseEntitlement(Stable("source-close", index),
                    (Guid)active[0]["entitlement_id"]!, EntitlementClosedReason.SourceDocumentCorrected,
                    new DateOnly(2026, 9, 1), ShipmentAt.AddMinutes(-2), Actor,
                    new DocumentRef(ShipmentDocumentType, ShipmentDocumentId), "Benchmark command-policy replacement"), cancellationToken);
            await InvokeAsync<CreateEntitlementResult>(new CreateEntitlement(Stable("source-entitlement", index), unit.UnitId,
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
        foreach (var (unit, index) in units.Select((unit, index) => (unit, index)))
        {
            var sequencePool = index < ApprovedClaims + OpenClaims / 2;
            var pool = sequencePool ? "SEQUENCE" : "GAP-FREE";
            var submitted = await InvokeAsync<SubmitClaimResult>(new SubmitClaim(Stable($"claim-{pool}", index), unit.UnitId, null,
                new DateOnly(2026, 9, 2), Customer, Customer, ServiceEntity, BenchOptions.LegalEntity, "AQ", "EUR",
                [new ClaimLineInput(ClaimLineKind.Parts, CoverageFamilyCode.Base, 10m, Description: "Benchmark open claim")],
                [new ClaimEvidenceInput(ClaimEvidenceKind.Attachment, $"BENCH-CONCURRENCY-{pool}", "Synthetic benchmark evidence")],
                ClaimRemedy.Replacement, $"BENCH-CONCURRENCY-{pool}", ClaimAt, Actor), cancellationToken);
            claimIds.Add(submitted.ClaimId);
        }
        for (var index = 0; index < ApprovedClaims; index++)
            await InvokeAsync<AdjudicateClaimResult>(new AdjudicateClaim(Stable("claim-SEQUENCE-adjudicate", index), claimIds[index],
                ClaimAt.AddMinutes(1), Actor, new ClaimReviewer(Actor, Yojana.Platform.Contracts.Security.YojanaRole.All)), cancellationToken);

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
                SUM(CASE WHEN c.status = 'approved' THEN 1 ELSE 0 END) AS approved_total
            FROM serialisation.claim c
            LEFT JOIN (
                SELECT DISTINCT tenant_id, claim_id FROM serialisation.claim_evidence
                WHERE tenant_id = @tenant AND reference = 'BENCH-CONCURRENCY-DOCUMENT'
            ) d ON d.tenant_id = c.tenant_id AND d.claim_id = c.claim_id
            WHERE c.tenant_id = @tenant AND c.submitted_by = @actor
            GROUP BY c.tenant_id;
            """, 0, ("@tenant", Tenant), ("@actor", Actor));
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
        if (existing > 0) return existing;

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
            ORDER BY i.normalised_value;
            """, 120, ("@tenant", Tenant), ("@take", ReplacementStockUnits), ("@document_type", ShipmentDocumentType),
            ("@document_id", ShipmentDocumentId));
        var selected = rows.Where(row => !excluded.Contains((Guid)row["unit_id"]!)).ToList();
        if (selected.Count == 0) throw new InvalidOperationException("No loaded units remain for the replacement-stock working set.");
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
            ("@return_at", ReturnAt));
        var row = rows.Single();
        int Count(string name) => Convert.ToInt32(row[name]);
        var failures = new List<string>();
        // Every prepared unit must be consistent. The prepared count itself is compared with what the run needs, not
        // with this run's target: topping up a database prepared earlier (with a different working-set size) reuses the
        // earlier refurbishment set, so the target arithmetic and the tagged set legitimately differ.
        var prepared = Count("prepared");
        var requiredForRun = ConcurrencyWorkload.RequiredRmaEligibleUnits(options) + RefurbishmentUnits;
        if (prepared < requiredForRun) failures.Add($"prepared={prepared} < required {requiredForRun}");
        if (Count("customer_consistent") != prepared - RefurbishmentUnits) failures.Add($"customer_consistent={Count("customer_consistent")}");
        if (Count("refurbishment_consistent") != RefurbishmentUnits) failures.Add($"refurbishment_consistent={Count("refurbishment_consistent")}");
        if (Count("live_base") != prepared) failures.Add($"live_base={Count("live_base")}");
        if (Count("open_serial") != prepared) failures.Add($"open_serial={Count("open_serial")}");
        if (Count("entitlement_closed") != ApprovedClaims) failures.Add($"entitlement_closed={Count("entitlement_closed")}");
        if (Count("entitlement_created") != ApprovedClaims) failures.Add($"entitlement_created={Count("entitlement_created")}");
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

    private sealed record WorkingUnit(Guid UnitId, Guid ItemId, string Serial, Guid EntitlementId);
}
