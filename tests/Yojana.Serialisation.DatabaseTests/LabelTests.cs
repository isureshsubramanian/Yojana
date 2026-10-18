using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Migrations;
using Yojana.Serialisation.Entitlements;
using Yojana.Serialisation.Labels;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P6-T09/T10: MOD-10 invariants and service-desk tickets T5, T6, T7, T44 and T50.</summary>
[Collection("serialisation host")]
public sealed class LabelTests(SerialisationHostFixture fixture)
{
    private static readonly DateTime At = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Entity = Guid.Parse("10000000-0000-0000-0000-000000000006");
    private static readonly AmendmentCommitter Manager = new("service.manager", [YojanaRole.ServiceManager]);

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>()
            .InvokeAsync<T>(message, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private static ClaimsPrincipal Principal(TenantId tenant, string userRef, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(YojanaClaim.UserId, userRef),
            new(YojanaClaim.TenantId, tenant.ToString()),
        };
        claims.AddRange(roles.Select(role => new Claim(YojanaClaim.Role, role)));
        return new(new ClaimsIdentity(claims, "label-tests"));
    }

    private async Task<(Guid UnitId, Guid ItemId, Guid InternalIdentifierId)> UnitAsync(TenantId tenant, string marker,
        string? manufacturerSerial = null, Guid? itemId = null, string itemClass = "systems")
    {
        var item = itemId ?? Guid.NewGuid();
        fixture.Services.GetRequiredService<InMemoryItemPorts>().Register(new ItemSerialisationProfile(item, "SYS-" + marker,
            ItemSerialisationProfile.TrackingModes.Serial, SerialisationPoint.ProductionRelease, "standard_10y",
            ItemSerialisationProfile.ReusePolicies.Never, ItemSerialisationProfile.Completeness.Block,
            IdentifierScheme.Internal, ItemClass: itemClass));
        fixture.Services.GetRequiredService<InMemoryItemNamePort>().Register(new ItemLabel(item, "SYS-" + marker, "Aster System " + marker));
        var identifiers = new List<IdentifierInput> { new(IdentifierType.InternalSerial, marker + "-INT") };
        if (manufacturerSerial is not null) identifiers.Add(new(IdentifierType.ManufacturerSerial, manufacturerSerial));
        var commissioned = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item,
            UnitOrigin.Manufactured, [new CommissionLine(identifiers)], At, "production.operator", Entity,
            LocationId: Guid.NewGuid(), LocationKind: LocationKind.Warehouse));
        var line = Assert.Single(commissioned.Lines);
        Assert.NotNull(line.UnitId);
        var unitId = line.UnitId.GetValueOrDefault();
        using var scope = fixture.ScopeFor(tenant);
        var internalIdentifierId = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Identifiers.AsNoTracking()
            .Where(value => value.UnitId == unitId && value.Type == IdentifierType.InternalSerial && value.ValidTo == null)
            .Select(value => value.IdentifierId).SingleAsync();
        return (unitId, item, internalIdentifierId);
    }

    private async Task<(Guid TemplateId, Guid VersionId)> ActiveTemplateAsync(TenantId tenant, Guid sampleUnit,
        string name, string appliesTo, string appliesToValue, string layout, IReadOnlyList<LabelBinding> bindings,
        string symbology = LabelSymbology.Code128, IReadOnlyList<string>? staticText = null, bool regulated = false)
    {
        var template = await InvokeAsync<LabelTemplateCreated>(tenant, new CreateLabelTemplate(Guid.NewGuid(), name,
            appliesTo, appliesToValue, "label.admin"));
        var saved = await InvokeAsync<LabelTemplateVersionSaved>(tenant, new SaveLabelTemplateDraft(Guid.NewGuid(),
            template.TemplateId, null, At, null, LabelLayoutKind.Zpl, layout, staticText ?? [], bindings, symbology,
            sampleUnit, regulated, "label.admin"));
        var preview = await InvokeAsync<LabelTemplatePreview>(tenant, new PreviewLabelTemplate(saved.TemplateVersionId,
            sampleUnit, "label.admin"));
        Assert.Null(preview.RejectionReason);
        if (saved.LegalReview)
        {
            await InvokeAsync<LabelTemplateVersionChanged>(tenant, new RequestLabelLegalReview(Guid.NewGuid(), saved.TemplateVersionId, "label.admin"));
            await InvokeAsync<LabelTemplateVersionChanged>(tenant, new SignLabelLegalReview(Guid.NewGuid(), saved.TemplateVersionId, "legal.reviewer"));
        }
        if (regulated)
            await InvokeAsync<LabelTemplateVersionChanged>(tenant, new ApproveRegulatedLabel(Guid.NewGuid(), saved.TemplateVersionId, "regulatory.approver"));
        await InvokeAsync<LabelTemplateVersionChanged>(tenant, new ActivateLabelTemplateVersion(Guid.NewGuid(),
            saved.TemplateVersionId, At.AddMinutes(1), "label.admin"));
        return (template.TemplateId, saved.TemplateVersionId);
    }

    [DatabaseFact]
    public async Task A_technician_can_print_and_reprint_but_cannot_activate_a_template()
    {
        var tenant = await fixture.NewTenantAsync("labels-tech");
        var unit = await UnitAsync(tenant, "TECH", "TECH-MFG");
        var template = await ActiveTemplateAsync(tenant, unit.UnitId, "Operational label",
            LabelTemplateAppliesTo.ItemFamily, "systems", "SER={{serial}}",
            [new("serial", LabelBindableField.InternalSerial, true)]);
        using (var scope = fixture.ScopeFor(tenant))
        {
            var authorisation = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
            var technician = Principal(tenant, "technician.one", YojanaRole.Technician);
            Assert.True((await authorisation.AuthorizeAsync(technician, null, YojanaPolicy.LabelPrinter)).Succeeded);
            Assert.False((await authorisation.AuthorizeAsync(technician, null, YojanaPolicy.LabelAdministrator)).Succeeded);
        }

        var before = await UnitConsistencyAsync(tenant, unit.UnitId);
        var printed = await InvokeAsync<LabelPrintResult>(tenant, new PrintLabel(Guid.NewGuid(), unit.UnitId,
            template.TemplateId, null, "service-bench-1", At.AddMinutes(2), Entity, "technician.one"));
        var reprinted = await InvokeAsync<LabelPrintResult>(tenant, new ReprintLabel(Guid.NewGuid(), printed.JobId,
            "service-bench-1", At.AddMinutes(3), Entity, "technician.one"));

        Assert.Equal(LabelPrintStatus.Confirmed, printed.Status);
        Assert.Equal(LabelPrintStatus.Confirmed, reprinted.Status);
        Assert.Matches("^PJ-2026-[0-9]{5}$", printed.JobNumber);
        Assert.Matches("^PJ-2026-[0-9]{5}$", reprinted.JobNumber);
        Assert.NotEqual(printed.JobNumber, reprinted.JobNumber);
        var after = await UnitConsistencyAsync(tenant, unit.UnitId);
        Assert.Equal(before.OwnerEpisodeId, after.OwnerEpisodeId);
        Assert.Equal(before.CustodyEpisodeId, after.CustodyEpisodeId);
        Assert.Equal(LocationKind.Warehouse, after.CustodyLocationKind);
        Assert.Equal(before.Entitlements.Select(row => row.EntitlementId), after.Entitlements.Select(row => row.EntitlementId));
        Assert.Equal(before.IdentifierIds.Order(), after.IdentifierIds.Order());
        Assert.Equal([LabelEventType.Printed, LabelEventType.Reprinted], after.LabelEvents);
    }

    [DatabaseFact]
    public async Task A_label_administrator_without_the_legal_reviewer_role_cannot_sign()
    {
        var tenant = await fixture.NewTenantAsync("labels-legal-role");
        var unit = await UnitAsync(tenant, "LEGAL", "LEGAL-MFG");
        var created = await InvokeAsync<LabelTemplateCreated>(tenant, new CreateLabelTemplate(Guid.NewGuid(),
            "Legal role label", LabelTemplateAppliesTo.IdentifierType, IdentifierType.ManufacturerSerial, "label.admin"));
        await InvokeAsync<LabelTemplateVersionSaved>(tenant, new SaveLabelTemplateDraft(Guid.NewGuid(),
            created.TemplateId, null, At, null, LabelLayoutKind.Zpl, "{{serial}} Warranty Void", ["Warranty Void"],
            [new("serial", LabelBindableField.ManufacturerSerial, true)], LabelSymbology.Code128, unit.UnitId, false,
            "label.admin"));

        using var scope = fixture.ScopeFor(tenant);
        var authorisation = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        var administrator = Principal(tenant, "label.admin", YojanaRole.LabelAdministrator);
        var reviewer = Principal(tenant, "legal.reviewer", YojanaRole.LegalReviewer);
        Assert.True((await authorisation.AuthorizeAsync(administrator, null, YojanaPolicy.LabelAdministrator)).Succeeded);
        Assert.False((await authorisation.AuthorizeAsync(administrator, null, YojanaPolicy.LegalReviewer)).Succeeded);
        Assert.True((await authorisation.AuthorizeAsync(reviewer, null, YojanaPolicy.LegalReviewer)).Succeeded);
    }

    [DatabaseFact]
    public async Task A_draft_author_cannot_sign_legal_review_or_regulated_approval()
    {
        var tenant = await fixture.NewTenantAsync("labels-author-separation");
        var unit = await UnitAsync(tenant, "AUTHOR", "AUTHOR-MFG");
        var created = await InvokeAsync<LabelTemplateCreated>(tenant, new CreateLabelTemplate(Guid.NewGuid(),
            "Separated approval label", LabelTemplateAppliesTo.IdentifierType, IdentifierType.ManufacturerSerial,
            "draft.author"));
        var saved = await InvokeAsync<LabelTemplateVersionSaved>(tenant, new SaveLabelTemplateDraft(Guid.NewGuid(),
            created.TemplateId, null, At, null, LabelLayoutKind.Zpl, "{{serial}} Warranty Void", ["Warranty Void"],
            [new("serial", LabelBindableField.ManufacturerSerial, true)], LabelSymbology.Code128, unit.UnitId, true,
            "draft.author"));
        await InvokeAsync<LabelTemplateVersionChanged>(tenant, new RequestLabelLegalReview(Guid.NewGuid(),
            saved.TemplateVersionId, "label.admin"));

        var legalConflict = await Assert.ThrowsAsync<CommandRefusedException>(() =>
            InvokeAsync<LabelTemplateVersionChanged>(tenant, new SignLabelLegalReview(Guid.NewGuid(),
                saved.TemplateVersionId, "draft.author")));
        var regulatedConflict = await Assert.ThrowsAsync<CommandRefusedException>(() =>
            InvokeAsync<LabelTemplateVersionChanged>(tenant, new ApproveRegulatedLabel(Guid.NewGuid(),
                saved.TemplateVersionId, "draft.author")));
        Assert.Equal("label_legal_review_author_conflict", legalConflict.Rule);
        Assert.Equal("label_regulated_approval_author_conflict", regulatedConflict.Rule);

        await InvokeAsync<LabelTemplateVersionChanged>(tenant, new SignLabelLegalReview(Guid.NewGuid(),
            saved.TemplateVersionId, "legal.reviewer"));
        await InvokeAsync<LabelTemplateVersionChanged>(tenant, new ApproveRegulatedLabel(Guid.NewGuid(),
            saved.TemplateVersionId, "legal.reviewer"));
        using var scope = fixture.ScopeFor(tenant);
        var version = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().LabelTemplateVersions
            .AsNoTracking().SingleAsync(row => row.TemplateVersionId == saved.TemplateVersionId);
        Assert.Equal("draft.author", version.ActorRef);
        Assert.Equal("legal.reviewer", version.LegalReviewedBy);
        Assert.Equal("legal.reviewer", version.ApprovedBy);
    }

    [DatabaseFact]
    public async Task Tickets_T5_and_T7_require_content_bound_review_preview_and_regulated_approval_then_freeze_the_version()
    {
        var tenant = await fixture.NewTenantAsync("labels-review");
        var unit = await UnitAsync(tenant, "REV", "REV-MFG");
        var created = await InvokeAsync<LabelTemplateCreated>(tenant, new CreateLabelTemplate(Guid.NewGuid(),
            "Regulated serial label", LabelTemplateAppliesTo.IdentifierType, IdentifierType.ManufacturerSerial, "label.admin"));

        var unknown = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<LabelTemplateVersionSaved>(tenant,
            new SaveLabelTemplateDraft(Guid.NewGuid(), created.TemplateId, null, At, null, LabelLayoutKind.Zpl,
                "^FD{{serial}}^FS", [], [new("serial", "unit.unknown", true)], LabelSymbology.Code128, unit.UnitId, false, "label.admin")));
        Assert.Equal("label_template_draft_invalid", unknown.Rule);
        var staticRevision = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<LabelTemplateVersionSaved>(tenant,
            new SaveLabelTemplateDraft(Guid.NewGuid(), created.TemplateId, null, At, null, LabelLayoutKind.Zpl,
                "^FD{{serial}}^FS", ["Rev A"], [new("serial", LabelBindableField.ManufacturerSerial, true)],
                LabelSymbology.Code128, unit.UnitId, false, "label.admin")));
        Assert.Contains("revision", staticRevision.Message, StringComparison.OrdinalIgnoreCase);

        var commandId = Guid.NewGuid();
        var saved = await InvokeAsync<LabelTemplateVersionSaved>(tenant, new SaveLabelTemplateDraft(commandId,
            created.TemplateId, null, At, null, LabelLayoutKind.Zpl, "^FD{{serial}} Warranty Void If Removed^FS",
            ["Warranty Void If Removed"], [new("serial", LabelBindableField.ManufacturerSerial, true)],
            LabelSymbology.Code128, unit.UnitId, true, "label.admin"));
        Assert.True(saved.LegalReview);
        Assert.True((await InvokeAsync<LabelTemplateVersionSaved>(tenant, new SaveLabelTemplateDraft(commandId,
            created.TemplateId, null, At, null, LabelLayoutKind.Zpl, "^FD{{serial}} Warranty Void If Removed^FS",
            ["Warranty Void If Removed"], [new("serial", LabelBindableField.ManufacturerSerial, true)],
            LabelSymbology.Code128, unit.UnitId, true, "label.admin"))).Replayed);

        Assert.Equal("label_preview_required", (await Assert.ThrowsAsync<CommandRefusedException>(() =>
            InvokeAsync<LabelTemplateVersionChanged>(tenant, new ActivateLabelTemplateVersion(Guid.NewGuid(), saved.TemplateVersionId,
                At.AddMinutes(1), "label.admin")))).Rule);
        Assert.Null((await InvokeAsync<LabelTemplatePreview>(tenant, new PreviewLabelTemplate(saved.TemplateVersionId,
            unit.UnitId, "label.admin"))).RejectionReason);
        Assert.Equal("label_legal_review_required", (await Assert.ThrowsAsync<CommandRefusedException>(() =>
            InvokeAsync<LabelTemplateVersionChanged>(tenant, new ActivateLabelTemplateVersion(Guid.NewGuid(), saved.TemplateVersionId,
                At.AddMinutes(1), "label.admin")))).Rule);
        await InvokeAsync<LabelTemplateVersionChanged>(tenant, new RequestLabelLegalReview(Guid.NewGuid(), saved.TemplateVersionId, "label.admin"));
        await InvokeAsync<LabelTemplateVersionChanged>(tenant, new SignLabelLegalReview(Guid.NewGuid(), saved.TemplateVersionId, "legal.reviewer"));
        Assert.Equal("label_regulated_approval_required", (await Assert.ThrowsAsync<CommandRefusedException>(() =>
            InvokeAsync<LabelTemplateVersionChanged>(tenant, new ActivateLabelTemplateVersion(Guid.NewGuid(), saved.TemplateVersionId,
                At.AddMinutes(1), "label.admin")))).Rule);
        await InvokeAsync<LabelTemplateVersionChanged>(tenant, new ApproveRegulatedLabel(Guid.NewGuid(), saved.TemplateVersionId, "regulatory.approver"));
        var activated = await InvokeAsync<LabelTemplateVersionChanged>(tenant, new ActivateLabelTemplateVersion(Guid.NewGuid(),
            saved.TemplateVersionId, At.AddMinutes(1), "label.admin"));
        Assert.Equal(LabelTemplateStatus.Active, activated.Status);

        var immutable = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<LabelTemplateVersionSaved>(tenant,
            new SaveLabelTemplateDraft(Guid.NewGuid(), created.TemplateId, saved.TemplateVersionId, At, null,
                LabelLayoutKind.Zpl, "^FD{{serial}} changed^FS", [], [new("serial", LabelBindableField.ManufacturerSerial, true)],
                LabelSymbology.Code128, unit.UnitId, false, "label.admin")));
        Assert.Equal("label_template_version_immutable", immutable.Rule);

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.True(await db.LabelTemplateVersions.TemporalAll().IgnoreQueryFilters()
            .CountAsync(row => row.TenantId == tenant && row.TemplateVersionId == saved.TemplateVersionId) > 1);
    }

    [DatabaseFact]
    public async Task A_tenant_configured_phrase_is_rechecked_at_activation_and_requires_legal_sign_off()
    {
        var tenant = await fixture.NewTenantAsync("labels-phrase");
        var unit = await UnitAsync(tenant, "PHRASE", "PHRASE-MFG");
        var created = await InvokeAsync<LabelTemplateCreated>(tenant, new CreateLabelTemplate(Guid.NewGuid(),
            "Tamper label", LabelTemplateAppliesTo.IdentifierType, IdentifierType.ManufacturerSerial, "label.admin"));
        var saved = await InvokeAsync<LabelTemplateVersionSaved>(tenant, new SaveLabelTemplateDraft(Guid.NewGuid(),
            created.TemplateId, null, At, null, LabelLayoutKind.Html, "<p>{{serial}} Seal Broken</p>", ["Seal Broken"],
            [new("serial", LabelBindableField.ManufacturerSerial, true)], LabelSymbology.DataMatrix, unit.UnitId, false, "label.admin"));
        Assert.False(saved.LegalReview);
        Assert.Null((await InvokeAsync<LabelTemplatePreview>(tenant, new PreviewLabelTemplate(saved.TemplateVersionId,
            unit.UnitId, "label.admin"))).RejectionReason);
        using (var scope = fixture.ScopeFor(tenant))
            await scope.ServiceProvider.GetRequiredService<LabelSettings>().SetAsync(new LabelPolicy(["seal broken"], false, "default"), CancellationToken.None);
        Assert.Equal("label_legal_review_required", (await Assert.ThrowsAsync<CommandRefusedException>(() =>
            InvokeAsync<LabelTemplateVersionChanged>(tenant, new ActivateLabelTemplateVersion(Guid.NewGuid(),
                saved.TemplateVersionId, At.AddMinutes(1), "label.admin")))).Rule);
        await InvokeAsync<LabelTemplateVersionChanged>(tenant, new RequestLabelLegalReview(Guid.NewGuid(), saved.TemplateVersionId, "label.admin"));
        await InvokeAsync<LabelTemplateVersionChanged>(tenant, new SignLabelLegalReview(Guid.NewGuid(), saved.TemplateVersionId, "legal.reviewer"));
        Assert.Equal(LabelTemplateStatus.Active, (await InvokeAsync<LabelTemplateVersionChanged>(tenant,
            new ActivateLabelTemplateVersion(Guid.NewGuid(), saved.TemplateVersionId, At.AddMinutes(1), "label.admin"))).Status);
    }

    [DatabaseFact]
    public async Task Legal_review_phrases_use_whole_word_and_phrase_boundaries()
    {
        var tenant = await fixture.NewTenantAsync("labels-phrase-boundary");
        var unit = await UnitAsync(tenant, "BOUNDARY", "BOUNDARY-MFG");
        using (var scope = fixture.ScopeFor(tenant))
            await scope.ServiceProvider.GetRequiredService<LabelSettings>()
                .SetAsync(new LabelPolicy(["void"], false, "default"), CancellationToken.None);
        var created = await InvokeAsync<LabelTemplateCreated>(tenant, new CreateLabelTemplate(Guid.NewGuid(),
            "Phrase boundary label", LabelTemplateAppliesTo.IdentifierType, IdentifierType.ManufacturerSerial, "label.admin"));

        var avoid = await InvokeAsync<LabelTemplateVersionSaved>(tenant, new SaveLabelTemplateDraft(Guid.NewGuid(),
            created.TemplateId, null, At, null, LabelLayoutKind.Html, "<p>{{serial}} avoid devoid</p>", ["avoid devoid"],
            [new("serial", LabelBindableField.ManufacturerSerial, true)], LabelSymbology.DataMatrix, unit.UnitId, false, "label.admin"));
        var hit = await InvokeAsync<LabelTemplateVersionSaved>(tenant, new SaveLabelTemplateDraft(Guid.NewGuid(),
            created.TemplateId, null, At, null, LabelLayoutKind.Html, "<p>{{serial}} warranty void.</p>", ["warranty void."],
            [new("serial", LabelBindableField.ManufacturerSerial, true)], LabelSymbology.DataMatrix, unit.UnitId, false, "label.admin"));

        Assert.False(avoid.LegalReview);
        Assert.True(hit.LegalReview);
    }

    [DatabaseFact]
    public async Task Preview_returns_a_real_QR_image_for_the_serial_lookup_url()
    {
        var tenant = await fixture.NewTenantAsync("labels-qr");
        var unit = await UnitAsync(tenant, "QR", "QR 2610/0012");
        var created = await InvokeAsync<LabelTemplateCreated>(tenant, new CreateLabelTemplate(Guid.NewGuid(),
            "QR lookup label", LabelTemplateAppliesTo.IdentifierType, IdentifierType.ManufacturerSerial, "label.admin"));
        var saved = await InvokeAsync<LabelTemplateVersionSaved>(tenant, new SaveLabelTemplateDraft(Guid.NewGuid(),
            created.TemplateId, null, At, null, LabelLayoutKind.Zpl, "^XA^FO20,20^BQN,2,6^FDLA,{{lookup}}^FS^XZ", [],
            [new("lookup", LabelBindableField.LookupUrl, true)], LabelSymbology.Qr, unit.UnitId, false, "label.admin"));

        var preview = await InvokeAsync<LabelTemplatePreview>(tenant, new PreviewLabelTemplate(saved.TemplateVersionId,
            unit.UnitId, "label.admin"));

        Assert.Null(preview.RejectionReason);
        Assert.Contains("/api/units/lookup?q=QR%202610%2F0012", preview.RenderedPayload);
        Assert.NotNull(preview.Barcode);
        Assert.Equal(LabelSymbology.Qr, preview.Barcode.Symbology);
        Assert.Equal("/api/units/lookup?q=QR%202610%2F0012", preview.Barcode.Content);
        Assert.StartsWith("data:image/png;base64,", preview.Barcode.DataUrl, StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task Ticket_T6_required_empty_and_Code39_asterisk_are_rejected_before_any_printer_send()
    {
        var tenant = await fixture.NewTenantAsync("labels-blank");
        var sample = await UnitAsync(tenant, "SAFE", "SAFE-MFG");
        var missing = await UnitAsync(tenant, "BLANK", itemId: sample.ItemId);
        var unsafeUnit = await UnitAsync(tenant, "STAR", "AB*CD", sample.ItemId);
        var template = await ActiveTemplateAsync(tenant, sample.UnitId, "Code 39 serial", LabelTemplateAppliesTo.ItemFamily,
            "systems", "^XA^FO20,20^FD{{serial}}^FS^XZ",
            [new("serial", LabelBindableField.ManufacturerSerial, true)], LabelSymbology.Code39);
        var printer = fixture.Services.GetRequiredService<InMemoryLabelPrinterPort>();

        var blank = await InvokeAsync<LabelPrintResult>(tenant, new PrintLabel(Guid.NewGuid(), missing.UnitId,
            template.TemplateId, null, "line-1", At.AddMinutes(2), Entity, "label.admin"));
        Assert.Equal(LabelPrintStatus.Rejected, blank.Status);
        Assert.Contains(LabelBindableField.ManufacturerSerial, blank.RejectionReason);
        Assert.DoesNotContain(printer.Sent, request => request.JobId == blank.JobId);

        var unsafePrint = await InvokeAsync<LabelPrintResult>(tenant, new PrintLabel(Guid.NewGuid(), unsafeUnit.UnitId,
            template.TemplateId, null, "line-1", At.AddMinutes(2), Entity, "label.admin"));
        Assert.Equal(LabelPrintStatus.Rejected, unsafePrint.Status);
        Assert.Contains("Code 39", unsafePrint.RejectionReason);
        Assert.DoesNotContain(printer.Sent, request => request.JobId == unsafePrint.JobId);
    }

    [DatabaseFact]
    public async Task Print_job_filters_and_summary_run_over_the_complete_tenant_range()
    {
        var tenant = await fixture.NewTenantAsync("labels-monitor");
        var complete = await UnitAsync(tenant, "MONITOR-A", "MONITOR-MFG-A");
        var incomplete = await UnitAsync(tenant, "MONITOR-B", itemId: complete.ItemId);
        var template = await ActiveTemplateAsync(tenant, complete.UnitId, "Monitor label",
            LabelTemplateAppliesTo.ItemFamily, "systems", "SER={{serial}}",
            [new("serial", LabelBindableField.ManufacturerSerial, true)]);
        var first = await InvokeAsync<LabelPrintResult>(tenant, new PrintLabel(Guid.NewGuid(), complete.UnitId,
            template.TemplateId, null, "pack-1", At.AddMinutes(2), Entity, "label.printer"));
        await InvokeAsync<LabelPrintResult>(tenant, new PrintLabel(Guid.NewGuid(), incomplete.UnitId,
            template.TemplateId, null, "pack-2", At.AddMinutes(3), Entity, "label.printer"));
        await InvokeAsync<LabelPrintResult>(tenant, new ReprintLabel(Guid.NewGuid(), first.JobId,
            "pack-2", At.AddMinutes(4), Entity, "label.printer"));
        await InvokeAsync<LabelPrintResult>(tenant, new PrintLabel(Guid.NewGuid(), complete.UnitId,
            template.TemplateId, null, "pack-1", At.AddMinutes(5), Entity, "system", Guid.NewGuid()));

        using var scope = fixture.ScopeFor(tenant);
        var queries = scope.ServiceProvider.GetRequiredService<LabelQueries>();
        var printer = await queries.JobsAsync(null, null, null, "pack-2", null, At, At.AddHours(1),
            0, 50, CancellationToken.None);
        Assert.Equal(2, printer.Total);
        Assert.All(printer.Rows, row => Assert.Equal("pack-2", row.PrinterRef));
        var reprints = await queries.JobsAsync(null, null, null, null, LabelPrintJobKind.Reprint, At,
            At.AddHours(1), 0, 50, CancellationToken.None);
        Assert.Single(reprints.Rows);
        Assert.NotNull(reprints.Rows[0].OriginalJobId);
        var events = await queries.JobsAsync(null, null, null, null, LabelPrintJobKind.Event, At,
            At.AddHours(1), 0, 50, CancellationToken.None);
        Assert.Single(events.Rows);
        Assert.NotNull(events.Rows[0].SourceEventId);
        var between = await queries.JobsAsync(null, null, null, null, null, At.AddMinutes(3.5),
            At.AddMinutes(4.5), 0, 50, CancellationToken.None);
        Assert.Single(between.Rows);
        Assert.NotNull(between.Rows[0].OriginalJobId);

        var summary = await queries.SummaryAsync(At, At.AddHours(1), CancellationToken.None);
        Assert.Equal((4, 3, 0, 1, 1, 1), (summary.Printed, summary.Confirmed, summary.Waiting,
            summary.Rejected, summary.Reprints, summary.FromEvents));
    }

    [DatabaseFact]
    public async Task Batch_print_is_set_based_replay_safe_and_reports_unknown_and_empty_required_fields()
    {
        var tenant = await fixture.NewTenantAsync("labels-batch");
        var complete = await UnitAsync(tenant, "BATCH-A", "BATCH-MFG-A");
        var incomplete = await UnitAsync(tenant, "BATCH-B", itemId: complete.ItemId);
        var template = await ActiveTemplateAsync(tenant, complete.UnitId, "Batch label",
            LabelTemplateAppliesTo.ItemFamily, "systems", "SER={{serial}}",
            [new("serial", LabelBindableField.ManufacturerSerial, true)]);
        var beforeComplete = await UnitConsistencyAsync(tenant, complete.UnitId);
        var beforeIncomplete = await UnitConsistencyAsync(tenant, incomplete.UnitId);
        var commandId = Guid.NewGuid();
        var command = new BatchPrintLabels(commandId, template.VersionId, "batch-printer",
            ["BATCH-A-INT", "BATCH-B-INT", "UNKNOWN-999"], At.AddMinutes(2), "label.printer");

        var result = await InvokeAsync<LabelBatchPrintResult>(tenant, command);
        Assert.False(result.Replayed);
        Assert.Equal(3, result.Outcomes.Count);
        Assert.Equal(LabelPrintStatus.Confirmed, result.Outcomes[0].Status);
        Assert.Equal("label_required_field_empty", result.Outcomes[1].RejectionRule);
        Assert.Equal(LabelBindableField.ManufacturerSerial, result.Outcomes[1].EmptyRequiredField);
        Assert.Contains(LabelBindableField.ManufacturerSerial, result.Outcomes[1].RejectionReason);
        Assert.Equal("label_batch_unknown_serial", result.Outcomes[2].RejectionRule);
        Assert.Null(result.Outcomes[2].JobId);

        var replay = await InvokeAsync<LabelBatchPrintResult>(tenant, command with { Serials = ["changed"] });
        Assert.True(replay.Replayed);
        Assert.Equal(result.Outcomes, replay.Outcomes);

        var afterComplete = await UnitConsistencyAsync(tenant, complete.UnitId);
        Assert.Equal(beforeComplete.OwnerEpisodeId, afterComplete.OwnerEpisodeId);
        Assert.Equal(beforeComplete.CustodyEpisodeId, afterComplete.CustodyEpisodeId);
        Assert.Equal(beforeComplete.CustodyLocationKind, afterComplete.CustodyLocationKind);
        Assert.Equal(beforeComplete.Entitlements, afterComplete.Entitlements);
        Assert.Equal(beforeComplete.IdentifierIds.Order(), afterComplete.IdentifierIds.Order());
        Assert.Equal([LabelEventType.Printed], afterComplete.LabelEvents);
        var afterIncomplete = await UnitConsistencyAsync(tenant, incomplete.UnitId);
        Assert.Equal(beforeIncomplete.OwnerEpisodeId, afterIncomplete.OwnerEpisodeId);
        Assert.Equal(beforeIncomplete.CustodyEpisodeId, afterIncomplete.CustodyEpisodeId);
        Assert.Equal(beforeIncomplete.CustodyLocationKind, afterIncomplete.CustodyLocationKind);
        Assert.Equal(beforeIncomplete.Entitlements, afterIncomplete.Entitlements);
        Assert.Equal(beforeIncomplete.IdentifierIds.Order(), afterIncomplete.IdentifierIds.Order());
        Assert.Empty(afterIncomplete.LabelEvents);
    }

    [DatabaseFact]
    public async Task Tickets_T44_and_T50_keep_the_printed_expiry_and_reprint_the_stored_payload_after_template_and_entitlement_changes()
    {
        var setup = await EntitledUnitAsync("labels-expiry");
        var template = await ActiveTemplateAsync(setup.Tenant, setup.UnitId, "System label",
            LabelTemplateAppliesTo.ItemFamily, "systems", "SER={{serial}} EXP={{expiry}}",
            [new("serial", LabelBindableField.InternalSerial, true), new("expiry", LabelBindableField.BaseEntitlementEndDate, true)]);
        var printer = fixture.Services.GetRequiredService<InMemoryLabelPrinterPort>();

        var off = await InvokeAsync<LabelPrintResult>(setup.Tenant, new PrintLabel(Guid.NewGuid(), setup.UnitId,
            template.TemplateId, null, "line-2", At.AddDays(2), Entity, "label.admin"));
        Assert.Equal(LabelPrintStatus.Rejected, off.Status);
        Assert.Contains(LabelBindableField.BaseEntitlementEndDate, off.RejectionReason);
        using (var scope = fixture.ScopeFor(setup.Tenant))
            await scope.ServiceProvider.GetRequiredService<LabelSettings>().SetAsync(new LabelPolicy(["void", "voids warranty", "warranty void"], true, "line-2"), CancellationToken.None);

        var printed = await InvokeAsync<LabelPrintResult>(setup.Tenant, new PrintLabel(Guid.NewGuid(), setup.UnitId,
            template.TemplateId, null, "line-2", At.AddDays(2).AddMinutes(1), Entity, "label.admin"));
        Assert.Equal(LabelPrintStatus.Confirmed, printed.Status);
        var sent = Assert.Single(printer.Sent, request => request.JobId == printed.JobId);
        Assert.Contains("2028-09-15", sent.Payload);

        var before = await UnitConsistencyAsync(setup.Tenant, setup.UnitId);
        Assert.Null(before.OwnerEpisodeId);
        Assert.Equal(LocationKind.Warehouse, before.CustodyLocationKind);
        Assert.Single(before.Entitlements);
        Assert.Equal(setup.IdentifierIds.Order(), before.IdentifierIds.Order());
        Assert.Contains(LabelEventType.Printed, before.LabelEvents);

        await InvokeAsync<AmendEntitlementResult>(setup.Tenant, new AmendEntitlement(Guid.NewGuid(), setup.EntitlementId,
            AmendmentReason.Correction, At.AddDays(3), "service.manager", NewEnd: new DateOnly(2028, 10, 15),
            Note: "ticket correction", CommittedBy: Manager));
        var newDraft = await InvokeAsync<LabelTemplateVersionSaved>(setup.Tenant, new SaveLabelTemplateDraft(Guid.NewGuid(),
            template.TemplateId, null, At.AddDays(3), null, LabelLayoutKind.Zpl, "CHANGED {{serial}} {{expiry}}", [],
            [new("serial", LabelBindableField.InternalSerial, true), new("expiry", LabelBindableField.BaseEntitlementEndDate, true)],
            LabelSymbology.Code128, setup.UnitId, false, "label.admin"));
        Assert.Null((await InvokeAsync<LabelTemplatePreview>(setup.Tenant, new PreviewLabelTemplate(newDraft.TemplateVersionId,
            setup.UnitId, "label.admin"))).RejectionReason);
        await InvokeAsync<LabelTemplateVersionChanged>(setup.Tenant, new ActivateLabelTemplateVersion(Guid.NewGuid(),
            newDraft.TemplateVersionId, At.AddDays(3), "label.admin"));

        var reprinted = await InvokeAsync<LabelPrintResult>(setup.Tenant, new ReprintLabel(Guid.NewGuid(), printed.JobId,
            "line-3", At.AddDays(4), Entity, "label.admin"));
        Assert.Equal(LabelPrintStatus.Confirmed, reprinted.Status);
        var resent = Assert.Single(printer.Sent, request => request.JobId == reprinted.JobId);
        Assert.Equal(sent.Payload, resent.Payload);
        Assert.Equal(sent.PayloadHash, resent.PayloadHash);
        Assert.Contains("2028-09-15", resent.Payload);
        Assert.DoesNotContain("2028-10-15", resent.Payload);
        Assert.DoesNotContain("CHANGED", resent.Payload);

        var after = await UnitConsistencyAsync(setup.Tenant, setup.UnitId);
        Assert.Null(after.OwnerEpisodeId);
        Assert.Equal(before.CustodyEpisodeId, after.CustodyEpisodeId);
        Assert.Equal(LocationKind.Warehouse, after.CustodyLocationKind);
        Assert.Equal(setup.IdentifierIds.Order(), after.IdentifierIds.Order());
        Assert.Equal(new DateOnly(2028, 10, 15), Assert.Single(after.Entitlements).EndDate);
        Assert.Equal([LabelEventType.Printed, LabelEventType.Reprinted], after.LabelEvents);

        var published = Assert.Single(await PublishedEventSink.WaitForAsync<LabelPrinted>(message => message.JobId == printed.JobId,
            1, TimeSpan.FromSeconds(20)));
        Assert.Equal((LabelPrinted.Schema, "1.0", sent.PayloadHash),
            (published.Header.Schema, published.Header.Version, published.PayloadHash));
        using var schema = System.Text.Json.JsonDocument.Parse(PublishedEventSchemas.Read(LabelPrinted.Schema, LabelPrinted.Version));
        Assert.Equal(LabelPrinted.Schema, schema.RootElement.GetProperty("title").GetString());
        var reprintPublished = Assert.Single(await PublishedEventSink.WaitForAsync<LabelReprinted>(message =>
            message.JobId == reprinted.JobId, 1, TimeSpan.FromSeconds(20)));
        Assert.Equal((printed.JobId, sent.PayloadHash), (reprintPublished.OriginalJobId, reprintPublished.PayloadHash));
        using var reprintSchema = System.Text.Json.JsonDocument.Parse(PublishedEventSchemas.Read(LabelReprinted.Schema,
            LabelReprinted.Version));
        Assert.Equal(LabelReprinted.Schema, reprintSchema.RootElement.GetProperty("title").GetString());
    }

    [DatabaseFact]
    public async Task Existing_swap_source_correction_and_replacement_print_requests_are_consumed_once_and_missing_templates_are_rejected()
    {
        var tenant = await fixture.NewTenantAsync("labels-consume");
        var unit = await UnitAsync(tenant, "REQ");
        var sourceA = Guid.NewGuid();
        var sourceB = Guid.NewGuid();
        var sourceC = Guid.NewGuid();
        var requests = new object[]
        {
            new AssetTagPrintJobRequested(Guid.NewGuid(), unit.UnitId, unit.InternalIdentifierId, "REQ-TAG", Guid.NewGuid(),
                Guid.NewGuid(), sourceA, At.AddDays(1), Entity, "serial.admin"),
            new LabelReprintJobRequested(Guid.NewGuid(), unit.UnitId, Guid.NewGuid(), sourceB,
                new DocumentRef("sales_order", Guid.NewGuid()), "source_document_corrected", At.AddDays(1), Entity, "order.supervisor"),
            new ReplacementAssetTagPrintRequested(new PublishedEventHeader(ReplacementAssetTagPrintRequested.Schema,
                ReplacementAssetTagPrintRequested.Version, sourceC, unit.UnitId, At.AddDays(1), At.AddDays(1), 1,
                Entity, "service.manager", null), Guid.NewGuid(), Guid.NewGuid(), unit.InternalIdentifierId, "REQ-TAG"),
        };
        using (var scope = fixture.ScopeFor(tenant))
        {
            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
            foreach (var request in requests)
            {
                await bus.PublishAsync(request, new DeliveryOptions { TenantId = tenant.ToString() });
                await bus.PublishAsync(request, new DeliveryOptions { TenantId = tenant.ToString() });
            }
        }

        var expected = new[] { sourceA, sourceB, sourceC };
        var deadline = DateTime.UtcNow.AddSeconds(20);
        List<Yojana.Serialisation.Data.Entities.PrintJob> jobs = [];
        while (DateTime.UtcNow < deadline)
        {
            using var scope = fixture.ScopeFor(tenant);
            jobs = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().PrintJobs.AsNoTracking()
                .Where(row => row.SourceEventId != null && expected.Contains(row.SourceEventId.Value)).ToListAsync();
            if (jobs.Count == 3) break;
            await Task.Delay(100);
        }
        Assert.Equal(3, jobs.Count);
        Assert.All(jobs, job =>
        {
            Assert.Equal(LabelPrintStatus.Rejected, job.Status);
            Assert.Contains("no active label template", job.RejectionReason);
        });
        Assert.Equal(expected.Order(), jobs.Select(job => job.SourceEventId!.Value).Order());
    }

    [DatabaseFact]
    public void Print_job_migration_backfills_in_tenant_year_order_with_the_security_policy_bounded()
    {
        var migration = new TestableP6Polish();
        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.SqlServer");
        migration.BuildUp(builder);
        var statements = builder.Operations.OfType<SqlOperation>().Select(operation => operation.Sql).ToList();

        var off = statements.FindIndex(sql => sql.Contains("STATE = OFF", StringComparison.Ordinal));
        var backfill = statements.FindIndex(sql => sql.Contains("ROW_NUMBER() OVER", StringComparison.Ordinal));
        var on = statements.FindIndex(sql => sql.Contains("STATE = ON", StringComparison.Ordinal));
        Assert.True(off >= 0 && off < backfill && backfill < on);
        Assert.Contains("PARTITION BY tenant_id, YEAR(requested_at)", statements[backfill], StringComparison.Ordinal);
        Assert.Contains("ORDER BY requested_at, job_id", statements[backfill], StringComparison.Ordinal);
        Assert.Contains("MERGE [serialisation].[number_series]", statements[backfill], StringComparison.Ordinal);
    }

    private async Task<(TenantId Tenant, Guid UnitId, Guid EntitlementId, IReadOnlyList<Guid> IdentifierIds)> EntitledUnitAsync(string prefix)
    {
        var tenant = await fixture.NewTenantAsync(prefix);
        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily(CoverageFamilyCode.Base, "Base warranty", false, AssuranceOrService.Assurance));
        await InvokeAsync<ReplacementPolicyRow>(tenant, new DefineReplacementPolicy("STD-90", ReplacementRule.MaxRemainingOrMinDays, 90, null, null));
        var code = "LBL-" + Guid.NewGuid().ToString("N")[..6];
        var policy = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), code, code,
            CoverageFamilyCode.Base, EntitlementType.Manufacturer, new DateOnly(2026, 7, 31), PolicyValidatorTestsTerms.Standard24(), "policy.admin"));
        await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(policy.PolicyVersionId, "policy.admin"));
        var item = Guid.NewGuid();
        var unit = await UnitAsync(tenant, "EXP", itemId: item);
        var site = Guid.NewGuid();
        var warranties = fixture.Services.GetRequiredService<InMemoryWarrantyPorts>();
        warranties.Register(new ItemWarrantyPolicy(item, CoverageFamilyCode.Base, code, true));
        warranties.RegisterZone(site, "Europe/Berlin");
        var entitlement = await InvokeAsync<CreateEntitlementResult>(tenant, new CreateEntitlement(Guid.NewGuid(), unit.UnitId,
            CoverageFamilyCode.Base, new DateOnly(2026, 9, 2), Entity, At.AddDays(1), "service.desk",
            new Dictionary<string, DateTime> { [StartTrigger.Invoice] = new DateTime(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc) },
            OwnerPartyRef: Guid.NewGuid(), OwnerPartyType: PartyType.Customer, Country: "GB", SiteLocationId: site,
            SourceDocument: new DocumentRef("invoice", Guid.NewGuid(), 1)));
        Assert.Equal(new DateOnly(2028, 9, 15), entitlement.EndDate);
        using var scope = fixture.ScopeFor(tenant);
        var ids = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Identifiers.AsNoTracking()
            .Where(row => row.UnitId == unit.UnitId && row.ValidTo == null).Select(row => row.IdentifierId).ToListAsync();
        return (tenant, unit.UnitId, entitlement.EntitlementId, ids);
    }

    private async Task<UnitConsistency> UnitConsistencyAsync(TenantId tenant, Guid unitId)
    {
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var ownership = await db.Episodes.AsNoTracking().SingleOrDefaultAsync(row => row.UnitId == unitId
            && row.Kind == EpisodeKind.Ownership && row.ValidTo == null);
        var custody = await db.Episodes.AsNoTracking().SingleAsync(row => row.UnitId == unitId
            && row.Kind == EpisodeKind.Custody && row.ValidTo == null);
        var entitlements = await db.Entitlements.AsNoTracking().Where(row => row.UnitId == unitId).ToListAsync();
        var identifiers = await db.Identifiers.AsNoTracking().Where(row => row.UnitId == unitId && row.ValidTo == null)
            .Select(row => row.IdentifierId).ToListAsync();
        var events = await db.Events.AsNoTracking().Where(row => row.UnitId == unitId
            && (row.EventType == LabelEventType.Printed || row.EventType == LabelEventType.Reprinted))
            .OrderBy(row => row.Seq).Select(row => row.EventType).ToListAsync();
        return new(ownership?.EpisodeId, custody.EpisodeId, custody.LocationKind, entitlements, identifiers, events);
    }

    private sealed record UnitConsistency(Guid? OwnerEpisodeId, Guid CustodyEpisodeId, string? CustodyLocationKind,
        IReadOnlyList<Yojana.Serialisation.Data.Entities.Entitlement> Entitlements, IReadOnlyList<Guid> IdentifierIds,
        IReadOnlyList<string> LabelEvents);

    private sealed class TestableP6Polish : P6Polish
    {
        public void BuildUp(MigrationBuilder builder) => base.Up(builder);
    }
}
