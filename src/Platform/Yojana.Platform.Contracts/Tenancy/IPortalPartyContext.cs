namespace Yojana.Platform.Contracts.Tenancy;

/// <summary>
/// Optional customer-party boundary on a tenant scope. Only authenticated customer-portal endpoints set it;
/// back-office requests, messages, jobs and anonymous public lookup leave it unset.
/// </summary>
public interface IPortalPartyContext
{
    bool HasPortalParty { get; }
    Guid PortalPartyRef { get; }
}
