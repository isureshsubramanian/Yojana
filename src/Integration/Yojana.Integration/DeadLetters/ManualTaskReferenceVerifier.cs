namespace Yojana.Integration.DeadLetters;

public enum ManualTaskReferenceMatch { Found, NotFound, Unavailable }

/// <summary>Narrow connector seam used before accepting an operator-entered ERP document number.</summary>
public interface IManualTaskReferenceVerifier
{
    Task<ManualTaskReferenceMatch> VerifyAsync(Guid connectionId, string commandType,
        string erpExternalId, string draft, CancellationToken cancellationToken);
}

/// <summary>The gateway has no connector-specific lookup until an adapter supplies one; the operator may acknowledge it.</summary>
public sealed class UnavailableManualTaskReferenceVerifier : IManualTaskReferenceVerifier
{
    public Task<ManualTaskReferenceMatch> VerifyAsync(Guid connectionId, string commandType,
        string erpExternalId, string draft, CancellationToken cancellationToken) =>
        Task.FromResult(ManualTaskReferenceMatch.Unavailable);
}
