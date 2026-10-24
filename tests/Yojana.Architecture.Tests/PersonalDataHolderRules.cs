using System.Text.RegularExpressions;
using Xunit;

namespace Yojana.Architecture.Tests;

public sealed class PersonalDataHolderRules
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void Every_personal_data_entity_belongs_to_a_module_with_a_registered_holder()
    {
        var modules = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Platform"] = "src/Platform/Yojana.Platform/PlatformSetup.cs",
            ["Serialisation"] = "src/Serialisation/Yojana.Serialisation/SerialisationModule.cs",
            ["Integration"] = "src/Integration/Yojana.Integration/IntegrationModule.cs",
            ["Knowledge"] = "src/Knowledge/Yojana.Knowledge/KnowledgeModule.cs",
        };
        var attributedFiles = Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && File.ReadAllText(path).Contains("[PersonalData]", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(attributedFiles);
        foreach (var file in attributedFiles)
        {
            var relative = Path.GetRelativePath(Root, file).Replace('\\', '/');
            var module = modules.Keys.SingleOrDefault(name => relative.StartsWith($"src/{name}/", StringComparison.Ordinal));
            Assert.False(module is null, $"Personal-data entity is outside a holder-owned module: {relative}");
            var registration = File.ReadAllText(Path.Combine(Root, modules[module!]));
            Assert.Contains("AddScoped<IPersonalDataHolder>", registration, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Adr0017_inventory_entities_are_marked()
    {
        var expected = new[]
        {
            "AppUser", "UserInvitation", "UserAuthenticator", "UserPasskey", "UserRecoveryCode",
            "PasswordResetToken", "SecurityEvent", "MailMessage", "CustomerMailMessage", "PortalRegistration",
            "UnitEvent", "UnitEventArchive", "CertificateInstance", "Claim", "ClaimLine", "ClaimEvidence",
            "RmaLine", "ReturnReceipt", "WorkbenchRequest", "CorrectionRecord", "SerialRange", "ImportJob",
            "ImportRowResult", "ReferenceProjection", "ExternalIdMap",
            "InboundMessage", "CanonicalDocument", "DeadLetterItem", "OutboundCommandRow",
            "MigrationPlanOwnership", "TransactionLogHeaderStagingRow", "MarketingDocumentHeaderStagingRow",
            "Question", "QuestionReply", "QuestionReplyRevision", "KnowledgeEntry", "KnowledgeEntryVersion",
        };
        var source = string.Join('\n', Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText));
        foreach (var entity in expected)
            Assert.Matches(new Regex($@"\[PersonalData\]\s*public sealed class\s+{entity}\b",
                RegexOptions.CultureInvariant), source);
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Yojana.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
