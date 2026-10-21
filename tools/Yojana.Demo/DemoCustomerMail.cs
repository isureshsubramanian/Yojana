using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Seeding;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.CustomerMail;

namespace Yojana.Demo;

public sealed partial class DemoLoader
{
    private const string StandardMailTemplate = "What's covered · Asterra standard";
    private const string ShortMailTemplate = "What's covered · short";

    /// <summary>
    /// Customer e-mail (P9-T20): the built-in What's covered layout published as the English default, so the unit's
    /// "E-mail to customer…" button shows, and a shorter variant without the PDF waiting for an administrator's review.
    /// Rerunning leaves both as they are.
    /// </summary>
    private async Task CreateCustomerMailTemplatesAsync(CancellationToken cancellationToken)
    {
        using var scope = TenantScope();
        var admin = await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Users.AsNoTracking()
            .SingleAsync(user => user.Email == $"admin@{DevelopmentSeeder.EmailDomain}", cancellationToken);
        var mailActor = new MailActor(admin.Id.ToString(), admin.DisplayName, admin.Email, [YojanaRole.TenantAdministrator]);
        var templates = scope.ServiceProvider.GetRequiredService<MailTemplateService>();
        var existing = (await templates.ListAsync(null, 0, 100, cancellationToken)).Rows.Select(row => row.Name).ToHashSet(StringComparer.Ordinal);

        if (!existing.Contains(StandardMailTemplate))
        {
            var created = await templates.CreateAsync(new CreateMailTemplateRequest(CustomerMailKind.CoverageSheet, StandardMailTemplate,
                "The full cover table with the customer PDF attached"), mailActor, cancellationToken);
            var published = await templates.PublishAsync(created.TemplateId, created.Versions.Single().VersionId, mailActor, cancellationToken);
            if (published.Template is null)
                throw new InvalidOperationException($"the built-in layout failed its checks: {string.Join(" ", published.Check.Errors)}");
        }

        if (!existing.Contains(ShortMailTemplate))
        {
            var created = await templates.CreateAsync(new CreateMailTemplateRequest(CustomerMailKind.CoverageSheet, ShortMailTemplate,
                "No table; a link to the portal instead"), mailActor, cancellationToken);
            var draft = created.Versions.Single();
            var english = draft.Content.Languages.Single();
            var shorter = new MailTemplateContent([english with
            {
                Subject = "Your {{item.model_name}} {{unit.serial}}: warranty cover",
                Heading = "Your cover at a glance",
                Blocks =
                [
                    new(MailBlockType.Paragraph, "Dear {{recipient.name}},"),
                    new(MailBlockType.Paragraph, "{{message.note}}", When: "message.note"),
                    new(MailBlockType.Paragraph, "Your {{item.model_name}}, serial **{{unit.serial}}**, is covered as of {{date sheet.as_of}}. "
                        + "Every cover, with what is left and until when, is on the unit's page."),
                    new(MailBlockType.Button, Label: "See what is covered", Link: "links.unit_portal"),
                    english.Blocks[^1],
                ],
            }], AttachPdf: false, AskForNote: true);
            var saved = await templates.SaveDraftAsync(created.TemplateId,
                new SaveMailTemplateDraftRequest(draft.VersionId, draft.ExpectedVersion, shorter, SubmitForReview: true), mailActor, cancellationToken);
            if (saved.Template is null)
                throw new InvalidOperationException($"the short layout failed its checks: {string.Join(" ", saved.Check.Errors)}");
        }
    }
}
