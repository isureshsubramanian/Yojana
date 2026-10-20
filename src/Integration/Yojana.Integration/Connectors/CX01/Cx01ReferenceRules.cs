namespace Yojana.Integration.Connectors.CX01;

internal static class Cx01ReferenceRules
{
    public static void Filterable(string collection, string? field)
    {
        if (field is "Description" or "Resolution")
            throw new Cx01DocumentRuleException($"The reference field {collection}.{field} is a memo field that cannot be filtered; choose a filterable field such as Subject or a text user field.");
    }

    public static void Validate(Cx01ConnectionSettings settings)
    {
        foreach (var collection in Cx01DocumentCatalogue.Collections.Where(x => x != "Attachments2"))
            Filterable(collection, Cx01DocumentCatalogue.ReferenceField(collection, settings));
    }

    public static async Task CheckAsync(ICx01SurveyReader survey, Guid connectionId, string collection, string reference,
        Cx01ConnectionSettings settings, CancellationToken ct)
    {
        var field = Cx01DocumentCatalogue.ReferenceField(collection, settings);
        Filterable(collection, field);
        var maximum = settings.Documents?.ReferenceFieldLengths?.GetValueOrDefault(collection + "." + field)
            ?? await survey.FieldLengthAsync(connectionId, collection, field, ct);
        if (maximum is > 0 && settings.OwnReferencePrefix.Length + reference.Length > maximum)
            throw new Cx01DocumentRuleException($"The reference for {collection}.{field} is {settings.OwnReferencePrefix.Length + reference.Length} characters including the prefix; the field allows {maximum}. Configure a longer filterable field; references are never truncated.");
    }
}
