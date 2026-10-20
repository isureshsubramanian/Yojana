using Yojana.Integration.Contracts;
using Yojana.Integration.Outbox;

namespace Yojana.Integration.Connectors.CX01;

public sealed class Cx01OutboundCompleteness(Cx01Settings settings, ICx01AttachmentContent content,
    ICx01OutboundFacts facts) : IOutboundCompletenessCheck
{
    public async Task<IReadOnlyList<string>> MissingAsync(Guid connectionId, string outboundMode, IOutboundCommand command, CancellationToken ct)
    {
        if (outboundMode != "documents") return [];
        var configured = await settings.GetAsync(connectionId, ct);
        var missing = RequiredFields(command, configured.Documents ?? new()).ToList();
        if (command is AttachDocument attachment)
        {
            // This check and Cx01OutboundDocuments share the scoped content port: BeginAttempt ensures a retry
            // renders afresh, and the uploader reads the exact bytes validated here. Separate caches could upload
            // different content or render twice while the connection lock is held.
            content.BeginAttempt();
            if (!string.IsNullOrWhiteSpace(attachment.ContentRef))
            {
                try { missing.AddRange(ContentFields(await content.ReadAsync(attachment.ContentRef, (configured.Documents ?? new()).AttachmentSizeLimit, ct))); }
                catch (Cx01DocumentRuleException ex) { missing.Add("content_ref (" + ex.Message + ")"); }
            }
            if (!string.IsNullOrWhiteSpace(attachment.TargetRecordRef))
            {
                try
                {
                    var target = await facts.ReferenceAsync(connectionId, "document", attachment.TargetRecordRef, ct);
                    Cx01AttachmentMapper.Target(target ?? attachment.TargetRecordRef);
                }
                catch (Cx01UnmappableException) { missing.Add("target_record_ref"); }
            }
        }
        return missing.Distinct().ToList();
    }

    public static IReadOnlyList<string> ContentFields(Cx01AttachmentContent? file)
    {
        if (file is null) return ["content_ref"];
        var missing = new List<string>();
        if (file.Bytes.Length == 0) missing.Add("content.bytes");
        if (string.IsNullOrWhiteSpace(file.ContentType)) missing.Add("content.content_type");
        return missing;
    }

    public static IReadOnlyList<string> RequiredFields(IOutboundCommand command, Cx01DocumentSettings settings)
    {
        var missing = new List<string>();
        void Text(string? value, string field) { if (string.IsNullOrWhiteSpace(value)) missing.Add(field); }
        void Currency(string? value, string field)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "XXX" || value.Length != 3 || !value.All(char.IsAsciiLetterUpper))
            { missing.Add(field); return; }
            if (string.IsNullOrWhiteSpace(settings.CurrencyMap?.GetValueOrDefault(value)))
                missing.Add(field + " (currency not mapped)");
            else if (string.IsNullOrWhiteSpace(settings.LocalCurrencyCode))
                missing.Add("settings.local_currency_code");
        }
        void Unit(UnitReference? unit, string field)
        {
            if (unit is null) { missing.Add(field); return; }
            if (unit.UnitId == Guid.Empty) missing.Add(field + ".unit_id");
            if (string.IsNullOrWhiteSpace(unit.IdentifierValue) || Guid.TryParse(unit.IdentifierValue, out var id) && id == unit.UnitId)
                missing.Add(field + ".identifier_value");
        }
        void Customer(string? customer)
        {
            if (string.IsNullOrWhiteSpace(customer) || customer.StartsWith("unit-owner/", StringComparison.Ordinal)) missing.Add("customer_ref");
        }
        switch (command)
        {
            case CreateReplacementDelivery delivery:
                Customer(delivery.CustomerRef); Unit(delivery.ReplacementUnit, "replacement_unit");
                if (delivery.ShipToRef == "default") missing.Add("ship_to_ref"); else Text(delivery.ShipToRef, "ship_to_ref");
                if (delivery.PricePolicy?.Value is not ("zero" or "difference" or "full")) missing.Add("price_policy");
                if (delivery.PricePolicy?.Value != "zero")
                {
                    if (delivery.Price is null || delivery.Price.Amount <= 0) missing.Add("price.amount");
                    Currency(delivery.Price?.Currency, "price.currency");
                }
                break;
            case CreateCreditMemo credit:
                Customer(credit.CustomerRef);
                if (credit.Lines is not { Count: > 0 }) { missing.Add("lines"); break; }
                for (var i = 0; i < credit.Lines.Count; i++)
                {
                    var line = credit.Lines[i];
                    if (line is null) { missing.Add($"lines[{i}]"); continue; }
                    if (line.Amount is null || line.Amount.Amount <= 0) missing.Add($"lines[{i}].amount.amount");
                    Currency(line.Amount?.Currency, $"lines[{i}].amount.currency");
                    if (line.Quantity <= 0) missing.Add($"lines[{i}].quantity");
                    if (line.UnitId is null && string.IsNullOrWhiteSpace(line.ItemExternalId)) missing.Add($"lines[{i}].item_external_id");
                }
                break;
            case PostWarrantyExpense expense:
                if (expense.JournalLines is not { Count: > 0 } || expense.AccrualSuppressed) { missing.Add("journal_lines"); break; }
                for (var i = 0; i < expense.JournalLines.Count; i++)
                {
                    var line = expense.JournalLines[i];
                    if (line is null) { missing.Add($"journal_lines[{i}]"); continue; }
                    Currency(line.Currency, $"journal_lines[{i}].currency");
                    if (line.Currency is not null && settings.LocalCurrencyCode is not null
                        && settings.CurrencyMap?.GetValueOrDefault(line.Currency) is { } remoteCurrency && remoteCurrency != settings.LocalCurrencyCode)
                        missing.Add($"journal_lines[{i}].local_amounts");
                    if (string.IsNullOrWhiteSpace(line.AccountRef) || string.IsNullOrWhiteSpace(settings.AccountMap?.GetValueOrDefault(line.AccountRef)))
                        missing.Add($"journal_lines[{i}].account_ref ({line.AccountRef})");
                    if (line.Debit < 0 || line.Credit < 0 || (line.Debit > 0) == (line.Credit > 0)) missing.Add($"journal_lines[{i}].amount");
                }
                if (expense.JournalLines.Where(x => x is not null).Sum(x => x.Debit - x.Credit) != 0) missing.Add("journal_lines.balancing_account");
                break;
            case ChangeStockStatusOrLocation stock:
                Unit(stock.Unit, "unit"); Text(stock.FromLocationOrStatus, "from_location_or_status"); Text(stock.ToLocationOrStatus, "to_location_or_status");
                break;
            case CreateServiceOrder service:
                Customer(service.CustomerRef); Unit(service.Unit, "unit"); Text(service.ProblemDescription, "problem_description");
                break;
            case AttachDocument attachment:
                Text(attachment.TargetRecordRef, "target_record_ref"); Text(attachment.ContentRef, "content_ref");
                if (string.IsNullOrWhiteSpace(attachment.DisplayName) || string.IsNullOrWhiteSpace(Path.GetExtension(attachment.DisplayName))) missing.Add("display_name");
                break;
        }
        return missing;
    }
}
