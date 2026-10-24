using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Gateway;
using Yojana.Integration.LegacyMigration.Mapping;
using Yojana.Integration.LegacyMigration.Staging;
using Yojana.Platform.Contracts.Privacy;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Privacy;

public sealed class IntegrationPersonalDataHolder(
    IntegrationDbContext db,
    ITenantContext tenant,
    ExternalIdErasureProtector protector) : IPersonalDataHolder
{
    private const int BatchSize = 500;
    public string Store => "integration";

    public async Task<PersonalDataPreview> PreviewAsync(ErasureSubject subject,
        CancellationToken cancellationToken)
    {
        if (subject.Kind != ErasureSubjectKind.Party) return Empty();
        var projection = await db.ReferenceProjections.AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == tenant.TenantId && x.EntityType == "party" && x.ModuleId == subject.SubjectId,
            cancellationToken);
        var maps = await db.ExternalIdMaps.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.EntityType == "party" && x.ModuleId == subject.SubjectId
            && !x.ExternalId.StartsWith("hmac256:")).ToListAsync(cancellationToken);
        var known = subject.KnownValues.Concat(maps.Select(x => x.ExternalId.StartsWith("hmac256:",
                StringComparison.Ordinal) ? null : x.ExternalId))
            .Concat([projection?.Code, projection?.DisplayName]).Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>().ToList();
        if (projection is not null)
        {
            try
            {
                var attributes = JsonSerializer.Deserialize<PartyReferenceAttributes>(projection.AttributesJson,
                    IntegrationContracts.Json);
                if (!string.IsNullOrWhiteSpace(attributes?.TaxId)) known.Add(attributes.TaxId);
            }
            catch (JsonException) { }
        }
        known = known.Distinct(StringComparer.Ordinal).ToList();
        known = known.Concat(await LinkedPartyPayloadValuesAsync(known, cancellationToken))
            .Distinct(StringComparer.Ordinal).ToList();
        var payloads = await CountStoredPayloadsAsync(known, cancellationToken);
        var migration = await CountMigrationAsync(known, cancellationToken);
        return new([
            new("reference_projection", projection is not null && (projection.Code != subject.Code
                || projection.DisplayName != subject.Token || projection.AttributesJson != "{}") ? 1 : 0,
                "name tokenised and tax identifier removed"),
            new("external_id_map", maps.Count, "party code replaced by tenant-keyed HMAC-SHA-256"),
            new("stored_messages", payloads, "personal JSON paths rewritten on exact-value match"),
            new("migration", migration, "staging and plan party names and codes cleared"),
        ], [], known);
    }

    public async Task<PersonalDataEraseResult> EraseAsync(ErasureSubject subject, Guid erasureId,
        CancellationToken cancellationToken)
    {
        if (subject.Kind != ErasureSubjectKind.Party) return new([]);
        var projection = await db.ReferenceProjections.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.EntityType == "party" && x.ModuleId == subject.SubjectId, cancellationToken);
        var projectionChanged = projection is not null && (projection.Code != subject.Code
            || projection.DisplayName != subject.Token || projection.AttributesJson != "{}");
        if (projection is not null)
        {
            projection.Code = subject.Code;
            projection.DisplayName = subject.Token;
            projection.AttributesJson = "{}";
        }
        await db.SaveChangesAsync(cancellationToken);
        var maps = await ProtectExternalIdsAsync(subject.SubjectId, cancellationToken);
        var payloads = await RewriteStoredPayloadsAsync(subject, cancellationToken);
        var migration = await ClearMigrationAsync(subject.KnownValues, cancellationToken);
        return new([
            new("reference_projection", projectionChanged ? 1 : 0, "name tokenised and tax identifier removed"),
            new("external_id_map", maps, "party code replaced by tenant-keyed HMAC-SHA-256"),
            new("stored_messages", payloads, "personal JSON paths rewritten on exact-value match"),
            new("migration", migration, "staging and plan party names and codes cleared"),
        ]);
    }

    private async Task<int> ProtectExternalIdsAsync(Guid subjectId, CancellationToken cancellationToken)
    {
        var changed = 0;
        var cursor = Guid.Empty;
        while (true)
        {
            var rows = await db.ExternalIdMaps.Where(x => x.TenantId == tenant.TenantId
                    && x.EntityType == "party" && x.ModuleId == subjectId
                    && !x.ExternalId.StartsWith("hmac256:") && x.ExternalIdMapId.CompareTo(cursor) > 0)
                .OrderBy(x => x.ExternalIdMapId).Take(BatchSize).ToListAsync(cancellationToken);
            if (rows.Count == 0) break;
            foreach (var row in rows)
                row.ExternalId = await protector.ProtectAsync(row.ExternalId, cancellationToken);
            cursor = rows[^1].ExternalIdMapId;
            changed += rows.Count;
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }
        return changed;
    }

    private async Task<int> RewriteStoredPayloadsAsync(ErasureSubject subject,
        CancellationToken cancellationToken)
    {
        var values = Values(subject.KnownValues);
        if (values.Count == 0) return 0;
        var changed = 0;
        var inboundPredicate = PersonalDataQuery.ContainsAny<InboundMessage>(values,
            x => x.RawPayload, x => x.CanonicalMessage);
        var inboundCursor = Guid.Empty;
        while (true)
        {
            var rows = await db.InboundMessages.Where(x => x.TenantId == tenant.TenantId
                    && x.InboundMessageId.CompareTo(inboundCursor) > 0).Where(inboundPredicate)
                .OrderBy(x => x.InboundMessageId).Take(BatchSize).ToListAsync(cancellationToken);
            if (rows.Count == 0) break;
            foreach (var row in rows)
            {
                var raw = Rewrite(row.RawPayload, subject); row.RawPayload = raw.Json; changed += raw.Changed;
                var canonical = Rewrite(row.CanonicalMessage, subject); row.CanonicalMessage = canonical.Json;
                changed += canonical.Changed;
            }
            inboundCursor = rows[^1].InboundMessageId;
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }

        var documentPredicate = PersonalDataQuery.ContainsAny<CanonicalDocument>(values, x => x.Payload);
        var documentCursor = Guid.Empty;
        while (true)
        {
            var rows = await db.CanonicalDocuments.Where(x => x.TenantId == tenant.TenantId
                    && x.DocumentId.CompareTo(documentCursor) > 0).Where(documentPredicate)
                .OrderBy(x => x.DocumentId).Take(BatchSize).ToListAsync(cancellationToken);
            if (rows.Count == 0) break;
            foreach (var row in rows)
            {
                var result = Rewrite(row.Payload, subject); row.Payload = result.Json; changed += result.Changed;
            }
            documentCursor = rows[^1].DocumentId;
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }

        var letterPredicate = PersonalDataQuery.ContainsAny<DeadLetterItem>(values,
            x => x.RawPayload, x => x.CanonicalAttempt);
        var letterCursor = Guid.Empty;
        while (true)
        {
            var rows = await db.DeadLetterItems.Where(x => x.TenantId == tenant.TenantId
                    && x.DeadLetterItemId.CompareTo(letterCursor) > 0).Where(letterPredicate)
                .OrderBy(x => x.DeadLetterItemId).Take(BatchSize).ToListAsync(cancellationToken);
            if (rows.Count == 0) break;
            foreach (var row in rows)
            {
                var raw = Rewrite(row.RawPayload, subject); row.RawPayload = raw.Json; changed += raw.Changed;
                var canonical = Rewrite(row.CanonicalAttempt, subject); row.CanonicalAttempt = canonical.Json;
                changed += canonical.Changed;
            }
            letterCursor = rows[^1].DeadLetterItemId;
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }

        var outboundPredicate = PersonalDataQuery.ContainsAny<OutboundCommandRow>(values, x => x.Payload);
        var outboundCursor = Guid.Empty;
        while (true)
        {
            var rows = await db.OutboundCommands.Where(x => x.TenantId == tenant.TenantId
                    && x.OutboundCommandId.CompareTo(outboundCursor) > 0).Where(outboundPredicate)
                .OrderBy(x => x.OutboundCommandId).Take(BatchSize).ToListAsync(cancellationToken);
            if (rows.Count == 0) break;
            foreach (var row in rows)
            {
                var result = Rewrite(row.Payload, subject); row.Payload = result.Json; changed += result.Changed;
            }
            outboundCursor = rows[^1].OutboundCommandId;
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }
        return changed;
    }

    private async Task<int> CountStoredPayloadsAsync(List<string> values,
        CancellationToken cancellationToken)
    {
        if (values.Count == 0) return 0;
        var count = 0;
        var inboundPredicate = PersonalDataQuery.ContainsAny<InboundMessage>(values,
            x => x.RawPayload, x => x.CanonicalMessage);
        var inboundCursor = Guid.Empty;
        while (true)
        {
            var rows = await db.InboundMessages.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                    && x.InboundMessageId.CompareTo(inboundCursor) > 0).Where(inboundPredicate)
                .OrderBy(x => x.InboundMessageId).Take(BatchSize)
                .Select(x => new { x.InboundMessageId, x.RawPayload, x.CanonicalMessage })
                .ToListAsync(cancellationToken);
            if (rows.Count == 0) break;
            count += rows.Sum(x => (ContainsPersonalPath(x.RawPayload, values) ? 1 : 0)
                + (ContainsPersonalPath(x.CanonicalMessage, values) ? 1 : 0));
            inboundCursor = rows[^1].InboundMessageId;
        }

        var documentPredicate = PersonalDataQuery.ContainsAny<CanonicalDocument>(values, x => x.Payload);
        var documentCursor = Guid.Empty;
        while (true)
        {
            var rows = await db.CanonicalDocuments.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                    && x.DocumentId.CompareTo(documentCursor) > 0).Where(documentPredicate)
                .OrderBy(x => x.DocumentId).Take(BatchSize)
                .Select(x => new { x.DocumentId, x.Payload }).ToListAsync(cancellationToken);
            if (rows.Count == 0) break;
            count += rows.Count(x => ContainsPersonalPath(x.Payload, values));
            documentCursor = rows[^1].DocumentId;
        }

        var letterPredicate = PersonalDataQuery.ContainsAny<DeadLetterItem>(values,
            x => x.RawPayload, x => x.CanonicalAttempt);
        var letterCursor = Guid.Empty;
        while (true)
        {
            var rows = await db.DeadLetterItems.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                    && x.DeadLetterItemId.CompareTo(letterCursor) > 0).Where(letterPredicate)
                .OrderBy(x => x.DeadLetterItemId).Take(BatchSize)
                .Select(x => new { x.DeadLetterItemId, x.RawPayload, x.CanonicalAttempt })
                .ToListAsync(cancellationToken);
            if (rows.Count == 0) break;
            count += rows.Sum(x => (ContainsPersonalPath(x.RawPayload, values) ? 1 : 0)
                + (ContainsPersonalPath(x.CanonicalAttempt, values) ? 1 : 0));
            letterCursor = rows[^1].DeadLetterItemId;
        }

        var outboundPredicate = PersonalDataQuery.ContainsAny<OutboundCommandRow>(values, x => x.Payload);
        var outboundCursor = Guid.Empty;
        while (true)
        {
            var rows = await db.OutboundCommands.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                    && x.OutboundCommandId.CompareTo(outboundCursor) > 0).Where(outboundPredicate)
                .OrderBy(x => x.OutboundCommandId).Take(BatchSize)
                .Select(x => new { x.OutboundCommandId, x.Payload }).ToListAsync(cancellationToken);
            if (rows.Count == 0) break;
            count += rows.Count(x => ContainsPersonalPath(x.Payload, values));
            outboundCursor = rows[^1].OutboundCommandId;
        }
        return count;
    }

    private async Task<IReadOnlyList<string>> LinkedPartyPayloadValuesAsync(List<string> anchors,
        CancellationToken cancellationToken)
    {
        if (anchors.Count == 0) return [];
        var values = new HashSet<string>(StringComparer.Ordinal);
        var inboundPredicate = PersonalDataQuery.ContainsAny<InboundMessage>(anchors,
            x => x.RawPayload, x => x.CanonicalMessage);
        var inboundCursor = Guid.Empty;
        while (true)
        {
            var rows = await db.InboundMessages.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                    && (x.ErpEntity == "party" || x.MessageType == "PartyUpserted")
                    && x.InboundMessageId.CompareTo(inboundCursor) > 0).Where(inboundPredicate)
                .OrderBy(x => x.InboundMessageId).Take(BatchSize)
                .Select(x => new { x.InboundMessageId, x.RawPayload, x.CanonicalMessage })
                .ToListAsync(cancellationToken);
            if (rows.Count == 0) break;
            foreach (var json in rows.SelectMany(x => new[] { x.RawPayload, x.CanonicalMessage }))
            {
                try
                {
                    var node = JsonNode.Parse(json);
                    if (node is not null && ContainsNode(node, anchors)) CollectPersonalValues(node, values);
                }
                catch (JsonException) { }
            }
            inboundCursor = rows[^1].InboundMessageId;
        }

        var letterPredicate = PersonalDataQuery.ContainsAny<DeadLetterItem>(anchors,
            x => x.RawPayload, x => x.CanonicalAttempt);
        var letterCursor = Guid.Empty;
        while (true)
        {
            var rows = await db.DeadLetterItems.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                    && x.RecordType == "PartyUpserted" && x.DeadLetterItemId.CompareTo(letterCursor) > 0)
                .Where(letterPredicate).OrderBy(x => x.DeadLetterItemId).Take(BatchSize)
                .Select(x => new { x.DeadLetterItemId, x.RawPayload, x.CanonicalAttempt })
                .ToListAsync(cancellationToken);
            if (rows.Count == 0) break;
            foreach (var json in rows.SelectMany(x => new[] { x.RawPayload, x.CanonicalAttempt }))
            {
                try
                {
                    var node = JsonNode.Parse(json);
                    if (node is not null && ContainsNode(node, anchors)) CollectPersonalValues(node, values);
                }
                catch (JsonException) { }
            }
            letterCursor = rows[^1].DeadLetterItemId;
        }
        return values.ToList();
    }

    private static void CollectPersonalValues(JsonNode node, ISet<string> values)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                if (pair.Value is JsonValue value && IsPersonalPath(pair.Key)
                    && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                    values.Add(text);
                else if (pair.Value is not null) CollectPersonalValues(pair.Value, values);
            }
        }
        else if (node is JsonArray array)
            foreach (var child in array.Where(x => x is not null)) CollectPersonalValues(child!, values);
    }

    private async Task<int> CountMigrationAsync(List<string> values,
        CancellationToken cancellationToken)
    {
        if (values.Count == 0) return 0;
        return await db.MigrationPlanOwnership.AsNoTracking().CountAsync(x => x.TenantId == tenant.TenantId
                && (values.Contains(x.PartyCode!) || values.Contains(x.PartyName!)), cancellationToken)
            + await db.Set<TransactionLogHeaderStagingRow>().AsNoTracking().CountAsync(x =>
                x.TenantId == tenant.TenantId && values.Contains(x.PartyCode!), cancellationToken)
            + await db.Set<MarketingDocumentHeaderStagingRow>().AsNoTracking().CountAsync(x =>
                x.TenantId == tenant.TenantId && values.Contains(x.PartyCode!), cancellationToken);
    }

    private async Task<int> ClearMigrationAsync(IReadOnlyList<string> values,
        CancellationToken cancellationToken)
    {
        if (values.Count == 0) return 0;
        return await db.MigrationPlanOwnership.Where(x => x.TenantId == tenant.TenantId
                && (values.Contains(x.PartyCode!) || values.Contains(x.PartyName!)))
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.PartyCode, (string?)null)
                    .SetProperty(x => x.PartyName, (string?)null).SetProperty(x => x.Pseudonymised, true),
                    cancellationToken)
            + await db.Set<TransactionLogHeaderStagingRow>().Where(x => x.TenantId == tenant.TenantId
                    && values.Contains(x.PartyCode!))
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.PartyCode, (string?)null), cancellationToken)
            + await db.Set<MarketingDocumentHeaderStagingRow>().Where(x => x.TenantId == tenant.TenantId
                    && values.Contains(x.PartyCode!))
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.PartyCode, (string?)null), cancellationToken);
    }

    private static (string Json, int Changed) Rewrite(string json, ErasureSubject subject)
    {
        try
        {
            var node = JsonNode.Parse(json);
            if (node is null) return (json, 0);
            var count = RewriteNode(node, null, subject);
            return count > 0
                ? (node.ToJsonString(new JsonSerializerOptions { WriteIndented = false }), 1)
                : (json, 0);
        }
        catch (JsonException) { return (json, 0); }
    }

    private static int RewriteNode(JsonNode node, string? propertyName, ErasureSubject subject)
    {
        var count = 0;
        if (node is JsonObject obj)
        {
            foreach (var pair in obj.ToList())
            {
                if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text)
                    && IsPersonalPath(pair.Key) && subject.KnownValues.Contains(text, StringComparer.Ordinal))
                {
                    obj[pair.Key] = pair.Key.Contains("code", StringComparison.OrdinalIgnoreCase)
                        || pair.Key.Contains("external_id", StringComparison.OrdinalIgnoreCase)
                        ? subject.Code : pair.Key.Contains("email", StringComparison.OrdinalIgnoreCase)
                            ? subject.Email : subject.Token;
                    count++;
                }
                else if (pair.Value is not null) count += RewriteNode(pair.Value, pair.Key, subject);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.Where(x => x is not null)) count += RewriteNode(child!, propertyName, subject);
        }
        return count;
    }

    private static bool ContainsPersonalPath(string json, IReadOnlyList<string> values)
    {
        try
        {
            var node = JsonNode.Parse(json);
            return node is not null && ContainsNode(node, values);
        }
        catch (JsonException) { return false; }
    }

    private static bool ContainsNode(JsonNode node, IReadOnlyList<string> values)
    {
        if (node is JsonObject obj)
            return obj.Any(pair => pair.Value is JsonValue value && value.TryGetValue<string>(out var text)
                    && IsPersonalPath(pair.Key) && values.Contains(text, StringComparer.Ordinal)
                || pair.Value is not null && ContainsNode(pair.Value, values));
        return node is JsonArray array && array.Any(child => child is not null && ContainsNode(child, values));
    }

    private static bool IsPersonalPath(string name) => name.Contains("name", StringComparison.OrdinalIgnoreCase)
        || name.Contains("code", StringComparison.OrdinalIgnoreCase)
        || name.Contains("email", StringComparison.OrdinalIgnoreCase)
        || name.Contains("phone", StringComparison.OrdinalIgnoreCase)
        || name.Contains("contact", StringComparison.OrdinalIgnoreCase)
        || name.Contains("tax", StringComparison.OrdinalIgnoreCase)
        || name.Contains("address", StringComparison.OrdinalIgnoreCase)
        || name.Contains("external_id", StringComparison.OrdinalIgnoreCase);

    private static List<string> Values(IEnumerable<string> values) => values
        .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToList();

    private static PersonalDataPreview Empty() => new([], [], []);
}
