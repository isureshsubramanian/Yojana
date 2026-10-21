using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Mail;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Entitlements;

namespace Yojana.Serialisation.CustomerMail;

/// <summary>The record a message is about, as the kind supplies it: its facts for the template and who it belongs to.</summary>
public sealed record CustomerMailRecord(Guid RecordId, string AboutLabel, Guid? CustomerPartyRef, string? CustomerName,
    JsonObject Facts, string? AttachmentName);

/// <summary>
/// One kind of customer message: its typed field list, its built-in layout, and how a record becomes the facts a template
/// is filled from. A new kind (certificate issued, claim outcome, RMA update) is one more implementation registered in
/// <see cref="SerialisationModule"/>; templates, compose, sending and the sent log do not change.
/// </summary>
public interface ICustomerMailKind
{
    MailKindView View { get; }

    MailTemplateContent Starter();

    /// <summary>Real records to preview against in the designer, newest first.</summary>
    Task<IReadOnlyList<MailSampleRecord>> SamplesAsync(int take, CancellationToken cancellationToken);

    Task<CustomerMailRecord?> ReadAsync(Guid recordId, CancellationToken cancellationToken);

    /// <summary>The customer document sent with the message, made now from the same record; null when none can be made.</summary>
    Task<MailAttachmentFile?> AttachmentAsync(Guid recordId, string language, CancellationToken cancellationToken);

    /// <summary>Why the customer receives this message, shown above the company line.</summary>
    string Footer(string companyName, string? customerName);
}

public sealed record MailAttachmentFile(string FileName, byte[] Content, string MediaType = "application/pdf");

public sealed class CustomerMailKinds(IEnumerable<ICustomerMailKind> kinds)
{
    private readonly IReadOnlyDictionary<string, ICustomerMailKind> byKind = kinds.ToDictionary(x => x.View.Kind, StringComparer.Ordinal);

    public IReadOnlyList<MailKindView> Views => byKind.Values.Select(x => x.View).OrderBy(x => x.Name).ToList();

    public ICustomerMailKind Get(string kind) => byKind.TryGetValue(kind, out var found) ? found
        : throw new CommandRefusedException("mail_kind_unknown", $"'{kind}' is not a kind of customer message.");

    public bool Exists(string kind) => byKind.ContainsKey(kind);
}

/// <summary>The unit's What's covered sheet (P9-T19), e-mailed to the customer with the sheet as a PDF.</summary>
public sealed class CoverageSheetMailKind(SerialisationDbContext db, ITenantContext tenant, WhatsCoveredQuery coverage,
    IUnitCustomerDocument documents, MailOptions mail) : ICustomerMailKind
{
    private const int SummaryItems = 3;

    public MailKindView View { get; } = new(CustomerMailKind.CoverageSheet, "What's covered sheet", "Unit",
        "What's covered", "Greeting, cover table, not-covered summary, attachment, service request button.", HasPdf: true,
        [
            new("recipient.name", "Contact name", "Recipient"),
            new("customer.name", "Customer", "Recipient"),
            new("unit.serial", "Serial", "Unit"),
            new("item.model_name", "Model", "Unit"),
            new("sale.customer_reference", "Customer's PO", "Unit"),
            new("covers", "Each live cover", "Cover", Repeats: true),
            new("covers.family_name", "Cover", "Cover", ItemOf: "covers"),
            new("covers.policy_name", "Plan", "Cover", ItemOf: "covers"),
            new("covers.from", "Starts", "Cover", ItemOf: "covers", Format: "date"),
            new("covers.until", "Until", "Cover", ItemOf: "covers", Format: "date"),
            new("covers.customer_summary", "What it covers, with what is left", "Cover", ItemOf: "covers"),
            new("not_covered.summary", "Not covered summary", "Cover"),
            new("offers", "Coverable add-ons", "Cover", Repeats: true),
            new("offers.policy_name", "Add-on", "Cover", ItemOf: "offers"),
            new("offers.until", "Would run until", "Cover", ItemOf: "offers", Format: "date"),
            new("message.note", "Personal note", "Message"),
            new("sender.name", "Signed by", "Message"),
            new("sender.team", "Signer's team", "Message"),
            new("sender.company", "Company", "Message"),
            new("links.unit_portal", "Portal link", "Message"),
            new("sheet.as_of", "As of", "Message", Format: "date"),
            new("sheet.attachment_name", "Attachment name", "Message"),
        ]);

    public MailTemplateContent Starter() => new(
    [
        new MailLanguageContent("en", "Warranty cover for your {{item.model_name}} {{unit.serial}}",
            "What is covered for {{unit.serial}} and until when.", "Warranty cover", "What your equipment is covered for",
            [
                new(MailBlockType.Paragraph, "Dear {{recipient.name}},"),
                new(MailBlockType.Paragraph, "{{message.note}}", When: "message.note"),
                new(MailBlockType.Paragraph, "Here is the warranty cover for your {{item.model_name}}, serial **{{unit.serial}}**, as of {{date sheet.as_of}}. The full list of what is and is not covered is attached as a PDF."),
                new(MailBlockType.Table, Each: "covers", EmptyText: "This unit has no live cover today.",
                    Columns: [new("Cover", "{{family_name}}"), new("Until", "{{date until}}"), new("Notes", "{{customer_summary}}")]),
                new(MailBlockType.Paragraph, "What is not covered includes {{not_covered.summary}}.{{#each offers}} {{policy_name}} can be added, running until {{date until}}.{{else}}{{/each}}", When: "not_covered.summary"),
                new(MailBlockType.Panel, Facts:
                [
                    new("Attached", "{{sheet.attachment_name}}"),
                    new("Your reference", "{{#if sale.customer_reference}}{{sale.customer_reference}} · {{/if}}{{customer.name}}"),
                    new("Need to claim?", "Reply to this e-mail or raise a service request below."),
                ]),
                new(MailBlockType.Button, Label: "Raise a service request", Link: "links.unit_portal"),
                new(MailBlockType.SignOff, "Kind regards,\n{{sender.name}}\n{{#if sender.team}}{{sender.team}} · {{/if}}{{sender.company}}"),
            ]),
    ], AttachPdf: true, AskForNote: true);

    public async Task<IReadOnlyList<MailSampleRecord>> SamplesAsync(int take, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var units = await (from entitlement in db.Entitlements.AsNoTracking()
                           where entitlement.TenantId == tenantId && entitlement.Status == EntitlementStatus.Active
                           group entitlement by entitlement.UnitId into covers
                           orderby covers.Count() descending, covers.Key descending
                           select new { UnitId = covers.Key, Covers = covers.Count() }).Take(take).ToListAsync(cancellationToken);
        var samples = new List<MailSampleRecord>();
        foreach (var unit in units)
        {
            var sheet = await coverage.ReadAsync(unit.UnitId, cancellationToken);
            if (sheet is null) continue;
            samples.Add(new(unit.UnitId, Label(sheet)));
        }
        return samples;
    }

    public async Task<CustomerMailRecord?> ReadAsync(Guid recordId, CancellationToken cancellationToken)
    {
        var sheet = await coverage.ReadAsync(recordId, cancellationToken);
        if (sheet is null) return null;
        var tenantId = tenant.TenantId;
        // The owner is the unit's current one (as on the sheet); the open ownership episode adds the customer's PO.
        var owner = await db.Current.AsNoTracking().Where(x => x.TenantId == tenantId && x.UnitId == recordId)
            .Select(x => x.OwnerPartyRef).FirstOrDefaultAsync(cancellationToken);
        var customerPo = await db.Episodes.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.UnitId == recordId && x.Kind == EpisodeKind.Ownership && x.ValidTo == null)
            .Select(x => x.CustomerPoRef).FirstOrDefaultAsync(cancellationToken);
        var attachmentName = AttachmentName(sheet.Serial);
        var facts = new JsonObject
        {
            ["customer"] = new JsonObject { ["name"] = sheet.CustomerName },
            ["unit"] = new JsonObject { ["serial"] = sheet.Serial },
            ["item"] = new JsonObject { ["model_name"] = sheet.ItemName },
            ["sale"] = new JsonObject { ["customer_reference"] = customerPo },
            ["covers"] = new JsonArray(sheet.Covers.Select(cover => (JsonNode)new JsonObject
            {
                ["family_name"] = cover.FamilyName,
                ["policy_name"] = cover.PolicyName,
                ["from"] = Date(cover.From),
                ["until"] = Date(cover.Until),
                ["customer_summary"] = CoverSummary(sheet, cover),
            }).ToArray()),
            ["not_covered"] = new JsonObject { ["summary"] = Summary(sheet.Items.Where(x => !x.Covered).Select(x => x.CustomerWording)) },
            ["offers"] = new JsonArray((sheet.Offers ?? []).Select(offer => (JsonNode)new JsonObject
            {
                ["policy_name"] = offer.PolicyName,
                ["until"] = Date(offer.Until),
            }).ToArray()),
            ["links"] = new JsonObject { ["unit_portal"] = mail.Link($"/portal/units/{recordId:D}") },
            ["sheet"] = new JsonObject { ["as_of"] = Date(sheet.AsOf), ["attachment_name"] = attachmentName },
        };
        return new CustomerMailRecord(recordId, Label(sheet), owner, sheet.CustomerName, facts, attachmentName);
    }

    public async Task<MailAttachmentFile?> AttachmentAsync(Guid recordId, string language, CancellationToken cancellationToken)
    {
        var document = await documents.CreateAsync(recordId, language, cancellationToken);
        return document?.PdfBytes is { Length: > 0 } pdf ? new MailAttachmentFile(AttachmentName(document.MergeValues["unit.serial"]), pdf) : null;
    }

    public string Footer(string companyName, string? customerName) => customerName is null
        ? $"You receive this e-mail about equipment serviced by {companyName}."
        : $"You receive this e-mail because {customerName} owns equipment serviced by {companyName}.";

    private static string Label(WhatsCoveredPage sheet) =>
        $"{sheet.Serial} · {sheet.CustomerName ?? "no owner recorded"} · {sheet.Covers.Count} live cover{(sheet.Covers.Count == 1 ? "" : "s")}";

    private static string AttachmentName(string serial) =>
        $"What's covered · {new string(serial.Where(c => !char.IsControl(c) && c is not ('/' or '\\' or ':' or '"')).ToArray())}.pdf";

    private static string? Date(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>What the cover grants, in the customer's wording, with what is left of its limits or when it starts.</summary>
    private static string CoverSummary(WhatsCoveredPage sheet, ItemCover cover)
    {
        var granted = Summary(sheet.Items.Where(x => x.Covered && x.Grants.Any(g => g.Cover.EntitlementId == cover.EntitlementId))
            .Select(x => x.CustomerWording));
        var extras = new List<string>();
        if (cover.Future && cover.From is { } from) extras.Add($"starts {from:d MMM yyyy}");
        extras.AddRange(cover.Usage.Select(usage =>
            $"{usage.Remaining.ToString("0.##", CultureInfo.InvariantCulture)} {Meter(usage.MeterType, usage.Remaining)} left{(usage.Period == "contract_year" ? " this year" : "")}"));
        return string.Join(" · ", new[] { granted }.Concat(extras).Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private static string Summary(IEnumerable<string> wording)
    {
        var all = wording.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
        if (all.Count == 0) return "";
        // Later items continue the sentence in lower case, except a leading acronym ("UPS failure").
        var shown = all.Take(SummaryItems).Select((x, i) => i == 0 || x.Length < 2 || char.IsUpper(x[1]) ? x : char.ToLowerInvariant(x[0]) + x[1..]).ToList();
        var more = all.Count - shown.Count;
        return more > 0 ? $"{string.Join(", ", shown)} and {more} more" : shown.Count == 1 ? shown[0]
            : $"{string.Join(", ", shown[..^1])} and {shown[^1]}";
    }

    private static string Meter(string code, decimal remaining)
    {
        var words = code.Replace('-', '_').Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Equals("nbd", StringComparison.OrdinalIgnoreCase) ? "next-business-day" : word.ToLowerInvariant()).ToList();
        if (words.Count == 0) return code;
        var last = words[^1];
        if (remaining != 1 && !last.EndsWith('s')) words[^1] = last + "s";
        if (remaining == 1 && last.EndsWith('s') && last.Length > 1) words[^1] = last[..^1];
        return string.Join(' ', words);
    }
}
