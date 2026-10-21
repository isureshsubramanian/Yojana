namespace Yojana.Serialisation.Contracts;

/// <summary>Kinds of customer e-mail (P9-T20). The value is also the platform mail kind recorded in the delivery log.</summary>
public static class CustomerMailKind
{
    public const string CoverageSheet = "customer.coverage_sheet";
}

public static class MailTemplateStatus
{
    public const string Draft = "draft";
    public const string Published = "published";
    public const string Retired = "retired";
}

/// <summary>
/// Layout blocks of a customer e-mail. Text in a block is the shared template language (<c>{{field}}</c>, <c>#if</c>,
/// <c>#each</c> with <c>{{else}}</c>) plus three marks: <c>**bold**</c>, <c>*italic*</c> and <c>[label]({{links.…}})</c>.
/// The server turns blocks into e-mail-safe HTML and a plain-text part; the browser never sends markup.
/// </summary>
public static class MailBlockType
{
    public const string Heading = "heading";
    public const string Paragraph = "paragraph";
    public const string Table = "table";
    public const string Panel = "panel";
    public const string Button = "button";
    public const string SignOff = "sign_off";

    public static readonly IReadOnlyList<string> All = [Heading, Paragraph, Table, Panel, Button, SignOff];
}

/// <param name="When">A field path; the block shows only when it has a value (an <c>#if</c> around the block).</param>
/// <param name="Each">For a table: the repeating field the rows come from.</param>
/// <param name="EmptyText">For a table: the sentence shown instead when the list is empty (required).</param>
/// <param name="Link">For a button: a <c>links.*</c> field.</param>
public sealed record MailBlock(
    string Type,
    string? Text = null,
    string? When = null,
    string? Each = null,
    string? EmptyText = null,
    IReadOnlyList<MailTableColumn>? Columns = null,
    IReadOnlyList<MailPanelFact>? Facts = null,
    string? Label = null,
    string? Link = null);

public sealed record MailTableColumn(string Header, string Text);

public sealed record MailPanelFact(string Label, string Text);

/// <param name="Preheader">The line an inbox shows next to the subject.</param>
/// <param name="BandLabel">The small capitals at the right of the header band.</param>
public sealed record MailLanguageContent(
    string Language,
    string Subject,
    string? Preheader,
    string BandLabel,
    string Heading,
    IReadOnlyList<MailBlock> Blocks);

/// <param name="AttachPdf">The record's customer document goes with every message; the sender cannot remove it (owner
/// decision 2026-10-21).</param>
/// <param name="AskForNote">The compose screen offers a personal note placed after the greeting.</param>
public sealed record MailTemplateContent(IReadOnlyList<MailLanguageContent> Languages, bool AttachPdf, bool AskForNote);

/// <param name="Path">As written in a template; inside a repeating field the item fields are qualified
/// (<c>covers.family_name</c>) and written unqualified inside the block (<c>{{family_name}}</c>).</param>
/// <param name="Repeats">A list: use it with <c>#each</c> or a table block.</param>
/// <param name="ItemOf">For an item field, the repeating field it belongs to.</param>
public sealed record MailField(string Path, string Label, string Group, bool Repeats = false, string? ItemOf = null,
    string? Format = null);

public sealed record MailKindView(string Kind, string Name, string RecordLabel, string StarterName,
    string StarterDescription, bool HasPdf, IReadOnlyList<MailField> Fields);

public sealed record MailTemplateCheck(bool Passed, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings,
    IReadOnlyList<string> FieldsUsed, int SubjectLength);

public sealed record MailTemplateRow(Guid TemplateId, string Name, string? Description, string Kind, string KindName,
    string? StartedFrom, IReadOnlyList<string> Languages, int? PublishedVersion, DateTime? PublishedAt, int? DraftVersion,
    bool WaitingForReview, bool Retired, DateTime LastChangeAt, string LastChangeBy, int Sent30Days,
    IReadOnlyList<string> DefaultFor);

public sealed record MailTemplateKpis(int Published, int PublishedKinds, int Drafts, int WaitingForReview,
    int Sent30Days, decimal? DeliveredShare, IReadOnlyList<string> Languages);

public sealed record MailTemplatePage(IReadOnlyList<MailTemplateRow> Rows, int Total, int Skip, int Take,
    MailTemplateKpis Kpis, IReadOnlyList<MailKindView> Kinds);

public sealed record MailTemplateVersionView(Guid VersionId, int Version, string Status, MailTemplateContent Content,
    DateTime CreatedAt, string CreatedBy, DateTime UpdatedAt, string UpdatedBy, string? PublishedBy, DateTime? PublishedAt,
    bool SubmittedForReview, string ExpectedVersion);

public sealed record MailTemplateDetail(Guid TemplateId, string Name, string? Description, string Kind, string? StartedFrom,
    IReadOnlyList<MailTemplateVersionView> Versions, IReadOnlyList<string> DefaultFor, MailKindView KindView,
    bool CanPublish, IReadOnlyList<MailSampleRecord> SampleRecords);

public sealed record MailSampleRecord(Guid RecordId, string Label);

public sealed record CreateMailTemplateRequest(string Kind, string Name, string? Description = null,
    Guid? CopyFromTemplateId = null);

/// <param name="VersionId">The draft being edited, or null to start the next version from <paramref name="Content"/>.</param>
/// <param name="ExpectedVersion">The draft's version token when editing one.</param>
public sealed record SaveMailTemplateDraftRequest(Guid? VersionId, string? ExpectedVersion, MailTemplateContent Content,
    bool SubmitForReview = false);

public sealed record MailTemplateSaveResult(MailTemplateDetail? Template, MailTemplateCheck Check);

public sealed record SetMailTemplateDefaultRequest(IReadOnlyList<string> Languages);

/// <summary>Renders either the given unsaved content or a stored version against a real record.</summary>
public sealed record MailPreviewRequest(Guid? RecordId, string Language, MailTemplateContent? Content = null,
    Guid? VersionId = null, string? Note = null);

public sealed record MailPreviewView(string Subject, string? Preheader, string Html, string Text, MailTemplateCheck Check,
    string? AttachmentName, string? RecordLabel);

public sealed record MailTestSendRequest(Guid? RecordId, string Language, MailTemplateContent Content);

public sealed record MailQueuedView(IReadOnlyList<Guid> MessageIds);

// Compose (from a unit's What's covered page).

public sealed record CustomerContact(string Name, string Address, string? Role);

public sealed record ComposeTemplateChoice(Guid TemplateId, Guid VersionId, string Name, int Version, DateTime PublishedAt,
    IReadOnlyList<string> Languages, IReadOnlyList<string> DefaultFor, bool AskForNote, bool AttachPdf);

public sealed record CoverageMailComposeView(Guid UnitId, string Serial, string ItemName, string? CustomerName,
    IReadOnlyList<CustomerContact> Contacts, IReadOnlyList<ComposeTemplateChoice> Templates, string? CustomerLanguage,
    string SenderName, string? ReplyTo, string FromAddress, string SignedByName, string? SignedByTeam, string AttachmentName);

/// <param name="Confirmed">An address that is not one of the customer's contacts is sent only when the person confirmed it.</param>
public sealed record MailRecipientInput(string Address, string? Name = null, bool Confirmed = false);

/// <param name="SignedBy"><c>me</c> (the sender's name and role) or <c>team</c> (the company's sender name only).</param>
/// <param name="CopyToMeOnly">Sends one copy to the signed-in person instead of the recipients.</param>
public sealed record SendCoverageMail(Guid CommandId, Guid TemplateVersionId, string Language,
    IReadOnlyList<MailRecipientInput> To, IReadOnlyList<MailRecipientInput>? Cc = null, string? Note = null,
    string SignedBy = "me", bool CopyToMeOnly = false)
{
    public Guid UnitId { get; init; }
    public string ActorRef { get; init; } = "";
    public string ActorName { get; init; } = "";
    public string ActorEmail { get; init; } = "";
    public IReadOnlyList<string> ActorRoles { get; init; } = [];
}

public sealed record ComposePreviewRequest(Guid TemplateVersionId, string Language, string? RecipientName = null,
    string? Note = null, string SignedBy = "me");

// Sent log.

public static class CustomerMailStatus
{
    public const string Queued = "queued";
    public const string Retrying = "retrying";
    public const string Sent = "sent";
    public const string Failed = "failed";
}

public sealed record SentMailRow(Guid MessageId, DateTime QueuedAt, string To, string? ToName, int CcCount,
    string? CustomerName, string Subject, string TemplateName, int TemplateVersion, string Language, string Kind,
    string KindName, string AboutLabel, Guid? UnitId, string SentBy, bool Automatic, string Status, int Attempts,
    DateTime? NextAttemptAt, string? LastError);

public sealed record SentMailKpis(int Total, int Sent, int Retrying, int Failed, decimal? DeliveredShare,
    string? MostSentKind, int MostSentCount);

public sealed record MailKindName(string Kind, string Name);

public sealed record SentMailPage(IReadOnlyList<SentMailRow> Rows, int Total, string? NextCursor, SentMailKpis Kpis,
    IReadOnlyList<MailKindName> Kinds);

public sealed record SentMailEvent(DateTime At, string What, string? Detail);

public sealed record SentMailDetail(SentMailRow Row, IReadOnlyList<MailRecipientInput> Cc, string Html, string Text,
    string? AttachmentName, bool HasNote, string? Transport, DateTime? SentAt, IReadOnlyList<SentMailEvent> Timeline);
