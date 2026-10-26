using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Yojana.Platform.Contracts;
using Yojana.Platform.Privacy;
using Yojana.Platform.Security;
using Xunit;

namespace Yojana.Platform.DatabaseTests;

public sealed class ADR21B4SortDeclarationTests
{
    public static TheoryData<Type, string, string[]> Declarations => new()
    {
        { typeof(PrivacyEndpoints), "Pages", ["recorded", "reference", "received", "status"] },
        { typeof(SecurityEndpoints), "EventPages", ["when", "event"] },
        { typeof(UserManagementEndpoints), "Pages", ["default"] },
    };

    [Theory]
    [MemberData(nameof(Declarations))]
    public void Every_list_sort_is_gapless_across_equal_page_boundaries_and_uses_shared_refusals(
        Type owner, string fieldName, string[] keys)
    {
        var pages = owner.GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        SortDeclarationChecks.Check(pages, keys);
    }
}

internal static class SortDeclarationChecks
{
    public static void Check(object pages, string[] keys)
    {
        var rowType = pages.GetType().GetGenericArguments()[0];
        var rows = Rows(rowType);
        foreach (var key in keys)
        foreach (var direction in new[] { "asc", "desc" })
        {
            var firstRequest = Parse(pages, key, direction, 3);
            var first = Apply(firstRequest, rows).Take(3).ToList();
            var pageCursor = (string)firstRequest.GetType().GetMethod("CursorFor")!.Invoke(firstRequest, [first[^1]])!;
            var second = Apply(Parse(pages, key, direction, 3, pageCursor), rows).Take(3).ToList();
            var expected = Apply(Parse(pages, key, direction, 6), rows).Take(6).ToList();
            Assert.Empty(first.Intersect(second, ReferenceEqualityComparer.Instance));
            Assert.Equal(expected, first.Concat(second), ReferenceEqualityComparer.Instance);
        }
        Assert.Equal(200, Take(Parse(pages, keys[0], "asc", 200)));
        Assert.Equal("sort_not_supported", Refusal(() => Parse(pages, "missing", "asc", 25)).Rule);
        Assert.Equal("page_size_too_large", Refusal(() => Parse(pages, keys[0], "asc", 201)).Rule);
        var request = Parse(pages, keys[0], "asc", 3);
        var cursor = (string)request.GetType().GetMethod("CursorFor")!.Invoke(request,
            [Apply(request, rows).First()])!;
        Assert.Equal("sort_changed", Refusal(() => Parse(pages, keys[0], "desc", 3, cursor)).Rule);
    }

    private static int Take(object request) => (int)request.GetType().GetProperty("Take")!.GetValue(request)!;
    private static object Parse(object pages, string? sort, string? direction, int take, string? cursor = null) =>
        pages.GetType().GetMethod("Parse")!.Invoke(pages, [sort, direction, take, cursor, null, 50])!;
    private static IEnumerable<object> Apply(object request, object rows) =>
        ((IEnumerable)request.GetType().GetMethod("Apply")!.Invoke(request, [rows])!).Cast<object>();

    private static object Rows(Type rowType)
    {
        var array = Array.CreateInstance(rowType, 7);
        for (var index = 0; index < array.Length; index++) array.SetValue(Row(rowType, index + 1), index);
        return typeof(Queryable).GetMethods().Single(method => method.Name == nameof(Queryable.AsQueryable)
                && method.IsGenericMethodDefinition && method.GetParameters().Length == 1)
            .MakeGenericMethod(rowType).Invoke(null, [array])!;
    }

    private static object Row(Type type, int number)
    {
        var row = RuntimeHelpers.GetUninitializedObject(type);
        foreach (var property in type.GetProperties().Where(property => property.SetMethod is not null))
            property.SetValue(row, property.Name == "PageKey" ? $"key-{number:D2}" : Value(property.PropertyType, number));
        return row;
    }

    private static object? Value(Type type, int number)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying) return Value(underlying, number);
        if (type == typeof(string)) return "equal";
        if (type == typeof(Guid)) return Guid.Parse($"00000000-0000-0000-0000-{number:D12}");
        if (type == typeof(DateTime)) return new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
        if (type == typeof(DateOnly)) return new DateOnly(2026, 9, 1);
        if (type == typeof(int)) return 1;
        if (type == typeof(long)) return 1L;
        if (type == typeof(bool)) return false;
        if (type == typeof(byte[])) return Array.Empty<byte>();
        if (type.IsValueType) return Activator.CreateInstance(type);
        return Row(type, number);
    }

    private static CommandRefusedException Refusal(Action action)
    {
        var exception = Assert.Throws<TargetInvocationException>(action);
        return Assert.IsType<CommandRefusedException>(exception.InnerException);
    }
}
