namespace Yojana.Integration.Contracts;

/// <summary>One approved building block P9-T07 applies to a migrated unit with the line's real dates.</summary>
public sealed record ApprovedTemplateBlock(string CoverageFamily, Guid PolicyId, int PolicyVersion);

public sealed record ApprovedTemplateDecomposition(
    string TemplateKey,
    string TemplateName,
    IReadOnlyList<ApprovedTemplateBlock> Blocks);

/// <summary>Resolves an extracted contract line through its contract and approved template mapping.</summary>
public interface IApprovedTemplateDecompositionQuery
{
    Task<ApprovedTemplateDecomposition?> ForStagedContractLineAsync(
        string sourceSystem,
        string contractLineSourceKey,
        CancellationToken cancellationToken);
}
