using Yojana.Integration.Connections;
using Yojana.Integration.Connectors;
using Yojana.Integration.Connectors.CX01;
using Yojana.Integration.Connectors.CX02;
using Yojana.Integration.Connectors.CX04;

namespace Yojana.Integration.Tests;

public sealed class ExtensionStatusTests
{
    [Fact]
    public void Shipped_version_and_guide_are_read_from_the_embedded_extension_source()
    {
        Assert.Equal("1.0.0.0", Cx02ExtensionAssets.ShippedVersion);
        Assert.Contains("# CX02 tenant-side extension source", Cx02ExtensionAssets.InstallationGuide,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.0", false)]
    [InlineData("1.0.0.0", false)]
    [InlineData("v1.1", true)]
    [InlineData("2.0", true)]
    [InlineData("unknown", false)]
    public void Newer_than_one_point_zero_uses_major_and_minor_versions(string value, bool expected) =>
        Assert.Equal(expected, ExtensionStatusService.IsNewerThanOnePointZero(value));

    [Fact]
    public void Connector_features_are_declared_without_a_web_connector_id_rule()
    {
        IConnectionLifecycleAdapter cx01 = new Cx01ConnectionLifecycleAdapter(null!, null!, null!, null!, null!, null!);
        Assert.Equal(ConnectionFeatures.None, cx01.Features);
        Assert.True(new Cx02ConnectionLifecycleAdapter(null!, null!, null!).Features.TenantSideExtension);
        var cx04 = new Cx04ConnectionLifecycleAdapter(null!, null!, null!, null!, null!);
        Assert.True(cx04.Features.InteractiveSignIn);
        Assert.False(cx04.Features.TenantSideExtension);
        Assert.Equal("CX01", Cx01Capabilities.ConnectorCode);
    }
}
