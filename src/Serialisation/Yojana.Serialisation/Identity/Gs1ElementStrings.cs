using Solidsoft.Reply.Parsers.Gs1Ai;

namespace Yojana.Serialisation.Identity;

/// <summary>One parsed GS1 element: the application identifier and its value, with any parser findings.</summary>
public sealed record Gs1Element(string ApplicationIdentifier, string Value, string Description, IReadOnlyList<string> Errors, bool IsFatal);

/// <summary>
/// GS1 element strings (FNC1 form with ASCII 29 separators, or HRI form with parentheses) through the validating
/// parser (Solidsoft.Reply.Parsers.Gs1Ai, Apache-2.0). Used by the SGTIN, GIAI and UDI validators and by the scan
/// parsing service (P1-T08). The parser never throws; findings come back per element.
/// </summary>
public static class Gs1ElementStrings
{
    public static IReadOnlyList<Gs1Element> Parse(string elementStrings)
    {
        var elements = new List<Gs1Element>();
        Parser.Parse(elementStrings, resolved =>
        {
            var errors = resolved.Exceptions.Select(e => e.Message).ToList();
            elements.Add(new Gs1Element(resolved.Identifier, resolved.Value, resolved.Description, errors, resolved.Exceptions.Any(e => e.IsFatal)));
        });
        return elements;
    }

    /// <summary>Reasons an element string sequence is not acceptable: nothing parsed, or any element with a parser error.</summary>
    public static IReadOnlyList<string> Validate(string elementStrings)
    {
        var elements = Parse(elementStrings);
        if (elements.Count == 0)
        {
            return ["gs1_no_elements"];
        }

        return elements.Where(e => e.Errors.Count > 0).Select(e => $"gs1_ai_{e.ApplicationIdentifier}:{string.Join("; ", e.Errors)}").ToList();
    }

    public static bool Contains(string elementStrings, string applicationIdentifier) =>
        Parse(elementStrings).Any(e => e.ApplicationIdentifier == applicationIdentifier && e.Errors.Count == 0);

    /// <summary>The value of one AI in the sequence, or null.</summary>
    public static string? ValueOf(string elementStrings, string applicationIdentifier) =>
        Parse(elementStrings).FirstOrDefault(e => e.ApplicationIdentifier == applicationIdentifier)?.Value;
}
