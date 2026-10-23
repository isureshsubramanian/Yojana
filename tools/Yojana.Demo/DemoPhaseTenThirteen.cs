using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Backfills;
using Yojana.Integration.Connections;
using Yojana.Integration.Connectors;
using Yojana.Integration.Connectors.CX02;
using Yojana.Integration.Connectors.CX02.Testing;
using Yojana.Integration.Connectors.CX04;
using Yojana.Integration.Connectors.CX04.Testing;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.DeadLetters;
using Yojana.Integration.ErpChange;
using Yojana.Integration.Gateway;
using Yojana.Integration.Verification;
using Yojana.Knowledge.Contracts;
using Yojana.Knowledge.Data;
using Yojana.Platform.Contracts.Records;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;
using Yojana.Platform.Seeding;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Demo;

public sealed partial class DemoLoader
{
    private const string ErpChangeActor = "system:demo-erp-change";

    private async Task CreatePhaseThirteenScenariosAsync(CancellationToken cancellationToken)
    {
        var people = await KnowledgePeopleAsync(cancellationToken);
        var mediaPolicy = await EnsureKnowledgeMediaPolicyVersionAsync(1, cancellationToken);
        var server = Units("RS-1100")[31];
        var storage = Units("SS-1110")[0];
        var desktop = Units("DW-300")[0];

        var dimm = await EnsureAnsweredQuestionAsync(server.UnitId,
            "Replacing a failed DIMM under next-business-day cover",
            "The bank server reports a corrected-memory fault. May the field engineer replace one DIMM on site under next-business-day cover?",
            YojanaRole.Technician, people.OrderDesk,
            "Yes. Record the failed slot and diagnostic code, fit an approved DIMM, run the memory test and retain the service report. The next-business-day cover includes on-site parts and labour.",
            people.Technician, people, cancellationToken);
        var dimmEntry = await EnsurePublishedEntryAsync(dimm,
            "Replacing a failed DIMM under next-business-day cover",
            "A covered server has a confirmed memory-module fault and the engineer needs the approved on-site path.",
            "Replace the failed DIMM on site when diagnostics identify the slot and the unit has live next-business-day cover.",
            "Record the slot and diagnostic code. Fit an approved DIMM. Run the full memory test. Attach the service report to the case.",
            [new(KnowledgeTagKind.Role, YojanaRole.Technician), new(KnowledgeTagKind.Task, "dimm replacement")],
            [new(RecordKind.Unit, server.UnitId)], [], people.OrderDesk, people.Reviewer,
            cancellationToken);

        var media = await EnsureAnsweredQuestionAsync(storage.UnitId,
            "Media retention when a failed drive is returned",
            "The HPC storage array has a failed drive containing research data. Does the customer keep the failed drive after replacement?",
            YojanaRole.ServiceDesk, people.Technician,
            "Use the media-retention option recorded on the cover. Leave the failed drive with the customer, record its serial and obtain the customer's retention acknowledgement before closing the visit.",
            people.ServiceDesk, people, cancellationToken);
        var mediaEntry = await EnsurePublishedEntryAsync(media,
            "Media retention when a failed drive is returned",
            "A failed storage drive may contain customer data and the service team must decide whether it travels with the return.",
            "When media retention is present, leave the failed drive with the customer and retain the signed acknowledgement instead of the drive.",
            "Check the cover. Record the failed-drive serial. Obtain the acknowledgement. Mark the return as media retained and do not pack the drive.",
            [new(KnowledgeTagKind.Role, YojanaRole.ServiceDesk), new(KnowledgeTagKind.Task, "media retention")],
            [new(RecordKind.Unit, storage.UnitId)],
            [new(WrittenAgainstKind.PolicyVersion, mediaPolicy.ToString("D"), "Media-retention service policy")],
            people.Technician, people.Reviewer, cancellationToken);

        var refurbished = await EnsureAnsweredQuestionAsync(desktop.UnitId,
            "Customer consent for a refurbished replacement",
            "A DW-300 desktop is eligible for replacement. What consent must the order desk record before offering a refurbished unit?",
            YojanaRole.OrderDeskSupervisor, people.ServiceDesk,
            "Obtain explicit consent before allocation. State that the replacement is refurbished, confirm equivalent or better specification and cover, and record the customer's decision on the replacement case.",
            people.OrderDesk, people, cancellationToken);
        var refurbishedEntry = await EnsurePublishedEntryAsync(refurbished,
            "Customer consent for a refurbished replacement",
            "The order desk is considering a refurbished unit as a warranty replacement.",
            "Offer it only after explicit customer consent, with equivalent-or-better specification and no reduction in remaining cover.",
            "Explain the refurbished condition. Confirm specification and remaining cover. Record consent before allocating the serialised replacement.",
            [new(KnowledgeTagKind.Role, YojanaRole.OrderDeskSupervisor), new(KnowledgeTagKind.Task, "replacement consent")],
            [new(RecordKind.Unit, desktop.UnitId)], [], people.ServiceDesk, people.Reviewer,
            cancellationToken);

        var accidental = await EnsureAnsweredQuestionAsync(desktop.UnitId,
            "Checking accidental-damage cover before depot repair",
            "The desktop has impact damage. Which evidence must be checked before the depot accepts the repair as covered?",
            YojanaRole.ServiceDesk, people.OrderDesk,
            "Confirm that an accidental-damage add-on is live on the incident date, photograph the impact, record the customer's account and check that no exclusion applies before authorising depot work.",
            people.ServiceDesk, people, cancellationToken);
        var accidentalEntry = await EnsurePublishedEntryAsync(accidental,
            "Checking accidental-damage cover before depot repair",
            "A unit arrives with impact damage and the service desk must decide whether an add-on can fund the repair.",
            "Authorise covered repair only after the incident-date add-on and evidence have both been checked.",
            "Check entitlement dates. Capture photographs and the customer's account. Review exclusions. Record the decision before depot work starts.",
            [new(KnowledgeTagKind.Role, YojanaRole.ServiceDesk), new(KnowledgeTagKind.Task, "accidental damage")],
            [new(RecordKind.Unit, desktop.UnitId)], [], people.OrderDesk, people.Reviewer,
            cancellationToken);

        await EnsureOpenQuestionAsync(server.UnitId, "Confirming the approved DIMM stock before dispatch",
            "Which approved spare should be reserved for the bank server before the engineer departs?",
            YojanaRole.Technician, people.OrderDesk, cancellationToken);
        await EnsureOpenQuestionAsync(storage.UnitId, "Media retention when a failed drive is returned — follow-up",
            "The published guidance covers a single drive. Does the same acknowledgement cover two failed drives from one storage visit?",
            YojanaRole.ServiceDesk, people.OrderDesk, cancellationToken);
        await EnsureClosedAndReopenedQuestionAsync(desktop.UnitId,
            "Next-business-day timing after a missed remote diagnosis",
            "Does the response clock restart when remote diagnosis is repeated the following morning?",
            people.ServiceDesk, people, cancellationToken);

        await EnsureTrainingPathAsync(YojanaRole.Technician,
            "Start with safe field diagnosis, covered parts and data-bearing media handling.",
            [dimmEntry, mediaEntry, accidentalEntry], people, cancellationToken);
        await EnsureTrainingPathAsync(YojanaRole.OrderDeskSupervisor,
            "Use these reviewed answers when agreeing replacement condition and service options with customers.",
            [refurbishedEntry, mediaEntry, dimmEntry], people, cancellationToken);

        await EnsureKnowledgeMediaPolicyVersionAsync(2, cancellationToken);
        await WaitForKnowledgeReviewAsync(mediaEntry, cancellationToken);
        await VerifyKnowledgeDemoAsync(people, cancellationToken);
    }

    private async Task<KnowledgePeople> KnowledgePeopleAsync(CancellationToken cancellationToken)
    {
        using var scope = TenantScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var users = await db.Users.Include(user => user.Memberships).Where(user => new[]
            {
                $"{YojanaRole.OrderDeskSupervisor}@{DevelopmentSeeder.EmailDomain}",
                $"{YojanaRole.Technician}@{DevelopmentSeeder.EmailDomain}",
                $"{YojanaRole.ServiceDesk}@{DevelopmentSeeder.EmailDomain}",
                $"{YojanaRole.ServiceSupervisor}@{DevelopmentSeeder.EmailDomain}",
                DevelopmentSeeder.AdministratorEmail(DevelopmentSeeder.EmailDomain),
            }.Contains(user.Email)).ToListAsync(cancellationToken);
        QuestionActor Person(string role)
        {
            var user = users.Single(value => value.Email == $"{role}@{DevelopmentSeeder.EmailDomain}");
            return new(user.Id, user.Memberships.Single(value => value.TenantId == tenant).Roles);
        }
        var admin = users.Single(value => value.Email == DevelopmentSeeder.AdministratorEmail(DevelopmentSeeder.EmailDomain));
        var member = await EnsureNewTeamMemberAsync(db, admin, cancellationToken);
        return new(Person(YojanaRole.OrderDeskSupervisor), Person(YojanaRole.Technician),
            Person(YojanaRole.ServiceDesk), Person(YojanaRole.ServiceSupervisor),
            new(admin.Id, admin.Memberships.Single(value => value.TenantId == tenant).Roles),
            new(member.Id, member.Memberships.Single(value => value.TenantId == tenant).Roles));
    }

    private async Task<AppUser> EnsureNewTeamMemberAsync(PlatformDbContext db, AppUser passwordSource,
        CancellationToken cancellationToken)
    {
        const string email = "new.team.member@dev.yojana.local";
        var user = await db.Users.Include(value => value.Memberships)
            .SingleOrDefaultAsync(value => value.Email == email, cancellationToken);
        if (user is null)
        {
            user = new AppUser
            {
                Id = DemoIds.FromName("user:new-team-member"), Email = email, DisplayName = "Meera Solanki",
                PasswordHash = passwordSource.PasswordHash, CreatedAt = DemoDates.At(today.AddDays(-12), 9),
            };
            db.Users.Add(user);
            user.Memberships.Add(new UserTenantMembership
            {
                UserId = user.Id, TenantId = tenant, CreatedAt = DemoDates.At(today.AddDays(-12), 9),
                Roles = [YojanaRole.Technician, YojanaRole.OrderDeskSupervisor],
            });
        }
        else
        {
            user.DisplayName = "Meera Solanki";
            user.IsActive = true;
            user.PasswordHash = passwordSource.PasswordHash;
            var membership = user.Memberships.Single(value => value.TenantId == tenant);
            membership.Roles = [YojanaRole.Technician, YojanaRole.OrderDeskSupervisor];
            membership.DeactivatedAt = null;
            membership.DeactivatedByUserId = null;
        }
        await db.SaveChangesAsync(cancellationToken);
        return user;
    }

    private async Task<Guid> EnsureAnsweredQuestionAsync(Guid recordId, string title, string body, string role,
        QuestionActor asker, string reply, QuestionActor responder, KnowledgePeople people,
        CancellationToken cancellationToken)
    {
        var questionId = await EnsureQuestionAsync(recordId, title, body, role, asker, cancellationToken);
        using (var scope = TenantScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>();
            var question = await db.Questions.AsNoTracking().SingleAsync(value => value.QuestionId == questionId,
                cancellationToken);
            if (question.Status == QuestionStatus.Answered) return questionId;
        }
        Guid replyId;
        using (var scope = TenantScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>();
            replyId = await db.Replies.AsNoTracking().Where(value => value.QuestionId == questionId
                    && value.Body == reply).Select(value => (Guid?)value.ReplyId)
                .SingleOrDefaultAsync(cancellationToken) ?? Guid.Empty;
        }
        if (replyId == Guid.Empty)
            replyId = (await InvokeAsync<ReplyToQuestionResult>(new ReplyToQuestion(questionId, reply, responder),
                cancellationToken)).ReplyId;
        await InvokeAsync<QuestionChangeResult>(new AcceptQuestionAnswer(questionId, replyId, asker),
            cancellationToken);
        return questionId;
    }

    private Task<Guid> EnsureOpenQuestionAsync(Guid recordId, string title, string body, string role,
        QuestionActor asker, CancellationToken cancellationToken) =>
        EnsureQuestionAsync(recordId, title, body, role, asker, cancellationToken);

    private async Task<Guid> EnsureQuestionAsync(Guid recordId, string title, string body, string role,
        QuestionActor asker, CancellationToken cancellationToken)
    {
        using (var scope = TenantScope())
        {
            var existing = await scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>().Questions.AsNoTracking()
                .Where(value => value.Title == title).Select(value => (Guid?)value.QuestionId)
                .SingleOrDefaultAsync(cancellationToken);
            if (existing is { } found) return found;
        }
        return (await InvokeAsync<AskQuestionResult>(new AskQuestion(recordId, RecordKind.Unit, title, body,
            null, role, asker), cancellationToken)).QuestionId;
    }

    private async Task EnsureClosedAndReopenedQuestionAsync(Guid recordId, string title, string body,
        QuestionActor asker, KnowledgePeople people, CancellationToken cancellationToken)
    {
        var id = await EnsureQuestionAsync(recordId, title, body, YojanaRole.ServiceSupervisor, asker,
            cancellationToken);
        using var scope = TenantScope();
        var question = await scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>().Questions.AsNoTracking()
            .SingleAsync(value => value.QuestionId == id, cancellationToken);
        if (question.Status == QuestionStatus.Open && question.ReopenedCount == 0)
        {
            await InvokeAsync<QuestionChangeResult>(new CloseQuestionWithoutAnswer(id,
                "Closed while the service-level interpretation was checked.", asker), cancellationToken);
            await InvokeAsync<QuestionChangeResult>(new ReopenQuestion(id, asker), cancellationToken);
        }
    }

    private async Task<Guid> EnsurePublishedEntryAsync(Guid questionId, string title, string problem, string answer,
        string steps, IReadOnlyList<EntryTagInput> tags, IReadOnlyList<EntryRecordLinkInput> links,
        IReadOnlyList<WrittenAgainstInput> writtenAgainst, QuestionActor author, QuestionActor reviewer,
        CancellationToken cancellationToken)
    {
        Guid entryId;
        using (var scope = TenantScope())
        {
            entryId = await scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>().Entries.AsNoTracking()
                .Where(value => value.SourceQuestionId == questionId).Select(value => (Guid?)value.EntryId)
                .SingleOrDefaultAsync(cancellationToken) ?? Guid.Empty;
        }
        if (entryId == Guid.Empty)
            entryId = (await InvokeAsync<EntryChangeResult>(new CreateEntryFromAnswer(questionId, author),
                cancellationToken)).EntryId;
        string status;
        using (var scope = TenantScope())
        {
            var entry = await scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>().Entries.AsNoTracking()
                .SingleAsync(value => value.EntryId == entryId, cancellationToken);
            status = entry.Status;
            if (status == KnowledgeEntryStatus.Draft && entry.CurrentVersion == 1)
                await InvokeAsync<EntryChangeResult>(new EditDraft(entryId,
                    new(title, problem, answer, steps, tags, links, writtenAgainst), author), cancellationToken);
        }
        using (var scope = TenantScope())
            status = await scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>().Entries.AsNoTracking()
                .Where(value => value.EntryId == entryId).Select(value => value.Status)
                .SingleAsync(cancellationToken);
        if (status == KnowledgeEntryStatus.Draft)
            await InvokeAsync<EntryChangeResult>(new SubmitEntryForReview(entryId, author), cancellationToken);
        using (var scope = TenantScope())
            status = await scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>().Entries.AsNoTracking()
                .Where(value => value.EntryId == entryId).Select(value => value.Status)
                .SingleAsync(cancellationToken);
        if (status == KnowledgeEntryStatus.InReview)
            await InvokeAsync<EntryChangeResult>(new ApproveEntry(entryId,
                "Reviewed against the demonstration service policy.", reviewer), cancellationToken);
        return entryId;
    }

    private async Task<Guid> EnsureKnowledgeMediaPolicyVersionAsync(int version,
        CancellationToken cancellationToken)
    {
        const string code = "DEMO-KNOWLEDGE-MEDIA";
        using (var scope = TenantScope())
        {
            var existing = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().PolicyVersions
                .AsNoTracking().Where(value => value.PolicyCode == code && value.Version == version)
                .Select(value => new { value.PolicyVersionId, value.Status }).SingleOrDefaultAsync(cancellationToken);
            if (existing?.Status == PolicyVersionStatus.Published) return existing.PolicyVersionId;
            if (existing is not null)
            {
                await InvokeAsync<PolicyDraftResult>(new PublishPolicyVersion(existing.PolicyVersionId, actor),
                    cancellationToken);
                return existing.PolicyVersionId;
            }
        }
        var definition = Policy(code, "Media-retention service policy", CoverageFamilyCode.Base, 60, "on_site",
            travel: 100, responseHours: 8);
        var draft = await InvokeAsync<PolicyDraftResult>(new CreatePolicyDraft(
            DemoIds.FromName($"policy:{code}:v{version}"), code, definition.Name, definition.Family,
            definition.Type, today.AddDays(version == 1 ? -90 : -1), definition.Terms, actor), cancellationToken);
        return (await InvokeAsync<PolicyDraftResult>(new PublishPolicyVersion(draft.PolicyVersionId, actor),
            cancellationToken)).PolicyVersionId;
    }

    private async Task EnsureTrainingPathAsync(string role, string introduction, IReadOnlyList<Guid> entries,
        KnowledgePeople people, CancellationToken cancellationToken)
    {
        Guid? pathId;
        using (var scope = TenantScope())
            pathId = await scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>().TrainingPaths.AsNoTracking()
                .Where(value => value.Role == role).Select(value => (Guid?)value.PathId)
                .SingleOrDefaultAsync(cancellationToken);
        if (pathId is null)
            pathId = (await InvokeAsync<TrainingPathChangeResult>(new SaveTrainingPath(role, introduction,
                entries, 0, people.Reviewer), cancellationToken)).PathId;
        using var verifyScope = TenantScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<KnowledgeDbContext>();
        var first = entries[0];
        if (!await db.TrainingProgress.AsNoTracking().AnyAsync(value => value.PathId == pathId
                && value.EntryId == first && value.UserId == people.NewMember.UserId, cancellationToken))
            await InvokeAsync<TrainingReadResult>(new MarkTrainingEntryRead(role, first, people.NewMember),
                cancellationToken);
    }

    private async Task WaitForKnowledgeReviewAsync(Guid entryId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var scope = TenantScope();
            if (await scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>().Entries.AsNoTracking()
                    .AnyAsync(value => value.EntryId == entryId && value.NeedsReview, cancellationToken)) return;
            await Task.Delay(50, cancellationToken);
        }
        throw new InvalidOperationException("the media-retention entry was not flagged after its policy version changed");
    }

    private async Task VerifyKnowledgeDemoAsync(KnowledgePeople people, CancellationToken cancellationToken)
    {
        using var scope = TenantScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>();
        if (await db.Questions.CountAsync(cancellationToken) < 7
            || await db.Entries.CountAsync(value => value.Status == KnowledgeEntryStatus.Published, cancellationToken) < 4
            || await db.TrainingPaths.CountAsync(cancellationToken) < 2)
            throw new InvalidOperationException("the questions and knowledge demonstration is incomplete");
        var bell = await scope.ServiceProvider.GetRequiredService<Yojana.Knowledge.Questions.QuestionNotificationService>()
            .BellAsync(new(people.Presenter.UserId, people.Presenter.Roles), 10, cancellationToken);
        if (bell.Count == 0)
            throw new InvalidOperationException("the presenter must have at least one knowledge bell notification");
    }

    private sealed record KnowledgePeople(QuestionActor OrderDesk, QuestionActor Technician,
        QuestionActor ServiceDesk, QuestionActor Reviewer, QuestionActor Presenter, QuestionActor NewMember);

    private async Task CreatePhaseTenScenariosAsync(CancellationToken cancellationToken)
    {
        await using var receiver = new DemoNotificationReceiver();
        await using var cx02 = new Cx02Simulator();
        await using var cx04 = new Cx04Simulator();
        var sourceId = DemoIds.FromName("integration:connection:business-erp-cx01");
        ErpChangeSourceFacts sourceFacts = null!;
        await StepAsync("  prepare ERP-change source facts", async ct =>
            sourceFacts = await PrepareErpChangeSourceAsync(sourceId, ct), cancellationToken);
        SeedCx02ChangeCompany(cx02, sourceFacts);

        var cx02Request = new RegisterConnection(DemoIds.FromName("integration:connection:new-erp-cx02"),
            Cx02Capabilities.ConnectorCode, "Asterra new ERP", LegalEntityId,
            Cx02Settings.ToJson(new(cx02.BaseUri, Cx02Simulator.DefaultTenant, Cx02Simulator.DefaultTenant,
                Cx02Simulator.DefaultEnvironment, Cx02Simulator.CompanyId, "1.0", receiver.Uri,
                cx02.TokenUri(), PollPageSize: 200, BusinessEventHintsEnabled: false)),
            [new(Cx02Client.CredentialPurpose, JsonSerializer.Serialize(new Cx02ClientCredential(
                "fictional-demo-client", cx02.Options.ClientSecret, "fictional-demo-client-state"),
                IntegrationContracts.Json), DemoDates.At(today.AddDays(365)), DemoDates.At(today.AddDays(335)))]) ;

        Guid cx02Id = Guid.Empty;
        await StepAsync("  stage the CX02 ERP change", async ct =>
            cx02Id = await EnsureCx02ErpChangeAsync(sourceId, cx02Request, cx02, ct), cancellationToken);
        await StepAsync("  verify the CX02 extension", ct => EnsureCx02ExtensionAsync(cx02Id, ct),
            cancellationToken);
        await StepAsync("  create the CX02 acceptance run", ct => EnsureAcceptanceRunAsync(cx02Id, ct),
            cancellationToken);
        Guid cx04Id = Guid.Empty;
        await StepAsync("  sign in the CX04 connection", async ct =>
            cx04Id = await EnsureCx04SignedInAsync(cx04, ct), cancellationToken);
        await StepAsync("  verify the integration screens", ct =>
            VerifyPhaseTenScreensAsync(sourceId, cx02Id, cx04Id, ct), cancellationToken);
    }

    private async Task<ErpChangeSourceFacts> PrepareErpChangeSourceAsync(Guid sourceId,
        CancellationToken cancellationToken)
    {
        using var scope = TenantScope();
        var provider = scope.ServiceProvider;
        var db = provider.GetRequiredService<IntegrationDbContext>();
        await provider.GetRequiredService<ConnectorCatalogue>().SeedGenericEntriesAsync(cancellationToken);
        await db.ConnectorCatalogue.Where(value => value.ConnectorCode == Cx02Capabilities.ConnectorCode
                || value.ConnectorCode == Cx04Capabilities.ConnectorCode)
            .ExecuteUpdateAsync(update => update.SetProperty(value => value.Enabled, true), cancellationToken);
        var source = await db.Connections.SingleAsync(value => value.ConnectionId == sourceId, cancellationToken);
        source.Status = ConnectionStatus.Active;
        var sourceHealth = await db.ConnectionHealth.SingleAsync(value => value.ConnectionId == sourceId,
            cancellationToken);
        sourceHealth.Status = "healthy";
        sourceHealth.IsLive = true;
        sourceHealth.NotificationState = "site_relay";
        sourceHealth.UpdatedAt = DemoDates.At(today, 9);
        await db.SaveChangesAsync(cancellationToken);

        var migration = provider.GetRequiredService<IMigrationReconciliationPort>();
        var usages = await migration.MasterReferenceUsageAsync(cancellationToken);
        await EnsureSourceReferencesAsync(sourceId, usages, provider, cancellationToken);
        var units = new List<MigrationReconciliationUnit>();
        Guid? cursor = null;
        do
        {
            var page = await migration.InStockUnitsAsync(cursor, 1000, cancellationToken);
            units.AddRange(page.Items);
            cursor = page.NextUnitId;
        } while (cursor is not null);
        if (units.Count < 4)
            throw new InvalidOperationException("the ERP-change demonstration needs at least four in-stock units");
        return new(usages, units);
    }

    private async Task EnsureSourceReferencesAsync(Guid sourceId,
        IReadOnlyList<MigrationMasterReferenceUsage> usages, IServiceProvider provider,
        CancellationToken cancellationToken)
    {
        var db = provider.GetRequiredService<IntegrationDbContext>();
        foreach (var usage in usages)
        {
            var externalId = usage.Kind == "item"
                ? catalogue.Items.Values.Single(value => value.Id == usage.ReferenceId).Code
                : catalogue.Parties.Values.Single(value => value.Id == usage.ReferenceId).Code;
            if (await db.ExternalIdMaps.AsNoTracking().AnyAsync(value => value.ConnectionId == sourceId
                    && value.EntityType == usage.Kind && value.ExternalId == externalId && value.ValidTo == null,
                cancellationToken)) continue;
            var messageId = DemoIds.FromName($"erp-change:source-message:{usage.Kind}:{externalId}");
            var letterId = DemoIds.FromName($"erp-change:source-letter:{usage.Kind}:{externalId}");
            var at = DemoDates.At(today.AddDays(-14), 9);
            object payload = usage.Kind == "item"
                ? new ItemUpserted(externalId,
                    catalogue.Items.Values.Single(value => value.Id == usage.ReferenceId).Name ?? externalId,
                    OpenEnum.Known("serial"), "EA", false)
                : new PartyUpserted(externalId, OpenEnum.Known("customer"),
                    catalogue.Parties.Values.Single(value => value.Id == usage.ReferenceId).Name ?? externalId,
                    null, "IN");
            var messageType = payload.GetType().Name;
            var envelope = new CanonicalEnvelope(IntegrationContracts.CurrentVersion, messageId, tenant.Value,
                sourceId, "CX01", usage.Kind == "item" ? "items" : "business_partners", externalId,
                new("demo", OpenEnum.Known("sequence")), new(at, TimeSpan.Zero), new(at, TimeSpan.Zero),
                OpenEnum.Known("observed"), OpenEnum.Known("complete"),
                $"demo/source/{usage.Kind}/{externalId}");
            var canonical = JsonSerializer.Serialize(new { envelope, message_type = messageType, payload },
                IntegrationContracts.Json);
            db.InboundMessages.Add(new InboundMessage
            {
                TenantId = tenant, InboundMessageId = messageId, MessageId = messageId,
                ConnectionId = sourceId, ConnectorCode = "CX01",
                ErpEntity = usage.Kind == "item" ? "items" : "business_partners",
                ErpExternalId = externalId, ErpChangeMarker = "demo", MarkerKind = "sequence",
                ContractVersion = IntegrationContracts.CurrentVersion, MessageType = messageType,
                RawPayload = canonical, CanonicalMessage = canonical, OccurredAt = at, ObservedAt = at,
                AcceptedAt = at, IsLatest = true, TranslationStatus = InboundTranslationStatus.NeedsAttention,
                AttentionReasonCode = usage.Kind + "_not_mapped",
            });
            db.DeadLetterItems.Add(new DeadLetterItem
            {
                TenantId = tenant, DeadLetterItemId = letterId, ConnectionId = sourceId,
                Direction = DeadLetterDirection.Inbound, RecordType = messageType,
                SourceKind = DeadLetterSourceKind.InboundMessage, SourceId = messageId,
                ReasonCode = usage.Kind + "_not_mapped", RawPayload = canonical,
                CanonicalAttempt = canonical, Failure = "The demonstration source reference needs mapping.",
                Status = DeadLetterStatus.NeedsAttention, CreatedAt = at, UpdatedAt = at,
            });
            await db.SaveChangesAsync(cancellationToken);
            await provider.GetRequiredService<DeadLetterService>().MapAndRetryAsync(letterId,
                new(usage.Kind, externalId, usage.ReferenceId), ErpChangeActor, cancellationToken);
            await provider.GetRequiredService<InboundGateway>().TranslateAsync(messageId, cancellationToken);
        }
    }

    private void SeedCx02ChangeCompany(Cx02Simulator simulator, ErpChangeSourceFacts facts)
    {
        var itemIds = facts.Usages.Where(value => value.Kind == "item").Select(value => value.ReferenceId)
            .ToHashSet();
        var partyIds = facts.Usages.Where(value => value.Kind == "party").Select(value => value.ReferenceId)
            .ToHashSet();
        var changed = DemoDates.At(today.AddDays(-2), 8).ToString("O");
        var itemRows = catalogue.Items.Values.Where(value => itemIds.Contains(value.Id)).Select(value =>
            Cx02SimulatorEntity.From(new
            {
                id = value.Id, number = value.Code, displayName = value.Name,
                itemTrackingCode = "Serial Specific", baseUnitOfMeasureCode = "EA",
                lastModifiedDateTime = changed,
            })).Append(Cx02SimulatorEntity.From(new
            {
                id = DemoIds.FromName("erp-change:extra-item"), number = "SPARE-UNLISTED",
                displayName = "Unlisted field-service spare", itemTrackingCode = "Serial Specific",
                baseUnitOfMeasureCode = "EA", lastModifiedDateTime = changed,
            })).ToArray();
        var partyRows = catalogue.Parties.Values.Where(value => partyIds.Contains(value.Id)).Select(value =>
            Cx02SimulatorEntity.From(new
            {
                id = value.Id, number = value.Code, displayName = value.Name,
                taxRegistrationNumber = (string?)null, countryLetterCode = "IN",
                lastModifiedDateTime = changed,
            })).Append(Cx02SimulatorEntity.From(new
            {
                id = DemoIds.FromName("erp-change:extra-party"), number = "C-TRIAL-UNLISTED",
                displayName = "Kaveri Test Laboratory", taxRegistrationNumber = (string?)null,
                countryLetterCode = "IN", lastModifiedDateTime = changed,
            })).ToArray();
        simulator.Seed("items", itemRows);
        simulator.Seed("customers", partyRows);

        var coveredMissing = facts.Units.Where(value => value.HasLiveEntitlement).Take(2)
            .Select(value => value.UnitId).ToHashSet();
        var normalised = facts.Units.First(value => !coveredMissing.Contains(value.UnitId));
        var moved = facts.Units.First(value => !coveredMissing.Contains(value.UnitId)
            && value.UnitId != normalised.UnitId);
        var ledgers = facts.Units.Where(value => !coveredMissing.Contains(value.UnitId)).Select((value, index) =>
        {
            var item = catalogue.Items.Values.Single(row => row.Id == value.ItemId);
            var serial = value.UnitId == normalised.UnitId
                ? string.Join('-', value.Identifier.Chunk(2).Select(chunk => new string(chunk)))
                : value.Identifier;
            var location = value.UnitId == moved.UnitId ? "FIELD-HOLD" : value.LocationKind;
            return Cx02SimulatorEntity.From(new
            {
                id = DemoIds.FromName($"erp-change:stock:{value.UnitId:N}"), entryNumber = 5000 + index,
                itemNumber = item.Code, postingDate = today.AddDays(-2).ToString("yyyy-MM-dd"),
                entryType = "Positive Adjmt.", sourceNumber = "OPENING",
                documentNumber = $"OPEN-{index + 1:D4}", documentType = "adjustment", quantity = 1,
                serialNumber = serial, lotNumber = "", packageNumber = "", locationCode = location,
                lastModifiedDateTime = changed,
            });
        }).Append(Cx02SimulatorEntity.From(new
        {
            id = DemoIds.FromName("erp-change:stock:extra"), entryNumber = 9999,
            itemNumber = catalogue.Items.Values.First(value => itemIds.Contains(value.Id)).Code,
            postingDate = today.AddDays(-2).ToString("yyyy-MM-dd"), entryType = "Positive Adjmt.",
            sourceNumber = "OPENING", documentNumber = "OPEN-EXTRA", documentType = "adjustment",
            quantity = 1, serialNumber = "CX02-EXTRA-0001", lotNumber = "", packageNumber = "",
            locationCode = "warehouse", lastModifiedDateTime = changed,
        })).ToArray();
        simulator.Seed("trackedItemLedgerEntries", ledgers);
    }

    private async Task<Guid> EnsureCx02ErpChangeAsync(Guid sourceId, RegisterConnection registration,
        Cx02Simulator simulator, CancellationToken cancellationToken)
    {
        Guid? targetId;
        using (var scope = TenantScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            targetId = await db.Connections.AsNoTracking().Where(value =>
                    value.RegistrationRequestId == registration.RequestId)
                .Select(value => (Guid?)value.ConnectionId).SingleOrDefaultAsync(cancellationToken);
            if (targetId is { } existing)
                await scope.ServiceProvider.GetRequiredService<Cx02Settings>().SetAsync(existing,
                    registration.ConnectorSettings.Deserialize<Cx02ConnectionSettings>(IntegrationContracts.Json)!,
                    cancellationToken);
        }
        using var runScope = TenantScope();
        var provider = runScope.ServiceProvider;
        var dbRun = provider.GetRequiredService<IntegrationDbContext>();
        var runs = provider.GetRequiredService<ErpChangeRunService>();
        var active = await runs.ActiveAsync(cancellationToken);
        if (active is null)
        {
            var hasHistory = await dbRun.ErpChangeRuns.AsNoTracking().AnyAsync(value =>
                value.CloseReason == "Earlier customer test move was cancelled after registration.",
                cancellationToken);
            if (!hasHistory)
            {
                var historical = await runs.StartAsync(new(sourceId, registration), ErpChangeActor,
                    cancellationToken);
                await runs.CancelAsync(historical.RunId,
                    new("Earlier customer test move was cancelled after registration."), ErpChangeActor,
                    cancellationToken);
            }
            targetId ??= await dbRun.Connections.AsNoTracking().Where(value =>
                    value.RegistrationRequestId == registration.RequestId)
                .Select(value => (Guid?)value.ConnectionId).SingleAsync(cancellationToken);
            var target = await dbRun.Connections.SingleAsync(value => value.ConnectionId == targetId,
                cancellationToken);
            target.Status = ConnectionStatus.Active;
            await dbRun.SaveChangesAsync(cancellationToken);
            active = await runs.StartAsync(new(sourceId, registration), ErpChangeActor, cancellationToken);
        }
        targetId = active.Target.ConnectionId;
        var health = await dbRun.ConnectionHealth.SingleOrDefaultAsync(value => value.ConnectionId == targetId,
            cancellationToken);
        if (health is null)
        {
            health = new ConnectionHealth { TenantId = tenant, ConnectionId = targetId.Value };
            dbRun.ConnectionHealth.Add(health);
        }
        health.Status = "healthy";
        health.IsLive = true;
        health.LastDailyCheckAt = DemoDates.At(today, 8);
        health.LastDailyCheckResult = "clean";
        health.NotificationState = "subscribed";
        health.UpdatedAt = DemoDates.At(today, 9);
        await dbRun.SaveChangesAsync(cancellationToken);

        if (active.CurrentStep == ErpChangeStep.Register)
        {
            var check = await runs.CheckAsync(active.RunId, ErpChangeActor, cancellationToken);
            if (!check.Passed) throw new InvalidOperationException(check.Detail);
            active = await runs.AdvanceAsync(active.RunId, ErpChangeActor, cancellationToken);
        }
        if (active.CurrentStep == ErpChangeStep.MasterData)
        {
            var master = provider.GetRequiredService<ErpChangeMasterDataService>();
            await master.RemapAsync(active.RunId, ErpChangeActor, cancellationToken);
            var open = await dbRun.ErpChangeMasterDataExceptions.Where(value =>
                    value.ErpChangeRunId == active.RunId && value.Status == ErpChangeExceptionStatus.Open)
                .OrderByDescending(value => value.LiveUnitsAffected).ToListAsync(cancellationToken);
            foreach (var exception in open)
                await master.MarkNotNeededAsync(active.RunId, exception.ErpChangeMasterDataExceptionId,
                    new(exception.LiveUnitsAffected == 0
                        ? "The sandbox-only record is not needed for the move."
                        : "The record will be created after the demonstration review."),
                    ErpChangeActor, cancellationToken);
            var check = await runs.CheckAsync(active.RunId, ErpChangeActor, cancellationToken);
            if (!check.Passed) throw new InvalidOperationException(check.Detail);
            active = await runs.AdvanceAsync(active.RunId, ErpChangeActor, cancellationToken);
        }
        if (active.CurrentStep == ErpChangeStep.Stock
            && !await dbRun.ErpChangeStockDifferences.AsNoTracking().AnyAsync(value =>
                value.ErpChangeRunId == active.RunId && value.IsCurrent, cancellationToken))
            await provider.GetRequiredService<ErpChangeStockService>().ReconcileAsync(active.RunId,
                "system:demo-inventory-controller", cancellationToken);
        return targetId.Value;
    }

    private async Task EnsureCx02ExtensionAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        using var scope = TenantScope();
        var provider = scope.ServiceProvider;
        await provider.GetRequiredService<Cx02WebhookMaintenance>().EnsureAsync(connectionId, cancellationToken);
        var view = await provider.GetRequiredService<ExtensionStatusService>()
            .CheckAsync(connectionId, ErpChangeActor, cancellationToken);
        if (view.InstalledVersion != "1.0" || view.ReachablePages != view.UsedPages
            || view.Notifications.LiveCount == 0)
            throw new InvalidOperationException("the CX02 extension demonstration is not fully reachable with live notifications");
    }

    private async Task EnsureAcceptanceRunAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        using var scope = TenantScope();
        var service = scope.ServiceProvider.GetRequiredService<ConnectorAcceptanceService>();
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var runId = await db.ConnectorAcceptanceRuns.AsNoTracking().Where(value =>
                value.ConnectionId == connectionId && value.SandboxLabel == "Asterra customer acceptance sandbox"
                && value.Status == ConnectorAcceptanceRunStatus.Open)
            .Select(value => (Guid?)value.ConnectorAcceptanceRunId).SingleOrDefaultAsync(cancellationToken);
        var run = runId is null
            ? await service.StartAsync(connectionId, new("Asterra customer acceptance sandbox"),
                ErpChangeActor, cancellationToken)
            : await service.DetailAsync(connectionId, runId.Value, cancellationToken);
        foreach (var item in run.Items.Where(value => value.Result == ConnectorAcceptanceItemResult.Pending).Take(5))
            run = await service.MarkAsync(connectionId, run.RunId, item.ItemId,
                new(ConnectorAcceptanceItemResult.Passed, "Checked in the customer sandbox.",
                    "The simulator returned the expected read-only result."), ErpChangeActor, cancellationToken);
        if (run.Items.All(value => value.Result != ConnectorAcceptanceItemResult.Pending)
            || !run.Items.Any(value => value.Result == ConnectorAcceptanceItemResult.Passed))
            throw new InvalidOperationException("the acceptance run must retain both passed and pending checks");
    }

    private async Task<Guid> EnsureCx04SignedInAsync(Cx04Simulator simulator,
        CancellationToken cancellationToken)
    {
        var requestId = DemoIds.FromName("integration:connection:service-erp-cx04");
        var settings = new Cx04ConnectionSettings(simulator.BaseUri, "fictional-demo-connected-application",
            new Uri("https://yojana.invalid/integration/cx04/callback"),
            Yojana.Integration.Connectors.CX04.Cx04LicenceTier.Essentials,
            Cx04RefreshExpiryModes.Absolute, 41, PollPageSize: 200);
        Guid connectionId;
        using (var scope = TenantScope())
        {
            var provider = scope.ServiceProvider;
            var db = provider.GetRequiredService<IntegrationDbContext>();
            connectionId = await db.Connections.AsNoTracking().Where(value =>
                    value.RegistrationRequestId == requestId).Select(value => (Guid?)value.ConnectionId)
                .SingleOrDefaultAsync(cancellationToken) ?? Guid.Empty;
            if (connectionId == Guid.Empty)
                connectionId = (await provider.GetRequiredService<ConnectionCommands>().RegisterAsync(
                    new(requestId, Cx04Capabilities.ConnectorCode, "Asterra service operations",
                        LegalEntityId, Cx04Settings.ToJson(settings), []), ErpChangeActor,
                    cancellationToken)).ConnectionId;
            else
                await provider.GetRequiredService<Cx04Settings>().SetAsync(connectionId, settings,
                    cancellationToken);
        }
        using var authScope = TenantScope();
        var authorisation = authScope.ServiceProvider.GetRequiredService<Cx04Authorisation>();
        var current = await authorisation.ReadAsync(connectionId, null, 25, cancellationToken);
        if (current.Status != Cx04AuthorisationStatus.Active)
        {
            var start = await authorisation.BeginAsync(connectionId, cancellationToken);
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler);
            using var response = await client.GetAsync(start.ConsentUrl, cancellationToken);
            if (response.StatusCode != HttpStatusCode.Redirect)
                throw new InvalidOperationException("the CX04 simulator did not return a consent callback");
            var query = System.Web.HttpUtility.ParseQueryString(response.Headers.Location!.Query);
            await authorisation.CompleteAsync(connectionId, new(query["code"]!, query["state"]!),
                ErpChangeActor, cancellationToken);
        }
        var signedIn = await authorisation.ReadAsync(connectionId, null, 25, cancellationToken);
        if (signedIn.Status != Cx04AuthorisationStatus.Active || signedIn.RefreshChainExpiresAt is null
            || (DateOnly.FromDateTime(signedIn.RefreshChainExpiresAt.Value)
                .DayNumber - DateOnly.FromDateTime(DateTime.UtcNow).DayNumber) != 41)
            throw new InvalidOperationException("the CX04 sign-in demonstration must show 41 days remaining");
        return connectionId;
    }

    private async Task VerifyPhaseTenScreensAsync(Guid sourceId, Guid cx02Id, Guid cx04Id,
        CancellationToken cancellationToken)
    {
        using var scope = TenantScope();
        var provider = scope.ServiceProvider;
        var connections = await provider.GetRequiredService<ConnectionQueries>()
            .ListAsync(null, 50, false, cancellationToken);
        if (!connections.Items.Any(value => value.ConnectionId == sourceId)
            || !connections.Items.Any(value => value.ConnectionId == cx02Id)
            || !connections.Items.Any(value => value.ConnectionId == cx04Id))
            throw new InvalidOperationException("the connection list did not load all demonstration connections");
        var attention = await provider.GetRequiredService<DeadLetterService>()
            .ListAsync(new(null, null, null, null, null, null, null, null, 25), cancellationToken);
        var manual = await provider.GetRequiredService<DeadLetterService>()
            .ManualTasksAsync(ManualTaskStatus.Open, null, null, 25, cancellationToken);
        if (attention.Items.Count == 0 || manual.Items.Count == 0)
            throw new InvalidOperationException("the P8 attention and hand-entry screens lost their demonstration data");
        var db = provider.GetRequiredService<IntegrationDbContext>();
        var active = await db.ErpChangeRuns.AsNoTracking().SingleAsync(value =>
            value.Status == ErpChangeRunStatus.Active, cancellationToken);
        var reasons = await db.ErpChangeStockDifferences.AsNoTracking().Where(value =>
                value.ErpChangeRunId == active.ErpChangeRunId && value.IsCurrent)
            .Select(value => value.ReasonClass).Distinct().ToListAsync(cancellationToken);
        if (!new[] { StockDifferenceReason.MissingInTarget, StockDifferenceReason.ExtraInTarget,
                StockDifferenceReason.LocationDiffers, StockDifferenceReason.SerialDiffersByNormalisation }
            .All(reasons.Contains))
            throw new InvalidOperationException("the ERP-change stock demonstration must contain every reason class");
        if (!await db.ErpChangeStockDifferences.AsNoTracking().AnyAsync(value =>
                value.ErpChangeRunId == active.ErpChangeRunId && value.IsCurrent
                && value.Status == ErpChangeDifferenceStatus.Open && value.HasLiveEntitlement,
            cancellationToken))
            throw new InvalidOperationException("the ERP-change stock demonstration needs an open covered unit");
        if (!await db.ReconciliationRuns.AsNoTracking().AnyAsync(cancellationToken)
            || !await db.BackfillCursors.AsNoTracking().AnyAsync(value => value.ConnectionId == sourceId,
                cancellationToken))
            throw new InvalidOperationException("the P8/P9 reconciliation or backfill demonstration data is missing");
    }

    private sealed record ErpChangeSourceFacts(IReadOnlyList<MigrationMasterReferenceUsage> Usages,
        IReadOnlyList<MigrationReconciliationUnit> Units);

    private sealed class DemoNotificationReceiver : IAsyncDisposable
    {
        private readonly HttpListener listener;
        private readonly CancellationTokenSource stop = new();
        private readonly Task loop;

        public DemoNotificationReceiver()
        {
            for (var attempt = 0; ; attempt++)
            {
                using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                socket.Start();
                var port = ((IPEndPoint)socket.LocalEndpoint).Port;
                socket.Stop();
                var candidate = new HttpListener();
                Uri = new($"http://127.0.0.1:{port}/");
                candidate.Prefixes.Add(Uri.ToString());
                try
                {
                    candidate.Start();
                    listener = candidate;
                    break;
                }
                catch (HttpListenerException) when (attempt < 19)
                {
                    candidate.Close();
                }
            }
            loop = RunAsync();
        }

        public Uri Uri { get; }

        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            listener.Stop();
            try { await loop; }
            catch (HttpListenerException) { }
            listener.Close();
            stop.Dispose();
        }

        private async Task RunAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); }
                catch (Exception) when (stop.IsCancellationRequested) { break; }
                _ = Task.Run(() => HandleAsync(context), stop.Token);
            }
        }

        private static async Task HandleAsync(HttpListenerContext context)
        {
            var validation = context.Request.QueryString["validationToken"];
            if (validation is not null)
            {
                context.Response.ContentType = "text/plain";
                var bytes = Encoding.UTF8.GetBytes(validation);
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
            }
            else
            {
                using var reader = new StreamReader(context.Request.InputStream,
                    context.Request.ContentEncoding);
                _ = await reader.ReadToEndAsync();
            }
            context.Response.StatusCode = 200;
            context.Response.Close();
        }
    }
}
