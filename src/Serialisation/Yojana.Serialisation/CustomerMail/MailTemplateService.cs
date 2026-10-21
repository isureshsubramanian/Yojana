using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Mail;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Mail;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Documents;

namespace Yojana.Serialisation.CustomerMail;

/// <summary>The signed-in person as the server knows them from their claims; never taken from the browser.</summary>
public sealed record MailActor(string Ref, string Name, string Email, IReadOnlyList<string> Roles)
{
    public bool IsTenantAdministrator => Roles.Contains(YojanaRole.TenantAdministrator);
}

/// <summary>
/// Customer e-mail templates (P9-T20): versioned like certificate templates. Anyone with the designer saves drafts and
/// asks for review; only a tenant administrator publishes (owner decision 2026-10-21). Publishing retires the version it
/// replaces, so a template has at most one published version, and a sent message keeps the version it was made from.
/// </summary>
public sealed class MailTemplateService(SerialisationDbContext db, ITenantContext tenant, IClock clock,
    CustomerMailKinds kinds, MailTemplateComposer composer, CustomerMailRendering rendering, IUserDirectory people,
    IMailOutbox outbox)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<MailTemplatePage> ListAsync(string? status, int skip, int take, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var templates = await db.MailTemplates.AsNoTracking().Where(x => x.TenantId == tenantId).ToListAsync(cancellationToken);
        var versions = await db.MailTemplateVersions.AsNoTracking().Where(x => x.TenantId == tenantId).ToListAsync(cancellationToken);
        var defaults = await db.MailTemplateDefaults.AsNoTracking().Where(x => x.TenantId == tenantId).ToListAsync(cancellationToken);
        var since = clock.UtcNow.AddDays(-30);
        var sent = await db.CustomerMailMessages.AsNoTracking().Where(x => x.TenantId == tenantId && x.QueuedAt >= since && !x.CopyToSelf)
            .GroupBy(x => new { x.TemplateId, x.Status }).Select(g => new { g.Key.TemplateId, g.Key.Status, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var rows = templates.Select(template => Row(template, versions.Where(v => v.TemplateId == template.TemplateId).ToList(),
                defaults.Where(d => d.TemplateId == template.TemplateId).Select(d => d.Language).Order().ToList(),
                sent.Where(s => s.TemplateId == template.TemplateId).Sum(s => s.Count)))
            .OrderBy(x => x.Retired).ThenBy(x => x.KindName).ThenBy(x => x.Name).ToList();
        var names = await NamesAsync(rows.Select(x => x.LastChangeBy), cancellationToken);
        rows = rows.Select(x => x with { LastChangeBy = names.GetValueOrDefault(x.LastChangeBy, x.LastChangeBy) }).ToList();
        var filtered = status switch
        {
            MailTemplateStatus.Published => rows.Where(x => x.PublishedVersion is not null).ToList(),
            "drafts" or MailTemplateStatus.Draft => rows.Where(x => x.DraftVersion is not null).ToList(),
            _ => rows,
        };
        var published = rows.Where(x => x.PublishedVersion is not null).ToList();
        var settled = sent.Where(x => x.Status is CustomerMailStatus.Sent or CustomerMailStatus.Failed).Sum(x => x.Count);
        var kpis = new MailTemplateKpis(published.Count, published.Select(x => x.Kind).Distinct().Count(),
            rows.Count(x => x.DraftVersion is not null), rows.Count(x => x.WaitingForReview), sent.Sum(x => x.Count),
            settled == 0 ? null : Math.Round(100m * sent.Where(x => x.Status == CustomerMailStatus.Sent).Sum(x => x.Count) / settled, 1),
            published.SelectMany(x => x.Languages).Distinct().Order().ToList());
        var offset = Math.Max(0, skip);
        var size = Math.Clamp(take, 1, 100);
        return new MailTemplatePage(filtered.Skip(offset).Take(size).ToList(), filtered.Count, offset, size, kpis, kinds.Views);
    }

    public async Task<MailTemplateDetail?> GetAsync(Guid templateId, MailActor actor, CancellationToken cancellationToken)
    {
        var template = await db.MailTemplates.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.TemplateId == templateId, cancellationToken);
        return template is null ? null : await DetailAsync(template, actor, cancellationToken);
    }

    public async Task<MailTemplateDetail> CreateAsync(CreateMailTemplateRequest request, MailActor actor, CancellationToken cancellationToken)
    {
        var kind = kinds.Get(request.Kind);
        var name = (request.Name ?? "").Trim();
        if (name.Length is 0 or > 120) throw new CommandRefusedException("mail_template_name_invalid", "A template name is 1 to 120 characters.");
        if (request.Description is { Length: > 300 }) throw new CommandRefusedException("mail_template_description_invalid", "A description is at most 300 characters.");
        if (await db.MailTemplates.AnyAsync(x => x.TenantId == tenant.TenantId && x.Kind == kind.View.Kind && x.Name == name, cancellationToken))
            throw new CommandRefusedException("mail_template_name_exists", $"A {kind.View.Name} template called '{name}' already exists.");
        MailTemplateContent content;
        string startedFrom;
        if (request.CopyFromTemplateId is { } sourceId)
        {
            var source = await db.MailTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.TemplateId == sourceId, cancellationToken)
                ?? throw new CommandRefusedException("mail_template_unknown", "The template to copy does not exist.");
            if (source.Kind != kind.View.Kind) throw new CommandRefusedException("mail_template_kind_mismatch", "Copy a template of the same kind of message.");
            var sourceVersion = await db.MailTemplateVersions.AsNoTracking().Where(x => x.TenantId == tenant.TenantId && x.TemplateId == sourceId)
                .OrderByDescending(x => x.Status == MailTemplateStatus.Published).ThenByDescending(x => x.Version).FirstAsync(cancellationToken);
            content = Read(sourceVersion);
            startedFrom = source.Name;
        }
        else
        {
            content = kind.Starter();
            startedFrom = $"{kind.View.StarterName} (built in)";
        }
        var now = clock.UtcNow;
        var template = new MailTemplate
        {
            TenantId = tenant.TenantId, TemplateId = TimeOrderedId.New(now), Kind = kind.View.Kind, Name = name,
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            StartedFrom = startedFrom, CreatedAt = now, CreatedBy = actor.Ref,
        };
        db.MailTemplates.Add(template);
        db.MailTemplateVersions.Add(NewVersion(template, 1, content, actor, now));
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(template, actor, cancellationToken);
    }

    public async Task<MailTemplateSaveResult> SaveDraftAsync(Guid templateId, SaveMailTemplateDraftRequest request,
        MailActor actor, CancellationToken cancellationToken)
    {
        var template = await TemplateAsync(templateId, cancellationToken);
        var kind = kinds.Get(template.Kind);
        var content = Normalise(request.Content);
        var check = composer.Check(content, kind.View);
        if (request.SubmitForReview && !check.Passed) return new MailTemplateSaveResult(null, check);
        var now = clock.UtcNow;
        MailTemplateVersion version;
        if (request.VersionId is { } versionId)
        {
            version = await db.MailTemplateVersions.FirstOrDefaultAsync(x => x.TenantId == tenant.TenantId
                && x.TemplateId == templateId && x.VersionId == versionId, cancellationToken)
                ?? throw new CommandRefusedException("mail_template_version_unknown", "That version does not exist.");
            if (version.Status != MailTemplateStatus.Draft)
                throw new CommandRefusedException("mail_template_version_immutable", "A published or retired version is never edited; save a new draft.");
            DocumentVersion.RequireExpected(request.ExpectedVersion, version.RowVersion, version.UpdatedBy, version.UpdatedAt);
            version.Content = JsonSerializer.Serialize(content, Json);
            version.UpdatedAt = now;
            version.UpdatedBy = actor.Ref;
        }
        else
        {
            var existing = await db.MailTemplateVersions.Where(x => x.TenantId == tenant.TenantId && x.TemplateId == templateId)
                .Select(x => new { x.Version, x.Status }).ToListAsync(cancellationToken);
            if (existing.Any(x => x.Status == MailTemplateStatus.Draft))
                throw new CommandRefusedException("mail_template_draft_exists", "This template already has a draft; edit that one.");
            version = NewVersion(template, existing.Max(x => x.Version) + 1, content, actor, now);
            db.MailTemplateVersions.Add(version);
        }
        version.SubmittedForReview = request.SubmitForReview;
        await db.SaveChangesAsync(cancellationToken);
        return new MailTemplateSaveResult(await DetailAsync(template, actor, cancellationToken), check);
    }

    public async Task<MailTemplateSaveResult> PublishAsync(Guid templateId, Guid versionId, MailActor actor, CancellationToken cancellationToken)
    {
        if (!actor.IsTenantAdministrator)
            throw new CommandRefusedException("mail_template_publish_forbidden", "Only a tenant administrator publishes templates.", "Ask an administrator to publish.");
        var template = await TemplateAsync(templateId, cancellationToken);
        var kind = kinds.Get(template.Kind);
        var versions = await db.MailTemplateVersions.Where(x => x.TenantId == tenant.TenantId && x.TemplateId == templateId).ToListAsync(cancellationToken);
        var version = versions.FirstOrDefault(x => x.VersionId == versionId)
            ?? throw new CommandRefusedException("mail_template_version_unknown", "That version does not exist.");
        if (version.Status != MailTemplateStatus.Draft)
            throw new CommandRefusedException("mail_template_version_not_draft", "Only a draft is published.");
        var content = Read(version);
        var check = composer.Check(content, kind.View);
        if (!check.Passed) return new MailTemplateSaveResult(null, check);
        var now = clock.UtcNow;
        foreach (var previous in versions.Where(x => x.Status == MailTemplateStatus.Published))
        {
            previous.Status = MailTemplateStatus.Retired;
            previous.RetiredAt = now;
            previous.RetiredBy = actor.Ref;
        }
        version.Status = MailTemplateStatus.Published;
        version.PublishedAt = now;
        version.PublishedBy = actor.Ref;
        version.SubmittedForReview = false;
        // The first published template of a kind in a language becomes that language's default, so sending works at once.
        var taken = await db.MailTemplateDefaults.Where(x => x.TenantId == tenant.TenantId && x.Kind == template.Kind)
            .Select(x => x.Language).ToListAsync(cancellationToken);
        foreach (var language in content.Languages.Select(x => x.Language).Where(x => !taken.Contains(x)))
            db.MailTemplateDefaults.Add(new MailTemplateDefault
            {
                TenantId = tenant.TenantId, Kind = template.Kind, Language = language, TemplateId = templateId,
                UpdatedAt = now, UpdatedBy = actor.Ref,
            });
        await db.SaveChangesAsync(cancellationToken);
        return new MailTemplateSaveResult(await DetailAsync(template, actor, cancellationToken), check);
    }

    /// <summary>Stops a template being used: its published version is retired and it is no longer a default.</summary>
    public async Task<MailTemplateDetail> RetireAsync(Guid templateId, MailActor actor, CancellationToken cancellationToken)
    {
        if (!actor.IsTenantAdministrator)
            throw new CommandRefusedException("mail_template_publish_forbidden", "Only a tenant administrator retires templates.");
        var template = await TemplateAsync(templateId, cancellationToken);
        var now = clock.UtcNow;
        foreach (var version in await db.MailTemplateVersions.Where(x => x.TenantId == tenant.TenantId && x.TemplateId == templateId
                     && x.Status == MailTemplateStatus.Published).ToListAsync(cancellationToken))
        {
            version.Status = MailTemplateStatus.Retired;
            version.RetiredAt = now;
            version.RetiredBy = actor.Ref;
        }
        db.MailTemplateDefaults.RemoveRange(await db.MailTemplateDefaults
            .Where(x => x.TenantId == tenant.TenantId && x.TemplateId == templateId).ToListAsync(cancellationToken));
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(template, actor, cancellationToken);
    }

    public async Task<MailTemplateDetail> SetDefaultAsync(Guid templateId, SetMailTemplateDefaultRequest request, MailActor actor,
        CancellationToken cancellationToken)
    {
        if (!actor.IsTenantAdministrator)
            throw new CommandRefusedException("mail_template_publish_forbidden", "Only a tenant administrator chooses the default templates.");
        var template = await TemplateAsync(templateId, cancellationToken);
        var published = await db.MailTemplateVersions.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.TemplateId == templateId && x.Status == MailTemplateStatus.Published, cancellationToken)
            ?? throw new CommandRefusedException("mail_template_not_published", "Publish the template before making it a default.");
        var languages = Read(published).Languages.Select(x => x.Language).ToHashSet(StringComparer.Ordinal);
        var missing = request.Languages.Where(x => !languages.Contains(x)).ToList();
        if (missing.Count > 0)
            throw new CommandRefusedException("mail_template_language_missing", $"The published version has no {string.Join(", ", missing.Select(MailTemplateComposer.LanguageName))} wording.");
        var now = clock.UtcNow;
        var current = await db.MailTemplateDefaults.Where(x => x.TenantId == tenant.TenantId && x.Kind == template.Kind).ToListAsync(cancellationToken);
        foreach (var language in request.Languages.Distinct())
        {
            var row = current.FirstOrDefault(x => x.Language == language);
            if (row is null)
                db.MailTemplateDefaults.Add(new MailTemplateDefault
                {
                    TenantId = tenant.TenantId, Kind = template.Kind, Language = language, TemplateId = templateId,
                    UpdatedAt = now, UpdatedBy = actor.Ref,
                });
            else
            {
                row.TemplateId = templateId;
                row.UpdatedAt = now;
                row.UpdatedBy = actor.Ref;
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(template, actor, cancellationToken);
    }

    public async Task<MailPreviewView> PreviewAsync(Guid templateId, MailPreviewRequest request, MailActor actor, CancellationToken cancellationToken)
    {
        var template = await TemplateAsync(templateId, cancellationToken);
        var kind = kinds.Get(template.Kind);
        MailTemplateContent content;
        if (request.Content is { } unsaved) content = Normalise(unsaved);
        else if (request.VersionId is { } versionId)
            content = Read(await db.MailTemplateVersions.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenant.TenantId
                && x.TemplateId == templateId && x.VersionId == versionId, cancellationToken)
                ?? throw new CommandRefusedException("mail_template_version_unknown", "That version does not exist."));
        else throw new CommandRefusedException("mail_preview_content_required", "Preview the unsaved layout or a stored version.");
        return await rendering.PreviewAsync(kind, content, request.Language, request.RecordId, null, request.Note, actor,
            signedBy: "me", cancellationToken);
    }

    /// <summary>Sends the unsaved layout, filled from a real record, to the designer's own address only.</summary>
    public async Task<MailQueuedView> TestSendAsync(Guid templateId, MailTestSendRequest request, MailActor actor, CancellationToken cancellationToken)
    {
        var template = await TemplateAsync(templateId, cancellationToken);
        var kind = kinds.Get(template.Kind);
        var content = Normalise(request.Content);
        var check = composer.Check(content, kind.View);
        if (!check.Passed)
            throw new CommandRefusedException("mail_template_invalid", string.Join(" ", check.Errors), "Fix the checks, then send the test again.");
        var recordId = request.RecordId ?? await FirstSampleAsync(kind, cancellationToken)
            ?? throw new CommandRefusedException("mail_preview_record_required", "There is no record to fill the test from yet.");
        var message = await rendering.RenderAsync(kind, content, request.Language, recordId, actor.Name, null, actor, "me", cancellationToken);
        var attachment = content.AttachPdf ? await kind.AttachmentAsync(recordId, request.Language, cancellationToken) : null;
        var id = await outbox.QueueAsync(new OutgoingMail(kind.View.Kind, tenant.TenantId, Guid.TryParse(actor.Ref, out var userId) ? userId : null,
            new MailRecipient(actor.Email, actor.Name), "[Test] " + message.Mail.Subject, message.Mail.Html, message.Mail.Text,
            Attachments: attachment is null ? null : [new MailAttachment(attachment.FileName, attachment.MediaType, attachment.Content)]),
            cancellationToken);
        return new MailQueuedView([id]);
    }

    public static MailTemplateContent Read(MailTemplateVersion version) =>
        JsonSerializer.Deserialize<MailTemplateContent>(version.Content, Json)
        ?? throw new InvalidDataException($"Mail template version {version.VersionId} has no content.");

    /// <summary>Trims what the designer sent and drops empty optional parts, so stored content compares cleanly.</summary>
    public static MailTemplateContent Normalise(MailTemplateContent content) => content with
    {
        Languages = (content.Languages ?? []).Select(language => language with
        {
            Language = (language.Language ?? "").Trim(),
            Subject = (language.Subject ?? "").Trim(),
            Preheader = string.IsNullOrWhiteSpace(language.Preheader) ? null : language.Preheader.Trim(),
            BandLabel = (language.BandLabel ?? "").Trim(),
            Heading = (language.Heading ?? "").Trim(),
            Blocks = (language.Blocks ?? []).Select(block => block with
            {
                Type = (block.Type ?? "").Trim(),
                When = string.IsNullOrWhiteSpace(block.When) ? null : block.When.Trim(),
                Each = string.IsNullOrWhiteSpace(block.Each) ? null : block.Each.Trim(),
                Link = string.IsNullOrWhiteSpace(block.Link) ? null : block.Link.Trim(),
            }).ToList(),
        }).ToList(),
    };

    internal static async Task<Guid?> FirstSampleAsync(ICustomerMailKind kind, CancellationToken cancellationToken) =>
        (await kind.SamplesAsync(1, cancellationToken)) is [var first, ..] ? first.RecordId : null;

    private async Task<MailTemplate> TemplateAsync(Guid templateId, CancellationToken cancellationToken) =>
        await db.MailTemplates.FirstOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.TemplateId == templateId, cancellationToken)
        ?? throw new CommandRefusedException("mail_template_unknown", "That template does not exist.");

    private static MailTemplateVersion NewVersion(MailTemplate template, int number, MailTemplateContent content, MailActor actor, DateTime now) => new()
    {
        TenantId = template.TenantId, VersionId = TimeOrderedId.New(now), TemplateId = template.TemplateId, Version = number,
        Status = MailTemplateStatus.Draft, Content = JsonSerializer.Serialize(content, Json),
        CreatedAt = now, CreatedBy = actor.Ref, UpdatedAt = now, UpdatedBy = actor.Ref,
    };

    private MailTemplateRow Row(MailTemplate template, IReadOnlyList<MailTemplateVersion> versions, IReadOnlyList<string> defaults, int sent)
    {
        var published = versions.FirstOrDefault(x => x.Status == MailTemplateStatus.Published);
        var draft = versions.FirstOrDefault(x => x.Status == MailTemplateStatus.Draft);
        var latest = versions.MaxBy(x => x.UpdatedAt)!;
        var languages = Read(published ?? draft ?? versions.MaxBy(x => x.Version)!).Languages.Select(x => x.Language).ToList();
        var kindName = kinds.Exists(template.Kind) ? kinds.Get(template.Kind).View.Name : template.Kind;
        return new MailTemplateRow(template.TemplateId, template.Name, template.Description, template.Kind, kindName,
            template.StartedFrom, languages, published?.Version, published?.PublishedAt, draft?.Version,
            draft?.SubmittedForReview ?? false, published is null && draft is null, latest.UpdatedAt,
            latest.PublishedBy is { } publisher && latest.PublishedAt >= latest.UpdatedAt ? publisher : latest.UpdatedBy, sent, defaults);
    }

    private async Task<MailTemplateDetail> DetailAsync(MailTemplate template, MailActor actor, CancellationToken cancellationToken)
    {
        var versions = await db.MailTemplateVersions.AsNoTracking().Where(x => x.TenantId == tenant.TenantId && x.TemplateId == template.TemplateId)
            .OrderByDescending(x => x.Version).ToListAsync(cancellationToken);
        var defaults = await db.MailTemplateDefaults.AsNoTracking().Where(x => x.TenantId == tenant.TenantId && x.TemplateId == template.TemplateId)
            .Select(x => x.Language).OrderBy(x => x).ToListAsync(cancellationToken);
        var names = await NamesAsync(versions.SelectMany(x => new[] { x.CreatedBy, x.UpdatedBy, x.PublishedBy ?? "" }), cancellationToken);
        string Name(string? reference) => reference is null ? "" : names.GetValueOrDefault(reference, reference);
        var kind = kinds.Get(template.Kind);
        return new MailTemplateDetail(template.TemplateId, template.Name, template.Description, template.Kind, template.StartedFrom,
            versions.Select(x => new MailTemplateVersionView(x.VersionId, x.Version, x.Status, Read(x), x.CreatedAt, Name(x.CreatedBy),
                x.UpdatedAt, Name(x.UpdatedBy), x.PublishedBy is null ? null : Name(x.PublishedBy), x.PublishedAt, x.SubmittedForReview,
                DocumentVersion.Encode(x.RowVersion))).ToList(),
            defaults, kind.View, actor.IsTenantAdministrator, await kind.SamplesAsync(5, cancellationToken));
    }

    private async Task<IReadOnlyDictionary<string, string>> NamesAsync(IEnumerable<string> references, CancellationToken cancellationToken)
    {
        var distinct = references.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Take(IUserDirectory.MaxReferences).ToList();
        return distinct.Count == 0 ? new Dictionary<string, string>() : await people.DisplayNamesAsync(distinct, cancellationToken);
    }
}

/// <summary>
/// Fills a template from a record for one recipient: the kind's facts plus who it is for, the personal note and the
/// signature, then the shell (company name, footer, closing line). Shared by the designer preview, test sends, compose
/// preview and sending, so what is previewed is what is sent.
/// </summary>
public sealed class CustomerMailRendering(MailTemplateComposer composer, CompanyMailIdentity company,
    Yojana.Serialisation.TimeZones.TenantTimeZoneSettings zones)
{
    public const int MaxNote = 1000;

    public sealed record Prepared(CustomerMailRecord Record, RenderedMail Mail);

    public async Task<Prepared> RenderAsync(ICustomerMailKind kind, MailTemplateContent content, string language, Guid recordId,
        string? recipientName, string? note, MailActor actor, string signedBy, CancellationToken cancellationToken)
    {
        var languageContent = content.Languages.FirstOrDefault(x => x.Language == language)
            ?? throw new CommandRefusedException("mail_template_language_missing", $"The template has no {MailTemplateComposer.LanguageName(language)} wording.");
        var record = await kind.ReadAsync(recordId, cancellationToken)
            ?? throw new CommandRefusedException("mail_record_unknown", "That record does not exist.");
        if (note is { Length: > MaxNote }) throw new CommandRefusedException("mail_note_too_long", $"A personal note is at most {MaxNote} characters.");
        var identity = await company.GetCurrentAsync(cancellationToken);
        var companyName = identity.CompanyName;
        var senderCompany = string.IsNullOrWhiteSpace(identity.SenderName) ? companyName : identity.SenderName;
        var payload = (JsonObject)record.Facts.DeepClone();
        payload["recipient"] = new JsonObject { ["name"] = string.IsNullOrWhiteSpace(recipientName) ? record.CustomerName ?? "customer" : recipientName.Trim() };
        payload["message"] = new JsonObject { ["note"] = string.IsNullOrWhiteSpace(note) ? null : note.Trim() };
        payload["sender"] = signedBy == "team"
            ? new JsonObject { ["name"] = senderCompany, ["team"] = null, ["company"] = companyName }
            : new JsonObject { ["name"] = actor.Name, ["team"] = Team(actor.Roles), ["company"] = companyName };
        payload["presentation"] = new JsonObject { ["time_zone"] = (await zones.PolicyAsync(cancellationToken))?.TenantTimeZone ?? "UTC" };
        using var document = JsonDocument.Parse(payload.ToJsonString());
        var shell = new MailShell(companyName, CompanyMailIdentity.Compose(companyName, identity.FooterLine),
            kind.Footer(companyName, record.CustomerName), $"Sent with Yojana on behalf of {companyName}.");
        return new Prepared(record, composer.Render(languageContent, document.RootElement, shell));
    }

    public async Task<MailPreviewView> PreviewAsync(ICustomerMailKind kind, MailTemplateContent content, string language, Guid? recordId,
        string? recipientName, string? note, MailActor actor, string signedBy, CancellationToken cancellationToken)
    {
        var check = composer.Check(content, kind.View);
        var record = recordId ?? await MailTemplateService.FirstSampleAsync(kind, cancellationToken);
        if (!check.Passed || record is null)
            return new MailPreviewView("", null, "", "", check, null, record is null ? "No record to fill the preview from yet." : null);
        var prepared = await RenderAsync(kind, content, language, record.Value, recipientName, note, actor, signedBy, cancellationToken);
        return new MailPreviewView(prepared.Mail.Subject, prepared.Mail.Preheader, prepared.Mail.Html, prepared.Mail.Text, check,
            content.AttachPdf ? prepared.Record.AttachmentName : null, prepared.Record.AboutLabel);
    }

    /// <summary>The signer's team as a readable role name, service roles first.</summary>
    public static string? Team(IReadOnlyList<string> roles)
    {
        string[] order = [YojanaRole.ServiceDesk, YojanaRole.CustomerService, YojanaRole.ServiceSupervisor, YojanaRole.ServiceManager];
        var role = order.FirstOrDefault(roles.Contains) ?? roles.FirstOrDefault(x => x != YojanaRole.TenantAdministrator) ?? (roles.Count == 0 ? null : roles[0]);
        if (role is null) return null;
        var words = string.Join(' ', role.Split('_'));
        return words.Length == 0 ? null : char.ToUpperInvariant(words[0]) + words[1..];
    }
}
