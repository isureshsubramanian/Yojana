using Xunit;

namespace Yojana.Architecture.Tests;

public sealed class IntegrationTransportRules
{
    [Fact]
    public void Message_queue_implementation_types_stay_inside_the_transport_boundary()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Yojana.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var src = Path.Combine(root.FullName, "src");
        var boundary = Path.Combine(src, "Integration", "Yojana.Integration", "Transport") + Path.DirectorySeparatorChar;
        var violations = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains("/obj/", StringComparison.Ordinal) && !path.Contains("/bin/", StringComparison.Ordinal)
                && !path.StartsWith(boundary, StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("Azure.Messaging.ServiceBus", StringComparison.Ordinal)
                || File.ReadAllText(path).Contains("Wolverine.AzureServiceBus", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(src, path)).ToList();
        Assert.Empty(violations);
    }
}
