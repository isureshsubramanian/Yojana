using System.Linq.Expressions;

namespace Yojana.Platform.Contracts.Privacy;

/// <summary>Builds SQL-translatable candidate predicates without making holders share persistence details.</summary>
public static class PersonalDataQuery
{
    public static Expression<Func<T, bool>> Or<T>(Expression<Func<T, bool>> left,
        Expression<Func<T, bool>> right)
    {
        var parameter = Expression.Parameter(typeof(T), "row");
        var leftBody = new ReplaceParameterVisitor(left.Parameters[0], parameter).Visit(left.Body)!;
        var rightBody = new ReplaceParameterVisitor(right.Parameters[0], parameter).Visit(right.Body)!;
        return Expression.Lambda<Func<T, bool>>(Expression.OrElse(leftBody, rightBody), parameter);
    }

    public static Expression<Func<T, bool>> ContainsAny<T>(IReadOnlyCollection<string> values,
        params Expression<Func<T, string?>>[] fields)
    {
        var parameter = Expression.Parameter(typeof(T), "row");
        Expression body = Expression.Constant(false);
        var contains = typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;
        foreach (var value in values.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal))
        {
            foreach (var field in fields)
            {
                var member = new ReplaceParameterVisitor(field.Parameters[0], parameter).Visit(field.Body)!;
                body = Expression.OrElse(body, Expression.AndAlso(
                    Expression.NotEqual(member, Expression.Constant(null, typeof(string))),
                    Expression.Call(member, contains, Expression.Constant(value))));
            }
        }
        return Expression.Lambda<Func<T, bool>>(body, parameter);
    }

    private sealed class ReplaceParameterVisitor(ParameterExpression source, ParameterExpression target)
        : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == source ? target : node;
    }
}
