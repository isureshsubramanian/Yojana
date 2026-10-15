using System.Data.SqlTypes;
using Xunit;
using Yojana.Platform.Contracts.Identifiers;

namespace Yojana.Platform.Tests;

public sealed class TimeOrderedIdTests
{
    /// <summary>SqlGuid implements SQL Server's uniqueidentifier comparison, so this is the ordering the clustered index sees.</summary>
    private static int SqlCompare(Guid a, Guid b) => new SqlGuid(a).CompareTo(new SqlGuid(b));

    [Fact]
    public void Generated_ids_are_ascending_under_sql_server_comparison()
    {
        var ids = Enumerable.Range(0, 20_000).Select(_ => TimeOrderedId.New()).ToList();
        for (var i = 1; i < ids.Count; i++)
        {
            Assert.True(SqlCompare(ids[i - 1], ids[i]) < 0, $"id {i} sorts before id {i - 1} under SQL Server comparison");
        }
    }

    [Fact]
    public void Standard_uuid_v7_is_not_ascending_under_sql_server_comparison()
    {
        // Documents the P0-T08 finding: Guid.CreateVersion7() is time-ordered left to right, which SQL Server compares last.
        var ids = Enumerable.Range(0, 2_000).Select(_ => Guid.CreateVersion7()).ToList();
        var inversions = 0;
        for (var i = 1; i < ids.Count; i++)
        {
            if (SqlCompare(ids[i - 1], ids[i]) > 0)
            {
                inversions++;
            }
        }

        Assert.True(inversions > ids.Count / 4, $"expected many inversions, saw {inversions}");
    }

    [Fact]
    public void Ids_are_unique_and_carry_their_timestamp()
    {
        var at = new DateTime(2026, 10, 15, 9, 30, 0, 123, DateTimeKind.Utc);
        var ids = Enumerable.Range(0, 1_000).Select(_ => TimeOrderedId.New(at)).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.True(TimeOrderedId.TimestampOf(id) >= at && TimeOrderedId.TimestampOf(id) < at.AddSeconds(1)));
    }

    [Fact]
    public void Later_instant_sorts_after_earlier_instant()
    {
        var earlier = TimeOrderedId.New(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var later = TimeOrderedId.New(new DateTime(2036, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.True(SqlCompare(earlier, later) < 0);
    }
}
