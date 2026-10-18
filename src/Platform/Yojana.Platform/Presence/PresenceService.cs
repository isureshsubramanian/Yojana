using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Presence;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Data.Platform;

namespace Yojana.Platform.Presence;

public sealed class PresenceService(PlatformDbContext db, ITenantContext tenant, IClock clock) : IPresenceService
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
    public const int MaxDocumentTypeLength = 80;
    public const int MaxDocumentIdLength = 160;

    public async Task<IReadOnlyList<PresenceEntry>> HeartbeatAsync(Guid userId, string documentType,
        string documentId, string mode, CancellationToken cancellationToken)
    {
        var key = Validate(documentType, documentId);
        if (!PresenceMode.IsKnown(mode))
            throw new ArgumentException("Presence mode must be 'viewing' or 'editing'.", nameof(mode));

        var tenantId = tenant.TenantId;
        var now = clock.UtcNow;
        // One seekable statement per heartbeat. HOLDLOCK makes two tabs for the same user/document an atomic upsert;
        // the composite primary key confines that lock to one presence key rather than serialising the document.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            MERGE [platform].[document_presence] WITH (HOLDLOCK) AS target
            USING (VALUES ({tenantId.Value}, {userId}, {key.DocumentType}, {key.DocumentId}, {mode}, {now}))
                AS source (tenant_id, user_id, document_type, document_id, mode, heartbeat_at)
            ON target.tenant_id = source.tenant_id
                AND target.user_id = source.user_id
                AND target.document_type = source.document_type
                AND target.document_id = source.document_id
            WHEN MATCHED THEN UPDATE SET
                mode = source.mode,
                since = CASE WHEN target.mode = source.mode THEN target.since ELSE source.heartbeat_at END,
                last_heartbeat = source.heartbeat_at
            WHEN NOT MATCHED THEN INSERT
                (tenant_id, user_id, document_type, document_id, mode, since, last_heartbeat)
                VALUES (source.tenant_id, source.user_id, source.document_type, source.document_id,
                    source.mode, source.heartbeat_at, source.heartbeat_at);
            """, cancellationToken);

        var cutoff = now - Lifetime;
        var others = await db.DocumentPresence.AsNoTracking()
            .Where(p => p.DocumentType == key.DocumentType && p.DocumentId == key.DocumentId
                && p.UserId != userId && p.LastHeartbeat >= cutoff)
            .OrderByDescending(p => p.Mode == PresenceMode.Editing)
            .ThenBy(p => p.Since)
            .Select(p => new { p.UserId, p.Mode, p.Since })
            .ToListAsync(cancellationToken);
        return others.Select(p => new PresenceEntry(p.UserId, p.Mode,
            DateTime.SpecifyKind(p.Since, DateTimeKind.Utc))).ToList();
    }

    public async Task LeaveAsync(Guid userId, string documentType, string documentId,
        CancellationToken cancellationToken)
    {
        var key = Validate(documentType, documentId);
        await db.DocumentPresence
            .Where(p => p.UserId == userId && p.DocumentType == key.DocumentType && p.DocumentId == key.DocumentId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private static (string DocumentType, string DocumentId) Validate(string documentType, string documentId)
    {
        var type = documentType?.Trim() ?? "";
        var id = documentId?.Trim() ?? "";
        if (type.Length is 0 or > MaxDocumentTypeLength)
            throw new ArgumentException($"Document type must be 1–{MaxDocumentTypeLength} characters.", nameof(documentType));
        if (id.Length is 0 or > MaxDocumentIdLength)
            throw new ArgumentException($"Document id must be 1–{MaxDocumentIdLength} characters.", nameof(documentId));
        return (type, id);
    }
}
