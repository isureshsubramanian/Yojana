using Microsoft.Extensions.Hosting;

namespace Yojana.Integration;

/// <summary>Module registration. Owns the <c>integration</c> schema; its DbContext and migrations arrive in P8.</summary>
public static class IntegrationModule
{
    public const string Schema = "integration";

    public static IHostApplicationBuilder AddIntegrationModule(this IHostApplicationBuilder builder) => builder;
}
