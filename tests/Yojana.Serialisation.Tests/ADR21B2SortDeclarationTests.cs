using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Yojana.Platform.Contracts;
using Yojana.Serialisation.Campaigns;
using Yojana.Serialisation.Claims;
using Yojana.Serialisation.CustomerMail;
using Yojana.Serialisation.Entitlements;
using Yojana.Serialisation.Imports;
using Yojana.Serialisation.Labels;
using Yojana.Serialisation.Refurbishments;
using Yojana.Serialisation.Replacements;
using Yojana.Serialisation.Rmas;
using Yojana.Serialisation.Selection;
using Xunit;

namespace Yojana.Serialisation.Tests;

public sealed class ADR21B2SortDeclarationTests
{
    public static TheoryData<Type, string, string[]> Declarations => new()
    {
        { typeof(CampaignQueries), "CampaignPages", ["code", "updated", "status"] },
        { typeof(CampaignQueries), "UnitPages", ["serial", "selected"] },
        { typeof(CampaignHandler), "PreviewPages", ["serial"] },
        { typeof(ClaimQueries), "Pages", ["number", "submitted", "status"] },
        { typeof(RecoveryQueries), "Pages", ["number", "raised", "status"] },
        { typeof(SentMailQueries), "Pages", ["queued", "status"] },
        { typeof(CoverDeviationQueries), "Pages", ["changed", "sold"] },
        { typeof(WarrantyLineExceptionQueries), "Pages", ["raised", "trigger", "status"] },
        { typeof(ImportQueries), "HistoryPages", ["file", "uploaded", "status"] },
        { typeof(ImportQueries), "FeedPages", ["message", "received", "status"] },
        { typeof(ImportQueries), "ComparisonPages", ["file", "ran", "status"] },
        { typeof(LabelQueries), "PrintJobPages", ["number", "requested", "status"] },
        { typeof(RefurbishmentQueries), "Pages", ["number", "opened", "status"] },
        { typeof(ReplacementLinkQueries), "Pages", ["due", "status"] },
        { typeof(RmaQueries), "Pages", ["number", "requested", "status"] },
        { typeof(BulkExecutor), "PreviewPages", ["priority", "serial", "outcome"] },
    };

    [Theory]
    [MemberData(nameof(Declarations))]
    public void Every_declared_sort_is_gapless_with_equal_values_and_uses_shared_refusals(
        Type owner, string fieldName, string[] expectedKeys)
    {
        var pages = owner.GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
            ?? throw new InvalidOperationException($"{owner.Name}.{fieldName} was not found.");
        var rowType = pages.GetType().GetGenericArguments()[0];
        var columns = (IDictionary)(pages.GetType().GetField("columns", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(pages)
            ?? throw new InvalidOperationException("SortedPage columns were not found."));
        Assert.Equal(expectedKeys.Order(), columns.Keys.Cast<string>().Order());

        var rows = TypedRows(rowType, 7);
        foreach (var key in expectedKeys)
        foreach (var direction in new[] { "asc", "desc" })
        {
            var firstRequest = Parse(pages, key, direction, 3);
            var first = Apply(firstRequest, rows).Take(3).ToList();
            var cursor = firstRequest.GetType().GetMethod("CursorFor")!.Invoke(firstRequest, [first[^1]]) as string;
            var secondRequest = Parse(pages, key, direction, 3, cursor);
            var second = Apply(secondRequest, rows).Take(3).ToList();
            var expected = Apply(Parse(pages, key, direction, 6), rows).Take(6).ToList();

            Assert.Empty(first.Intersect(second, ReferenceEqualityComparer.Instance));
            Assert.Equal(expected, first.Concat(second), ReferenceEqualityComparer.Instance);
        }

        Assert.Equal(200, (int)Parse(pages, expectedKeys[0], "asc", 200).GetType().GetProperty("Take")!.GetValue(
            Parse(pages, expectedKeys[0], "asc", 200))!);
        Assert.Equal("sort_not_supported", Refusal(() => Parse(pages, "not-a-column", "asc", 25)).Rule);
        Assert.Equal("page_size_too_large", Refusal(() => Parse(pages, expectedKeys[0], "asc", 201)).Rule);

        var request = Parse(pages, expectedKeys[0], "asc", 3);
        var firstRow = Apply(request, rows).First();
        var boundCursor = (string)request.GetType().GetMethod("CursorFor")!.Invoke(request, [firstRow])!;
        var changedKey = expectedKeys.Length > 1 ? expectedKeys[1] : expectedKeys[0];
        var changedDirection = expectedKeys.Length > 1 ? "asc" : "desc";
        Assert.Equal("sort_changed", Refusal(() => Parse(pages, changedKey, changedDirection, 3, boundCursor)).Rule);
    }

    private static object Parse(object pages, string? sort, string? direction, int take, string? cursor = null) =>
        Invoke(pages.GetType().GetMethod("Parse")!, pages, [sort, direction, take, cursor, null, 50]);

    private static IEnumerable<object> Apply(object request, object rows) =>
        ((IEnumerable)request.GetType().GetMethod("Apply")!.Invoke(request, [rows])!).Cast<object>();

    private static object TypedRows(Type rowType, int count)
    {
        var array = Array.CreateInstance(rowType, count);
        for (var index = 0; index < count; index++) array.SetValue(Row(rowType, index + 1), index);
        return typeof(Queryable).GetMethods().Single(method => method.Name == nameof(Queryable.AsQueryable)
                && method.IsGenericMethodDefinition && method.GetParameters().Length == 1)
            .MakeGenericMethod(rowType).Invoke(null, [array])!;
    }

    private static object Row(Type type, int number)
    {
        var row = RuntimeHelpers.GetUninitializedObject(type);
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                     .Where(property => property.SetMethod is not null))
            property.SetValue(row, Value(property.PropertyType, number));
        return row;
    }

    private static object? Value(Type type, int number)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null) return Value(underlying, number);
        if (type == typeof(string)) return "equal";
        if (type == typeof(Guid)) return Guid.Parse($"00000000-0000-0000-0000-{number:D12}");
        if (type == typeof(DateTime)) return new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
        if (type == typeof(DateOnly)) return new DateOnly(2026, 9, 1);
        if (type == typeof(int)) return 1;
        if (type == typeof(long)) return 1L;
        if (type == typeof(decimal)) return 1m;
        if (type == typeof(bool)) return false;
        if (type == typeof(byte[])) return Array.Empty<byte>();
        if (type.IsValueType) return Activator.CreateInstance(type);
        return type == typeof(LambdaExpression) ? null : Row(type, number);
    }

    private static CommandRefusedException Refusal(Action action)
    {
        var exception = Assert.Throws<TargetInvocationException>(action);
        return Assert.IsType<CommandRefusedException>(exception.InnerException);
    }

    private static object Invoke(MethodInfo method, object instance, object?[] arguments)
    {
        try { return method.Invoke(instance, arguments)!; }
        catch (TargetInvocationException) { throw; }
    }
}
