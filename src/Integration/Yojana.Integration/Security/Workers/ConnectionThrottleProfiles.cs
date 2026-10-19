using System.Collections.Concurrent;

namespace Yojana.Integration.Security.Workers;

public sealed record ConnectionThrottleProfile(
    int? RequestsPerSecond,
    int? RequestsPerMinute,
    int? RequestsPerDay,
    int Burst,
    int MaxConcurrentCalls,
    TimeSpan RetryAfterCeiling,
    int AlertThresholdPercent = 80)
{
    public ConnectionThrottleProfile Validate()
    {
        PositiveOrNull(RequestsPerSecond, nameof(RequestsPerSecond));
        PositiveOrNull(RequestsPerMinute, nameof(RequestsPerMinute));
        PositiveOrNull(RequestsPerDay, nameof(RequestsPerDay));
        if (Burst < 1) throw new ArgumentOutOfRangeException(nameof(Burst), "Burst must be at least one.");
        if (MaxConcurrentCalls < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrentCalls),
                "Maximum concurrent calls must be at least one.");
        if (RetryAfterCeiling <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(RetryAfterCeiling),
                "The Retry-After ceiling must be positive.");
        if (AlertThresholdPercent is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(AlertThresholdPercent),
                "The alert threshold must be between 1 and 100 percent.");
        return this;
    }

    private static void PositiveOrNull(int? value, string name)
    {
        if (value is <= 0) throw new ArgumentOutOfRangeException(name, "A configured cap must be positive.");
    }
}

/// <summary>Connector-owned onboarding data. It supplies a starting point, not a product-wide rule.</summary>
public sealed record ConnectorThrottleDeclaration(string ConnectorCode, ConnectionThrottleProfile DefaultProfile);

public sealed record StoredConnectionThrottleProfile(
    ConnectionWorkerKey WorkerKey,
    string ConnectorCode,
    ConnectionThrottleProfile Profile,
    DateTime UpdatedAtUtc);

public interface IConnectionThrottleProfileStore
{
    Task<StoredConnectionThrottleProfile?> GetAsync(ConnectionWorkerKey workerKey,
        CancellationToken cancellationToken);

    Task<bool> CreateIfMissingAsync(StoredConnectionThrottleProfile profile,
        CancellationToken cancellationToken);

    Task SaveAsync(StoredConnectionThrottleProfile profile, CancellationToken cancellationToken);
}

public sealed class InMemoryConnectionThrottleProfileStore : IConnectionThrottleProfileStore
{
    private readonly ConcurrentDictionary<ConnectionWorkerKey, StoredConnectionThrottleProfile> profiles = new();

    public Task<StoredConnectionThrottleProfile?> GetAsync(ConnectionWorkerKey workerKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(profiles.GetValueOrDefault(workerKey));
    }

    public Task<bool> CreateIfMissingAsync(StoredConnectionThrottleProfile profile,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(profiles.TryAdd(profile.WorkerKey, profile));
    }

    public Task SaveAsync(StoredConnectionThrottleProfile profile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        profiles[profile.WorkerKey] = profile;
        return Task.CompletedTask;
    }
}

public sealed class ConnectionThrottleProfiles(IConnectionThrottleProfileStore store,
    Yojana.Platform.Contracts.Time.IClock clock)
{
    public async Task<StoredConnectionThrottleProfile> OnboardAsync(ConnectionWorkerKey workerKey,
        ConnectorThrottleDeclaration declaration, CancellationToken cancellationToken)
    {
        ValidateKey(workerKey);
        if (string.IsNullOrWhiteSpace(declaration.ConnectorCode))
            throw new ArgumentException("A connector code is required.", nameof(declaration));
        var proposed = new StoredConnectionThrottleProfile(workerKey, declaration.ConnectorCode.Trim(),
            declaration.DefaultProfile.Validate(), clock.UtcNow);
        if (await store.CreateIfMissingAsync(proposed, cancellationToken)) return proposed;
        return await GetAsync(workerKey, cancellationToken);
    }

    public async Task<StoredConnectionThrottleProfile> UpdateAsync(ConnectionWorkerKey workerKey,
        ConnectionThrottleProfile profile, CancellationToken cancellationToken)
    {
        var current = await GetAsync(workerKey, cancellationToken);
        var updated = current with { Profile = profile.Validate(), UpdatedAtUtc = clock.UtcNow };
        await store.SaveAsync(updated, cancellationToken);
        return updated;
    }

    public async Task<StoredConnectionThrottleProfile> GetAsync(ConnectionWorkerKey workerKey,
        CancellationToken cancellationToken)
    {
        ValidateKey(workerKey);
        return await store.GetAsync(workerKey, cancellationToken)
            ?? throw new InvalidOperationException($"Connection {workerKey} has no throttle profile.");
    }

    private static void ValidateKey(ConnectionWorkerKey workerKey)
    {
        if (workerKey.TenantId.IsEmpty || workerKey.ConnectionId == Guid.Empty)
            throw new ArgumentException("Tenant and connection identifiers are required.", nameof(workerKey));
    }
}
