using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Entitlements;

/// <summary>Derives a sale business date only when a creating command carries both a sale document and sale evidence.</summary>
internal static class EntitlementPurchaseDate
{
    private static readonly string[] SaleEvidence = [StartTrigger.PlanPurchase, StartTrigger.Invoice, StartTrigger.FirstExternalSale];

    internal static DateOnly? From(CreateEntitlement command, string timeZone)
    {
        if (command.Provenance != EntitlementProvenance.Created || command.SourceDocument is null) return null;
        foreach (var trigger in SaleEvidence)
        {
            if (command.Evidence.TryGetValue(trigger, out var occurredAt))
                return TermArithmetic.LocalDay(occurredAt, timeZone);
        }
        return null;
    }
}
