using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Connections;
using Yojana.Integration.Data;
using Yojana.Integration.Security.Audit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Tenancy;
using Yojana.Relay.Contracts;

namespace Yojana.Integration.Relays;

public sealed class RelayRequestQueue(IntegrationDbContext db, ITenantContext tenant, IClock clock,
    IServiceScopeFactory scopes, IConnectionAuditSink audit, RelayPayloadProtection protection)
{
    public const int BodyLimit = Yojana.Relay.Contracts.RelayLimits.BodyBytes;
    public const int WireLimit = Yojana.Relay.Contracts.RelayLimits.WireBytes;

    public async Task<HttpResponseMessage> SendAsync(Guid connectionId, HttpRequestMessage request,
        int timeoutSeconds, int queueCapacity, RelayDeclaration declaration, CancellationToken ct)
    {
        var body = request.Content is null ? [] : await ReadBoundedAsync(await request.Content.ReadAsStreamAsync(ct), BodyLimit, ct);
        var headers = request.Headers.Concat(request.Content?.Headers.AsEnumerable() ?? [])
            .Where(pair => declaration.RequestHeaders.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        var id = Guid.CreateVersion7(clock.UtcNow);
        var work = new RelayWork(id, Guid.Empty, request.Method.Method, request.RequestUri!.AbsolutePath,
            request.RequestUri.Query, headers, body, clock.UtcNow.AddSeconds(timeoutSeconds));
        // This durable hand-off must commit independently of a connector's business transaction. Holding an
        // uncommitted queue insert while awaiting the agent would make the work invisible to every poller.
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(tenant.TenantId);
        var queueDb = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        if (!await queueDb.SiteRelays.AnyAsync(x => x.TenantId == tenant.TenantId && x.ConnectionId == connectionId
            && x.Status == "active", ct)) return Unreachable();
        var queueProtection = scope.ServiceProvider.GetRequiredService<RelayPayloadProtection>();
        var payload = await queueProtection.ProtectAsync(tenant.TenantId, connectionId, id, "request", work, ct);
        await using (var lease = await ConnectionOperationLock.AcquireAsync(queueDb, tenant.TenantId,
                         $"relay-queue:{connectionId:N}", ct))
        {
            if (await queueDb.RelayRequests.CountAsync(x => x.TenantId == tenant.TenantId && x.ConnectionId == connectionId
                && x.CompletedAt == null && x.ExpiresAt > clock.UtcNow, ct) >= queueCapacity) return Unreachable();
            queueDb.RelayRequests.Add(new()
            {
                TenantId = tenant.TenantId, RequestId = id, ConnectionId = connectionId, CreatedAt = clock.UtcNow,
                ExpiresAt = work.ExpiresAtUtc, PayloadJson = payload,
            });
            await queueDb.SaveChangesAsync(ct);
        }
        var timer = Stopwatch.StartNew();
        var waitMilliseconds = 150;
        try
        {
            while (timer.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
            {
                var resultJson = await queueDb.RelayRequests.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                    && x.RequestId == id).Select(x => x.ResultJson).SingleOrDefaultAsync(ct);
                if (resultJson is not null)
                {
                    var result = await queueProtection.UnprotectAsync<RelayResult>(tenant.TenantId, connectionId, id, "result", resultJson, ct);
                    var response = new HttpResponseMessage((HttpStatusCode)result.Status) { Content = new ByteArrayContent(result.Body) };
                    foreach (var pair in result.Headers.Where(pair => declaration.ResponseHeaders.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)))
                        if (!response.Headers.TryAddWithoutValidation(pair.Key, pair.Value))
                            response.Content.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
                    if (result.Status >= 400)
                    {
                        var failure = result.ProtocolVersion >= RelayProtocol.Version
                            ? RemoteFailureClassifier.FromWire(result.FailureCode, result.FailureDetail)
                            : RemoteFailureClassifier.FromWire(null, result.FailureDetail);
                        response.Headers.TryAddWithoutValidation("X-Yojana-Failure-Code", failure.Code);
                        response.Headers.TryAddWithoutValidation("X-Yojana-Failure-Detail", failure.Detail);
                    }
                    return response;
                }
                await Task.Delay(waitMilliseconds, ct);
                waitMilliseconds = Math.Min(1000, waitMilliseconds * 2);
            }
            return Unreachable();
        }
        finally
        {
            // Retain a small tombstone until purge; a late agent result cannot resurrect a timed-out exchange.
            await queueDb.RelayRequests.Where(x => x.TenantId == tenant.TenantId && x.RequestId == id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ExpiresAt, clock.UtcNow)
                    .SetProperty(x => x.PayloadJson, ""), CancellationToken.None);
        }
    }

    public async Task<RelayPollReply> PollAsync(SiteRelay relay, RelayPoll poll, CancellationToken ct)
    {
        if (poll.Capacity is < 1 or > 4 || string.IsNullOrWhiteSpace(poll.AgentVersion) || poll.AgentVersion.Length > 80
            || poll.ErpVersion is { Length: > 160 }
            || string.IsNullOrWhiteSpace(poll.HostName) || poll.HostName.Length > 160)
            throw new InvalidDataException("relay_poll_invalid");
        var first = relay.LastSeenAt is null;
        var changed = relay.AgentVersion is not null && relay.AgentVersion != poll.AgentVersion;
        relay.LastSeenAt = clock.UtcNow;
        relay.AgentVersion = poll.AgentVersion;
        relay.HostName = poll.HostName;
        if (!string.IsNullOrWhiteSpace(poll.ErpVersion)) relay.ErpVersion = poll.ErpVersion;
        await db.SaveChangesAsync(ct);
        if (first || changed)
            await audit.AppendAsync(new(tenant.TenantId, Guid.CreateVersion7(clock.UtcNow), relay.ConnectionId,
                first ? ConnectionAuditEventType.RelayFirstSeen : ConnectionAuditEventType.RelayVersionChanged,
                "system:site-relay", clock.UtcNow, JsonSerializer.Serialize(new { relay.RelayId, poll.AgentVersion })), ct);
        var hold = Stopwatch.StartNew();
        var holdWaitMilliseconds = 250;
        while (hold.Elapsed < TimeSpan.FromSeconds(25))
        {
            List<RelayWork> items;
            await using (var lease = await ConnectionOperationLock.AcquireAsync(db, tenant.TenantId,
                             $"relay-queue:{relay.ConnectionId:N}", ct))
            {
                if (!await db.SiteRelays.AsNoTracking().AnyAsync(x => x.TenantId == tenant.TenantId && x.RelayId == relay.RelayId
                    && x.Status == "active", ct)) return new([]);
                // A lease is issued once. An expired lease is uncertain, so it is never handed to another agent.
                // The existing outbox query-before-create path decides whether a timed-out write may be retried.
                var rows = await db.RelayRequests.Where(x => x.TenantId == tenant.TenantId && x.ConnectionId == relay.ConnectionId
                    && x.LeaseId == null && x.CompletedAt == null && x.ExpiresAt > clock.UtcNow)
                    .OrderBy(x => x.CreatedAt).ThenBy(x => x.RequestId).Take(Math.Clamp(poll.Capacity, 1, 4)).ToListAsync(ct);
                var leased = new List<RelayWork>();
                foreach (var row in rows)
                {
                    row.RelayId = relay.RelayId;
                    row.LeaseId = Guid.NewGuid();
                    row.LeasedUntil = row.ExpiresAt;
                    var work = await protection.UnprotectAsync<RelayWork>(tenant.TenantId, row.ConnectionId, row.RequestId, "request", row.PayloadJson, ct);
                    leased.Add(work with { LeaseId = row.LeaseId.Value });
                }
                await db.SaveChangesAsync(ct);
                items = leased;
            }
            if (items.Count > 0) return new(items);
            await Task.Delay(holdWaitMilliseconds, ct);
            holdWaitMilliseconds = Math.Min(1000, holdWaitMilliseconds * 2);
        }
        return new([]);
    }

    public async Task<RelayReceipt> CompleteAsync(SiteRelay relay, RelayResult result, CancellationToken ct)
    {
        if (result.Body.Length > BodyLimit || result.Status is < 100 or > 599 || result.Headers.Count > 32
            || result.Headers.Any(pair => pair.Key.Length > 160 || pair.Value.Length > 16
                || pair.Value.Any(value => value.Length > 8192 || value.Any(char.IsControl)))
            || result.ProtocolVersion >= RelayProtocol.Version
                && (!RemoteFailureCode.IsKnown(result.FailureCode) && result.Status >= 400
                    || result.FailureDetail is { Length: > RemoteFailureClassifier.DetailLimit }))
            throw new InvalidDataException("relay_result_invalid");
        await using var lease = await ConnectionOperationLock.AcquireAsync(db, tenant.TenantId,
            $"relay-queue:{relay.ConnectionId:N}", ct);
        var row = await db.RelayRequests.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.ConnectionId == relay.ConnectionId
            && x.RelayId == relay.RelayId && x.RequestId == result.RequestId && x.LeaseId == result.LeaseId, ct);
        if (row is null || row.ExpiresAt <= clock.UtcNow) return new(false);
        if (row.CompletedAt is not null) return new(true);
        var resultJson = await protection.ProtectAsync(tenant.TenantId, relay.ConnectionId, result.RequestId, "result", result, ct);
        // Record a denial before making the result observable. A retry may record another denied attempt,
        // identified by the same request id; a completed result must never hide a missing security audit.
        if (result.Denied)
            await audit.AppendAsync(new(tenant.TenantId, Guid.CreateVersion7(clock.UtcNow), relay.ConnectionId,
                ConnectionAuditEventType.RelayPathDenied, "system:site-relay", clock.UtcNow,
                JsonSerializer.Serialize(new { relay.RelayId, result.RequestId })), ct);
        row.CompletedAt = clock.UtcNow;
        row.ResultJson = resultJson;
        row.PayloadJson = "";
        await db.SaveChangesAsync(ct);
        return new(true);
    }

    public static async Task<byte[]> ReadBoundedAsync(Stream source, int maximum, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var count = await source.ReadAsync(buffer, ct);
            if (count == 0) return output.ToArray();
            if (output.Length + count > maximum) throw new InvalidDataException("relay_body_too_large");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
    }

    private static HttpResponseMessage Unreachable() => new(HttpStatusCode.ServiceUnavailable)
    { Content = new StringContent("{\"error\":\"relay_unreachable\"}", System.Text.Encoding.UTF8, "application/json") };
}
