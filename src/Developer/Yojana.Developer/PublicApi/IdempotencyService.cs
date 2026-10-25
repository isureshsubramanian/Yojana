using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Quartz;
using Yojana.Developer.Data;
using Yojana.Developer.Data.Entities;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Tenancy;
using CommandRefusedException = Yojana.Platform.Contracts.CommandRefusedException;

namespace Yojana.Developer.PublicApi;

public sealed class IdempotencyService(DeveloperDbContext db, ITenantContext tenant, IClock clock)
{
    public const string Header = "Idempotency-Key";
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    public async Task<IResult> ExecuteAsync<TRequest, TResponse>(HttpContext http, Guid clientId,
        string operation, TRequest request, Func<Guid, Task<(int StatusCode, TResponse Response)>> execute,
        CancellationToken cancellationToken)
    {
        var key = http.Request.Headers[Header].ToString();
        if (key.Length is < 1 or > 200 || key.Any(char.IsControl))
            return PublicApiHttp.Problem("idempotency_key_required",
                "Idempotency-Key must contain 1 to 200 printable characters.", StatusCodes.Status400BadRequest);
        var hash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { operation, request },
            PublicApiHttp.Json));
        var stored = await db.IdempotencyRecords.SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.ClientId == clientId && row.IdempotencyKey == key, cancellationToken);
        if (stored is not null && stored.ExpiresAt <= clock.UtcNow)
        {
            db.IdempotencyRecords.Remove(stored);
            await db.SaveChangesAsync(cancellationToken);
            stored = null;
        }
        if (stored is not null)
        {
            if (!CryptographicOperations.FixedTimeEquals(hash, stored.RequestHash))
                return PublicApiHttp.Problem("idempotency_key_reused",
                    "This Idempotency-Key was already used with a different request.");
            http.Response.Headers["Idempotency-Replayed"] = "true";
            return Results.Content(stored.ResponseBody, "application/json; charset=utf-8",
                statusCode: stored.StatusCode);
        }

        // Including the request hash lets a key start a genuinely new command after its 24-hour record expires,
        // while identical concurrent requests still converge on the same underlying command id.
        var commandId = CommandId(tenant.TenantId, clientId, key, hash);
        var result = await execute(commandId);
        var body = JsonSerializer.Serialize(result.Response, PublicApiHttp.Json);
        db.IdempotencyRecords.Add(new ApiIdempotencyRecord
        {
            TenantId = tenant.TenantId,
            ClientId = clientId,
            IdempotencyKey = key,
            RequestHash = hash,
            StatusCode = result.StatusCode,
            ResponseBody = body,
            CreatedAt = clock.UtcNow,
            ExpiresAt = clock.UtcNow.Add(Retention),
        });
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            stored = await db.IdempotencyRecords.AsNoTracking().SingleAsync(row => row.TenantId == tenant.TenantId
                && row.ClientId == clientId && row.IdempotencyKey == key, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(hash, stored.RequestHash))
                return PublicApiHttp.Problem("idempotency_key_reused",
                    "This Idempotency-Key was already used with a different request.");
            http.Response.Headers["Idempotency-Replayed"] = "true";
            return Results.Content(stored.ResponseBody, "application/json; charset=utf-8",
                statusCode: stored.StatusCode);
        }
        return Results.Content(body, "application/json; charset=utf-8", statusCode: result.StatusCode);
    }

    internal static Guid CommandId(TenantId tenantId, Guid clientId, string key, byte[] requestHash)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{tenantId.Value:N}:{clientId:N}:{key}:{Convert.ToHexString(requestHash)}"));
        return new Guid(bytes.AsSpan(0, 16));
    }
}

public sealed class IdempotencyCleanup(DeveloperDbContext db, IClock clock)
{
    public Task<int> DeleteExpiredAsync(CancellationToken cancellationToken) => db.IdempotencyRecords
        .Where(row => row.ExpiresAt <= clock.UtcNow).ExecuteDeleteAsync(cancellationToken);
}

[DisallowConcurrentExecution]
public sealed class IdempotencyCleanupJob(ITenantContextSetter setter, IdempotencyCleanup cleanup)
    : TenantJob(setter)
{
    public const string JobName = "developer-idempotency-cleanup";
    public const string Cron = "0 17 * * * ?";

    protected override Task ExecuteForTenant(IJobExecutionContext context, CancellationToken cancellationToken) =>
        cleanup.DeleteExpiredAsync(cancellationToken);
}
