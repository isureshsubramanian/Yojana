using Yojana.Platform.Contracts.Security;

namespace Yojana.Knowledge.Training;

/// <summary>Handler-safe façade over Platform's membership read contract.</summary>
public sealed class TrainingMemberDirectory(ITenantTrainingDirectory directory)
{
    public Task<TenantTrainingMember?> ActiveMemberAsync(Guid userId, CancellationToken cancellationToken) =>
        directory.ActiveMemberAsync(userId, cancellationToken);
}
