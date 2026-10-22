using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Yojana.Knowledge.Data;
using Yojana.Knowledge.Questions;
using Yojana.Platform;

namespace Yojana.Knowledge;

/// <summary>Module registration. Owns the <c>knowledge</c> schema through <see cref="KnowledgeDbContext"/>.</summary>
public static class KnowledgeModule
{
    public const string Schema = KnowledgeDbContext.SchemaName;

    public static IHostApplicationBuilder AddKnowledgeModule(this IHostApplicationBuilder builder)
    {
        builder.AddModuleDbContext<KnowledgeDbContext>(Schema);
        builder.Services.AddScoped<RecordReferenceLookup>();
        builder.Services.AddScoped<QuestionQueries>();
        return builder;
    }
}
