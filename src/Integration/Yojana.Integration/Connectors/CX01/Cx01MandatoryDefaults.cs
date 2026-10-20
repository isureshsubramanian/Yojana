namespace Yojana.Integration.Connectors.CX01;

internal static class Cx01MandatoryDefaults
{
    public static async Task ApplyAsync(ICx01SurveyReader survey, Guid connectionId, string collection,
        IDictionary<string, object?> body, Cx01ConnectionSettings configured, CancellationToken ct)
    {
        if (configured.MandatoryUserFieldDefaults is not { Count: > 0 }
            || !Cx01Protocol.TableByCollection.TryGetValue(collection, out var table)) return;
        foreach (var name in await survey.MandatoryWithoutDefaultFieldsAsync(connectionId, table, ct))
        {
            var wire = name.StartsWith("U_", StringComparison.Ordinal) ? name : "U_" + name;
            var entry = configured.MandatoryUserFieldDefaults.FirstOrDefault(x =>
                StringComparer.OrdinalIgnoreCase.Equals(x.Key, table + "." + wire));
            if (!string.IsNullOrWhiteSpace(entry.Key)) body.TryAdd(wire, entry.Value);
        }
    }
}
