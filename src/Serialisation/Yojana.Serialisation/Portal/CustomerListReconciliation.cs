using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Identity;
using Yojana.Serialisation.Imports;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Portal;

internal sealed record CustomerListSourceRow(
    int Ordinal,
    string RawIdentifier,
    string? IdentifierType,
    Guid? UnitId,
    string? CustomerPoRef,
    string? CustomerItemRef);

/// <summary>
/// Narrow source adapter between reconciliation and MOD-11/MOD-12. Reconciliation knows neither selection-set
/// resolution nor import storage details; it receives only ordered identifier rows and optional already-resolved units.
/// </summary>
public sealed class CustomerListSourceResolver(
    SerialisationDbContext db,
    IImportJobSource imports,
    ITenantContext tenant)
{
    internal async Task<(string Kind, Guid? ReferenceId, IReadOnlyList<CustomerListSourceRow> Rows)> ReadAsync(
        CustomerListSource source,
        CancellationToken cancellationToken)
    {
        var supplied = (source.Pasted is not null ? 1 : 0) + (source.ImportJobId is not null ? 1 : 0)
            + (source.SelectionId is not null ? 1 : 0);
        if (supplied != 1)
            throw new CommandRefusedException("customer_list_source_invalid",
                "Supply exactly one customer-list source: pasted values, an import job or a selection set.");

        if (source.Pasted is { } pasted)
        {
            if (pasted.Count > ImportService.MaximumRows)
                throw new CommandRefusedException("customer_list_too_large",
                    $"A customer list may contain at most {ImportService.MaximumRows:N0} rows.");
            return (CustomerListSourceKind.PastedList, null, pasted.Select((row, index) =>
                new CustomerListSourceRow(index + 1, row.Value, row.IdentifierType, null,
                    row.CustomerPoRef, row.CustomerItemRef)).ToList());
        }

        if (source.ImportJobId is { } importJobId)
        {
            var rows = await imports.ReadForReconciliationAsync(importJobId, cancellationToken);
            return (CustomerListSourceKind.ImportJob, importJobId, rows.Select((row, index) =>
                new CustomerListSourceRow(index + 1, row.RawValue, null, row.UnitId, null, null)).ToList());
        }

        var selectionId = source.SelectionId!.Value;
        var exists = await db.SelectionSets.AsNoTracking().AnyAsync(row => row.TenantId == tenant.TenantId
            && row.SelectionId == selectionId, cancellationToken);
        if (!exists)
            throw new CommandRefusedException("selection_unknown", $"Selection set {selectionId} was not found.");
        var members = await (from member in db.SelectionSetMembers.AsNoTracking()
                             where member.TenantId == tenant.TenantId && member.SelectionId == selectionId
                             orderby member.UnitId
                             select new { member.UnitId, member.Serial }).ToListAsync(cancellationToken);
        return (CustomerListSourceKind.SelectionSet, selectionId, members.Select((row, index) =>
            new CustomerListSourceRow(index + 1, row.Serial ?? "", IdentifierType.InternalSerial,
                row.UnitId, null, null)).ToList());
    }
}

public sealed class CustomerListReconciliationService(
    SerialisationDbContext db,
    CustomerListSourceResolver sources,
    IIdentifierPolicySource identifierPolicies,
    IItemNamePort itemNames,
    IPartyNamePort partyNames,
    ITenantContext tenant)
{
    private const int MaximumPageSize = 200;
    private static readonly string[] LookupTypes =
    [
        IdentifierType.InternalSerial, IdentifierType.ManufacturerSerial, IdentifierType.VendorSerial,
        IdentifierType.CustomerSerial, IdentifierType.Sgtin, IdentifierType.Giai, IdentifierType.Imei,
        IdentifierType.Mac, IdentifierType.Vin, IdentifierType.UdiDi, IdentifierType.UdiPi, IdentifierType.Uii,
        IdentifierType.CustomerAssetTag, IdentifierType.ChannelOrderRef, IdentifierType.SoftwareLicense,
    ];

    public async Task<CustomerListReconciliationPage> ReconcileAsync(ReconcileCustomerList command,
        CancellationToken cancellationToken)
    {
        if (command.CommandId == Guid.Empty || command.PartyRef == Guid.Empty)
            throw new CommandRefusedException("customer_list_identity_required",
                "A command id and customer party are required.");
        if (command.AssertedAt.Kind != DateTimeKind.Utc)
            throw new CommandRefusedException("event_time_not_utc", "The assertion time must be UTC.");

        var existing = await db.CustomerReconciliations.AsNoTracking().AnyAsync(row =>
            row.TenantId == tenant.TenantId && row.ReconciliationId == command.CommandId, cancellationToken);
        if (existing)
            return await PageAsync(command.CommandId, command.After, command.Take, true, cancellationToken);

        var source = await sources.ReadAsync(command.Source, cancellationToken);
        var prepared = source.Rows.Select(Prepare).ToList();
        var hits = await MatchAsync(prepared, cancellationToken);
        var matchedUnitIds = prepared.SelectMany(row => UnitsFor(row, hits)).Distinct().ToList();
        var units = matchedUnitIds.Count == 0 ? new HashSet<Guid>() : (await db.Units.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && matchedUnitIds.Contains(row.UnitId))
            .Select(row => row.UnitId).ToListAsync(cancellationToken)).ToHashSet();
        var owners = matchedUnitIds.Count == 0 ? [] : await db.Episodes.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && matchedUnitIds.Contains(row.UnitId)
                && row.Kind == EpisodeKind.Ownership && row.ValidTo == null)
            .Select(row => new { row.UnitId, row.PartyRef }).ToListAsync(cancellationToken);
        var ownerByUnit = owners.ToDictionary(row => row.UnitId, row => row.PartyRef);

        var resultRows = new List<CustomerReconciliationRow>(prepared.Count);
        var assertions = new List<CustomerAssertion>();
        foreach (var row in prepared)
        {
            var matched = UnitsFor(row, hits).Where(units.Contains).Distinct().ToList();
            var outcome = row.Reasons.Count > 0 || matched.Count > 1
                ? CustomerListMatchOutcome.Invalid
                : matched.Count == 0
                    ? CustomerListMatchOutcome.NotFound
                    : ownerByUnit.GetValueOrDefault(matched[0]) == command.PartyRef
                        ? CustomerListMatchOutcome.Owned
                        : CustomerListMatchOutcome.OwnedByAnotherParty;
            var reasons = row.Reasons.ToList();
            if (matched.Count > 1) reasons.Add("ambiguous_identifier");
            var unitId = matched.Count == 1 ? matched[0] : (Guid?)null;
            Guid? assertionId = null;
            if (outcome == CustomerListMatchOutcome.OwnedByAnotherParty && unitId is { } assertedUnit)
            {
                assertionId = TimeOrderedId.New(command.AssertedAt.AddTicks(row.Source.Ordinal));
                assertions.Add(new CustomerAssertion
                {
                    TenantId = tenant.TenantId,
                    AssertionId = assertionId.Value,
                    CommandId = assertionId.Value,
                    PartyRef = command.PartyRef,
                    UnitId = assertedUnit,
                    IdentifierType = row.MatchedType(hits) ?? row.Source.IdentifierType ?? IdentifierType.InternalSerial,
                    RawIdentifier = row.Source.RawIdentifier,
                    NormalisedIdentifier = row.MatchedNormalised(hits)
                        ?? (row.Candidates.Count > 0 ? row.Candidates[0].Value : ""),
                    CustomerPoRef = row.Source.CustomerPoRef,
                    CustomerItemRef = row.Source.CustomerItemRef,
                    Status = CustomerAssertionStatus.PendingVerification,
                    Source = "partner_report",
                    Provenance = "customer_asserted",
                    AssertedAt = command.AssertedAt,
                    AssertedBy = command.ActorRef,
                });
            }

            resultRows.Add(new CustomerReconciliationRow
            {
                TenantId = tenant.TenantId,
                ReconciliationId = command.CommandId,
                Ordinal = row.Source.Ordinal,
                RawIdentifier = row.Source.RawIdentifier,
                RequestedIdentifierType = row.Source.IdentifierType,
                MatchedIdentifierType = row.MatchedType(hits),
                NormalisedIdentifier = row.MatchedNormalised(hits)
                    ?? (row.Candidates.Count > 0 ? row.Candidates[0].Value : null),
                Outcome = outcome,
                ValidationReasonsJson = JsonSerializer.Serialize(reasons.Distinct(StringComparer.Ordinal)),
                UnitId = unitId,
                OtherOwnerPartyRef = outcome == CustomerListMatchOutcome.OwnedByAnotherParty && unitId is { } otherUnit
                    ? ownerByUnit.GetValueOrDefault(otherUnit) : null,
                AssertionId = assertionId,
                CustomerPoRef = row.Source.CustomerPoRef,
                CustomerItemRef = row.Source.CustomerItemRef,
            });
        }

        db.CustomerReconciliations.Add(new CustomerReconciliation
        {
            TenantId = tenant.TenantId,
            ReconciliationId = command.CommandId,
            PartyRef = command.PartyRef,
            SourceKind = source.Kind,
            SourceReferenceId = source.ReferenceId,
            TotalCount = resultRows.Count,
            OwnedCount = resultRows.Count(row => row.Outcome == CustomerListMatchOutcome.Owned),
            OtherOwnerCount = resultRows.Count(row => row.Outcome == CustomerListMatchOutcome.OwnedByAnotherParty),
            NotFoundCount = resultRows.Count(row => row.Outcome == CustomerListMatchOutcome.NotFound),
            InvalidCount = resultRows.Count(row => row.Outcome == CustomerListMatchOutcome.Invalid),
            ReconciledAt = command.AssertedAt,
            ReconciledBy = command.ActorRef,
        });
        db.CustomerAssertions.AddRange(assertions);
        db.CustomerReconciliationRows.AddRange(resultRows);
        await db.SaveChangesAsync(cancellationToken);
        return await PageAsync(command.CommandId, command.After, command.Take, false, cancellationToken);
    }

    public async Task<CustomerListReconciliationPage> PageAsync(Guid reconciliationId, string? after, int take,
        bool replayed, CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, MaximumPageSize);
        var job = await db.CustomerReconciliations.AsNoTracking().SingleOrDefaultAsync(row =>
            row.TenantId == tenant.TenantId && row.ReconciliationId == reconciliationId, cancellationToken)
            ?? throw new CommandRefusedException("customer_reconciliation_unknown",
                $"Customer reconciliation {reconciliationId} was not found.");
        var afterOrdinal = DecodeCursor(after);
        var stored = await db.CustomerReconciliationRows.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && row.ReconciliationId == reconciliationId
                && row.Ordinal > afterOrdinal)
            .OrderBy(row => row.Ordinal).Take(take + 1).ToListAsync(cancellationToken);
        var hasMore = stored.Count > take;
        stored = stored.Take(take).ToList();
        var views = await ViewsAsync(stored, cancellationToken);
        var customer = await partyNames.NamesAsync([job.PartyRef], cancellationToken);
        return new CustomerListReconciliationPage(job.ReconciliationId, job.SourceKind,
            PartyName(customer.GetValueOrDefault(job.PartyRef)), job.TotalCount, job.OwnedCount,
            job.OtherOwnerCount, job.NotFoundCount, job.InvalidCount, views,
            hasMore ? EncodeCursor(stored[^1].Ordinal) : null, replayed);
    }

    public async IAsyncEnumerable<IReadOnlyList<string?>> ExportAsync(Guid reconciliationId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? cursor = null;
        do
        {
            var page = await PageAsync(reconciliationId, cursor, MaximumPageSize, false, cancellationToken);
            foreach (var row in page.Items)
            {
                yield return
                [
                    row.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture), row.Input,
                    row.IdentifierType, row.NormalisedIdentifier, row.Outcome,
                    string.Join("; ", row.ValidationReasons), row.Unit, row.Item, row.OtherOwner,
                    row.AssertionStatus, row.CustomerPoRef, row.CustomerItemRef,
                ];
            }
            cursor = page.NextCursor;
        } while (cursor is not null);
    }

    private PreparedSourceRow Prepare(CustomerListSourceRow source)
    {
        if (source.UnitId is not null) return new(source, [], []);
        var candidates = new List<IdentifierCandidate>();
        var reasons = new List<string>();
        var types = source.IdentifierType is null ? LookupTypes : [source.IdentifierType];
        foreach (var type in types)
        {
            IdentifierPolicy policy;
            try { policy = identifierPolicies.For(type); }
            catch (ArgumentException)
            {
                return new(source, [], ["identifier_type_unknown"]);
            }
            var normalised = IdentifierNormaliser.Normalise(source.RawIdentifier, policy).Value;
            var validation = IdentifierValidator.Validate(normalised, policy);
            if (validation.IsValid) candidates.Add(new(type, normalised));
            else if (source.IdentifierType is not null) reasons.AddRange(validation.Reasons);
        }
        if (string.IsNullOrWhiteSpace(source.RawIdentifier)) reasons.Add("empty");
        else if (source.IdentifierType is null && candidates.Count == 0) reasons.Add("invalid_for_all_identifier_types");
        return new(source, candidates.Distinct().ToList(), reasons.Distinct(StringComparer.Ordinal).ToList());
    }

    private async Task<List<IdentifierHit>> MatchAsync(IReadOnlyList<PreparedSourceRow> rows,
        CancellationToken cancellationToken)
    {
        var hits = new List<IdentifierHit>();
        foreach (var group in rows.SelectMany(row => row.Candidates).Distinct()
                     .GroupBy(row => row.Type, StringComparer.Ordinal))
        {
            var type = group.Key;
            foreach (var values in group.Select(row => row.Value).Distinct(StringComparer.Ordinal).Chunk(1500))
            {
                var found = await db.Identifiers.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                        && row.Type == type && values.Contains(row.NormalisedValue))
                    .Select(row => new IdentifierHit(row.UnitId, row.Type, row.NormalisedValue))
                    .ToListAsync(cancellationToken);
                hits.AddRange(found);
            }
        }
        return hits.Distinct().ToList();
    }

    private static IEnumerable<Guid> UnitsFor(PreparedSourceRow row, IReadOnlyList<IdentifierHit> hits)
    {
        if (row.Source.UnitId is { } resolved) return [resolved];
        return hits.Where(hit => row.Candidates.Contains(new IdentifierCandidate(hit.Type, hit.Value)))
            .Select(hit => hit.UnitId);
    }

    private async Task<IReadOnlyList<CustomerListReconciliationRow>> ViewsAsync(
        IReadOnlyList<CustomerReconciliationRow> rows,
        CancellationToken cancellationToken)
    {
        var unitIds = rows.Where(row => row.UnitId != null).Select(row => row.UnitId!.Value).Distinct().ToList();
        var states = unitIds.Count == 0 ? [] : await db.Current.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && unitIds.Contains(row.UnitId))
            .Select(row => new { row.UnitId, row.ItemId }).ToListAsync(cancellationToken);
        var identifiers = unitIds.Count == 0 ? [] : await db.Identifiers.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && unitIds.Contains(row.UnitId) && row.ValidTo == null)
            .OrderBy(row => row.Type == IdentifierType.InternalSerial ? 0 : 1).ThenBy(row => row.Type)
            .Select(row => new { row.UnitId, row.RawValue }).ToListAsync(cancellationToken);
        var serials = identifiers.GroupBy(row => row.UnitId).ToDictionary(group => group.Key,
            group => group.First().RawValue);
        var itemLabels = await itemNames.NamesAsync(states.Select(row => row.ItemId).Distinct().ToList(), cancellationToken);
        var itemsByUnit = states.ToDictionary(row => row.UnitId,
            row => ItemName(itemLabels.GetValueOrDefault(row.ItemId)));
        var partyRefs = rows.Where(row => row.OtherOwnerPartyRef != null)
            .Select(row => row.OtherOwnerPartyRef!.Value).Distinct().ToList();
        var parties = await partyNames.NamesAsync(partyRefs, cancellationToken);
        var assertionIds = rows.Where(row => row.AssertionId != null).Select(row => row.AssertionId!.Value).ToList();
        var assertions = assertionIds.Count == 0 ? [] : await db.CustomerAssertions.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && assertionIds.Contains(row.AssertionId))
            .Select(row => new { row.AssertionId, row.Status }).ToListAsync(cancellationToken);
        var statusByAssertion = assertions.ToDictionary(row => row.AssertionId, row => row.Status);
        return rows.Select(row => new CustomerListReconciliationRow(row.Ordinal, row.RawIdentifier,
            row.MatchedIdentifierType ?? row.RequestedIdentifierType, row.NormalisedIdentifier, row.Outcome,
            JsonSerializer.Deserialize<List<string>>(row.ValidationReasonsJson) ?? [],
            row.UnitId is { } unitId ? serials.GetValueOrDefault(unitId) ?? "UNLABELLED UNIT" : null,
            row.UnitId is { } itemUnit ? itemsByUnit.GetValueOrDefault(itemUnit) ?? "ITEM NOT FOUND" : null,
            row.OtherOwnerPartyRef is { } owner ? PartyName(parties.GetValueOrDefault(owner)) : null,
            row.AssertionId,
            row.AssertionId is { } assertion ? statusByAssertion.GetValueOrDefault(assertion) : null,
            row.CustomerPoRef, row.CustomerItemRef)).ToList();
    }

    private static string ItemName(ItemLabel? label) => label is null ? "ITEM NOT FOUND"
        : label.Name is null ? label.Code : $"{label.Code} · {label.Name}";

    private static string PartyName(PartyLabel? label) => label?.Name ?? label?.Code ?? "PARTY NOT FOUND";

    private static string EncodeCursor(int ordinal) =>
        Convert.ToBase64String(BitConverter.GetBytes(ordinal)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static int DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return 0;
        try
        {
            var value = cursor.Replace('-', '+').Replace('_', '/');
            value += new string('=', (4 - value.Length % 4) % 4);
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length != sizeof(int)) throw new FormatException();
            return BitConverter.ToInt32(bytes);
        }
        catch (FormatException)
        {
            throw new CommandRefusedException("customer_reconciliation_cursor_invalid",
                "The customer-reconciliation cursor is invalid.");
        }
    }

    private sealed record IdentifierCandidate(string Type, string Value);
    private sealed record IdentifierHit(Guid UnitId, string Type, string Value);
    private sealed record PreparedSourceRow(CustomerListSourceRow Source,
        IReadOnlyList<IdentifierCandidate> Candidates, IReadOnlyList<string> Reasons)
    {
        public string? MatchedType(IReadOnlyList<IdentifierHit> hits) => hits.FirstOrDefault(hit =>
            Candidates.Contains(new IdentifierCandidate(hit.Type, hit.Value)))?.Type;

        public string? MatchedNormalised(IReadOnlyList<IdentifierHit> hits) => hits.FirstOrDefault(hit =>
            Candidates.Contains(new IdentifierCandidate(hit.Type, hit.Value)))?.Value;
    }
}

public static class ReconcileCustomerListHandler
{
    public static Task<CustomerListReconciliationPage> Handle(ReconcileCustomerList command,
        CustomerListReconciliationService service, CancellationToken cancellationToken) =>
        service.ReconcileAsync(command, cancellationToken);
}

public static class CustomerAssertionDecisionHandler
{
    private static readonly string[] ReviewerRoles =
        [YojanaRole.SerialAdministrator, YojanaRole.OrderDeskSupervisor];

    public static async Task<CustomerAssertionDecision> Handle(VerifyCustomerAssertion command,
        SerialisationDbContext db,
        IUnitEventWriter writer,
        EpisodeService episodes,
        ITenantContext tenant,
        IClock clock,
        CancellationToken cancellationToken)
    {
        RequireReviewer(command.ActorRoles);
        if (string.IsNullOrWhiteSpace(command.EvidenceRef))
            throw new CommandRefusedException("delivery_evidence_required",
                "Delivery evidence is required before an ownership assertion can be verified.");
        if (command.EvidenceDate.Kind != DateTimeKind.Utc)
            throw new CommandRefusedException("event_time_not_utc", "The delivery evidence date must be UTC.");
        var assertion = await db.CustomerAssertions.SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.AssertionId == command.AssertionId, cancellationToken)
            ?? throw new CommandRefusedException("customer_assertion_unknown",
                $"Customer assertion {command.AssertionId} was not found.");
        if (assertion.Status == CustomerAssertionStatus.Verified)
            return Decision(assertion, true);
        if (assertion.Status != CustomerAssertionStatus.PendingVerification)
            throw new CommandRefusedException("customer_assertion_already_decided",
                "A rejected ownership assertion cannot be verified; reconcile the corrected evidence as a new assertion.");
        if (assertion.UnitId is not { } unitId)
            throw new CommandRefusedException("customer_assertion_unmatched",
                "An unmatched customer assertion cannot open ownership.");

        var opened = await OpenOwnershipHandler.Handle(new OpenOwnership(command.CommandId, unitId,
            assertion.PartyRef, PartyType.Customer, EpisodeReason.Sale, command.EvidenceDate, command.ActorRef,
            command.LegalEntityId, Source: "partner_report", Confidence: Confidence.High,
            CustomerPoRef: assertion.CustomerPoRef, CustomerItemRef: assertion.CustomerItemRef,
            ClosingReason: "verified_customer_assertion"), db, writer, episodes, tenant, cancellationToken);
        assertion.Status = CustomerAssertionStatus.Verified;
        assertion.EvidenceRef = command.EvidenceRef.Trim();
        assertion.EvidenceDate = command.EvidenceDate;
        assertion.VerifiedAt = clock.UtcNow;
        assertion.VerifiedBy = command.ActorRef;
        assertion.OwnershipEpisodeId = opened.EpisodeId;
        await db.SaveChangesAsync(cancellationToken);
        return Decision(assertion, false);
    }

    public static async Task<CustomerAssertionDecision> Handle(RejectCustomerAssertion command,
        SerialisationDbContext db,
        ITenantContext tenant,
        IClock clock,
        CancellationToken cancellationToken)
    {
        RequireReviewer(command.ActorRoles);
        if (string.IsNullOrWhiteSpace(command.Reason))
            throw new CommandRefusedException("rejection_reason_required",
                "A reason is required when rejecting an ownership assertion.");
        var assertion = await db.CustomerAssertions.SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.AssertionId == command.AssertionId, cancellationToken)
            ?? throw new CommandRefusedException("customer_assertion_unknown",
                $"Customer assertion {command.AssertionId} was not found.");
        if (assertion.Status == CustomerAssertionStatus.Rejected)
            return Decision(assertion, true);
        if (assertion.Status != CustomerAssertionStatus.PendingVerification)
            throw new CommandRefusedException("customer_assertion_already_decided",
                "A verified ownership assertion cannot be rejected; correct ownership through its ledger command.");
        assertion.Status = CustomerAssertionStatus.Rejected;
        assertion.RejectionReason = command.Reason.Trim();
        assertion.RejectedAt = clock.UtcNow;
        assertion.RejectedBy = command.ActorRef;
        await db.SaveChangesAsync(cancellationToken);
        return Decision(assertion, false);
    }

    private static void RequireReviewer(IReadOnlyList<string> roles)
    {
        if (!roles.Any(role => ReviewerRoles.Contains(role, StringComparer.Ordinal)))
            throw new CommandRefusedException("customer_assertion_reviewer_required",
                "Only a serial administrator or order-desk supervisor may decide an ownership assertion.");
    }

    private static CustomerAssertionDecision Decision(CustomerAssertion assertion, bool replayed)
    {
        var at = assertion.VerifiedAt ?? assertion.RejectedAt ?? assertion.AssertedAt;
        var actor = assertion.VerifiedBy ?? assertion.RejectedBy ?? assertion.AssertedBy;
        return new(assertion.AssertionId, assertion.Status, assertion.EvidenceRef, assertion.RejectionReason,
            at, actor, assertion.OwnershipEpisodeId, replayed);
    }
}
