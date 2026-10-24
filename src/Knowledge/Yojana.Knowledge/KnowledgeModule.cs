using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Yojana.Knowledge.Data;
using Yojana.Knowledge.Questions;
using Yojana.Knowledge.Entries;
using Yojana.Knowledge.Training;
using Yojana.Knowledge.Reports;
using Yojana.Platform;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Contracts.Settings;
using Yojana.Knowledge.Settings;
using Yojana.Knowledge.Privacy;
using Yojana.Platform.Contracts.Privacy;

namespace Yojana.Knowledge;

/// <summary>Module registration. Owns the <c>knowledge</c> schema through <see cref="KnowledgeDbContext"/>.</summary>
public static class KnowledgeModule
{
    public const string Schema = KnowledgeDbContext.SchemaName;

    public static IHostApplicationBuilder AddKnowledgeModule(this IHostApplicationBuilder builder)
    {
        builder.AddModuleDbContext<KnowledgeDbContext>(Schema);
        builder.Services.AddScoped<KnowledgePersonalDataHolder>();
        builder.Services.AddScoped<IPersonalDataHolder>(services =>
            services.GetRequiredService<KnowledgePersonalDataHolder>());
        builder.Services.AddScoped<ISettingsSummaryContributor, KnowledgeScreenChangesSettingsSummaryContributor>();
        builder.Services.AddScoped<RecordReferenceLookup>();
        builder.Services.AddScoped<IQuestionTextSearch, SqlServerFullTextQuestionSearch>();
        // These factories keep ITenantSettings and the serialisation label ports behind narrow services. Wolverine
        // command handlers then have one transactional KnowledgeDbContext instead of accidentally spanning stores.
        builder.Services.AddScoped(sp => new EntryReviewerSettings(
            sp.GetRequiredService<Yojana.Platform.Contracts.Tenancy.ITenantSettings>()));
        builder.Services.AddScoped(sp => new EntryTagLabelResolver(
            sp.GetRequiredService<Yojana.Serialisation.Contracts.IItemFamilyNamePort>(),
            sp.GetRequiredService<Yojana.Serialisation.Contracts.IClaimCauseNamePort>()));
        builder.Services.AddScoped(sp => new WrittenAgainstResolver(
            sp.GetRequiredService<Yojana.Serialisation.Contracts.IPolicyVersionPickerPort>(),
            sp.GetRequiredService<ScreenReferenceCatalogue>()));
        builder.Services.AddScoped<KnowledgeEntryQueries>();
        builder.Services.AddScoped<KnowledgeScreenQueries>();
        builder.Services.AddSingleton<ScreenReferenceCatalogue>();
        builder.Services.AddScoped(sp => new TrainingSettings(sp.GetRequiredService<ITenantSettings>()));
        builder.Services.AddScoped(sp => new TrainingMemberDirectory(
            sp.GetRequiredService<Yojana.Platform.Contracts.Security.ITenantTrainingDirectory>()));
        builder.Services.AddScoped<TrainingPathQueries>();
        builder.Services.AddScoped<QuestionQueries>();
        builder.Services.AddScoped<QuestionAttachmentService>();
        builder.Services.AddScoped(sp => new QuestionRetentionSettings(sp.GetRequiredService<ITenantSettings>()));
        builder.Services.AddScoped<QuestionRetentionService>();
        builder.Services.AddScoped<QuestionPrivacyService>();
        builder.Services.AddScoped(sp => new KnowledgeReportSettings(sp.GetRequiredService<ITenantSettings>()));
        builder.Services.AddScoped<KnowledgeReportQueries>();
        builder.Services.AddScoped(sp => new QuestionNotificationDefaults(
            sp.GetRequiredService<ITenantSettings>()));
        builder.Services.AddScoped<QuestionNotificationService>();
        builder.Services.AddScoped<QuestionDailyDigestSchedule>();
        builder.Services.AddSingleton(RecurringTenantJob.For<QuestionDailyDigestJob>(
            QuestionDailyDigestJob.JobName, QuestionDailyDigestJob.Cron));
        builder.Services.AddSingleton(RecurringTenantJob.For<QuestionRetentionJob>(
            QuestionRetentionJob.JobName, QuestionRetentionJob.Cron));
        return builder;
    }
}
