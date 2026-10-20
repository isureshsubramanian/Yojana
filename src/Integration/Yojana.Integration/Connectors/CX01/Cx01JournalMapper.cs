using Yojana.Integration.Contracts;

namespace Yojana.Integration.Connectors.CX01;

/// <summary>JournalEntries requires balanced JournalEntryLines with AccountCode, Debit, Credit. No serial allocation;
/// accounts are translated exclusively through the per-connection map.</summary>
internal static class Cx01JournalMapper
{
    public static Dictionary<string, object?> Map(PostWarrantyExpense command, Cx01DocumentSettings settings)
    {
        if (command.AccrualSuppressed || command.JournalLines.Count == 0)
            throw new Cx01UnmappableException("missing journal lines in the module event");
        if (command.JournalLines.Any(x => string.IsNullOrWhiteSpace(x.Currency) || x.Currency == "XXX"))
            throw new Cx01UnmappableException("missing journal currency and balancing account in the module event");
        if (command.JournalLines.Any(x => Cx01Money.Code(x.Currency, settings) != settings.LocalCurrencyCode))
            throw new Cx01UnmappableException("missing local amounts for a foreign-currency journal in the module event; enter this journal by hand.");
        var lines = command.JournalLines.Select(line =>
        {
            if (settings.AccountMap?.GetValueOrDefault(line.AccountRef) is not { } account)
                throw new Cx01DocumentRuleException($"Map the warranty account '{line.AccountRef}' before posting the journal.");
            if (line.Debit < 0 || line.Credit < 0 || line.Debit > 0 && line.Credit > 0 || line.Debit == 0 && line.Credit == 0)
                throw new Cx01DocumentRuleException("Journal lines need non-negative, one-sided amounts.");
            return new Dictionary<string, object?> { ["AccountCode"] = account, ["Debit"] = Cx01Money.Round(line.Debit, line.Currency, settings), ["Credit"] = Cx01Money.Round(line.Credit, line.Currency, settings) };
        }).ToArray();
        if (lines.Any(x => (decimal)x["Debit"]! == 0 && (decimal)x["Credit"]! == 0))
            throw new Cx01DocumentRuleException("Journal lines must retain a positive amount after currency rounding.");
        if (lines.Sum(x => (decimal)x["Debit"]! - (decimal)x["Credit"]!) != 0 || command.JournalLines.Select(x => x.Currency).Distinct().Count() != 1)
            throw new Cx01DocumentRuleException("The journal must balance in one currency; the adapter never invents a balancing line.");
        return new() { ["JournalEntryLines"] = lines, ["Reference2"] = command.CohortTag };
    }
}
