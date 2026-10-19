using System.Text.Json;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.DeadLetters;

public sealed record ManualTaskDeskDefinition(string Code, string Label);
public sealed record ManualTaskDeskAssignment(string CommandType, string Desk);
public sealed record ManualTaskDeskPolicy(
    IReadOnlyList<ManualTaskDeskDefinition> Desks,
    IReadOnlyList<ManualTaskDeskAssignment> Assignments);
public sealed record ManualTaskDeskSettingsView(
    IReadOnlyList<ManualTaskDeskDefinition> Desks,
    IReadOnlyList<ManualTaskDeskAssignment> Assignments,
    string SettingsVersion);
public sealed record UpdateManualTaskDeskSettings(
    IReadOnlyList<ManualTaskDeskDefinition> Desks,
    IReadOnlyList<ManualTaskDeskAssignment> Assignments,
    string ExpectedVersion);

/// <summary>Tenant-owned hand-entry desks and command routing; task rows keep the resolved desk as history.</summary>
public sealed class ManualTaskDeskSettings(ITenantSettings settings)
{
    public const string Key = "integration.manual_task_desks";
    public static readonly ManualTaskDeskPolicy Default = new(
        [new("finance", "Finance"), new("warehouse", "Warehouse"), new("service", "Service"), new("integration", "Integration")],
        [
            new("CreateCreditMemo", "finance"),
            new("PostWarrantyExpense", "finance"),
            new("CreateReplacementDelivery", "warehouse"),
            new("ChangeStockStatusOrLocation", "warehouse"),
            new("CreateServiceOrder", "service"),
            new("AttachDocument", "service"),
        ]);

    public async Task<ManualTaskDeskSettingsView> GetAsync(CancellationToken cancellationToken)
    {
        var stored = await settings.GetVersionedAsync(Key, cancellationToken);
        var policy = stored is null ? Default : Normalise(Deserialise(stored.Value));
        return new(policy.Desks, policy.Assignments, stored?.Version ?? ITenantSettings.MissingVersion);
    }

    public async Task<ManualTaskDeskSettingsView> SetAsync(UpdateManualTaskDeskSettings update,
        CancellationToken cancellationToken)
    {
        var policy = Normalise(new(update.Desks, update.Assignments));
        var stored = await settings.SetAsync(Key, JsonSerializer.Serialize(policy), update.ExpectedVersion,
            cancellationToken);
        return new(policy.Desks, policy.Assignments, stored.Version);
    }

    public async Task<string> ResolveAsync(string commandType, CancellationToken cancellationToken)
    {
        var view = await GetAsync(cancellationToken);
        return view.Assignments.FirstOrDefault(row => StringComparer.Ordinal.Equals(row.CommandType, commandType))?.Desk
            ?? view.Desks[0].Code;
    }

    private static ManualTaskDeskPolicy Deserialise(string text) =>
        JsonSerializer.Deserialize<ManualTaskDeskPolicy>(text) ?? Default;

    private static ManualTaskDeskPolicy Normalise(ManualTaskDeskPolicy policy)
    {
        var desks = policy.Desks.Select(row => new ManualTaskDeskDefinition(row.Code.Trim().ToLowerInvariant(),
                row.Label.Trim()))
            .Where(row => row.Code.Length > 0 && row.Label.Length > 0).DistinctBy(row => row.Code)
            .OrderBy(row => row.Label, StringComparer.OrdinalIgnoreCase).ToList();
        if (desks.Count == 0) throw new DeadLetterRuleException("manual_task_desks_required",
            "At least one hand-entry desk is required.");
        if (desks.Any(row => row.Code.Length > 80 || row.Label.Length > 120))
            throw new DeadLetterRuleException("manual_task_desk_invalid", "Desk codes and labels are too long.");
        var known = desks.Select(row => row.Code).ToHashSet(StringComparer.Ordinal);
        var assignments = policy.Assignments.Select(row => new ManualTaskDeskAssignment(row.CommandType.Trim(),
                row.Desk.Trim().ToLowerInvariant()))
            .Where(row => row.CommandType.Length > 0).DistinctBy(row => row.CommandType)
            .OrderBy(row => row.CommandType, StringComparer.Ordinal).ToList();
        if (assignments.Any(row => !known.Contains(row.Desk)))
            throw new DeadLetterRuleException("manual_task_desk_unknown",
                "Every command assignment must name one of this tenant's desks.");
        return new(desks, assignments);
    }
}
