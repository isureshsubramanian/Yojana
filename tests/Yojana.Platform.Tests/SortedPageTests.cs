using Yojana.Platform.Contracts;
using Yojana.Platform.Contracts.Paging;
using Xunit;

namespace Yojana.Platform.Tests;

public sealed class SortedPageTests
{
    private sealed record Row(Guid Id, int Rank, string Code);

    private static readonly SortedPage<Row, Guid> Pages = new("rank", row => row.Id,
        new SortColumn<Row, int>("rank", row => row.Rank),
        new SortColumn<Row, string>("code", row => row.Code));

    [Fact]
    public void Cursor_keeps_equal_values_gapless_in_both_directions()
    {
        var rows = Enumerable.Range(1, 8).Select(number => new Row(Guid.Parse($"00000000-0000-0000-0000-{number:D12}"), number <= 5 ? 1 : 2, $"C{number}"))
            .AsQueryable();
        foreach (var direction in new[] { "asc", "desc" })
        {
            var firstRequest = Pages.Parse("rank", direction, 3);
            var first = firstRequest.Apply(rows).Take(firstRequest.Take).ToList();
            var secondRequest = Pages.Parse("rank", direction, 3, firstRequest.CursorFor(first[^1]));
            var second = secondRequest.Apply(rows).Take(secondRequest.Take).ToList();
            Assert.Empty(first.Select(row => row.Id).Intersect(second.Select(row => row.Id)));
            Assert.Equal(6, first.Concat(second).Select(row => row.Id).Distinct().Count());
        }
    }

    [Fact]
    public void Cursor_is_opaque_and_bound_to_sort_and_direction()
    {
        var row = new Row(Guid.Parse("00000000-0000-0000-0000-000000000001"), 4, "A-4");
        var request = Pages.Parse("rank", "asc", 25);
        var cursor = request.CursorFor(row);
        Assert.DoesNotContain("rank", cursor, StringComparison.Ordinal);
        Assert.Empty(Pages.Parse("rank", "asc", 25, cursor).Apply(new[] { row }.AsQueryable()));
        Assert.Equal("sort_changed", Assert.Throws<CommandRefusedException>(() => Pages.Parse("code", "asc", 25, cursor)).Rule);
        Assert.Equal("sort_changed", Assert.Throws<CommandRefusedException>(() => Pages.Parse("rank", "desc", 25, cursor)).Rule);
    }

    [Fact]
    public void Unknown_sort_and_oversized_page_are_refused_and_skip_pages_are_supported()
    {
        Assert.Equal("sort_not_supported", Assert.Throws<CommandRefusedException>(() => Pages.Parse("missing", "asc", 25)).Rule);
        Assert.Equal(200, Pages.Parse("rank", "asc", 200).Take);
        Assert.Equal("page_size_too_large", Assert.Throws<CommandRefusedException>(() => Pages.Parse("rank", "asc", 201)).Rule);
        var rows = Enumerable.Range(1, 5).Select(number => new Row(Guid.Parse($"00000000-0000-0000-0000-{number:D12}"), number, $"C{number}"));
        var page = Pages.Parse(null, null, 2, skip: 2).Apply(rows.AsQueryable()).Take(2).ToList();
        Assert.Equal([3, 4], page.Select(row => row.Rank));
    }
}
