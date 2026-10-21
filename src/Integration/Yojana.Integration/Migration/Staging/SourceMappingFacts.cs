namespace Yojana.Integration.LegacyMigration.Staging;

/// <summary>Source-specific values translated at the adapter boundary before mapping consumes them.</summary>
public static class SourceMappingFacts
{
    public static bool PersonalDataRemoved(string? value) => string.Equals(value, "D",
        StringComparison.OrdinalIgnoreCase);

    public static bool IsWarrantyContract(string? value) => string.Equals(value, "W",
        StringComparison.OrdinalIgnoreCase);
}
