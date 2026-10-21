using System.Text.Json;
using Xunit;
using Yojana.Serialisation.Certificates;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.CustomerMail;

namespace Yojana.Serialisation.Tests;

public sealed class MailTemplateComposerTests
{
    private static readonly MailTemplateComposer Composer = new(new CertificateTemplateEngine(new BuiltInCertificateTemplateLanguageCatalogue()));
    private static readonly MailShell Shell = new("Northwind Systems", "Service centre", "You receive this e-mail because you own equipment.",
        "Sent with Yojana on behalf of Northwind Systems.");

    private static readonly MailKindView Kind = new("customer.test", "Test sheet", "Unit", "Test (built in)", "", HasPdf: false,
    [
        new("recipient.name", "Recipient", "Recipient"),
        new("unit.serial", "Serial", "Unit"),
        new("message.note", "Note", "Message"),
        new("covers", "Covers", "Cover", Repeats: true),
        new("covers.family_name", "Cover name", "Cover", ItemOf: "covers"),
        new("links.unit_portal", "Unit page", "Links"),
    ]);

    private static MailLanguageContent English(params MailBlock[] blocks) =>
        new("en", "Cover for {{unit.serial}} & more", "Your cover at a glance", "Warranty", "What's covered", blocks);

    private static MailTemplateContent Content(params MailLanguageContent[] languages) => new(languages, AttachPdf: false, AskForNote: true);

    private static JsonElement Payload(object value) => JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);

    private static readonly JsonElement Record = Payload(new
    {
        recipient = new { name = "Priya <Raman>" },
        unit = new { serial = "RS11-0001" },
        message = new { note = (string?)null },
        covers = new[] { new { family_name = "Base warranty" }, new { family_name = "Accidental damage" } },
        links = new { unit_portal = "https://erp.example.invalid/portal/units/1" },
    });

    [Fact]
    public void Author_text_never_becomes_markup_and_the_three_marks_do()
    {
        var mail = Composer.Render(English(
            new MailBlock(MailBlockType.Paragraph, "Dear {{recipient.name}},\n<script>x</script> **bold** *soft* [your unit]({{links.unit_portal}})")),
            Record, Shell);

        Assert.Contains("Dear Priya &lt;Raman&gt;,<br>", mail.Html);
        Assert.Contains("&lt;script&gt;x&lt;/script&gt;", mail.Html);
        Assert.DoesNotContain("<script>", mail.Html);
        Assert.Contains("<strong>bold</strong>", mail.Html);
        Assert.Contains("<em>soft</em>", mail.Html);
        Assert.Contains("<a href=\"https://erp.example.invalid/portal/units/1\"", mail.Html);
        Assert.Contains("Dear Priya <Raman>,", mail.Text);
        Assert.Contains("bold soft your unit (https://erp.example.invalid/portal/units/1)", mail.Text);
        Assert.DoesNotContain("**", mail.Text);
    }

    [Fact]
    public void Subject_preview_line_and_heading_are_plain_single_lines()
    {
        var mail = Composer.Render(English(new MailBlock(MailBlockType.Paragraph, "Hello")), Record, Shell);

        Assert.Equal("Cover for RS11-0001 & more", mail.Subject);
        Assert.Equal("Your cover at a glance", mail.Preheader);
        Assert.StartsWith("What's covered\n\nHello", mail.Text);
        Assert.Contains("Sent with Yojana on behalf of Northwind Systems.", mail.Html);
        Assert.Contains("Sent with Yojana on behalf of Northwind Systems.", mail.Text);
    }

    [Fact]
    public void A_table_repeats_its_rows_and_shows_the_empty_sentence_for_an_empty_list()
    {
        var table = new MailBlock(MailBlockType.Table, Each: "covers", EmptyText: "No cover is active.",
            Columns: [new MailTableColumn("Cover", "{{family_name}}")]);

        var full = Composer.Render(English(table), Record, Shell);
        Assert.Contains("Base warranty", full.Html);
        Assert.Contains("Accidental damage", full.Html);
        Assert.DoesNotContain("No cover is active.", full.Html);
        Assert.Contains("- Cover: Base warranty", full.Text);

        var empty = Composer.Render(English(table), Payload(new { unit = new { serial = "X" }, covers = Array.Empty<object>() }), Shell);
        Assert.Contains("No cover is active.", empty.Html);
        Assert.Contains("No cover is active.", empty.Text);
    }

    [Fact]
    public void A_block_with_show_when_appears_only_when_its_field_has_a_value()
    {
        var content = English(new MailBlock(MailBlockType.Paragraph, "{{message.note}}", When: "message.note"),
            new MailBlock(MailBlockType.Paragraph, "Always"));

        var without = Composer.Render(content, Record, Shell);
        var with = Composer.Render(content, Payload(new { unit = new { serial = "X" }, message = new { note = "As requested." } }), Shell);

        Assert.DoesNotContain("As requested.", without.Html);
        Assert.Contains("Always", without.Html);
        Assert.Contains("As requested.", with.Html);
        Assert.Contains("As requested.", with.Text);
    }

    [Fact]
    public void The_check_accepts_a_sound_layout_and_lists_the_fields_it_uses()
    {
        var check = Composer.Check(Content(English(
            new MailBlock(MailBlockType.Paragraph, "Dear {{recipient.name}},"),
            new MailBlock(MailBlockType.Table, Each: "covers", EmptyText: "None.", Columns: [new MailTableColumn("Cover", "{{family_name}}")]),
            new MailBlock(MailBlockType.Button, Label: "Open", Link: "links.unit_portal"))), Kind);

        Assert.True(check.Passed, string.Join(" | ", check.Errors));
        Assert.Contains("recipient.name", check.FieldsUsed);
        Assert.Contains("unit.serial", check.FieldsUsed);
        Assert.Contains("links.unit_portal", check.FieldsUsed);
        Assert.Equal("Cover for {{unit.serial}} & more".Length, check.SubjectLength);
    }

    [Fact]
    public void The_check_refuses_what_could_mislead_a_customer_or_break_the_message()
    {
        var check = Composer.Check(new MailTemplateContent([
            English(
                new MailBlock(MailBlockType.Button, Label: "Pay now", Link: "unit.serial"),
                new MailBlock(MailBlockType.Paragraph, "{{upper unit.serial}}"),
                new MailBlock(MailBlockType.Paragraph, "{{#each covers}}{{family_name}}{{#each covers}}x{{else}}y{{/each}}{{/each}}"),
                new MailBlock(MailBlockType.Panel, Facts: []),
                new MailBlock("image", "logo.png"),
                new MailBlock(MailBlockType.Paragraph, "x", When: "unit.colour")),
            English(new MailBlock(MailBlockType.Paragraph, "Hello")) with { Subject = "Two\nlines" },
            English(new MailBlock(MailBlockType.Paragraph, "Hallo")) with { Language = "deutsch" },
        ], AttachPdf: true, AskForNote: false), Kind);

        Assert.False(check.Passed);
        Assert.Contains(check.Errors, x => x.Contains("a button links to one of the product's own links"));
        Assert.Contains(check.Errors, x => x.Contains("'upper' is not a helper"));
        Assert.Single(check.Errors, x => x.Contains("repeat over 'covers' needs an empty-list text"));
        Assert.Contains(check.Errors, x => x.Contains("a details panel has 1 to 8 rows"));
        Assert.Contains(check.Errors, x => x.Contains("'image' is not a block type"));
        Assert.Contains(check.Errors, x => x.Contains("'unit.colour', which is not a field"));
        Assert.Contains(check.Errors, x => x.Contains("Each language appears once."));
        Assert.Contains(check.Errors, x => x.Contains("the subject and preview line are one line each"));
        Assert.Contains(check.Errors, x => x.Contains("'deutsch' is not a language code"));
        Assert.Contains(check.Errors, x => x.Contains("Test sheet has no document to attach."));
        Assert.Contains(check.Warnings, x => x.Contains("different numbers of blocks"));
    }
}
