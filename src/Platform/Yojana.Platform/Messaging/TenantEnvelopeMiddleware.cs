using Wolverine;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Tenancy;

namespace Yojana.Platform.Messaging;

/// <summary>
/// Wolverine middleware applied to every handler: the tenant travels on the envelope
/// (<see cref="Envelope.TenantId"/>) and is placed into the scoped <see cref="ITenantContext"/> before the
/// handler runs, so the handler's DbContext and row-level security see the right tenant. A message
/// without a tenant leaves the scope tenant-less, which makes every tenant query match nothing.
/// </summary>
public static class TenantEnvelopeMiddleware
{
    public static void Before(Envelope envelope, ITenantContextSetter setter)
    {
        if (TenantId.TryParse(envelope.TenantId, out var tenantId))
        {
            setter.Set(tenantId);
        }
    }
}
