using Microsoft.EntityFrameworkCore;

namespace Yojana.Integration.LegacyMigration.Canonicalisation;

/// <summary>
/// The database function is the single normalisation seam used by profiling and canonicalisation.
/// TIGHT COUPLING (stored serial match keys): SourceExtractor, CanonicalisationWriter and their staging-column
/// migrations call this function to populate stored match columns. Changing its behaviour requires rebuilding every
/// stored key; otherwise staging rows written before and after the change no longer compare consistently.
/// </summary>
public static class CanonicalSerialNormalisation
{
    [DbFunction("canonical_serial", MigrationModel.Schema, IsBuiltIn = false, IsNullable = true)]
    public static string? Apply(string? serial, bool stripHyphens, bool stripSpaces, bool stripLeadingZeros) =>
        throw new InvalidOperationException("This method is translated to migration.canonical_serial by EF Core.");
}
