using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Mail;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Mail;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.CustomerMail;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Entitlements;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.DatabaseTests;

[Collection("serialisation host")]
public sealed class CustomerMailTests(SerialisationHostFixture fixture)
{
    private static readonly DateTime At = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(60);
    private const string Contact = "p.raman@meridian.example.invalid";

    private sealed record SetupData(TenantId Tenant, Guid Unit, Guid Owner, string Serial);

    private async Task<T> Invoke<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message, new DeliveryOptions { TenantId = tenant.ToString() });
    }

    private async Task<SetupData> Setup(string prefix)
    {
        var tenant = await fixture.NewTenantAsync(prefix, "USD");
        var item = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var serial = $"RS11-{Random.Shared.Next(1000, 9999)}-{Random.Shared.Next(10000, 99999)}";
        fixture.Services.GetRequiredService<InMemoryItemPorts>().Register(InMemoryItemPorts.DefaultProfile(item) with { ItemCode = "RS-1100", ItemClass = "rack_servers" });
        fixture.Services.GetRequiredService<InMemoryItemNamePort>().Register(new(item, "RS-1100", "RS-1100 rack server"));
        fixture.Services.GetRequiredService<InMemoryPartyNamePort>().Register(new(owner, "MERIDIAN", "Meridian Data Labs", PartyType.Customer));
        fixture.Services.GetRequiredService<InMemoryPartyContactPort>().Register(new(owner, "Priya Raman", Contact, "IT asset manager", "en"));
        var unit = (await Invoke<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Purchased,
            [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, serial)])], new DateTime(2025, 10, 1, 9, 0, 0, DateTimeKind.Utc), "quality.user", Entity))).Lines[0].UnitId!.Value;
        foreach (var (family, name, items, usage) in new (string, string, PolicyCoverItem[], UsageLimitTerm[])[]
        {
            (CoverageFamilyCode.Base, "Standard 36 months", [Item("BASE-HW", "Hardware failure in normal use"), Item("EX-THEFT", "Theft or loss", "excluded"), Item("EX-COSM", "Cosmetic damage", "excluded")], []),
            ("accidental_damage", "Accidental damage", [Item("ADP-DROP", "Accidental drops and knocks")], [new("claims", 2)]),
        })
        {
            await Invoke<CoverageFamilyRow>(tenant, new DefineCoverageFamily(family, family == CoverageFamilyCode.Base ? "Base warranty" : name,
                family != CoverageFamilyCode.Base, AssuranceOrService.Assurance));
            var terms = PolicyValidatorTestsTerms.Standard24() with
            {
                Duration = new(36, DurationRule.Months), Items = items, UsageLimits = usage, Start = new(StartTrigger.Invoice, [], null),
                ReplacementPolicyCode = null, Refurbished = [], RegionScope = ["WORLD"], ReferenceTimeZoneRule = PolicyTerms.TimeZoneUtc,
            };
            var draft = await Invoke<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), family.ToUpperInvariant(), name, family,
                EntitlementType.Manufacturer, new(2020, 1, 1), terms, "quality.user"));
            await Invoke<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft.PolicyVersionId, "quality.user"));
            await Invoke<CreateEntitlementResult>(tenant, new CreateEntitlement(Guid.NewGuid(), unit, family, new(2026, 1, 1), Entity, At.AddDays(-1), "quality.user",
                new Dictionary<string, DateTime> { [StartTrigger.Invoice] = new DateTime(2025, 11, 25, 0, 0, 0, DateTimeKind.Utc) }, draft.PolicyVersionId,
                owner, PartyType.Customer, owner, PartyType.Customer));
        }
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        (await db.Current.SingleAsync(x => x.UnitId == unit)).OwnerPartyRef = owner;
        await db.SaveChangesAsync();
        return new(tenant, unit, owner, serial);
    }

    private static PolicyCoverItem Item(string code, string wording, string type = "covered") =>
        new(code, type, wording, "Internal service instruction: verify the seal", "cause", CoverPartsScope.All, []);

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(JsonSerializerOptions.Web))!;
    }

    private static async Task<string> Rule(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("title").GetString()!;
    }

    /// <summary>Creates a template from the built-in layout, optionally changes its first language, and publishes it.</summary>
    private async Task<MailTemplateDetail> PublishedAsync(SetupData s, string name, Func<MailTemplateContent, MailTemplateContent>? change = null)
    {
        using var admin = await fixture.AuthenticatedClientAsync(s.Tenant, YojanaRole.TenantAdministrator);
        var created = await Read<MailTemplateDetail>(await admin.PostAsJsonAsync("/api/mail-templates", new CreateMailTemplateRequest(CustomerMailKind.CoverageSheet, name)));
        var draft = created.Versions.Single();
        if (change is not null)
            draft = (await Read<MailTemplateSaveResult>(await admin.PutAsJsonAsync($"/api/mail-templates/{created.TemplateId}/draft",
                new SaveMailTemplateDraftRequest(draft.VersionId, draft.ExpectedVersion, change(draft.Content))))).Template!.Versions.Single();
        return (await Read<MailTemplateSaveResult>(await admin.PostAsync($"/api/mail-templates/{created.TemplateId}/versions/{draft.VersionId}/publish", null))).Template!;
    }

    private static MailTemplateContent WithoutPdf(MailTemplateContent content) => content with { AttachPdf = false };

    private async Task<SentMailDetail> WaitForSettledAsync(HttpClient client, Guid messageId)
    {
        var deadline = DateTime.UtcNow + Settle;
        while (DateTime.UtcNow < deadline)
        {
            var detail = await client.GetFromJsonAsync<SentMailDetail>($"/api/customer-mail/{messageId}", JsonSerializerOptions.Web);
            if (detail!.Row.Status is CustomerMailStatus.Sent or CustomerMailStatus.Failed) return detail;
            await Task.Delay(250);
        }
        throw new TimeoutException($"customer mail {messageId} did not settle within {Settle}");
    }

    [DatabaseFact]
    public async Task The_built_in_layout_passes_its_checks_and_previews_the_units_own_cover_without_internal_notes()
    {
        var s = await Setup("mailstarter");
        using var designer = await fixture.AuthenticatedClientAsync(s.Tenant, YojanaRole.ServiceManager);
        var created = await Read<MailTemplateDetail>(await designer.PostAsJsonAsync("/api/mail-templates",
            new CreateMailTemplateRequest(CustomerMailKind.CoverageSheet, "What's covered · standard")));
        Assert.Equal("What's covered (built in)", created.StartedFrom);
        Assert.False(created.CanPublish);
        var draft = Assert.Single(created.Versions);
        Assert.Equal(MailTemplateStatus.Draft, draft.Status);
        Assert.Contains(created.SampleRecords, x => x.RecordId == s.Unit);

        var preview = await Read<MailPreviewView>(await designer.PostAsJsonAsync($"/api/mail-templates/{created.TemplateId}/preview",
            new MailPreviewRequest(s.Unit, "en", draft.Content, Note: "As requested <b>today</b>.")));
        Assert.True(preview.Check.Passed, string.Join(" | ", preview.Check.Errors));
        Assert.Equal($"Warranty cover for your RS-1100 rack server {s.Serial}", preview.Subject);
        Assert.Contains($"<strong>{s.Serial}</strong>", preview.Html);
        Assert.Contains("Base warranty", preview.Html);
        Assert.Contains("Accidental damage", preview.Html);
        Assert.Contains("2 claims left", preview.Html);
        Assert.Contains("Dear Meridian Data Labs,", preview.Html);
        Assert.Contains("As requested &lt;b&gt;today&lt;/b&gt;.", preview.Html);
        Assert.Contains("theft or loss", preview.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("http://127.0.0.1:5080/portal/units/" + s.Unit.ToString("D"), preview.Html);
        Assert.Contains("Sent with Yojana on behalf of", preview.Html);
        Assert.DoesNotContain("Internal service instruction", preview.Html);
        Assert.DoesNotContain("<img", preview.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("As requested <b>today</b>.", preview.Text);
        Assert.DoesNotContain("<p", preview.Text);
        Assert.Equal($"What's covered · {s.Serial}.pdf", preview.AttachmentName);

        Assert.Equal(HttpStatusCode.Forbidden, (await designer.PostAsync($"/api/mail-templates/{created.TemplateId}/versions/{draft.VersionId}/publish", null)).StatusCode);
        using var technician = await fixture.AuthenticatedClientAsync(s.Tenant, YojanaRole.Technician);
        Assert.Equal(HttpStatusCode.Forbidden, (await technician.GetAsync("/api/mail-templates")).StatusCode);
    }

    [DatabaseFact]
    public async Task Checks_refuse_unknown_fields_repeats_without_an_empty_text_and_links_outside_the_product()
    {
        var s = await Setup("mailchecks");
        using var designer = await fixture.AuthenticatedClientAsync(s.Tenant, YojanaRole.ServiceManager);
        var created = await Read<MailTemplateDetail>(await designer.PostAsJsonAsync("/api/mail-templates",
            new CreateMailTemplateRequest(CustomerMailKind.CoverageSheet, "Broken")));
        var draft = created.Versions.Single();
        var english = draft.Content.Languages[0];
        var broken = draft.Content with
        {
            Languages = [english with { Blocks = [..english.Blocks,
                new MailBlock(MailBlockType.Paragraph, "Your account manager is {{customer.account_manager}}."),
                new MailBlock(MailBlockType.Paragraph, "{{#each offers}}{{policy_name}}{{/each}}"),
                new MailBlock(MailBlockType.Paragraph, "Read [our terms](https://elsewhere.example.invalid)."),
                new MailBlock(MailBlockType.Table, Each: "unit.serial", Columns: [new("Cover", "{{family_name}}")])] }],
        };

        // A draft saves with failing checks (work in progress), but it cannot be sent for review or published.
        var saved = await Read<MailTemplateSaveResult>(await designer.PutAsJsonAsync($"/api/mail-templates/{created.TemplateId}/draft",
            new SaveMailTemplateDraftRequest(draft.VersionId, draft.ExpectedVersion, broken)));
        Assert.False(saved.Check.Passed);
        Assert.Contains(saved.Check.Errors, x => x.Contains("'customer.account_manager' is not a field"));
        Assert.Contains(saved.Check.Errors, x => x.Contains("repeat over 'offers' needs an empty-list text"));
        Assert.Contains(saved.Check.Errors, x => x.Contains("links point only to the product's own links"));
        Assert.Contains(saved.Check.Errors, x => x.Contains("a table repeats over a list field"));
        Assert.Contains(saved.Check.Errors, x => x.Contains("needs the sentence shown when the list is empty"));
        var current = saved.Template!.Versions.Single();

        var review = await designer.PutAsJsonAsync($"/api/mail-templates/{created.TemplateId}/draft",
            new SaveMailTemplateDraftRequest(current.VersionId, current.ExpectedVersion, broken, SubmitForReview: true));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, review.StatusCode);
        using var admin = await fixture.AuthenticatedClientAsync(s.Tenant, YojanaRole.TenantAdministrator);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await admin.PostAsync($"/api/mail-templates/{created.TemplateId}/versions/{current.VersionId}/publish", null)).StatusCode);
        var stale = await designer.PutAsJsonAsync($"/api/mail-templates/{created.TemplateId}/draft",
            new SaveMailTemplateDraftRequest(current.VersionId, draft.ExpectedVersion, draft.Content));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [DatabaseFact]
    public async Task Publishing_retires_the_previous_version_sets_the_default_and_shows_the_button_to_senders_only()
    {
        var s = await Setup("mailpublish");
        using var desk = await fixture.AuthenticatedClientAsync(s.Tenant, YojanaRole.ServiceDesk);
        Assert.False((await desk.GetFromJsonAsync<WhatsCoveredPage>($"/api/units/{s.Unit}/whats-covered", JsonSerializerOptions.Web))!.CustomerEmailEnabled);

        var v1 = await PublishedAsync(s, "Standard");
        Assert.Equal(["en"], v1.DefaultFor);
        Assert.True((await desk.GetFromJsonAsync<WhatsCoveredPage>($"/api/units/{s.Unit}/whats-covered", JsonSerializerOptions.Web))!.CustomerEmailEnabled);
        using var technician = await fixture.AuthenticatedClientAsync(s.Tenant, YojanaRole.Technician);
        Assert.False((await technician.GetFromJsonAsync<WhatsCoveredPage>($"/api/units/{s.Unit}/whats-covered", JsonSerializerOptions.Web))!.CustomerEmailEnabled);

        using var admin = await fixture.AuthenticatedClientAsync(s.Tenant, YojanaRole.TenantAdministrator);
        var published = v1.Versions.Single();
        var immutable = await admin.PutAsJsonAsync($"/api/mail-templates/{v1.TemplateId}/draft",
            new SaveMailTemplateDraftRequest(published.VersionId, published.ExpectedVersion, published.Content));
        Assert.Equal("mail_template_version_immutable", await Rule(immutable));
        var v2Draft = (await Read<MailTemplateSaveResult>(await admin.PutAsJsonAsync($"/api/mail-templates/{v1.TemplateId}/draft",
            new SaveMailTemplateDraftRequest(null, null, published.Content, SubmitForReview: true)))).Template!;
        Assert.True(v2Draft.Versions.Single(x => x.Version == 2).SubmittedForReview);
        var list = await admin.GetFromJsonAsync<MailTemplatePage>("/api/mail-templates", JsonSerializerOptions.Web);
        Assert.True(Assert.Single(list!.Rows).WaitingForReview);
        Assert.Equal(1, list.Kpis.WaitingForReview);
        var second = await admin.PutAsJsonAsync($"/api/mail-templates/{v1.TemplateId}/draft", new SaveMailTemplateDraftRequest(null, null, published.Content));
        Assert.Equal("mail_template_draft_exists", await Rule(second));
        var v2 = (await Read<MailTemplateSaveResult>(await admin.PostAsync(
            $"/api/mail-templates/{v1.TemplateId}/versions/{v2Draft.Versions.Single(x => x.Version == 2).VersionId}/publish", null))).Template!;
        Assert.Equal(MailTemplateStatus.Retired, v2.Versions.Single(x => x.Version == 1).Status);
        Assert.Equal(MailTemplateStatus.Published, v2.Versions.Single(x => x.Version == 2).Status);

        // A compose still holding v1 is told to pick the current version.
        var stale = await desk.PostAsJsonAsync($"/api/units/{s.Unit}/coverage-mail/send", new SendCoverageMail(Guid.NewGuid(), published.VersionId, "en",
            [new MailRecipientInput(Contact)]));
        Assert.Equal("mail_template_version_not_published", await Rule(stale));
    }

    [DatabaseFact]
    public async Task A_template_that_attaches_the_pdf_sends_nothing_when_the_pdf_cannot_be_made()
    {
        var s = await Setup("mailnopdf");
        var template = await PublishedAsync(s, "With PDF");
        using var desk = await fixture.AuthenticatedClientAsync(s.Tenant, YojanaRole.ServiceDesk);
        var response = await desk.PostAsJsonAsync($"/api/units/{s.Unit}/coverage-mail/send", new SendCoverageMail(Guid.NewGuid(),
            template.Versions.Single().VersionId, "en", [new MailRecipientInput(Contact)]));
        Assert.Equal("mail_attachment_unavailable", await Rule(response));
        using var scope = fixture.ScopeFor(s.Tenant);
        Assert.False(await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().CustomerMailMessages.AnyAsync(x => x.UnitId == s.Unit));
    }

    [DatabaseFact]
    public async Task Sending_queues_one_copy_per_recipient_with_the_shared_cc_records_it_once_and_tracks_delivery()
    {
        var s = await Setup("mailsend");
        var template = await PublishedAsync(s, "Short", WithoutPdf);
        var versionId = template.Versions.Single().VersionId;
        using var desk = await fixture.AuthenticatedClientAsync(s.Tenant, YojanaRole.ServiceDesk);

        var compose = await desk.GetFromJsonAsync<CoverageMailComposeView>($"/api/units/{s.Unit}/coverage-mail", JsonSerializerOptions.Web);
        Assert.Equal("Priya Raman", Assert.Single(compose!.Contacts).Name);
        Assert.Equal(versionId, Assert.Single(compose.Templates).VersionId);
        Assert.Equal("Service desk", compose.SignedByTeam);
        var preview = await Read<MailPreviewView>(await desk.PostAsJsonAsync($"/api/units/{s.Unit}/coverage-mail/preview",
            new ComposePreviewRequest(versionId, "en", "Priya Raman", "As requested.")));
        Assert.Contains("Dear Priya Raman,", preview.Html);
        Assert.Contains("Service desk", preview.Html);

        var unconfirmed = await desk.PostAsJsonAsync($"/api/units/{s.Unit}/coverage-mail/send", new SendCoverageMail(Guid.NewGuid(), versionId, "en",
            [new MailRecipientInput(Contact), new MailRecipientInput("facilities@meridian.example.invalid")]));
        Assert.Equal("mail_recipient_unconfirmed", await Rule(unconfirmed));

        var command = new SendCoverageMail(Guid.NewGuid(), versionId, "en",
            [new MailRecipientInput(Contact), new MailRecipientInput("ops@meridian.example.invalid", "Ops desk", Confirmed: true)],
            [new MailRecipientInput("facilities@meridian.example.invalid", "Facilities", Confirmed: true)], "As requested, here is the cover.");
        var queued = await desk.PostAsJsonAsync($"/api/units/{s.Unit}/coverage-mail/send", command);
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var ids = (await queued.Content.ReadFromJsonAsync<MailQueuedView>(JsonSerializerOptions.Web))!.MessageIds;
        Assert.Equal(2, ids.Count);
        var again = await (await desk.PostAsJsonAsync($"/api/units/{s.Unit}/coverage-mail/send", command)).Content.ReadFromJsonAsync<MailQueuedView>(JsonSerializerOptions.Web);
        Assert.Equal(ids.Order(), again!.MessageIds.Order());

        foreach (var id in ids)
        {
            var detail = await WaitForSettledAsync(desk, id);
            Assert.Equal(CustomerMailStatus.Sent, detail.Row.Status);
            Assert.Equal(1, detail.Row.Attempts);
            Assert.Equal("Short", detail.Row.TemplateName);
            Assert.Equal(1, detail.Row.TemplateVersion);
            Assert.Equal("Fictional Quality User", detail.Row.SentBy);
            Assert.Equal("facilities@meridian.example.invalid", Assert.Single(detail.Cc).Address);
            Assert.True(detail.HasNote);
            Assert.Contains("As requested, here is the cover.", detail.Html);
            Assert.Contains(detail.Timeline, x => x.What == "Delivered to the mail server");
            var eml = Path.Combine(SerialisationHostFixture.MailPickupDirectory, $"{id:N}.eml");
            var message = await MimeMessage.LoadAsync(eml);
            Assert.Equal("facilities@meridian.example.invalid", Assert.Single(message.Cc.Mailboxes).Address);
            Assert.Single(message.To.Mailboxes);
            Assert.Equal(detail.Row.Subject, message.Subject);
        }
        var toPriya = await desk.GetFromJsonAsync<SentMailDetail>($"/api/customer-mail/{ids[0]}", JsonSerializerOptions.Web);
        Assert.Contains("Dear Priya Raman,", toPriya!.Html);

        var page = await desk.GetFromJsonAsync<SentMailPage>("/api/customer-mail?take=1&status=sent", JsonSerializerOptions.Web);
        Assert.Equal(2, page!.Total);
        Assert.NotNull(page.NextCursor);
        var next = await desk.GetFromJsonAsync<SentMailPage>($"/api/customer-mail?take=1&status=sent&cursor={page.NextCursor}", JsonSerializerOptions.Web);
        Assert.NotEqual(page.Rows.Single().MessageId, next!.Rows.Single().MessageId);
        Assert.Null(next.NextCursor);
        Assert.Equal(2, page.Kpis.Sent);
        Assert.Equal("What's covered sheet", page.Kpis.MostSentKind);
        var csv = await desk.GetStringAsync("/api/customer-mail/export?format=csv");
        Assert.Contains("ops@meridian.example.invalid", csv);
        var found = await desk.GetFromJsonAsync<SentMailPage>("/api/customer-mail?q=ops%40meridian", JsonSerializerOptions.Web);
        Assert.Equal("ops@meridian.example.invalid", Assert.Single(found!.Rows).To);
        Assert.Equal(2, found.Kpis.Sent);
        Assert.Equal(2, (await desk.GetFromJsonAsync<SentMailPage>($"/api/customer-mail?q={s.Serial}", JsonSerializerOptions.Web))!.Total);

        using var other = await fixture.AuthenticatedClientAsync(await fixture.NewTenantAsync("mailother", "USD"), YojanaRole.ServiceDesk);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/customer-mail/{ids[0]}")).StatusCode);
        Assert.Equal(0, (await other.GetFromJsonAsync<SentMailPage>("/api/customer-mail", JsonSerializerOptions.Web))!.Total);
    }

    [DatabaseFact]
    public async Task A_copy_to_me_goes_only_to_the_sender_and_stays_out_of_the_sent_log_and_a_note_needs_a_template_that_asks()
    {
        var s = await Setup("mailcopy");
        var template = await PublishedAsync(s, "No note", content => WithoutPdf(content) with { AskForNote = false });
        var versionId = template.Versions.Single().VersionId;
        using var desk = await fixture.AuthenticatedClientAsync(s.Tenant, YojanaRole.ServiceDesk);
        var note = await desk.PostAsJsonAsync($"/api/units/{s.Unit}/coverage-mail/send",
            new SendCoverageMail(Guid.NewGuid(), versionId, "en", [new MailRecipientInput(Contact)], Note: "Hello"));
        Assert.Equal("mail_note_not_offered", await Rule(note));

        var copy = await desk.PostAsJsonAsync($"/api/units/{s.Unit}/coverage-mail/send",
            new SendCoverageMail(Guid.NewGuid(), versionId, "en", [new MailRecipientInput(Contact)], CopyToMeOnly: true, SignedBy: "team"));
        var id = Assert.Single((await copy.Content.ReadFromJsonAsync<MailQueuedView>(JsonSerializerOptions.Web))!.MessageIds);
        var detail = await WaitForSettledAsync(desk, id);
        Assert.Equal("quality.user@example.invalid", detail.Row.To);
        Assert.StartsWith("[Copy] ", detail.Row.Subject);
        Assert.DoesNotContain("Service desk ·", detail.Html);
        Assert.Equal(0, (await desk.GetFromJsonAsync<SentMailPage>($"/api/customer-mail?unitId={s.Unit}", JsonSerializerOptions.Web))!.Total);
    }

    [DatabaseFact]
    public async Task An_older_delivery_attempt_never_overwrites_a_later_one()
    {
        var s = await Setup("mailorder");
        var template = await PublishedAsync(s, "Order", WithoutPdf);
        using var desk = await fixture.AuthenticatedClientAsync(s.Tenant, YojanaRole.ServiceDesk);
        var queued = await desk.PostAsJsonAsync($"/api/units/{s.Unit}/coverage-mail/send", new SendCoverageMail(Guid.NewGuid(),
            template.Versions.Single().VersionId, "en", [new MailRecipientInput(Contact)]));
        var id = Assert.Single((await queued.Content.ReadFromJsonAsync<MailQueuedView>(JsonSerializerOptions.Web))!.MessageIds);
        await WaitForSettledAsync(desk, id);

        await Invoke<object>(s.Tenant, new MailDeliveryRecorded(id, CustomerMailKind.CoverageSheet, s.Tenant, MailDeliveryStatus.Failed, 5,
            DateTime.UtcNow, null, null, "smtp 550 RecipientNotAccepted", "smtp")).ContinueWith(_ => new object());
        await Invoke<object>(s.Tenant, new MailDeliveryRecorded(id, CustomerMailKind.CoverageSheet, s.Tenant, MailDeliveryStatus.Retrying, 4,
            DateTime.UtcNow, null, DateTime.UtcNow.AddMinutes(4), "busy", "smtp")).ContinueWith(_ => new object());
        var detail = await desk.GetFromJsonAsync<SentMailDetail>($"/api/customer-mail/{id}", JsonSerializerOptions.Web);
        Assert.Equal(CustomerMailStatus.Failed, detail!.Row.Status);
        Assert.Equal(5, detail.Row.Attempts);
        Assert.Contains(detail.Timeline, x => x.What == "Not delivered" && x.Detail!.Contains("550"));
    }

    [DatabaseFact]
    public async Task The_kind_attaches_the_customer_sheet_made_now_from_the_same_record()
    {
        var s = await Setup("mailpdf");
        using var scope = fixture.ScopeFor(s.Tenant);
        var query = scope.ServiceProvider.GetRequiredService<WhatsCoveredQuery>();
        var kind = new CoverageSheetMailKind(scope.ServiceProvider.GetRequiredService<SerialisationDbContext>(),
            scope.ServiceProvider.GetRequiredService<ITenantContext>(), query, new UnitCustomerDocument(query, new PdfStub()),
            scope.ServiceProvider.GetRequiredService<MailOptions>());
        var file = await kind.AttachmentAsync(s.Unit, "en", CancellationToken.None);
        Assert.Equal($"What's covered · {s.Serial}.pdf", file!.FileName);
        Assert.Equal("%PDF-probe"u8.ToArray(), file.Content);
        MailOutbox.Validate(new OutgoingMail(CustomerMailKind.CoverageSheet, s.Tenant, null, new MailRecipient(Contact), "Subject", "<p>x</p>", "x",
            Attachments: [new MailAttachment(file.FileName, file.MediaType, file.Content)]));
    }

    private sealed class PdfStub : IPdfRenderer
    {
        public Task<byte[]> RenderAsync(PdfRenderRequest request, CancellationToken ct)
        {
            Assert.DoesNotContain("Internal service instruction", request.Html);
            return Task.FromResult("%PDF-probe"u8.ToArray());
        }
    }
}
