using System.Text;
using Yojana.Knowledge.Questions;

namespace Yojana.Knowledge.Entries;

public sealed record ScreenReferenceChoice(string Reference, string Label);
public sealed record ScreenReferenceChoicePage(IReadOnlyList<ScreenReferenceChoice> Items, string? NextCursor);

/// <summary>The stable product-screen catalogue shared by entry pickers and the screen-change audit.</summary>
public sealed class ScreenReferenceCatalogue
{
    private static readonly IReadOnlyList<ScreenReferenceChoice> All =
    [
        new("certificates.viewer", "Certificate viewer"),
        new("claims.adjudication", "Claim adjudication"),
        new("claims.intake", "New claim"),
        new("entitlements.policy", "Warranty policy"),
        new("imports.detail", "Import job"),
        new("integration.message", "Integration message"),
        new("refurbishment.work_order", "Refurbishment work order"),
        new("returns.detail", "Return detail"),
        new("units.detail", "Unit detail"),
    ];

    public ScreenReferenceChoice? Resolve(string reference) => All.FirstOrDefault(x =>
        string.Equals(x.Reference, reference, StringComparison.Ordinal));

    public ScreenReferenceChoicePage Search(string? search, string? cursor, int take)
    {
        if (take is < 1 or > 100)
            throw new QuestionRuleException("page_size_invalid", "Take must be between 1 and 100.");
        var after = string.IsNullOrWhiteSpace(cursor) ? null : Decode(cursor);
        var query = All.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => x.Label.Contains(term, StringComparison.OrdinalIgnoreCase)
                || x.Reference.Contains(term, StringComparison.OrdinalIgnoreCase));
        }
        if (after is not null) query = query.Where(x => string.CompareOrdinal(x.Reference, after) > 0);
        var rows = query.OrderBy(x => x.Reference, StringComparer.Ordinal).Take(take + 1).ToList();
        var page = rows.Take(take).ToList();
        return new ScreenReferenceChoicePage(page, rows.Count > take && page.Count > 0
            ? Encode(page[^1].Reference) : null);
    }

    private static string Encode(string reference) => Convert.ToBase64String(Encoding.UTF8.GetBytes(reference));

    private static string Decode(string cursor)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(cursor)); }
        catch (FormatException)
        {
            throw new QuestionRuleException("cursor_invalid", "The screen-reference cursor is invalid.");
        }
    }
}
