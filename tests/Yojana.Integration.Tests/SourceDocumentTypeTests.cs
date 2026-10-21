using Yojana.Integration.LegacyMigration.Staging;

namespace Yojana.Integration.Tests;

public sealed class SourceDocumentTypeTests
{
    [Theory]
    [InlineData(20, 1, SourceMovementKind.Received)]
    [InlineData(59, 1, SourceMovementKind.Received)]
    [InlineData(18, 1, SourceMovementKind.Received)]
    [InlineData(202, 1, SourceMovementKind.Produced)]
    [InlineData(202, -1, SourceMovementKind.Consumed)]
    [InlineData(15, -1, SourceMovementKind.Shipped)]
    [InlineData(13, -1, SourceMovementKind.Shipped)]
    [InlineData(16, 1, SourceMovementKind.CustomerReturned)]
    [InlineData(14, 1, SourceMovementKind.CustomerReturned)]
    [InlineData(21, -1, SourceMovementKind.VendorReturned)]
    [InlineData(67, -1, SourceMovementKind.TransferredOut)]
    [InlineData(67, 1, SourceMovementKind.TransferredIn)]
    [InlineData(60, -1, SourceMovementKind.IssuedOut)]
    [InlineData(999, 1, SourceMovementKind.Unknown)]
    public void Incumbent_document_types_map_to_source_agnostic_movements(int documentType, int quantity,
        string expected)
    {
        Assert.Equal(expected, SourceDocumentTypes.Map(documentType, quantity, 1, -1).Kind);
    }

    [Fact]
    public void Allocation_rows_are_ignored_before_document_mapping()
    {
        Assert.Equal(SourceMovementKind.Ignored, SourceDocumentTypes.Map(15, -1m, 2, -1).Kind);
    }
}
