using System.Linq.Expressions;
using System.Text;
using System.Text.Json;

namespace Yojana.Platform.Contracts.Paging;

public enum SortDirection
{
    Ascending,
    Descending,
}

/// <summary>A declared, indexed sort column. List queries supply columns; this type owns ordering and cursor comparison.</summary>
public sealed class SortColumn<TRow, TValue>(string key, Expression<Func<TRow, TValue>> value,
    SortDirection defaultDirection = SortDirection.Ascending)
    : ISortColumn<TRow>
    where TValue : IComparable<TValue>
{
    public string Key { get; } = key;
    public SortDirection DefaultDirection { get; } = defaultDirection;

    public LambdaExpression ValueExpression => value;

    public IOrderedQueryable<TRow> Order(IQueryable<TRow> query, SortDirection direction) =>
        direction == SortDirection.Ascending ? query.OrderBy(value) : query.OrderByDescending(value);

    public IQueryable<TRow> After<TTie>(IQueryable<TRow> query, SortDirection direction, JsonElement lastValue,
        Expression<Func<TRow, TTie>> tieExpression, JsonElement lastTie) where TTie : IComparable<TTie>
    {
        var valueAtCursor = lastValue.Deserialize<TValue>(JsonOptions)!;
        var tieAtCursor = lastTie.Deserialize<TTie>(JsonOptions)!;
        var row = Expression.Parameter(typeof(TRow), "row");
        var selected = Replace(value.Body, value.Parameters[0], row);
        var selectedTie = Replace(tieExpression.Body, tieExpression.Parameters[0], row);
        var valueCompare = Compare(selected, Expression.Constant(valueAtCursor), direction);
        var valueEqual = Expression.Equal(selected, Expression.Constant(valueAtCursor));
        // The unique tie is deliberately ascending for every primary direction. This preserves established list
        // defaults such as "newest date, then identifier" while still making both directions gapless.
        var tieCompare = Compare(selectedTie, Expression.Constant(tieAtCursor), SortDirection.Ascending);
        var predicate = Expression.Lambda<Func<TRow, bool>>(
            Expression.OrElse(valueCompare, Expression.AndAlso(valueEqual, tieCompare)), row);
        return query.Where(predicate);
    }

    public JsonElement ReadValue(TRow row) => JsonSerializer.SerializeToElement(value.Compile()(row), JsonOptions);

    private static BinaryExpression Compare(Expression left, Expression right, SortDirection direction)
    {
        var compareTo = left.Type.GetMethod(nameof(IComparable<TValue>.CompareTo), [left.Type])
            ?? throw new InvalidOperationException($"{left.Type.Name} is not comparable.");
        var compared = Expression.Call(left, compareTo, right);
        return direction == SortDirection.Ascending
            ? Expression.GreaterThan(compared, Expression.Constant(0))
            : Expression.LessThan(compared, Expression.Constant(0));
    }

    private static Expression Replace(Expression body, ParameterExpression from, ParameterExpression to) =>
        new ParameterReplacement(from, to).Visit(body)!;

    private static CommandRefusedException InvalidCursor() =>
        new("cursor_invalid", "The paging cursor is not valid.", "return to the first page");

    private sealed class ParameterReplacement(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

/// <summary>A declared primary sort with an indexed secondary order before the unique row tie-breaker.</summary>
public sealed class CompoundSortColumn<TRow, TPrimary, TSecondary>(string key,
    Expression<Func<TRow, TPrimary>> primary, Expression<Func<TRow, TSecondary>> secondary,
    SortDirection defaultDirection = SortDirection.Ascending,
    SortDirection secondaryDirection = SortDirection.Ascending) : ISortColumn<TRow>
    where TPrimary : IComparable<TPrimary>
    where TSecondary : IComparable<TSecondary>
{
    public string Key { get; } = key;
    public SortDirection DefaultDirection { get; } = defaultDirection;
    public LambdaExpression ValueExpression => primary;

    public IOrderedQueryable<TRow> Order(IQueryable<TRow> query, SortDirection direction)
    {
        var ordered = direction == SortDirection.Ascending
            ? query.OrderBy(primary) : query.OrderByDescending(primary);
        return secondaryDirection == SortDirection.Ascending
            ? ordered.ThenBy(secondary) : ordered.ThenByDescending(secondary);
    }

    public IQueryable<TRow> After<TTie>(IQueryable<TRow> query, SortDirection direction,
        JsonElement lastValue, Expression<Func<TRow, TTie>> tieExpression, JsonElement lastTie)
        where TTie : IComparable<TTie>
    {
        var values = lastValue.Deserialize<CompoundCursor<TPrimary, TSecondary>>(JsonOptions)
            ?? throw InvalidCursor();
        var tieAtCursor = lastTie.Deserialize<TTie>(JsonOptions)!;
        var row = Expression.Parameter(typeof(TRow), "row");
        var selectedPrimary = Replace(primary.Body, primary.Parameters[0], row);
        var selectedSecondary = Replace(secondary.Body, secondary.Parameters[0], row);
        var selectedTie = Replace(tieExpression.Body, tieExpression.Parameters[0], row);
        var primaryConstant = Expression.Constant(values.Primary);
        var secondaryConstant = Expression.Constant(values.Secondary);
        var primaryAfter = Compare(selectedPrimary, primaryConstant, direction);
        var primaryEqual = Expression.Equal(selectedPrimary, primaryConstant);
        var secondaryAfter = Compare(selectedSecondary, secondaryConstant, secondaryDirection);
        var secondaryEqual = Expression.Equal(selectedSecondary, secondaryConstant);
        var tieAfter = Compare(selectedTie, Expression.Constant(tieAtCursor), SortDirection.Ascending);
        var predicate = Expression.Lambda<Func<TRow, bool>>(
            Expression.OrElse(primaryAfter, Expression.AndAlso(primaryEqual,
                Expression.OrElse(secondaryAfter, Expression.AndAlso(secondaryEqual, tieAfter)))), row);
        return query.Where(predicate);
    }

    public JsonElement ReadValue(TRow row) => JsonSerializer.SerializeToElement(
        new CompoundCursor<TPrimary, TSecondary>(primary.Compile()(row), secondary.Compile()(row)), JsonOptions);

    private static BinaryExpression Compare(Expression left, Expression right, SortDirection direction)
    {
        var compareTo = left.Type.GetMethod(nameof(IComparable<TPrimary>.CompareTo), [left.Type])
            ?? throw new InvalidOperationException($"{left.Type.Name} is not comparable.");
        var compared = Expression.Call(left, compareTo, right);
        return direction == SortDirection.Ascending
            ? Expression.GreaterThan(compared, Expression.Constant(0))
            : Expression.LessThan(compared, Expression.Constant(0));
    }

    private static Expression Replace(Expression body, ParameterExpression from, ParameterExpression to) =>
        new ParameterReplacement(from, to).Visit(body)!;

    private static CommandRefusedException InvalidCursor() =>
        new("cursor_invalid", "The paging cursor is not valid.", "return to the first page");

    private sealed class ParameterReplacement(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private sealed record CompoundCursor<TFirst, TSecond>(TFirst Primary, TSecond Secondary);
}

public interface ISortColumn<TRow>
{
    string Key { get; }
    SortDirection DefaultDirection { get; }
    LambdaExpression ValueExpression { get; }
    IOrderedQueryable<TRow> Order(IQueryable<TRow> query, SortDirection direction);
    IQueryable<TRow> After<TTie>(IQueryable<TRow> query, SortDirection direction, JsonElement lastValue,
        Expression<Func<TRow, TTie>> tieExpression, JsonElement lastTie) where TTie : IComparable<TTie>;
    JsonElement ReadValue(TRow row);
}

/// <summary>
/// Shared list paging policy: validates sort, direction and size, gives every order a unique tie-breaker, and binds
/// opaque cursors to the order that created them. Owning endpoints only declare their indexed column expressions.
/// </summary>
public sealed class SortedPage<TRow, TTie>(string defaultSort, Expression<Func<TRow, TTie>> tieBreaker,
    params ISortColumn<TRow>[] columns) where TTie : IComparable<TTie>
{
    public const int MaximumTake = 200;
    private readonly Dictionary<string, ISortColumn<TRow>> columns = columns.ToDictionary(column => column.Key, StringComparer.Ordinal);
    private readonly Func<TRow, TTie> readTie = tieBreaker.Compile();

    public SortedPageRequest<TRow, TTie> Parse(string? sort, string? direction, int? take, string? cursor = null,
        int? skip = null, int defaultTake = 50)
    {
        var key = string.IsNullOrWhiteSpace(sort) ? defaultSort : sort.Trim();
        if (!columns.TryGetValue(key, out var column))
            throw new CommandRefusedException("sort_not_supported", $"Sort '{key}' is not supported.");
        var parsedDirection = ParseDirection(direction, column.DefaultDirection);
        var size = take ?? defaultTake;
        if (size > MaximumTake)
            throw new CommandRefusedException("page_size_too_large", $"Page size cannot exceed {MaximumTake}.");
        if (size < 1)
            throw new CommandRefusedException("page_size_invalid", "Page size must be at least 1.");
        var offset = Math.Max(0, skip ?? 0);
        CursorPayload? payload = null;
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            payload = Decode(cursor);
            if (!string.Equals(payload.Sort, key, StringComparison.Ordinal)
                || payload.Direction != DirectionCode(parsedDirection))
                throw new CommandRefusedException("sort_changed", "The paging cursor belongs to another sort.",
                    "return to the first page");
        }
        return new(column, key, parsedDirection, size, offset, payload, tieBreaker, readTie);
    }

    private static SortDirection ParseDirection(string? direction, SortDirection fallback)
    {
        if (string.IsNullOrWhiteSpace(direction)) return fallback;
        return direction.Trim().ToLowerInvariant() switch
        {
            "asc" => SortDirection.Ascending,
            "desc" => SortDirection.Descending,
            _ => throw new CommandRefusedException("sort_not_supported", $"Sort direction '{direction}' is not supported."),
        };
    }

    internal static string Encode(CursorPayload payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    internal static CursorPayload Decode(string cursor)
    {
        try
        {
            var text = cursor.Replace('-', '+').Replace('_', '/');
            text += new string('=', (4 - text.Length % 4) % 4);
            return JsonSerializer.Deserialize<CursorPayload>(Convert.FromBase64String(text), JsonOptions)
                ?? throw InvalidCursor();
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw InvalidCursor();
        }
    }

    internal static string DirectionCode(SortDirection direction) =>
        direction == SortDirection.Ascending ? "asc" : "desc";

    private static CommandRefusedException InvalidCursor() =>
        new("cursor_invalid", "The paging cursor is not valid.", "return to the first page");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

public sealed class SortedPageRequest<TRow, TTie> where TTie : IComparable<TTie>
{
    private readonly ISortColumn<TRow> column;
    private readonly SortDirection direction;
    private readonly CursorPayload? cursor;
    private readonly Expression<Func<TRow, TTie>> tieBreaker;
    private readonly Func<TRow, TTie> readTie;

    internal SortedPageRequest(ISortColumn<TRow> column, string sort, SortDirection direction,
        int take, int skip, CursorPayload? cursor, Expression<Func<TRow, TTie>> tieBreaker, Func<TRow, TTie> readTie)
    {
        this.column = column;
        Sort = sort;
        this.direction = direction;
        Take = take;
        Skip = skip;
        this.cursor = cursor;
        this.tieBreaker = tieBreaker;
        this.readTie = readTie;
        Direction = SortedPage<TRow, TTie>.DirectionCode(direction);
    }

    public string Sort { get; }
    public string Direction { get; }
    public int Take { get; }
    public int Skip { get; }

    public IQueryable<TRow> Apply(IQueryable<TRow> query)
    {
        if (cursor is not null) query = column.After(query, direction, cursor.Value, tieBreaker, cursor.Tie);
        var ordered = column.Order(query, direction);
        var tied = ordered.ThenBy(tieBreaker);
        return Skip > 0 ? tied.Skip(Skip) : tied;
    }

    public string CursorFor(TRow lastRow) => SortedPage<TRow, TTie>.Encode(new CursorPayload(
        Sort, Direction, column.ReadValue(lastRow), JsonSerializer.SerializeToElement(readTie(lastRow))));
}

internal sealed record CursorPayload(string Sort, string Direction, JsonElement Value, JsonElement Tie);
