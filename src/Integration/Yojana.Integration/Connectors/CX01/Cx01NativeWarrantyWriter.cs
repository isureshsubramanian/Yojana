using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Coexistence;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Outbox;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.Connectors.CX01;

/// <summary>CX01-only translation and optimistic conflict check for the installed-base update.</summary>
public sealed class Cx01NativeWarrantyWriter(IntegrationDbContext db, ITenantContext tenant, IClock clock,
    Cx01Client client)
{
    private const string Collection = "CustomerEquipmentCards";
    private const string StatusField = "StatusOfSerialNumber";
    private const string WarrantyEndField = "ContractEndDate";

    public async Task<OutboundDocument?> FindAsync(OutboundLookup lookup, CancellationToken cancellationToken)
    {
        var command = Command(lookup.Payload);
        var state = await StateAsync(lookup.ConnectionId, command.Unit.UnitId, cancellationToken);
        using var observed = await ReadAsync(lookup.ConnectionId, state.RemoteRecordKey, cancellationToken);
        var remote = Values(observed.RootElement);
        var desired = Desired(command);
        if (!Equal(remote, desired, command.IncludesWarrantyContractFields)) return null;
        await RecordAsync(state, desired, observed.RootElement, cancellationToken);
        return new(state.RemoteRecordKey, Cx01Mapper.ChangeMarker(observed.RootElement, state.RemoteRecordKey));
    }

    public async Task<OutboundCreateResult> UpdateAsync(OutboundCreateRequest request,
        CancellationToken cancellationToken)
    {
        var command = Command(request.Payload);
        var state = await StateAsync(request.ConnectionId, command.Unit.UnitId, cancellationToken);
        using var observed = await ReadAsync(request.ConnectionId, state.RemoteRecordKey, cancellationToken);
        var remote = Values(observed.RootElement);
        var desired = Desired(command);
        if (Equal(remote, desired, command.IncludesWarrantyContractFields))
        {
            await RecordAsync(state, desired, observed.RootElement, cancellationToken);
            return OutboundCreateResult.Created(state.RemoteRecordKey,
                Cx01Mapper.ChangeMarker(observed.RootElement, state.RemoteRecordKey));
        }

        var baseline = state.AllowRemoteOverwriteOnce
            ? null : await ChangedBaselineAsync(state, remote, cancellationToken);
        if (baseline is not null)
        {
            var conflict = await ConflictAsync(state, command, baseline, remote, cancellationToken);
            return OutboundCreateResult.Conflict(conflict.ConflictId,
                "The remote installed-base record changed after it was last observed. The module values were not written.");
        }

        state.AllowRemoteOverwriteOnce = false;
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [StatusField] = desired.Status,
        };
        if (command.IncludesWarrantyContractFields)
            body[WarrantyEndField] = desired.WarrantyEndDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        try
        {
            using var response = await client.SendJsonAsync(request.ConnectionId, HttpMethod.Patch,
                Path(state.RemoteRecordKey), body, true, cancellationToken);
            using var after = await ReadAsync(request.ConnectionId, state.RemoteRecordKey, cancellationToken);
            var written = Values(after.RootElement);
            if (!Equal(written, desired, command.IncludesWarrantyContractFields))
                return OutboundCreateResult.Rejected(
                    "The remote installed-base record did not retain the warranty values; review its configured validation rules.");
            await RecordAsync(state, desired, after.RootElement, cancellationToken);
            return OutboundCreateResult.Created(state.RemoteRecordKey,
                Cx01Mapper.ChangeMarker(after.RootElement, state.RemoteRecordKey));
        }
        catch (Cx01RemoteException exception) when (exception.IsBusinessRejection)
        {
            return OutboundCreateResult.Rejected(exception.BusinessMessage());
        }
    }

    private async Task<NativeWarrantyRemoteValues?> ChangedBaselineAsync(NativeWarrantyPushState state,
        NativeWarrantyRemoteValues remote, CancellationToken cancellationToken)
    {
        var status = state.LastPushedRemoteStatus;
        DateOnly? end = state.LastPushedWarrantyEndDate;
        if (status is null)
        {
            var baselines = await db.CoexistenceBaselines.AsNoTracking().Where(row =>
                    row.TenantId == tenant.TenantId && row.ConnectionId == state.ConnectionId
                    && row.RecordType == "installed_base_record" && row.RecordKey == state.RemoteRecordKey
                    && (row.FieldName == StatusField || row.FieldName == WarrantyEndField))
                .ToDictionaryAsync(row => row.FieldName, row => row.Value, cancellationToken);
            status = baselines.GetValueOrDefault(StatusField);
            end = Date(baselines.GetValueOrDefault(WarrantyEndField));
        }
        var changed = status is not null && !StringComparer.Ordinal.Equals(status, remote.Status)
            || state.IncludesWarrantyContractFields && end != remote.WarrantyEndDate;
        return changed ? new(status ?? remote.Status, end) : null;
    }

    private async Task<CoexistenceConflict> ConflictAsync(NativeWarrantyPushState state,
        PushNativeWarrantyObject command, NativeWarrantyRemoteValues baseline,
        NativeWarrantyRemoteValues remote, CancellationToken cancellationToken)
    {
        var conflict = await db.CoexistenceConflicts.SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.ConnectionId == state.ConnectionId && row.RecordType == "installed_base_record"
            && row.RecordKey == state.RemoteRecordKey && row.FieldName == "native_warranty_values"
            && row.Status != CoexistenceConflictStatus.Closed, cancellationToken);
        if (conflict is null)
        {
            conflict = new CoexistenceConflict
            {
                TenantId = tenant.TenantId, ConflictId = TimeOrderedId.New(clock.UtcNow),
                ConnectionId = state.ConnectionId, RecordType = "installed_base_record",
                RecordKey = state.RemoteRecordKey,
                RecordLabel = string.IsNullOrWhiteSpace(command.Unit.IdentifierValue)
                    ? $"Installed-base record {state.RemoteRecordKey}" : command.Unit.IdentifierValue,
                Kind = CoexistenceConflictKind.PushedWarrantyFieldChangedRemotely,
                FieldName = "native_warranty_values", Status = CoexistenceConflictStatus.Open,
                DetectedAt = clock.UtcNow, LastSeenAt = clock.UtcNow,
                BaselineValue = JsonSerializer.Serialize(baseline, IntegrationContracts.Json),
                RemoteValue = JsonSerializer.Serialize(remote, IntegrationContracts.Json),
            };
            db.CoexistenceConflicts.Add(conflict);
        }
        else
        {
            conflict.RemoteValue = JsonSerializer.Serialize(remote, IntegrationContracts.Json);
            conflict.LastSeenAt = clock.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);
        return conflict;
    }

    private async Task RecordAsync(NativeWarrantyPushState state, NativeWarrantyRemoteValues values,
        JsonElement observed, CancellationToken cancellationToken)
    {
        state.LastPushedRemoteStatus = values.Status;
        if (state.IncludesWarrantyContractFields) state.LastPushedWarrantyEndDate = values.WarrantyEndDate;
        state.LastPushedAt = clock.UtcNow;
        state.AllowRemoteOverwriteOnce = false;
        var explaining = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["DeliveryCode"] = Scalar(observed, "DeliveryCode"),
            ["InvoiceCode"] = Scalar(observed, "InvoiceCode"),
        };
        var hash = CoexistenceDriftService.ExplainingHash(new(state.RemoteRecordKey, state.RemoteRecordKey,
            explaining), ["DeliveryCode", "InvoiceCode"]);
        await UpsertBaselineAsync(state, StatusField, values.Status, hash, cancellationToken);
        if (state.IncludesWarrantyContractFields)
            await UpsertBaselineAsync(state, WarrantyEndField,
                values.WarrantyEndDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), hash,
                cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task UpsertBaselineAsync(NativeWarrantyPushState state, string field, string? value, string hash,
        CancellationToken cancellationToken)
    {
        var row = await db.CoexistenceBaselines.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == state.ConnectionId && x.RecordType == "installed_base_record"
            && x.RecordKey == state.RemoteRecordKey && x.FieldName == field, cancellationToken);
        if (row is null)
            db.CoexistenceBaselines.Add(new()
            {
                TenantId = tenant.TenantId, ConnectionId = state.ConnectionId,
                RecordType = "installed_base_record", RecordKey = state.RemoteRecordKey, FieldName = field,
                Value = value, ExplainingHash = hash, Source = CoexistenceDriftService.SourcePushed,
                UpdatedAt = clock.UtcNow,
            });
        else
        {
            row.Value = value;
            row.ExplainingHash = hash;
            row.Source = CoexistenceDriftService.SourcePushed;
            row.UpdatedAt = clock.UtcNow;
        }
    }

    private async Task<NativeWarrantyPushState> StateAsync(Guid connectionId, Guid unitId,
        CancellationToken cancellationToken) =>
        await db.NativeWarrantyPushStates.SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.ConnectionId == connectionId && row.UnitId == unitId, cancellationToken)
        ?? throw new Cx01UnmappableException("The native warranty push has no coalescing state.");

    private Task<JsonDocument> ReadAsync(Guid connectionId, string key, CancellationToken cancellationToken) =>
        client.GetAsync(connectionId, Path(key), cancellationToken);

    private static string Path(string key) => Cx01Protocol.Root + Collection + "("
        + Cx01Protocol.KeyLiteral(key) + ")";

    private static PushNativeWarrantyObject Command(string? payload)
    {
        using var json = JsonDocument.Parse(payload ?? throw new Cx01UnmappableException(
            "The native warranty command payload is missing."));
        var root = json.RootElement.TryGetProperty("payload", out var nested) ? nested : json.RootElement;
        return root.Deserialize<PushNativeWarrantyObject>(IntegrationContracts.Json)
            ?? throw new Cx01UnmappableException("The native warranty command payload is invalid.");
    }

    private static NativeWarrantyRemoteValues Desired(PushNativeWarrantyObject command) => new(
        command.ModuleStatus switch
        {
            LifecycleStatus.Void or LifecycleStatus.ScrappedPreRelease or LifecycleStatus.Scrapped
                or LifecycleStatus.Decommissioned or LifecycleStatus.Merged => "sns_Terminated",
            LifecycleStatus.PendingIdentifier or LifecycleStatus.PreRelease or LifecycleStatus.Released
                or LifecycleStatus.Dormant => "sns_Active",
            _ => throw new Cx01UnmappableException(
                $"Module lifecycle status '{command.ModuleStatus}' has no verified CX01 installed-base status mapping."),
        }, command.WarrantyEndDate);

    private static NativeWarrantyRemoteValues Values(JsonElement row) =>
        new(Scalar(row, StatusField) ?? "", Date(Scalar(row, WarrantyEndField)));

    private static bool Equal(NativeWarrantyRemoteValues remote, NativeWarrantyRemoteValues desired,
        bool includeContract) => StringComparer.Ordinal.Equals(remote.Status, desired.Status)
            && (!includeContract || remote.WarrantyEndDate == desired.WarrantyEndDate);

    private static DateOnly? Date(string? value) => DateOnly.TryParse(value, CultureInfo.InvariantCulture,
        DateTimeStyles.AllowWhiteSpaces, out var parsed) ? parsed : null;

    private static string? Scalar(JsonElement row, string field)
    {
        if (!row.TryGetProperty(field, out var value) || value.ValueKind is JsonValueKind.Null
            or JsonValueKind.Undefined) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }
}
