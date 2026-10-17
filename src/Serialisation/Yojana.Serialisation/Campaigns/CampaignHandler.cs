using Microsoft.EntityFrameworkCore;
using Wolverine;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Identity;
using Yojana.Serialisation.Queries;

namespace Yojana.Serialisation.Campaigns;

/// <summary>Campaign writes and selection. Criteria are replaced as one narrow child collection.</summary>
public static class CampaignHandler
{
    private const int EventBatchSize = 1000;

    public static async Task<CampaignResult> Handle(CreateCampaign command, SerialisationDbContext db,
        ISerialSchemeSource schemes, IIdentifierPolicySource identifierPolicies, ITenantContext tenant,
        CancellationToken cancellationToken)
    {
        ValidateCampaign(command.Code, command.Name, command.CoverageSource, command.ReserveTag, command.ReasonCode, command.ReasonNote,
            command.PartsPct, command.LabourPct, command.TravelPct);
        var existing = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenant.TenantId && c.CampaignId == command.CommandId, cancellationToken);
        if (existing is not null)
        {
            var criteria = await db.CampaignCriteria.AsNoTracking().Where(c => c.TenantId == tenant.TenantId && c.CampaignId == existing.CampaignId).ToListAsync(cancellationToken);
            return new(ToRow(existing, criteria), true);
        }

        if (await db.Campaigns.AnyAsync(c => c.TenantId == tenant.TenantId && c.Code == command.Code.Trim(), cancellationToken))
            throw new CommandRefusedException("campaign_code_exists", $"Campaign code '{command.Code.Trim()}' already exists.");
        var campaign = new Campaign
        {
            TenantId = tenant.TenantId,
            CampaignId = command.CommandId,
            Code = command.Code.Trim(),
            Name = command.Name.Trim(),
            CoverageSource = command.CoverageSource,
            ReserveTag = command.ReserveTag.Trim(),
            PartsPct = command.PartsPct,
            LabourPct = command.LabourPct,
            TravelPct = command.TravelPct,
            ReasonCode = command.ReasonCode,
            ReasonNote = Note(command.ReasonNote),
            RunsUntil = command.RunsUntil,
            Status = CampaignStatus.Draft,
            CreatedAt = command.EventTime,
            CreatedBy = command.ActorRef,
            UpdatedAt = command.EventTime,
            UpdatedBy = command.ActorRef,
        };
        var criteriaRows = CampaignCriteriaRules.Prepare(campaign.CampaignId, command.Criteria, schemes, identifierPolicies, command.EventTime);
        foreach (var row in criteriaRows) row.TenantId = tenant.TenantId;
        db.Campaigns.Add(campaign);
        db.CampaignCriteria.AddRange(criteriaRows);
        return new(ToRow(campaign, criteriaRows));
    }

    public static async Task<CampaignResult> Handle(UpdateCampaign command, SerialisationDbContext db,
        ISerialSchemeSource schemes, IIdentifierPolicySource identifierPolicies, ITenantContext tenant,
        CancellationToken cancellationToken)
    {
        ValidateCampaign(command.Code, command.Name, command.CoverageSource, command.ReserveTag, command.ReasonCode, command.ReasonNote,
            command.PartsPct, command.LabourPct, command.TravelPct);
        if (!CampaignStatus.All.Contains(command.Status, StringComparer.Ordinal))
            throw new CommandRefusedException("campaign_status_unknown", $"Campaign status '{command.Status}' is not known.");
        var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.TenantId == tenant.TenantId && c.CampaignId == command.CampaignId, cancellationToken)
            ?? throw new CommandRefusedException("campaign_unknown", $"Campaign {command.CampaignId:N} does not exist.");
        var oldCriteria = await db.CampaignCriteria.Where(c => c.TenantId == tenant.TenantId && c.CampaignId == campaign.CampaignId).ToListAsync(cancellationToken);
        if (!CampaignCriteriaRules.CanMove(campaign.Status, command.Status))
            throw new CommandRefusedException("campaign_status_transition_invalid", $"A campaign cannot move from {campaign.Status} to {command.Status}.");
        if (campaign.SelectedAt is { } selectedAt && command.RunsUntil is { } runsUntil
            && runsUntil < DateOnly.FromDateTime(selectedAt))
            throw new CommandRefusedException("campaign_runs_until_before_selection", $"Runs until {runsUntil:yyyy-MM-dd} cannot be before the first selection on {selectedAt:yyyy-MM-dd}.");

        if (campaign.Status == CampaignStatus.Active)
        {
            if (command.Status is not (CampaignStatus.Active or CampaignStatus.Closed))
                throw new CommandRefusedException("campaign_active", "An active campaign can only be closed.");
            if (command.Code.Trim() != campaign.Code || command.Name.Trim() != campaign.Name
                || command.CoverageSource != campaign.CoverageSource || command.ReserveTag.Trim() != campaign.ReserveTag)
                throw new CommandRefusedException("campaign_active", "An active campaign can change its reason, runs-until date and cover percentages, or be closed; its identity stays fixed.");
            campaign.Status = command.Status;
        }
        else
        {
            if (command.Status == CampaignStatus.Active
                && !await db.CampaignUnits.AnyAsync(u => u.TenantId == tenant.TenantId && u.CampaignId == campaign.CampaignId, cancellationToken))
                throw new CommandRefusedException("campaign_units_required", "Select at least one unit before activating the campaign.");
            var code = command.Code.Trim();
            if (await db.Campaigns.AnyAsync(c => c.TenantId == tenant.TenantId && c.CampaignId != campaign.CampaignId && c.Code == code, cancellationToken))
                throw new CommandRefusedException("campaign_code_exists", $"Campaign code '{code}' already exists.");
            var criteria = CampaignCriteriaRules.Prepare(campaign.CampaignId, command.Criteria, schemes, identifierPolicies, command.EventTime);
            foreach (var row in criteria) row.TenantId = tenant.TenantId;
            db.CampaignCriteria.RemoveRange(oldCriteria);
            db.CampaignCriteria.AddRange(criteria);
            oldCriteria = criteria.ToList();
            campaign.Code = code;
            campaign.Name = command.Name.Trim();
            campaign.CoverageSource = command.CoverageSource;
            campaign.ReserveTag = command.ReserveTag.Trim();
            campaign.Status = command.Status;
        }

        campaign.ReasonCode = command.ReasonCode;
        campaign.ReasonNote = Note(command.ReasonNote);
        campaign.RunsUntil = command.RunsUntil;
        campaign.PartsPct = command.PartsPct;
        campaign.LabourPct = command.LabourPct;
        campaign.TravelPct = command.TravelPct;

        campaign.UpdatedAt = command.EventTime;
        campaign.UpdatedBy = command.ActorRef;
        return new(ToRow(campaign, oldCriteria));
    }

    public static async Task<CampaignPreview> Handle(PreviewCampaign command, SerialisationDbContext db,
        CampaignSelectionFacts facts, ITenantContext tenant, CancellationToken cancellationToken)
    {
        var campaign = await Campaign(db, tenant.TenantId, command.CampaignId, cancellationToken);
        EnsureSelectable(campaign.Campaign);
        var matched = await facts.MatchingUnitIdsAsync(campaign.Criteria, cancellationToken);
        var take = Math.Clamp(command.Take, 1, 200);
        var ids = matched.Order().Skip(Math.Max(0, command.Skip)).Take(take).ToList();
        var rows = (await facts.CurrentMatchesAsync(ids, campaign.Criteria, cancellationToken)).ToDictionary(r => r.UnitId);
        return new(command.CampaignId, new Page<CampaignMatchRow>(ids.Select(id => rows[id]).ToList(), matched.Count, Math.Max(0, command.Skip), take));
    }

    public static async Task<SelectCampaignUnitsResult> Handle(SelectCampaignUnits command, SerialisationDbContext db,
        CampaignSelectionFacts facts, IMessageBus bus, ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        var loaded = await Campaign(db, tenant.TenantId, command.CampaignId, cancellationToken);
        EnsureSelectable(loaded.Campaign);
        if (loaded.Campaign.RunsUntil is { } runsUntil && DateOnly.FromDateTime(command.EventTime) > runsUntil)
            throw new CommandRefusedException("campaign_selection_ended", $"This campaign stopped accepting new selections after {runsUntil:yyyy-MM-dd}; existing selections remain in its history.");
        var matched = await facts.MatchingUnitIdsAsync(loaded.Criteria, cancellationToken);
        var existing = (await db.CampaignUnits.AsNoTracking().Where(u => u.TenantId == tenant.TenantId && u.CampaignId == command.CampaignId)
            .Select(u => u.UnitId).ToListAsync(cancellationToken)).ToHashSet();
        var addIds = matched.Where(id => !existing.Contains(id)).Order().ToList();
        var snapshots = await facts.SnapshotRecipientsAsync(addIds, command.CampaignId, command.CommandId, command.EventTime, command.ActorRef, cancellationToken);
        db.CampaignUnits.AddRange(snapshots);
        if (loaded.Campaign.Status == CampaignStatus.Draft) loaded.Campaign.Status = CampaignStatus.Selected;
        if (loaded.Campaign.SelectedAt is null && snapshots.Count > 0) loaded.Campaign.SelectedAt = command.EventTime;
        loaded.Campaign.UpdatedAt = command.EventTime;
        loaded.Campaign.UpdatedBy = command.ActorRef;

        var batches = snapshots.Chunk(EventBatchSize).ToList();
        var total = existing.Count + snapshots.Count;
        for (var index = 0; index < batches.Count; index++)
        {
            var units = batches[index].Select(u => new CampaignSelectedUnit(u.UnitId, u.OwnerPartyRef, u.OwnerPartyType, u.CustodianPartyRef, u.CustodianPartyType)).ToList();
            await bus.PublishAsync(new CampaignUnitsSelected(
                new(CampaignUnitsSelected.Schema, CampaignUnitsSelected.Version, TimeOrderedId.New(command.EventTime), command.EventTime,
                    clock.UtcNow, command.LegalEntityId, command.ActorRef, null),
                loaded.Campaign.CampaignId, loaded.Campaign.Code, loaded.Campaign.CoverageSource, loaded.Campaign.ReserveTag,
                loaded.Campaign.ReasonCode, loaded.Campaign.ReasonNote, loaded.Campaign.RunsUntil,
                loaded.Campaign.PartsPct, loaded.Campaign.LabourPct, loaded.Campaign.TravelPct,
                command.CommandId, index + 1, batches.Count, matched.Count, total, units),
                new DeliveryOptions { TenantId = tenant.TenantId.ToString() });
        }

        return new(command.CampaignId, matched.Count, snapshots.Count, total, batches.Count, snapshots.Count == 0);
    }

    private static async Task<(Campaign Campaign, List<CampaignCriterion> Criteria)> Campaign(
        SerialisationDbContext db, TenantId tenantId, Guid campaignId, CancellationToken cancellationToken)
    {
        var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.TenantId == tenantId && c.CampaignId == campaignId, cancellationToken)
            ?? throw new CommandRefusedException("campaign_unknown", $"Campaign {campaignId:N} does not exist.");
        var criteria = await db.CampaignCriteria.AsNoTracking().Where(c => c.TenantId == tenantId && c.CampaignId == campaignId)
            .OrderBy(c => c.Kind).ThenBy(c => c.CriterionId).ToListAsync(cancellationToken);
        return (campaign, criteria);
    }

    private static void EnsureSelectable(Campaign campaign)
    {
        if (campaign.Status is CampaignStatus.Active or CampaignStatus.Closed)
            throw new CommandRefusedException("campaign_not_selectable", $"A {campaign.Status} campaign cannot change its selected units.");
    }

    private static void ValidateCampaign(string code, string name, string coverageSource, string reserveTag, string reasonCode, string? reasonNote,
        decimal partsPct, decimal labourPct, decimal travelPct)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new CommandRefusedException("campaign_code_required", "A campaign code is required.");
        if (string.IsNullOrWhiteSpace(name)) throw new CommandRefusedException("campaign_name_required", "A campaign name is required.");
        if (!CampaignCoverageSource.All.Contains(coverageSource, StringComparer.Ordinal))
            throw new CommandRefusedException("campaign_coverage_source_invalid", "Campaign coverage_source is recall or goodwill.");
        if (string.IsNullOrWhiteSpace(reserveTag)) throw new CommandRefusedException("campaign_reserve_tag_required", "A campaign carries its own reserve tag.");
        if (string.IsNullOrWhiteSpace(reasonCode)) throw new CommandRefusedException("campaign_reason_required", "A campaign reason is required.");
        if (!CampaignReason.All.Contains(reasonCode, StringComparer.Ordinal))
            throw new CommandRefusedException("campaign_reason_unknown", $"Campaign reason '{reasonCode}' is not known.");
        if (reasonNote?.Trim().Length > 500)
            throw new CommandRefusedException("campaign_reason_note_too_long", "A campaign reason note is at most 500 characters.");
        if (partsPct is < 0 or > 100 || labourPct is < 0 or > 100 || travelPct is < 0 or > 100)
            throw new CommandRefusedException("campaign_cover_pct_out_of_range", "Campaign parts, labour and travel percentages must each be between 0 and 100.");
    }

    private static string? Note(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static CampaignRow ToRow(Campaign campaign, IReadOnlyList<CampaignCriterion> criteria) => new(
        campaign.CampaignId, campaign.Code, campaign.Name, campaign.CoverageSource, campaign.ReserveTag, campaign.Status,
        criteria.Select(c => new CampaignCriterionRow(c.CriterionId, c.Kind, c.Scheme, c.RangeStart, c.RangeEnd, c.LotId,
            c.ChildUnitId, c.ChildItemId, c.FirmwareValue, c.EffectiveAt, c.ChildLotId, c.FirmwareKey)).ToList(), campaign.CreatedAt, campaign.CreatedBy,
        campaign.UpdatedAt, campaign.UpdatedBy, campaign.SelectedAt, campaign.ReasonCode, campaign.ReasonNote, campaign.RunsUntil,
        campaign.PartsPct, campaign.LabourPct, campaign.TravelPct);
}

