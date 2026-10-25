using Microsoft.Extensions.Hosting;
using Yojana.Developer.Data;
using Yojana.Platform;

namespace Yojana.Developer;

/// <summary>
/// Module registration (ADR-0018). Owns the <c>developer</c> schema through <see cref="DeveloperDbContext"/>: the public
/// event catalogue, webhook subscriptions and deliveries, API clients and the public API. It reads other modules only
/// through their contracts, so it can change or be removed without editing a connector or the serial ledger.
/// </summary>
public static class DeveloperModule
{
    public const string Schema = DeveloperDbContext.SchemaName;

    public static IHostApplicationBuilder AddDeveloperModule(this IHostApplicationBuilder builder)
    {
        builder.AddModuleDbContext<DeveloperDbContext>(Schema);
        return builder;
    }
}
