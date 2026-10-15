using Microsoft.Extensions.Hosting;

namespace Yojana.Serialisation;

/// <summary>Module registration. Owns the <c>serialisation</c> schema; its DbContext and migrations arrive in P1.</summary>
public static class SerialisationModule
{
    public const string Schema = "serialisation";

    public static IHostApplicationBuilder AddSerialisationModule(this IHostApplicationBuilder builder) => builder;
}
