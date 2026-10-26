using System.Runtime.CompilerServices;
using Yojana.Integration;
using Yojana.Integration.Coexistence;
using Yojana.Integration.Connections;
using Yojana.Integration.Connectors.CX04;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.DeadLetters;
using Yojana.Integration.ErpChange;
using Yojana.Integration.LegacyMigration;
using Yojana.Integration.LegacyMigration.Apply;
using Yojana.Integration.LegacyMigration.LinkOverrides;
using Yojana.Integration.Relays;
using Yojana.Platform.Contracts;
using Yojana.Platform.Contracts.Paging;
using Xunit;

namespace Yojana.Integration.Tests;

public sealed class GridPagingDeclarationTests
{
    [Fact]
    public void Integration_list_declarations_are_gapless_in_both_directions_and_enforce_the_shared_policy()
    {
        Check(ConnectionEndpoints.ConnectorPages, Rows<ConnectorCatalogueEntry>("ConnectorCode", "CX",
            ("DisplayName", "Fictional connector")), row => row.ConnectorCode, "code", "name");
        Check(ConnectionEndpoints.ConnectionPages, Rows<Connection>("ConnectionId", Guid.NewGuid(),
            ("DisplayLabel", "Fictional connection"), ("ConnectorCode", "CX"), ("Status", "active")),
            row => row.ConnectionId, "registration_order", "name", "connector", "status");
        Check(ConnectionEndpoints.EventPages, Rows<ConnectionEventEntity>("SequenceNo", 1L,
            ("OccurredAt", new DateTime(2026, 9, 1))), row => row.SequenceNo, "when");
        Check(CoexistenceEndpoints.ConflictPages, Rows<CoexistenceConflict>("ConflictId", Guid.NewGuid(),
            ("DetectedAt", new DateTime(2026, 9, 1)), ("Status", "open")), row => row.ConflictId,
            "detected", "status");
        Check(RelayEndpoints.Pages, Rows<SiteRelay>("RelayId", Guid.NewGuid(),
            ("DisplayName", "Fictional relay"), ("Status", "active")), row => row.RelayId,
            "registration_order", "name", "status");
        Check(Cx04AuthorisationEndpoints.HistoryPages, Rows<Cx04HistoryProjection>("Sequence", 1L,
            ("OccurredAt", new DateTime(2026, 9, 1))), row => row.Sequence, "when");
        Check(IntegrationEndpoints.ManualTaskPages, Rows<ManualOutboundTask>("ManualOutboundTaskId", Guid.NewGuid(),
            ("CreatedAt", new DateTime(2026, 9, 1)), ("Desk", "operations")),
            row => row.ManualOutboundTaskId, "created", "desk");
        Check(IntegrationEndpoints.DeadLetterPages, Rows<DeadLetterService.DeadLetterListProjection>(
            "DeadLetterItemId", Guid.NewGuid(), ("CreatedAt", new DateTime(2026, 9, 1))),
            row => row.DeadLetterItemId, "received");
        Check(MigrationEndpoints.DeltaPages, Rows<MigrationDeltaItem>("ItemId", Guid.NewGuid(),
            ("Kind", "new_unit"), ("ItemCode", "SERVER-1"), ("Serial", "SERIAL-1")), row => row.ItemId,
            "load_order", "kind", "item", "serial");
        Check(MigrationEndpoints.RefusalPages, Rows<MigrationApplyRefusalRow>("RefusalId", Guid.NewGuid(),
            ("ItemCode", "SERVER-1")), row => row.RefusalId, "load_order", "item");
        Check(MigrationEndpoints.LinkOverridePages, Rows<MigrationLinkOverride>("SourceKey", "DOC",
            ("UploadedAt", new DateTime(2026, 9, 1))), row => row.SourceKey, "document", "uploaded");
        Check(ErpChangeEndpoints.RunPages, Rows<ErpChangeRun>("ErpChangeRunId", Guid.NewGuid(),
            ("StartedAt", new DateTime(2026, 9, 1)), ("Status", "completed")), row => row.ErpChangeRunId,
            "started", "status");
    }

    private static void Check<TRow, TTie>(SortedPage<TRow, TTie> pages, IReadOnlyList<TRow> rows,
        Func<TRow, TTie> tie, params string[] keys) where TTie : IComparable<TTie>
    {
        foreach (var key in keys)
        foreach (var direction in new[] { "asc", "desc" })
        {
            var firstRequest = pages.Parse(key, direction, 2);
            var first = firstRequest.Apply(rows.AsQueryable()).Take(2).ToList();
            var secondRequest = pages.Parse(key, direction, 2, firstRequest.CursorFor(first[^1]));
            var second = secondRequest.Apply(rows.AsQueryable()).Take(2).ToList();
            Assert.Empty(first.Select(tie).Intersect(second.Select(tie)));
            Assert.Equal(4, first.Concat(second).Select(tie).Distinct().Count());
        }
        Assert.Equal("sort_not_supported",
            Assert.Throws<CommandRefusedException>(() => pages.Parse("missing", "asc", 25)).Rule);
        Assert.Equal(200, pages.Parse(keys[0], "asc", 200).Take);
        Assert.Equal("page_size_too_large",
            Assert.Throws<CommandRefusedException>(() => pages.Parse(keys[0], "asc", 201)).Rule);
        var cursor = pages.Parse(keys[0], "asc", 2).CursorFor(rows[1]);
        Assert.Equal("sort_changed",
            Assert.Throws<CommandRefusedException>(() => pages.Parse(keys[0], "desc", 2, cursor)).Rule);
    }

    private static List<T> Rows<T>(string tieName, object tieSeed,
        params (string Name, object Value)[] equalValues) where T : class
    {
        return Enumerable.Range(1, 6).Select(number =>
        {
            var row = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
            Set(row, tieName, tieSeed switch
            {
                Guid => Guid.Parse($"00000000-0000-0000-0000-{number:D12}"),
                long => (long)number,
                string value => $"{value}-{number:D2}",
                _ => throw new InvalidOperationException("Unsupported tie type."),
            });
            foreach (var (name, value) in equalValues) Set(row, name, value);
            return row;
        }).ToList();
    }

    private static void Set<T>(T row, string name, object value) =>
        typeof(T).GetProperty(name)!.SetValue(row, value);
}
