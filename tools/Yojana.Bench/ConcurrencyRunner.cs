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
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid LegalEntity = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Customer = Guid.Parse("20000000-0000-0000-0000-000000000007");
    private static readonly Guid ServiceEntity = Guid.Parse("20000000-0000-0000-0000-000000000017");
    private static readonly Guid Quarantine = Guid.Parse("40000000-0000-0000-0000-000000000001");
    private static readonly DateTime BusinessTime = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentBag<LoadSample> samples = [];
    private readonly ConcurrentDictionary<Guid, ConcurrentBag<SuccessfulEdit>> successfulEdits = new();
    private readonly ConcurrentBag<string> notes = [];
    private readonly ConcurrentDictionary<string, int> exhausted = new(StringComparer.Ordinal);
    private ResourcePool resources = new([], [], [], [], [], [], []);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var clients = await Task.WhenAll(Enumerable.Range(0, options.Engineers).Select(i => SignInAsync(i, cancellationToken)));
        try
        {
            await ConfigureNumberingAsync(clients[0], cancellationToken);
            await EnsureCurrencyAsync(clients[0], cancellationToken);
            await EnsureUnitsAsync(clients[0], cancellationToken);
            resources = await LoadResourcesAsync(cancellationToken);
            await EnsureHotDocumentsAsync(clients[0], cancellationToken);
            await EnsureClaimsAsync(clients[0], cancellationToken);
            resources = await LoadResourcesAsync(cancellationToken);
            await EnsureRefurbishmentsAsync(clients[0], cancellationToken);
            resources = await LoadResourcesAsync(cancellationToken);
            var beforeRetries = await RetryMetricAsync(clients[0], cancellationToken);
            var monitor = new ConcurrencySqlMonitor(options.ConnectionString);
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
            var retriesExhausted = exhausted.Values.Sum();
            var diagnostics = new ConcurrencyDiagnostics(
                Math.Max(0, afterRetries - beforeRetries), retriesExhausted, diagnostic.NumberSeriesWait,
                diagnostic.LockWaitDelta, diagnostic.Deadlocks, expectedConflicts, lost);
            notes.Add("All mutations used the authenticated HTTP API; SQL access selected existing IDs and sampled diagnostics only.");
            notes.Add("Presence heartbeats ran every 20 seconds per simulated engineer; the development account may represent several sessions for one seeded identity.");
            notes.Add($"Workload weights: {string.Join(", ", Weights().Select(pair => $"{pair.Key}={pair.Value}"))}.");
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

    private static async Task EnsureCurrencyAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.PutAsJsonAsync("/api/tenant/currency", new { currency = "EUR", accepted = new[] { "EUR" } }, Json, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task EnsureUnitsAsync(HttpClient client, CancellationToken cancellationToken)
    {
        await using var connection = await Sql.OpenAsync(options.ConnectionString, Tenant);
        if (await Sql.ScalarAsync<int>(connection, "SELECT COUNT(*) FROM serialisation.unit") >= 40) return;
        var token = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var lines = Enumerable.Range(1, 40).Select(index => new
        {
            identifiers = new[] { new { type = "internal_serial", rawValue = $"LOAD-{token}-{index:D3}" } },
            clientReference = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        var result = await SendAsync(client, HttpMethod.Post, "/api/units/commission", new
        {
            commandId = Guid.NewGuid(), itemId = Guid.Parse("10000000-0000-0000-0000-000000000008"), origin = "purchased",
            lines, eventTime = BusinessTime, legalEntityId = LegalEntity, locationId = Guid.Parse("40000000-0000-0000-0000-000000000004"),
            locationKind = "warehouse", source = "load_harness",
        }, cancellationToken);
        using (result.Json) { if (!result.Success) notes.Add($"Unit preparation failed: {result.ErrorType}."); }
    }

    private async Task EnsureRefurbishmentsAsync(HttpClient client, CancellationToken cancellationToken)
    {
        if (resources.Refurbishments.Count >= 4 || resources.Units.Count == 0) return;
        foreach (var unit in resources.Units.Take(4))
        {
            var result = await SendAsync(client, HttpMethod.Post, "/api/refurbishments", new
            {
                commandId = Guid.NewGuid(), targetUnitId = unit.Id, kind = "refurbish", openedAt = BusinessTime,
                legalEntityId = LegalEntity, repairLocationId = Guid.Parse("40000000-0000-0000-0000-000000000006"),
            }, cancellationToken);
            using (result.Json) { if (!result.Success) notes.Add($"Refurbishment preparation failed: {result.ErrorType}."); }
        }
    }

    private async Task EnsureClaimsAsync(HttpClient client, CancellationToken cancellationToken)
    {
        if (resources.Claims.Count >= 8 || resources.Units.Count == 0) return;
        var random = new Random(20261018);
        for (var i = resources.Claims.Count; i < 8; i++)
        {
            var result = await ClaimAsync(client, random, cancellationToken);
            using (result.Json) { if (!result.Success) notes.Add($"Claim preparation failed: {result.ErrorType}."); }
        }
    }

    private async Task<ResourcePool> LoadResourcesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await Sql.OpenAsync(options.ConnectionString, Tenant);
        var units = await PairsAsync(connection, """
            SELECT TOP (500) c.unit_id AS id, COALESCE(i.raw_value, CONVERT(nvarchar(36), c.unit_id)) AS label
            FROM serialisation.unit_current c OUTER APPLY (
                SELECT TOP (1) raw_value FROM serialisation.unit_identifier i
                WHERE i.tenant_id = c.tenant_id AND i.unit_id = c.unit_id AND i.valid_to IS NULL ORDER BY i.valid_from) i
            ORDER BY c.unit_id;
            """, cancellationToken);
        return new ResourcePool(units,
            await IdsAsync(connection, "SELECT TOP (200) rma_id FROM serialisation.rma ORDER BY requested_at DESC", cancellationToken),
            await IdsAsync(connection, "SELECT TOP (200) claim_id FROM serialisation.claim ORDER BY submitted_at DESC", cancellationToken),
            await IdsAsync(connection, "SELECT TOP (100) work_order_id FROM serialisation.refurbishment_work_order WHERE status = 'open' ORDER BY opened_at DESC", cancellationToken),
            await IdsAsync(connection, "SELECT TOP (20) campaign_id FROM serialisation.campaign WHERE status = 'draft' ORDER BY updated_at DESC", cancellationToken),
            await IdsAsync(connection, "SELECT TOP (200) entitlement_id FROM serialisation.entitlement WHERE status IN ('active','suspended') ORDER BY created_at DESC", cancellationToken),
            await IdsAsync(connection, "SELECT TOP (100) warranty_replacement_id FROM serialisation.warranty_replacement ORDER BY created_at DESC", cancellationToken));
    }

    private static async Task<List<Guid>> IdsAsync(SqlConnection connection, string sql, CancellationToken cancellationToken) =>
        (await Sql.RowsAsync(connection, sql)).Select(row => (Guid)row.Values.First(value => value is Guid)!).ToList();

    private static async Task<List<(Guid Id, string Label)>> PairsAsync(SqlConnection connection, string sql, CancellationToken cancellationToken) =>
        (await Sql.RowsAsync(connection, sql)).Select(row => ((Guid)row["id"]!, (string)row["label"]!)).ToList();

    private async Task EnsureHotDocumentsAsync(HttpClient client, CancellationToken cancellationToken)
    {
        for (var i = resources.Campaigns.Count; i < 4; i++)
        {
            var id = Guid.NewGuid();
            using var response = await client.PostAsJsonAsync("/api/campaigns", new
            {
                commandId = id,
                code = $"LOAD-{id:N}"[..18],
                name = $"Load contention document {i + 1}",
                coverageSource = "goodwill",
                reserveTag = "LOAD-HARNESS",
                criteria = Array.Empty<object>(),
                eventTime = BusinessTime,
                reasonCode = "reliability",
                reasonNote = "Load harness document",
                runsUntil = (DateOnly?)null,
                partsPct = 100m,
                labourPct = 100m,
                travelPct = 100m,
            }, Json, cancellationToken);
            if (!response.IsSuccessStatusCode) notes.Add($"Could not create hot document {i + 1}: {(int)response.StatusCode}.");
        }
    }

    private async Task WorkerAsync(HttpClient client, int engineer, DateTime deadline, CancellationToken cancellationToken)
    {
        var random = new Random(20261018 + engineer);
        var weighted = Expand(Weights());
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
        if (resources.Units.Count == 0) return await Missing("unit");
        var unit = Pick(resources.Units, random, Hot(random));
        var opened = await SendAsync(client, HttpMethod.Post, "/api/rmas/request", new
        {
            commandId = Guid.NewGuid(), requestDate = new DateOnly(2026, 9, 1), returningPartyRef = Customer,
            settlementPartyRef = Customer, serviceEntityId = ServiceEntity, obligorEntityId = LegalEntity,
            serviceCountry = "ZZ", currency = "EUR", lines = new[] { new { unitId = unit.Id, coverageFamily = "base_warranty", requestedAmount = 25m, claimLineKind = "parts", description = "Load harness return" } },
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
            lines = lineIds.Select(id => new { rmaLineId = id, receivedIdentifier = (object?)null, conditionGrade = "B", valuationRule = "load", outcome = "accepted" }),
            dispatchedAt = BusinessTime.AddMinutes(2), receivedAt = BusinessTime.AddMinutes(3), eventTime = BusinessTime.AddMinutes(3),
        }, cancellationToken);
    }

    private async Task<ResponseResult> ClaimAsync(HttpClient client, Random random, CancellationToken cancellationToken)
    {
        if (resources.Units.Count == 0) return await Missing("unit");
        var opened = await SendAsync(client, HttpMethod.Post, "/api/claims", new
        {
            commandId = Guid.NewGuid(), unitId = Pick(resources.Units, random, Hot(random)).Id, identifier = (object?)null,
            claimDate = new DateOnly(2026, 9, 1), claimantPartyRef = Customer, settlementPartyRef = Customer,
            serviceEntityId = ServiceEntity, obligorEntityId = LegalEntity, serviceCountry = "ZZ", currency = "EUR",
            lines = new[] { new { kind = "parts", coverageFamily = "base_warranty", requestedAmount = 10m, description = "Load harness claim" } },
            evidence = new[] { new { kind = "note", reference = "LOAD", description = "Synthetic load evidence" } },
            remedy = "repair", consumerChoiceEvidenceRef = "LOAD", eventTime = BusinessTime,
        }, cancellationToken);
        if (!opened.Success || opened.Json is null || !GuidProperty(opened.Json.RootElement, "claimId", out var claimId)) return opened;
        return await SendAsync(client, HttpMethod.Post, $"/api/claims/{claimId}/adjudicate",
            new { commandId = Guid.NewGuid(), eventTime = BusinessTime.AddMinutes(1), denialReason = "evidence_insufficient", denialNote = "Synthetic load adjudication" }, cancellationToken);
    }

    private Task<ResponseResult> ReplacementAsync(HttpClient client, Random random, CancellationToken cancellationToken)
    {
        if (resources.Claims.Count == 0 || resources.Entitlements.Count == 0 || resources.Units.Count < 2) return Missing("replacement_prerequisite");
        return SendAsync(client, HttpMethod.Post, "/api/warranty-replacements", new
        {
            commandId = Guid.NewGuid(), sourceKind = "claim", sourceId = Pick(resources.Claims, random),
            sourceEntitlementId = Pick(resources.Entitlements, random), replacementUnitId = Pick(resources.Units, random).Id,
            replacementReason = "warranty_defect", pricePolicy = "zero", differenceAmount = 0m, currency = "EUR",
            serviceCountry = "ZZ", consumerContract = false, consentRefurbished = true, eventTime = BusinessTime, legalEntityId = LegalEntity,
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
        if (resources.Campaigns.Count == 0) return await Missing("campaign");
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
            successfulEdits.GetOrAdd(id, _ => []).Add(new SuccessfulEdit(campaign.GetProperty("updatedAt").GetDateTime(), token));
        }
        return response;
    }

    private async Task MeasureAsync(string operation, Func<Task<ResponseResult>> action, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var result = await action();
            var expected = result.StatusCode == 409 && result.ErrorType == "document_changed";
            if (result.StatusCode >= 500 && result.ErrorType.Contains("concurrency", StringComparison.OrdinalIgnoreCase)) exhausted.AddOrUpdate(result.ErrorType, 1, (_, count) => count + 1);
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
        foreach (var (id, edits) in successfulEdits)
        {
            var response = await SendAsync(client, HttpMethod.Get, $"/api/campaigns/{id}", null, cancellationToken);
            using (response.Json)
            {
                if (!response.Success || response.Json is null) { lost++; continue; }
                var final = response.Json.RootElement;
                var maxAt = edits.Max(edit => edit.UpdatedAt);
                var expected = edits.Where(edit => edit.UpdatedAt == maxAt).ToList();
                if (!expected.Any(edit => edit.ReasonNote == NullableText(final, "reasonNote"))) lost++;
            }
        }
        return lost;
    }

    private static async Task<long> RetryMetricAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var response = await SendAsync(client, HttpMethod.Get, "/api/platform/metrics/concurrency", null, cancellationToken);
        using (response.Json)
        {
            return response.Json?.RootElement.GetProperty("concurrencyExceptionsObserved").GetInt64() ?? 0;
        }
    }

    private bool Hot(Random random) => random.Next(100) < options.ContentionPercent;
    private static T Pick<T>(IReadOnlyList<T> values, Random random, bool hot = false) => values[random.Next(hot ? Math.Min(4, values.Count) : values.Count)];
    private static Task<ResponseResult> Missing(string resource) => Task.FromResult(new ResponseResult(424, $"no_{resource}", false, null));

    private IReadOnlyDictionary<string, int> Weights()
    {
        var defaults = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["unit_lookup"] = 12, ["unit_search"] = 10, ["rma_list"] = 9, ["rma_detail"] = 8,
            ["claim_list"] = 9, ["claim_detail"] = 8, ["rma_create_receive"] = 7,
            ["claim_intake_adjudication"] = 7, ["warranty_replacement_create"] = 5,
            ["refurbishment_append"] = 8, ["document_edit"] = 12, ["background_recheck"] = 5,
        };
        if (string.IsNullOrWhiteSpace(options.Weights)) return defaults;
        var parsed = options.Weights.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2, StringSplitOptions.TrimEntries))
            .ToDictionary(part => part[0], part => int.Parse(part[1], System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal);
        if (parsed.Count == 0 || parsed.Values.Any(value => value <= 0)) throw new ArgumentException("--weights uses operation=positive_integer pairs");
        return parsed;
    }

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

    private sealed record ResponseResult(int StatusCode, string ErrorType, bool Success, JsonDocument? Json);
    private sealed record SuccessfulEdit(DateTime UpdatedAt, string ReasonNote);
    private sealed record ResourcePool(List<(Guid Id, string Label)> Units, List<Guid> Rmas, List<Guid> Claims,
        List<Guid> Refurbishments, List<Guid> Campaigns, List<Guid> Entitlements, List<Guid> Replacements);
}
