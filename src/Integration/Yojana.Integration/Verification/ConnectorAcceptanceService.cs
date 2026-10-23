using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Contracts;
using Yojana.Integration.Connectors;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Gateway;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.Verification;

public sealed class ConnectorAcceptanceService(
    IntegrationDbContext db,
    ITenantContext tenant,
    IClock clock,
    IUserDirectory people,
    ConnectorVerificationManifestStore manifests,
    IServiceProvider services)
{
    public async Task<ConnectorVerificationView> OverviewAsync(Guid connectionId,
        CancellationToken cancellationToken)
    {
        var connection = await ConnectionAsync(connectionId, cancellationToken);
        var manifest = manifests.Get(connection.ConnectorCode);
        var capabilities = await CapabilitiesAsync(connectionId, cancellationToken);
        var runs = await db.ConnectorAcceptanceRuns.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && row.ConnectionId == connectionId)
            .OrderByDescending(row => row.StartedAt).ThenByDescending(row => row.ConnectorAcceptanceRunId)
            .Take(25).ToListAsync(cancellationToken);
        var runIds = runs.Select(row => row.ConnectorAcceptanceRunId).ToList();
        var items = await db.ConnectorAcceptanceItems.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && runIds.Contains(row.ConnectorAcceptanceRunId))
            .ToListAsync(cancellationToken);
        var actors = runs.SelectMany(run => new[] { run.StartedBy, run.CompletedBy }).OfType<string>()
            .Distinct().ToList();
        var names = actors.Count == 0 ? new Dictionary<string, string>()
            : new Dictionary<string, string>(await people.DisplayNamesAsync(actors, cancellationToken));
        var summaries = runs.Select(run =>
        {
            var runItems = items.Where(item => item.ConnectorAcceptanceRunId == run.ConnectorAcceptanceRunId).ToList();
            return new AcceptanceRunSummary(run.ConnectorAcceptanceRunId, run.SandboxLabel, run.Status,
                Name(run.StartedBy, names), run.StartedAt,
                run.CompletedBy is null ? null : Name(run.CompletedBy, names), run.CompletedAt,
                runItems.Count(item => item.Result == ConnectorAcceptanceItemResult.Passed),
                runItems.Count(item => item.Result == ConnectorAcceptanceItemResult.Failed),
                runItems.Count(item => item.Result == ConnectorAcceptanceItemResult.NotApplicable),
                runItems.Count(item => item.Result == ConnectorAcceptanceItemResult.Pending));
        }).ToList();
        var applicable = ConnectorAcceptanceScenarioCatalogue.All.Count(scenario =>
            scenario.RequiredCapabilities.All(capability => capabilities.GetValueOrDefault(capability)
                is not null and not CapabilitySupport.Unsupported));
        return new(connectionId, connection.DisplayLabel, connection.ConnectorCode, manifest.LiveVerification,
            runs.Any(run => run.Status == ConnectorAcceptanceRunStatus.Passed),
            manifest.Facts.Count(fact => fact.Kind == ConnectorVerificationKind.Documented),
            manifest.Facts.Count(fact => fact.Kind == ConnectorVerificationKind.Inferred),
            manifest.Facts.Count(fact => fact.Kind == ConnectorVerificationKind.Assumed),
            ConnectorAcceptanceScenarioCatalogue.All.Count, applicable,
            manifest.Facts.Select(fact => new VerificationFactView(fact.Id, fact.Fact, fact.ValueUsed,
                fact.Kind, fact.SourceLabel, fact.DateRead, fact.UsedAt, fact.AcceptanceItem)).ToList(), summaries,
            services.GetKeyedService<IConnectionLifecycleAdapter>(connection.ConnectorCode)?.Features
                ?? ConnectionFeatures.None);
    }

    public async Task<AcceptanceRunView> StartAsync(Guid connectionId, StartAcceptanceRunRequest request,
        string actor, CancellationToken cancellationToken)
    {
        RequireActor(actor);
        if (string.IsNullOrWhiteSpace(request.SandboxLabel) || request.SandboxLabel.Trim().Length > 160)
            throw Rule("acceptance_sandbox_label_required",
                "Give the customer sandbox a non-secret label of at most 160 characters.");
        var connection = await ConnectionAsync(connectionId, cancellationToken);
        var manifest = manifests.Get(connection.ConnectorCode);
        if (await db.ConnectorAcceptanceRuns.AnyAsync(row => row.TenantId == tenant.TenantId
                && row.ConnectionId == connectionId && row.Status == ConnectorAcceptanceRunStatus.Open,
            cancellationToken))
            throw Rule("acceptance_run_already_open", "Complete the open acceptance run before starting another.");
        var now = clock.UtcNow;
        var runId = TimeOrderedId.New(now);
        db.ConnectorAcceptanceRuns.Add(new()
        {
            TenantId = tenant.TenantId, ConnectorAcceptanceRunId = runId, ConnectionId = connectionId,
            ConnectorCode = connection.ConnectorCode, SandboxLabel = request.SandboxLabel.Trim(),
            Status = ConnectorAcceptanceRunStatus.Open, StartedBy = actor, StartedAt = now,
        });
        foreach (var fact in manifest.Facts.Where(fact => fact.AcceptanceItem))
            AddItem(runId, $"fact:{fact.Id}", ConnectorAcceptanceItemKind.Fact, fact.Fact,
                ConnectorAcceptanceItemResult.Pending, null);
        var capabilities = await CapabilitiesAsync(connectionId, cancellationToken);
        foreach (var scenario in ConnectorAcceptanceScenarioCatalogue.All)
        {
            var missing = scenario.RequiredCapabilities.Where(capability => capabilities.GetValueOrDefault(capability)
                is null or CapabilitySupport.Unsupported).ToList();
            AddItem(runId, $"scenario:{scenario.Id}", ConnectorAcceptanceItemKind.EdgeScenario,
                scenario.Description, missing.Count == 0 ? ConnectorAcceptanceItemResult.Pending
                    : ConnectorAcceptanceItemResult.NotApplicable,
                missing.Count == 0 ? null : $"The connector does not declare: {string.Join(", ", missing)}.",
                missing.Count == 0 ? null : "system:capability-catalogue", missing.Count == 0 ? null : now);
        }
        foreach (var capability in capabilities.Where(pair => OutboundCapabilityTypes.All.Contains(pair.Key)
                     && pair.Value != CapabilitySupport.Unsupported).OrderBy(pair => pair.Key))
            AddItem(runId, $"outbound:{capability.Key}", ConnectorAcceptanceItemKind.OutboundCommand,
                $"Run {capability.Key} against the customer sandbox and re-read the resulting record.",
                ConnectorAcceptanceItemResult.Pending, null);
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(connectionId, runId, cancellationToken);
    }

    public async Task<AcceptanceRunView> MarkAsync(Guid connectionId, Guid runId, Guid itemId,
        MarkAcceptanceItemRequest request, string actor, CancellationToken cancellationToken)
    {
        RequireActor(actor);
        ValidateText(request.Note, 2000, "acceptance_note_too_long", "The note may contain at most 2,000 characters.");
        ValidateText(request.Evidence, 4000, "acceptance_evidence_too_long",
            "The evidence may contain at most 4,000 characters.");
        if (request.Result is not (ConnectorAcceptanceItemResult.Passed or ConnectorAcceptanceItemResult.Failed
                or ConnectorAcceptanceItemResult.NotApplicable))
            throw Rule("acceptance_item_result_invalid", "Choose passed, failed or not applicable.");
        if (request.Result == ConnectorAcceptanceItemResult.NotApplicable && string.IsNullOrWhiteSpace(request.Note))
            throw Rule("acceptance_not_applicable_reason_required",
                "A not-applicable item needs a reason.");
        var run = await OpenRunAsync(connectionId, runId, cancellationToken);
        var item = await db.ConnectorAcceptanceItems.SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.ConnectorAcceptanceRunId == run.ConnectorAcceptanceRunId
            && row.ConnectorAcceptanceItemId == itemId, cancellationToken)
            ?? throw Rule("acceptance_item_not_found", "The acceptance checklist item was not found.");
        if (item.Result == ConnectorAcceptanceItemResult.Failed
            && request.Result != ConnectorAcceptanceItemResult.Failed)
            throw Rule("acceptance_failed_item_is_final",
                "A failed item remains failed in this run; start a new run after correcting the connector or sandbox.");
        ApplyResult(item, request.Result, request.Note, request.Evidence, actor, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(connectionId, runId, cancellationToken);
    }

    public async Task<ImportEdgeResultsResult> ImportAsync(Guid connectionId, Guid runId,
        ImportEdgeResultsRequest request, string actor, CancellationToken cancellationToken)
    {
        RequireActor(actor);
        var run = await OpenRunAsync(connectionId, runId, cancellationToken);
        EdgeResultFile file;
        try
        {
            file = request.ResultFile.Deserialize<EdgeResultFile>(IntegrationContracts.Json)
                ?? throw new JsonException("The result file was empty.");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
                                          or NotSupportedException)
        {
            throw Rule("acceptance_import_shape_invalid", $"The edge result file is invalid: {exception.Message}");
        }
        ValidateImport(file, run.ConnectorCode);
        var expectedHash = EdgeResultFileHash.Compute(file.Connector, file.GeneratedAt, file.Results);
        if (!string.Equals(expectedHash, file.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw Rule("acceptance_import_hash_invalid", "The edge result file content hash does not match its contents.");
        var hash = expectedHash.ToLowerInvariant();
        if (await db.ConnectorAcceptanceImports.AnyAsync(row => row.TenantId == tenant.TenantId
            && row.ContentHash == hash, cancellationToken))
            throw Rule("acceptance_import_replayed", "This edge result file has already been imported.");
        var rows = await db.ConnectorAcceptanceItems.Where(row => row.TenantId == tenant.TenantId
                && row.ConnectorAcceptanceRunId == runId && row.Kind == ConnectorAcceptanceItemKind.EdgeScenario)
            .ToDictionaryAsync(row => row.Code, cancellationToken);
        foreach (var result in file.Results)
        {
            var code = $"scenario:{result.ScenarioId}";
            if (!rows.TryGetValue(code, out var item))
                throw Rule("acceptance_import_scenario_unknown",
                    $"Scenario '{result.ScenarioId}' is not in this run's checklist.");
            var mapped = result.Result switch
            {
                "passed" => ConnectorAcceptanceItemResult.Passed,
                "failed" => ConnectorAcceptanceItemResult.Failed,
                "skipped" => ConnectorAcceptanceItemResult.NotApplicable,
                _ => throw Rule("acceptance_import_result_invalid",
                    $"Scenario '{result.ScenarioId}' has an invalid result."),
            };
            if (item.Result == ConnectorAcceptanceItemResult.Failed
                && mapped != ConnectorAcceptanceItemResult.Failed)
                continue;
            ApplyResult(item, mapped, result.SkipReason,
                $"Live edge harness: {result.StartedAt:O} to {result.CompletedAt:O}", actor, clock.UtcNow);
        }
        db.ConnectorAcceptanceImports.Add(new()
        {
            TenantId = tenant.TenantId,
            ConnectorAcceptanceImportId = TimeOrderedId.New(clock.UtcNow),
            ConnectorAcceptanceRunId = runId,
            ContentHash = hash,
            ConnectorCode = file.Connector,
            ScenarioCount = file.Results.Count,
            ImportedBy = actor,
            ImportedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
        return new(runId, file.Results.Count, hash);
    }

    public async Task<AcceptanceRunView> CompleteAsync(Guid connectionId, Guid runId, string actor,
        CancellationToken cancellationToken)
    {
        RequireActor(actor);
        var run = await OpenRunAsync(connectionId, runId, cancellationToken);
        var items = await db.ConnectorAcceptanceItems.Where(row => row.TenantId == tenant.TenantId
            && row.ConnectorAcceptanceRunId == runId).ToListAsync(cancellationToken);
        if (items.Count == 0 || items.Any(item => item.Result == ConnectorAcceptanceItemResult.Pending))
            throw Rule("acceptance_items_incomplete", "Mark every checklist item before completing the run.");
        if (items.Any(item => item.Result == ConnectorAcceptanceItemResult.NotApplicable
                && string.IsNullOrWhiteSpace(item.Note)))
            throw Rule("acceptance_not_applicable_reason_required",
                "Every not-applicable item needs a reason.");
        run.Status = items.Any(item => item.Result == ConnectorAcceptanceItemResult.Failed)
            ? ConnectorAcceptanceRunStatus.Failed : ConnectorAcceptanceRunStatus.Passed;
        run.CompletedBy = actor;
        run.CompletedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(connectionId, runId, cancellationToken);
    }

    public async Task<AcceptanceRunView> DetailAsync(Guid connectionId, Guid runId,
        CancellationToken cancellationToken)
    {
        var connection = await ConnectionAsync(connectionId, cancellationToken);
        var run = await db.ConnectorAcceptanceRuns.AsNoTracking().SingleOrDefaultAsync(row =>
            row.TenantId == tenant.TenantId && row.ConnectionId == connectionId
            && row.ConnectorAcceptanceRunId == runId, cancellationToken)
            ?? throw Rule("acceptance_run_not_found", "The acceptance run was not found.");
        var items = await db.ConnectorAcceptanceItems.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.ConnectorAcceptanceRunId == runId).OrderBy(row => row.Kind).ThenBy(row => row.Code)
            .ToListAsync(cancellationToken);
        var actors = items.Select(row => row.MarkedBy).Append(run.StartedBy).Append(run.CompletedBy)
            .OfType<string>().Distinct().ToList();
        var names = new Dictionary<string, string>(await people.DisplayNamesAsync(actors, cancellationToken));
        return new(runId, connectionId, connection.DisplayLabel, run.ConnectorCode, run.SandboxLabel, run.Status,
            Name(run.StartedBy, names), run.StartedAt,
            run.CompletedBy is null ? null : Name(run.CompletedBy, names), run.CompletedAt,
            items.Select(item => new AcceptanceItemView(item.ConnectorAcceptanceItemId, item.Code, item.Kind,
                item.Label, item.Result, item.Note, item.Evidence,
                item.MarkedBy is null ? null : Name(item.MarkedBy, names), item.MarkedAt)).ToList());
    }

    public Task<bool> PassedAsync(Guid connectionId, CancellationToken cancellationToken) =>
        db.ConnectorAcceptanceRuns.AsNoTracking().AnyAsync(row => row.TenantId == tenant.TenantId
            && row.ConnectionId == connectionId && row.Status == ConnectorAcceptanceRunStatus.Passed,
            cancellationToken);

    private async Task<Connection> ConnectionAsync(Guid connectionId, CancellationToken cancellationToken) =>
        await db.Connections.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.ConnectionId == connectionId, cancellationToken)
        ?? throw Rule("connection_not_found", "The connection was not found.");

    private async Task<ConnectorAcceptanceRun> OpenRunAsync(Guid connectionId, Guid runId,
        CancellationToken cancellationToken) => await db.ConnectorAcceptanceRuns.SingleOrDefaultAsync(row =>
            row.TenantId == tenant.TenantId && row.ConnectionId == connectionId
            && row.ConnectorAcceptanceRunId == runId && row.Status == ConnectorAcceptanceRunStatus.Open,
            cancellationToken) ?? throw Rule("acceptance_open_run_not_found", "The open acceptance run was not found.");

    private async Task<Dictionary<string, string>> CapabilitiesAsync(Guid connectionId,
        CancellationToken cancellationToken) => await db.ConnectionCapabilities.AsNoTracking()
        .Where(row => row.TenantId == tenant.TenantId && row.ConnectionId == connectionId)
        .ToDictionaryAsync(row => row.CanonicalMessage, row => row.SupportLevel, cancellationToken);

    private void AddItem(Guid runId, string code, string kind, string label, string result,
        string? note, string? markedBy = null, DateTime? markedAt = null) => db.ConnectorAcceptanceItems.Add(new()
    {
        TenantId = tenant.TenantId,
        ConnectorAcceptanceItemId = TimeOrderedId.New(clock.UtcNow),
        ConnectorAcceptanceRunId = runId,
        Code = code,
        Kind = kind,
        Label = label,
        Result = result,
        Note = note,
        MarkedBy = markedBy,
        MarkedAt = markedAt,
    });

    private static void ApplyResult(ConnectorAcceptanceItem item, string result, string? note,
        string? evidence, string actor, DateTime at)
    {
        item.Result = result;
        item.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        item.Evidence = string.IsNullOrWhiteSpace(evidence) ? null : evidence.Trim();
        item.MarkedBy = actor;
        item.MarkedAt = at;
    }

    private static void ValidateImport(EdgeResultFile file, string connectorCode)
    {
        if (!string.Equals(file.Connector, connectorCode, StringComparison.Ordinal))
            throw Rule("acceptance_import_connector_mismatch",
                "The edge result file belongs to a different connector.");
        if (file.Results is null || file.Results.Count is < 1 or > 100
            || file.Results.Any(result => result is null)
            || file.Results.Select(result => result.ScenarioId)
                .Distinct(StringComparer.Ordinal).Count() != file.Results.Count)
            throw Rule("acceptance_import_shape_invalid", "The edge result file needs 1 to 100 unique scenarios.");
        foreach (var result in file.Results)
        {
            if (result.CompletedAt < result.StartedAt || string.IsNullOrWhiteSpace(result.ScenarioId)
                || result.ScenarioId.Length > 100)
                throw Rule("acceptance_import_shape_invalid", "An edge result has invalid timestamps or scenario id.");
            if (result.Result == "skipped" && string.IsNullOrWhiteSpace(result.SkipReason))
                throw Rule("acceptance_import_skip_reason_required", "A skipped edge scenario needs a reason.");
            if (result.Result is not ("passed" or "failed" or "skipped"))
                throw Rule("acceptance_import_result_invalid",
                    $"Scenario '{result.ScenarioId}' has an invalid result.");
            if (result.Result != "skipped" && result.SkipReason is not null)
                throw Rule("acceptance_import_shape_invalid", "Only skipped edge scenarios may carry a skip reason.");
        }
    }

    private static void RequireActor(string actor)
    {
        if (string.IsNullOrWhiteSpace(actor))
            throw Rule("audit_actor_required", "The authenticated user has no actor id.");
    }

    private static void ValidateText(string? value, int maximum, string rule, string message)
    {
        if (value?.Length > maximum) throw Rule(rule, message);
    }

    private static string Name(string actor, IReadOnlyDictionary<string, string> names) =>
        names.GetValueOrDefault(actor) ?? (actor.StartsWith("system:", StringComparison.Ordinal)
            ? "System" : "Former or unknown user");

    private static ConnectorAcceptanceRuleException Rule(string rule, string message) => new(rule, message);
}

public static class EdgeResultFileHash
{
    public static string Compute(string connector, DateTime generatedAt,
        IReadOnlyList<EdgeScenarioResult> results)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            Connector = connector,
            GeneratedAt = generatedAt,
            Results = results.OrderBy(result => result.ScenarioId).Select(result => new
            {
                result.ScenarioId, result.Result, result.SkipReason, result.StartedAt, result.CompletedAt,
            }),
        }, IntegrationContracts.Json);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

public sealed class ConnectorAcceptanceRuleException(string rule, string message)
    : CommandRefusedException(rule, message);
