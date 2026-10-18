using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Yojana.Bench;

/// <summary>P6-T18 mixed HTTP workload. SQL is read-only: resource discovery and server diagnostics only.</summary>
public sealed class ConcurrencyRunner(BenchOptions options)
{
    private static readonly Guid LegalEntity = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Customer = Guid.Parse("b0c00000-0000-4000-8000-000000000001");
    private static readonly Guid ServiceEntity = Guid.Parse("b0c00000-0000-4000-8000-000000000002");
    private static readonly Guid Quarantine = Guid.Parse("b0c00000-0000-4000-8000-000000000004");
    private static readonly DateTime BusinessTime = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentBag<LoadSample> samples = [];
    private readonly ConcurrentBag<SuccessfulEdit> successfulEdits = [];
    private readonly ConcurrentQueue<ReplacementSource> replacementSources = new();
    private readonly ConcurrentQueue<(Guid Id, string Label)> rmaUnits = new();
    private readonly ConcurrentQueue<Guid> openClaims = new();
    private readonly ConcurrentQueue<Guid> replacementUnits = new();
    private readonly ConcurrentBag<string> notes = [];
    private readonly ConcurrentDictionary<string, int> capacityWarnings = new(StringComparer.Ordinal);
    private ResourcePool resources = new([], [], [], [], [], [], [], [], [], []);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var clients = await Task.WhenAll(Enumerable.Range(0, options.Engineers).Select(i => SignInAsync(i, cancellationToken)));
        try
        {
            await ConfigureNumberingAsync(clients[0], cancellationToken);
            resources = await LoadResourcesAsync(cancellationToken);
            ValidatePreparedResources();
            foreach (var unit in resources.RmaUnits) rmaUnits.Enqueue(unit);
            foreach (var claim in resources.OpenClaims) openClaims.Enqueue(claim);
            foreach (var source in resources.ReplacementSources) replacementSources.Enqueue(source);
            foreach (var unit in resources.ReplacementUnits) replacementUnits.Enqueue(unit);
            var beforeRetries = await RetryMetricAsync(clients[0], cancellationToken);
            var monitor = new ConcurrencySqlMonitor(options.ConnectionString, options.ConcurrencyTenantId);
            await monitor.StartAsync(cancellationToken);
            var started = DateTime.UtcNow;
            var deadline = DateTime.UtcNow + options.Duration;
            var watch = Stopwatch.StartNew();
            var workers = clients.Select((client, engineer) => WorkerAsync(client, engineer, deadline, cancellationToken)).ToList();
            var presence = clients.Select((client, engineer) => PresenceAsync(client, engineer, deadline, cancellationToken)).ToList();
            var sampler = SampleSqlAsync(monitor, deadline, cancellationToken);
            await Task.WhenAll(workers.Concat(presence).Append(sampler));
            watch.Stop();
            var afterRetries = await RetryMetricAsync(clients[0], cancellationToken);
            var diagnostic = await monitor.FinishAsync(cancellationToken);
            var lost = await VerifyEditsAsync(clients[0], cancellationToken);
            var expectedConflicts = samples.Count(sample => sample.ExpectedConflict);
            var diagnostics = new ConcurrencyDiagnostics(
                Math.Max(0, afterRetries.Observed - beforeRetries.Observed),
                checked((int)Math.Max(0, afterRetries.Exhausted - beforeRetries.Exhausted)), diagnostic.NumberSeriesWait,
                diagnostic.LockWaitDelta,
                Math.Max(0, afterRetries.ConnectionAcquisitions - beforeRetries.ConnectionAcquisitions),
                Math.Max(0, afterRetries.ConnectionAcquisitionMilliseconds - beforeRetries.ConnectionAcquisitionMilliseconds),
                afterRetries.ConnectionAcquisitionMaximumMilliseconds,
                diagnostic.Deadlocks, expectedConflicts, lost, diagnostic.WaitMilliseconds,
                diagnostic.PublishedQueueStart, diagnostic.PublishedQueueMaximum, diagnostic.PublishedQueueEnd,
                diagnostic.RmaCreateQueries);
            notes.Add($"All mutations used the authenticated HTTP API for tenant {options.ConcurrencyTenantLabel}; tenant-scoped SQL selected prepared IDs and sampled diagnostics only.");
            notes.Add("Presence heartbeats ran every 20 seconds per simulated engineer; one seeded administrator identity represents several sessions.");
            notes.Add($"Workload weights: {string.Join(", ", ConcurrencyWorkload.Weights(options).Select(pair => $"{pair.Key}={pair.Value}"))}.");
            foreach (var warning in capacityWarnings.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                notes.Add($"Harness-capacity warning: {warning.Key} was unavailable {warning.Value:N0} time(s); these attempts are excluded from operation errors and latency.");
            var paths = await ConcurrencyReportWriter.WriteAsync(options, started, watch.Elapsed, samples, diagnostics, notes.Order().ToList(), cancellationToken);
            Console.WriteLine($"report: {paths.Markdown}");
            Console.WriteLine($"csv: {paths.Csv}");
            Console.WriteLine($"lost updates: {lost}; document_changed: {expectedConflicts}; concurrency exceptions: {diagnostics.ConcurrencyExceptions}");
        }
        finally
        {
            foreach (var client in clients) client.Dispose();
        }
    }

    private async Task<HttpClient> SignInAsync(int engineer, CancellationToken cancellationToken)
    {
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AutomaticDecompression = DecompressionMethods.All };
        var client = new HttpClient(handler) { BaseAddress = options.Url, Timeout = TimeSpan.FromSeconds(60) };
        var response = await client.PostAsJsonAsync("/api/auth/sign-in", new { options.Email, options.Password }, Json, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            client.Dispose();
            throw new InvalidOperationException($"Engineer {engineer + 1} could not sign in ({(int)response.StatusCode}).");
        }
        return client;
    }

    private async Task ConfigureNumberingAsync(HttpClient client, CancellationToken cancellationToken)
    {
        foreach (var series in new[] { "RMA", "CLM", "WRP", "RFB", "RCV", "CRT" })
        {
            using var response = await client.PutAsJsonAsync($"/api/numbering/settings/{series}",
                new { commandId = Guid.NewGuid(), mode = options.NumberingMode }, Json, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
    }


    private async Task<ResourcePool> LoadResourcesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await Sql.OpenAsync(options.ConnectionString, options.ConcurrencyTenantId);
        var units = await PairsAsync(connection, """
            SELECT TOP (500) c.unit_id AS id, COALESCE(i.raw_value, CONVERT(nvarchar(36), c.unit_id)) AS label
            FROM serialisation.unit_current c
            JOIN serialisation.unit_event prepared ON prepared.tenant_id = c.tenant_id AND prepared.unit_id = c.unit_id
                AND prepared.document_type = 'bench_concurrency' AND prepared.document_id = @shipment
                AND prepared.event_type = 'unit.shipped'
            OUTER APPLY (
                SELECT TOP (1) raw_value FROM serialisation.unit_identifier i
                WHERE i.tenant_id = c.tenant_id AND i.unit_id = c.unit_id AND i.valid_to IS NULL ORDER BY i.valid_from) i
            WHERE c.tenant_id = @tenant
            ORDER BY c.unit_id;
            """, cancellationToken, ("@tenant", options.ConcurrencyTenantId), ("@shipment", ConcurrencyPreparation.ShipmentDocumentId));
        var rmaUnits = await PairsAsync(connection, """
            SELECT TOP (@take) c.unit_id AS id, i.raw_value AS label
            FROM serialisation.unit_current c
            JOIN serialisation.unit_event prepared ON prepared.tenant_id = c.tenant_id AND prepared.unit_id = c.unit_id
                AND prepared.document_type = 'bench_concurrency' AND prepared.document_id = @shipment
                AND prepared.event_type = 'unit.shipped'
            CROSS APPLY (SELECT TOP (1) raw_value FROM serialisation.unit_identifier i
                WHERE i.tenant_id = c.tenant_id AND i.unit_id = c.unit_id AND i.valid_to IS NULL ORDER BY i.valid_from) i
            WHERE c.tenant_id = @tenant
              AND NOT EXISTS (SELECT 1 FROM serialisation.refurbishment_work_order w
                WHERE w.tenant_id = c.tenant_id AND w.target_unit_id = c.unit_id)
              AND NOT EXISTS (SELECT 1 FROM serialisation.claim claim_source
                WHERE claim_source.tenant_id = c.tenant_id AND claim_source.unit_id = c.unit_id
                  AND claim_source.submitted_by = @actor AND claim_source.status = 'approved')
              AND c.location_kind IN ('customer_site','in_transit')
            ORDER BY c.unit_id;
            """, cancellationToken, ("@tenant", options.ConcurrencyTenantId), ("@shipment", ConcurrencyPreparation.ShipmentDocumentId),
            ("@take", ConcurrencyWorkload.RequiredRmaEligibleUnits(options)), ("@actor", ConcurrencyPreparation.Actor));
        var openClaims = await IdsAsync(connection, """
            SELECT TOP (150) c.claim_id FROM serialisation.claim c
            WHERE c.tenant_id = @tenant AND c.submitted_by = @actor AND c.status = 'submitted'
              AND EXISTS (SELECT 1 FROM serialisation.claim_evidence e
                  WHERE e.tenant_id = c.tenant_id AND e.claim_id = c.claim_id
                    AND e.reference = @claim_pool)
            ORDER BY c.claim_id;
            """, cancellationToken, ("@tenant", options.ConcurrencyTenantId), ("@actor", ConcurrencyPreparation.Actor),
            ("@claim_pool", ConcurrencyPreparation.ClaimPoolReference(options.NumberingMode)));
        var editableClaims = await IdsAsync(connection, """
            SELECT TOP (50) c.claim_id FROM serialisation.claim c
            WHERE c.tenant_id = @tenant AND c.submitted_by = @actor AND c.status = 'submitted'
              AND EXISTS (SELECT 1 FROM serialisation.claim_evidence e
                  WHERE e.tenant_id = c.tenant_id AND e.claim_id = c.claim_id
                    AND e.reference = 'BENCH-CONCURRENCY-DOCUMENT')
            ORDER BY c.claim_id;
            """, cancellationToken, ("@tenant", options.ConcurrencyTenantId), ("@actor", ConcurrencyPreparation.Actor));
        var replacementSources = (await Sql.RowsAsync(connection, """
            SELECT TOP (@take) c.claim_id, l.entitlement_id
            FROM serialisation.claim c
            JOIN serialisation.claim_line l ON l.tenant_id = c.tenant_id AND l.claim_id = c.claim_id
            JOIN serialisation.entitlement e ON e.tenant_id = l.tenant_id AND e.entitlement_id = l.entitlement_id
            JOIN serialisation.warranty_policy_version p ON p.tenant_id = e.tenant_id AND p.policy_version_id = e.policy_version_id
            WHERE c.tenant_id = @tenant AND c.submitted_by = @actor AND c.status = 'approved' AND l.entitlement_id IS NOT NULL
              AND e.status = 'active'
              AND EXISTS (SELECT 1 FROM serialisation.claim_evidence evidence
                  WHERE evidence.tenant_id = c.tenant_id AND evidence.claim_id = c.claim_id
                    AND evidence.reference = @claim_pool)
            ORDER BY c.claim_id;
            """, 0, ("@tenant", options.ConcurrencyTenantId), ("@actor", ConcurrencyPreparation.Actor),
            ("@claim_pool", ConcurrencyPreparation.ClaimPoolReference(options.NumberingMode)),
            ("@take", ConcurrencyWorkload.RequiredReplacementCapacity(options))))
            .Select(row => new ReplacementSource((Guid)row["claim_id"]!, (Guid)row["entitlement_id"]!)).ToList();
        var replacementUnits = await IdsAsync(connection, """
            SELECT TOP (@take) sm.unit_id
            FROM serialisation.selection_set_member sm
            WHERE sm.tenant_id = @tenant AND sm.selection_id = @selection
              AND NOT EXISTS (SELECT 1 FROM serialisation.replacement_link r
                  WHERE r.tenant_id = sm.tenant_id AND r.replacement_unit_id = sm.unit_id
                    AND r.state IN ('proposed','awaiting_return'))
              AND NOT EXISTS (SELECT 1 FROM serialisation.unit_event e
                  WHERE e.tenant_id = sm.tenant_id AND e.unit_id = sm.unit_id
                    AND e.document_type = 'bench_concurrency' AND e.document_id = @shipment)
            ORDER BY sm.unit_id;
            """, cancellationToken, ("@tenant", options.ConcurrencyTenantId), ("@selection", ConcurrencyPreparation.StockSelectionId),
            ("@shipment", ConcurrencyPreparation.ShipmentDocumentId),
            ("@take", ConcurrencyWorkload.RequiredReplacementCapacity(options)));
        return new ResourcePool(units, rmaUnits,
            await IdsAsync(connection, "SELECT TOP (200) rma_id FROM serialisation.rma WHERE tenant_id = @tenant ORDER BY requested_at DESC", cancellationToken, ("@tenant", options.ConcurrencyTenantId)),
            editableClaims,
            await IdsAsync(connection, "SELECT TOP (200) work_order_id FROM serialisation.refurbishment_work_order WHERE tenant_id = @tenant AND status = 'open' ORDER BY opened_at DESC", cancellationToken, ("@tenant", options.ConcurrencyTenantId)),
            await IdsAsync(connection, "SELECT TOP (250) campaign_id FROM serialisation.campaign WHERE tenant_id = @tenant AND status = 'draft' AND created_by = @actor ORDER BY updated_at DESC", cancellationToken, ("@tenant", options.ConcurrencyTenantId), ("@actor", ConcurrencyPreparation.Actor)),
            await IdsAsync(connection, "SELECT TOP (250) policy_version_id FROM serialisation.warranty_policy_version WHERE tenant_id = @tenant AND status = 'draft' AND created_by = @actor ORDER BY created_at DESC", cancellationToken, ("@tenant", options.ConcurrencyTenantId), ("@actor", ConcurrencyPreparation.Actor)),
            openClaims, replacementSources, replacementUnits);
    }

    private static async Task<List<Guid>> IdsAsync(SqlConnection connection, string sql, CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters) =>
        (await Sql.RowsAsync(connection, sql, 0, parameters)).Select(row => (Guid)row.Values.First(value => value is Guid)!).ToList();

    private static async Task<List<(Guid Id, string Label)>> PairsAsync(SqlConnection connection, string sql,
        CancellationToken cancellationToken, params (string Name, object Value)[] parameters) =>
        (await Sql.RowsAsync(connection, sql, 0, parameters)).Select(row => ((Guid)row["id"]!, (string)row["label"]!)).ToList();

    private void ValidatePreparedResources()
    {
        var missing = new List<string>();
        if (resources.RmaUnits.Count == 0) missing.Add("RMA-eligible units");
        if (resources.Refurbishments.Count == 0) missing.Add("open refurbishment work orders");
        if (resources.OpenClaims.Count == 0) missing.Add("open claims");
        if (resources.ReplacementSources.Count == 0) missing.Add("approved replacement sources");
        if (resources.ReplacementUnits.Count == 0) missing.Add("replacement-stock units");
        if (resources.Campaigns.Count + resources.PolicyDrafts.Count == 0) missing.Add("editable documents");
        if (missing.Count > 0)
            throw new InvalidOperationException("Concurrency working set is incomplete (" + string.Join(", ", missing)
                + $"). Run prepare-concurrency --tenant {options.TenantIndex} first.");
    }

    private async Task WorkerAsync(HttpClient client, int engineer, DateTime deadline, CancellationToken cancellationToken)
    {
        var random = new Random(20261018 + engineer);
        var weighted = Expand(ConcurrencyWorkload.Weights(options));
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            var operation = weighted[random.Next(weighted.Count)];
            await MeasureAsync(operation, () => ExecuteAsync(client, operation, random, engineer, cancellationToken), cancellationToken);
        }
    }

    private async Task PresenceAsync(HttpClient client, int engineer, DateTime deadline, CancellationToken cancellationToken)
    {
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            var document = resources.Campaigns.Count == 0 ? $"engineer-{engineer}" : Pick(resources.Campaigns, new Random(engineer + Environment.TickCount), true).ToString("N");
            await MeasureAsync("presence_heartbeat", () => SendAsync(client, HttpMethod.Post, "/api/presence/heartbeat",
                new { documentType = "campaign", documentId = document, mode = "editing" }, cancellationToken), cancellationToken);
            var remaining = deadline - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
                try { await Task.Delay(remaining < TimeSpan.FromSeconds(20) ? remaining : TimeSpan.FromSeconds(20), cancellationToken); } catch (OperationCanceledException) { }
        }
    }

    private async Task SampleSqlAsync(ConcurrencySqlMonitor monitor, DateTime deadline, CancellationToken cancellationToken)
    {
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            try { await monitor.SampleAsync(cancellationToken); await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken); }
            catch (OperationCanceledException) { }
            catch (SqlException ex) { notes.Add($"SQL diagnostic sample failed: {ex.Number}."); }
        }
    }

    private Task<ResponseResult> ExecuteAsync(HttpClient client, string operation, Random random, int engineer, CancellationToken cancellationToken) => operation switch
    {
        "unit_lookup" => resources.Units.Count == 0 ? Missing("unit") : SendAsync(client, HttpMethod.Get, $"/api/units/lookup?q={Uri.EscapeDataString(Pick(resources.Units, random).Label)}", null, cancellationToken),
        "unit_search" => SendAsync(client, HttpMethod.Get, "/api/units/search?take=25&sort=identifier", null, cancellationToken),
        "rma_list" => SendAsync(client, HttpMethod.Get, "/api/rmas?take=25", null, cancellationToken),
        "rma_detail" => resources.Rmas.Count == 0 ? Missing("rma") : SendAsync(client, HttpMethod.Get, $"/api/rmas/{Pick(resources.Rmas, random)}", null, cancellationToken),
        "claim_list" => SendAsync(client, HttpMethod.Get, "/api/claims?status=all&take=25", null, cancellationToken),
        "claim_detail" => resources.Claims.Count == 0 ? Missing("claim") : SendAsync(client, HttpMethod.Get, $"/api/claims/{Pick(resources.Claims, random)}", null, cancellationToken),
        "rma_create_receive" => RmaAsync(client, random, cancellationToken),
        "claim_intake_adjudication" => ClaimAsync(client, random, cancellationToken),
        "warranty_replacement_create" => ReplacementAsync(client, random, cancellationToken),
        "refurbishment_append" => RefurbishmentAsync(client, random, cancellationToken),
        "document_edit" => DocumentEditAsync(client, random, engineer, cancellationToken),
        "background_recheck" => resources.Units.Count == 0 ? Missing("unit") : SendAsync(client, HttpMethod.Get, $"/api/units/{Pick(resources.Units, random, Hot(random)).Id}/warranty-check?on=2026-09-01", null, cancellationToken),
        _ => Missing(operation),
    };

    private async Task<ResponseResult> RmaAsync(HttpClient client, Random random, CancellationToken cancellationToken)
    {
        if (!rmaUnits.TryDequeue(out var unit))
        {
            await Task.Delay(100, cancellationToken);
            return new(204, "", true, null, "RMA-eligible working set exhausted");
        }
        var opened = await SendAsync(client, HttpMethod.Post, "/api/rmas/request", new
        {
            commandId = Guid.NewGuid(), requestDate = new DateOnly(2026, 9, 1), returningPartyRef = Customer,
            settlementPartyRef = Customer, serviceEntityId = ServiceEntity, obligorEntityId = LegalEntity,
            serviceCountry = "AQ", currency = "EUR", lines = new[] { new { unitId = unit.Id, coverageFamily = "base_warranty", requestedAmount = 25m, claimLineKind = "parts", description = "Load harness return" } },
            eventTime = BusinessTime,
        }, cancellationToken);
        if (!opened.Success || opened.Json is null || !GuidProperty(opened.Json.RootElement, "rmaId", out var rmaId)) return opened;
        var authorised = await SendAsync(client, HttpMethod.Post, $"/api/rmas/{rmaId}/authorise", new { commandId = Guid.NewGuid(), eventTime = BusinessTime.AddMinutes(1) }, cancellationToken);
        if (!authorised.Success || authorised.Json is null) return authorised;
        var lineIds = FindGuids(authorised.Json.RootElement, "rmaLineId");
        if (lineIds.Count == 0) return new(409, "rma_line_missing", false, authorised.Json);
        return await SendAsync(client, HttpMethod.Post, $"/api/rmas/{rmaId}/receive", new
        {
            commandId = Guid.NewGuid(), receivingLocationId = Quarantine,
            lines = lineIds.Select(id => new { rmaLineId = id, receivedIdentifier = new { value = unit.Label, identifierType = "internal_serial" }, conditionGrade = "used", valuationRule = "benchmark_graded_receipt", outcome = "accepted" }),
            dispatchedAt = BusinessTime.AddMinutes(2), receivedAt = BusinessTime.AddMinutes(3), eventTime = BusinessTime.AddMinutes(3),
        }, cancellationToken);
    }

    private async Task<ResponseResult> ClaimAsync(HttpClient client, Random random, CancellationToken cancellationToken)
    {
        if (openClaims.TryDequeue(out var preparedClaim))
        {
            var preparedResult = await SendAsync(client, HttpMethod.Post, $"/api/claims/{preparedClaim}/adjudicate",
                new { commandId = Guid.NewGuid(), eventTime = BusinessTime.AddMinutes(1) }, cancellationToken);
            return preparedResult;
        }
        if (resources.Units.Count == 0) return await Missing("unit");
        var opened = await SendAsync(client, HttpMethod.Post, "/api/claims", new
        {
            commandId = Guid.NewGuid(), unitId = Pick(resources.Units, random, Hot(random)).Id, identifier = (object?)null,
            claimDate = new DateOnly(2026, 9, 1), claimantPartyRef = Customer, settlementPartyRef = Customer,
            serviceEntityId = ServiceEntity, obligorEntityId = LegalEntity, serviceCountry = "AQ", currency = "EUR",
            lines = new[] { new { kind = "parts", coverageFamily = "base_warranty", requestedAmount = 10m, description = "Load harness claim" } },
            evidence = new[] { new { kind = "note", reference = "LOAD", description = "Synthetic load evidence" } },
            remedy = "repair", consumerChoiceEvidenceRef = "LOAD", eventTime = BusinessTime,
        }, cancellationToken);
        if (!opened.Success || opened.Json is null || !GuidProperty(opened.Json.RootElement, "claimId", out var claimId)) return opened;
        var adjudicated = await SendAsync(client, HttpMethod.Post, $"/api/claims/{claimId}/adjudicate",
            new { commandId = Guid.NewGuid(), eventTime = BusinessTime.AddMinutes(1) }, cancellationToken);
        return adjudicated;
    }

    private Task<ResponseResult> ReplacementAsync(HttpClient client, Random random, CancellationToken cancellationToken)
    {
        _ = random;
        if (!replacementSources.TryDequeue(out var source)) return Capacity("approved replacement sources exhausted");
        if (!replacementUnits.TryDequeue(out var replacementUnit)) return Capacity("replacement-stock units exhausted");
        return SendAsync(client, HttpMethod.Post, "/api/warranty-replacements", new
        {
            commandId = Guid.NewGuid(), sourceKind = "claim", sourceId = source.ClaimId,
            sourceEntitlementId = source.EntitlementId, replacementUnitId = replacementUnit,
            replacementReason = "warranty_defect", pricePolicy = "zero", differenceAmount = 0m, currency = "EUR",
            serviceCountry = "AQ", consumerContract = false, consentRefurbished = true, eventTime = BusinessTime, legalEntityId = LegalEntity,
        }, cancellationToken);
    }

    private Task<ResponseResult> RefurbishmentAsync(HttpClient client, Random random, CancellationToken cancellationToken)
    {
        if (resources.Refurbishments.Count == 0) return Missing("refurbishment");
        var id = Pick(resources.Refurbishments, random, Hot(random));
        return random.Next(2) == 0
            ? SendAsync(client, HttpMethod.Post, $"/api/refurbishments/{id}/labour", new { commandId = Guid.NewGuid(), operationId = (Guid?)null, hours = 0.1m, hourlyRate = 1m, currency = "EUR", eventTime = BusinessTime, legalEntityId = LegalEntity }, cancellationToken)
            : SendAsync(client, HttpMethod.Post, $"/api/refurbishments/{id}/costs", new { commandId = Guid.NewGuid(), kind = "other", amount = 0.1m, currency = "EUR", description = "Load harness", eventTime = BusinessTime, legalEntityId = LegalEntity }, cancellationToken);
    }

    private async Task<ResponseResult> DocumentEditAsync(HttpClient client, Random random, int engineer, CancellationToken cancellationToken)
    {
        var kinds = new List<string>();
        if (resources.Campaigns.Count > 0) kinds.Add("campaign");
        if (resources.PolicyDrafts.Count > 0) kinds.Add("policy");
        if (resources.Claims.Count > 0) kinds.Add("claim");
        if (kinds.Count == 0) return await Missing("editable_document");
        return kinds[random.Next(kinds.Count)] switch
        {
            "policy" => await PolicyEditAsync(client, random, engineer, cancellationToken),
            "claim" => await ClaimEditAsync(client, random, engineer, cancellationToken),
            _ => await CampaignEditAsync(client, random, engineer, cancellationToken),
        };
    }

    private async Task<ResponseResult> CampaignEditAsync(HttpClient client, Random random, int engineer, CancellationToken cancellationToken)
    {
        var id = Pick(resources.Campaigns, random, true);
        var loaded = await SendAsync(client, HttpMethod.Get, $"/api/campaigns/{id}", null, cancellationToken);
        if (!loaded.Success || loaded.Json is null) return loaded;
        var row = loaded.Json.RootElement;
        await Task.Delay(random.Next(1, 20), cancellationToken);
        var token = $"load-{engineer}-{Guid.NewGuid():N}";
        var response = await SendAsync(client, HttpMethod.Put, $"/api/campaigns/{id}", new
        {
            commandId = Guid.NewGuid(), campaignId = id, code = Text(row, "code"), name = Text(row, "name"),
            coverageSource = Text(row, "coverageSource"), reserveTag = Text(row, "reserveTag"), status = Text(row, "status"),
            criteria = row.GetProperty("criteria"), eventTime = BusinessTime, reasonCode = Text(row, "reasonCode"), reasonNote = token,
            runsUntil = NullableText(row, "runsUntil"), partsPct = Decimal(row, "partsPct"), labourPct = Decimal(row, "labourPct"),
            travelPct = Decimal(row, "travelPct"), expectedVersion = Text(row, "version"),
        }, cancellationToken);
        if (response.Success && response.Json is not null && response.Json.RootElement.TryGetProperty("campaign", out var campaign))
        {
            successfulEdits.Add(new SuccessfulEdit("campaign", id, token));
        }
        return response;
    }

    private async Task<ResponseResult> PolicyEditAsync(HttpClient client, Random random, int engineer, CancellationToken cancellationToken)
    {
        var id = Pick(resources.PolicyDrafts, random, true);
        var loaded = await SendAsync(client, HttpMethod.Get, $"/api/warranty-policies/versions/{id}", null, cancellationToken);
        if (!loaded.Success || loaded.Json is null) return loaded;
        var row = loaded.Json.RootElement;
        await Task.Delay(random.Next(1, 20), cancellationToken);
        var token = $"Benchmark edit {engineer}-{Guid.NewGuid():N}";
        var response = await SendAsync(client, HttpMethod.Put, $"/api/warranty-policies/versions/{id}", new
        {
            policyVersionId = id, name = token, appliesToContractsFrom = Text(row, "appliesToContractsFrom"),
            terms = row.GetProperty("terms"), expectedVersion = Text(row, "documentVersion"),
        }, cancellationToken);
        if (response.Success) successfulEdits.Add(new SuccessfulEdit("policy", id, token));
        return response;
    }

    private async Task<ResponseResult> ClaimEditAsync(HttpClient client, Random random, int engineer, CancellationToken cancellationToken)
    {
        var id = Pick(resources.Claims, random, true);
        var loaded = await SendAsync(client, HttpMethod.Get, $"/api/claims/{id}", null, cancellationToken);
        if (!loaded.Success || loaded.Json is null) return loaded;
        var token = $"Benchmark evidence {engineer}-{Guid.NewGuid():N}";
        await Task.Delay(random.Next(1, 20), cancellationToken);
        var response = await SendAsync(client, HttpMethod.Post, $"/api/claims/{id}/request-evidence", new
        {
            commandId = Guid.NewGuid(), note = token, eventTime = BusinessTime,
            expectedVersion = Text(loaded.Json.RootElement, "version"),
        }, cancellationToken);
        if (response.Success) successfulEdits.Add(new SuccessfulEdit("claim", id, token));
        return response;
    }

    private async Task MeasureAsync(string operation, Func<Task<ResponseResult>> action, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var result = await action();
            if (result.HarnessWarning is { } warning)
            {
                capacityWarnings.AddOrUpdate(warning, 1, (_, count) => count + 1);
                return;
            }
            var expected = result.StatusCode == 409 && result.ErrorType == "document_changed";
            samples.Add(new(operation, watch.Elapsed.TotalMilliseconds, result.StatusCode, result.Success ? null : result.ErrorType, expected));
            result.Json?.Dispose();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            samples.Add(new(operation, watch.Elapsed.TotalMilliseconds, 0, ex.GetType().Name, false));
        }
    }

    private static async Task<ResponseResult> SendAsync(HttpClient client, HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        JsonDocument? json = null;
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(text))
        {
            try { json = JsonDocument.Parse(text); } catch (JsonException) { }
        }
        var error = response.IsSuccessStatusCode ? "" : Error(json?.RootElement, response.StatusCode);
        return new((int)response.StatusCode, error, response.IsSuccessStatusCode, json);
    }

    private async Task<int> VerifyEditsAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var lost = 0;
        foreach (var group in successfulEdits.GroupBy(edit => (edit.DocumentType, edit.Id)))
        {
            var (type, id) = group.Key;
            var path = type switch
            {
                "policy" => $"/api/warranty-policies/versions/{id}",
                "claim" => $"/api/claims/{id}",
                _ => $"/api/campaigns/{id}",
            };
            var response = await SendAsync(client, HttpMethod.Get, path, null, cancellationToken);
            using (response.Json)
            {
                if (!response.Success || response.Json is null) { lost++; continue; }
                var final = response.Json.RootElement;
                var tokens = group.Select(edit => edit.Token).ToHashSet(StringComparer.Ordinal);
                var found = type switch
                {
                    "policy" => tokens.Contains(Text(final, "name")),
                    "claim" => final.GetProperty("evidence").EnumerateArray().Select(row => NullableText(row, "description"))
                        .Where(value => value is not null).Any(value => tokens.Contains(value!)),
                    _ => tokens.Contains(NullableText(final, "reasonNote") ?? ""),
                };
                if (!found) lost++;
            }
        }
        return lost;
    }

    private static async Task<RetryMetric> RetryMetricAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var response = await SendAsync(client, HttpMethod.Get, "/api/platform/metrics/concurrency", null, cancellationToken);
        using (response.Json)
        {
            if (response.Json is null) return new(0, 0);
            var root = response.Json.RootElement;
            var acquisition = root.GetProperty("connectionAcquisition");
            return new(root.GetProperty("concurrencyExceptionsObserved").GetInt64(),
                root.GetProperty("retriesExhausted").GetInt64(), acquisition.GetProperty("count").GetInt64(),
                acquisition.GetProperty("totalMilliseconds").GetDouble(), acquisition.GetProperty("maximumMilliseconds").GetDouble());
        }
    }

    private bool Hot(Random random) => random.Next(100) < options.ContentionPercent;
    private static T Pick<T>(IReadOnlyList<T> values, Random random, bool hot = false) => values[random.Next(hot ? Math.Min(4, values.Count) : values.Count)];
    private static Task<ResponseResult> Missing(string resource) => Task.FromResult(new ResponseResult(424, $"no_{resource}", false, null));
    private static Task<ResponseResult> Capacity(string warning) => Task.FromResult(new ResponseResult(204, "", true, null, warning));

    private static List<string> Expand(IReadOnlyDictionary<string, int> weights) => weights.SelectMany(pair => Enumerable.Repeat(pair.Key, pair.Value)).ToList();
    private static string Error(JsonElement? json, HttpStatusCode status)
    {
        if (json is { ValueKind: JsonValueKind.Object } value)
        {
            foreach (var property in new[] { "rule", "title", "reason", "error" })
                if (value.TryGetProperty(property, out var found) && found.ValueKind == JsonValueKind.String) return found.GetString() ?? status.ToString();
        }
        return status == HttpStatusCode.InternalServerError ? "internal_server_error" : status.ToString().ToLowerInvariant();
    }

    private static string Text(JsonElement row, string property) => row.GetProperty(property).GetString() ?? "";
    private static string? NullableText(JsonElement row, string property) => row.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static decimal Decimal(JsonElement row, string property) => row.GetProperty(property).GetDecimal();
    private static bool GuidProperty(JsonElement row, string property, out Guid value)
    {
        value = Guid.Empty;
        return row.TryGetProperty(property, out var found) && found.TryGetGuid(out value);
    }
    private static List<Guid> FindGuids(JsonElement root, string property)
    {
        var result = new List<Guid>();
        Walk(root);
        return result;
        void Walk(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object) foreach (var item in node.EnumerateObject()) { if (item.NameEquals(property) && item.Value.TryGetGuid(out var id)) result.Add(id); else Walk(item.Value); }
            else if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) Walk(item);
        }
    }

    private sealed record ResponseResult(int StatusCode, string ErrorType, bool Success, JsonDocument? Json, string? HarnessWarning = null);
    private sealed record RetryMetric(long Observed, long Exhausted, long ConnectionAcquisitions = 0,
        double ConnectionAcquisitionMilliseconds = 0, double ConnectionAcquisitionMaximumMilliseconds = 0);
    private sealed record SuccessfulEdit(string DocumentType, Guid Id, string Token);
    private sealed record ReplacementSource(Guid ClaimId, Guid EntitlementId);
    private sealed record ResourcePool(
        List<(Guid Id, string Label)> Units,
        List<(Guid Id, string Label)> RmaUnits,
        List<Guid> Rmas,
        List<Guid> Claims,
        List<Guid> Refurbishments,
        List<Guid> Campaigns,
        List<Guid> PolicyDrafts,
        List<Guid> OpenClaims,
        List<ReplacementSource> ReplacementSources,
        List<Guid> ReplacementUnits);
}
