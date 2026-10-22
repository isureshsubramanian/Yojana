using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Yojana.Knowledge.Data;
using Yojana.Knowledge.Questions;
using Yojana.Knowledge.Entries;
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
        // These factories keep ITenantSettings and the serialisation label ports behind narrow services. Wolverine
        // command handlers then have one transactional KnowledgeDbContext instead of accidentally spanning stores.
        builder.Services.AddScoped(sp => new EntryReviewerSettings(
            sp.GetRequiredService<Yojana.Platform.Contracts.Tenancy.ITenantSettings>()));
        builder.Services.AddScoped(sp => new EntryTagLabelResolver(
            sp.GetRequiredService<Yojana.Serialisation.Contracts.IItemFamilyNamePort>(),
            sp.GetRequiredService<Yojana.Serialisation.Contracts.IClaimCauseNamePort>()));
        builder.Services.AddScoped<KnowledgeEntryQueries>();
        builder.Services.AddScoped<QuestionQueries>();
        builder.Services.AddScoped(sp => new QuestionNotificationDefaults(
            sp.GetRequiredService<ITenantSettings>()));
        builder.Services.AddScoped<QuestionNotificationService>();
        builder.Services.AddSingleton(RecurringTenantJob.For<QuestionDailyDigestJob>(
            QuestionDailyDigestJob.JobName, QuestionDailyDigestJob.Cron));
        return builder;
    }
}
