using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Yojana.Knowledge.Data;
using Yojana.Knowledge.Questions;
using Yojana.Platform;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Scheduling;

namespace Yojana.Knowledge;

/// <summary>Module registration. Owns the <c>knowledge</c> schema through <see cref="KnowledgeDbContext"/>.</summary>
public static class KnowledgeModule
{
    public const string Schema = KnowledgeDbContext.SchemaName;

    public static IHostApplicationBuilder AddKnowledgeModule(this IHostApplicationBuilder builder)
    {
        builder.AddModuleDbContext<KnowledgeDbContext>(Schema);
        builder.Services.AddScoped<RecordReferenceLookup>();
        builder.Services.AddScoped<IQuestionTextSearch, SqlServerFullTextQuestionSearch>();
        builder.Services.AddScoped<QuestionQueries>();
        builder.Services.AddScoped(sp => new QuestionNotificationDefaults(
            sp.GetRequiredService<ITenantSettings>()));
        builder.Services.AddScoped<QuestionNotificationService>();
        builder.Services.AddSingleton(RecurringTenantJob.For<QuestionDailyDigestJob>(
            QuestionDailyDigestJob.JobName, QuestionDailyDigestJob.Cron));
        return builder;
    }
}
