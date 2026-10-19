using Yojana.Integration.Gateway;

namespace Yojana.Integration.Tests;

public sealed class DeterministicIdTests
{
    [Fact]
    public void Derived_ids_are_stable_rfc_9562_version_8_values()
    {
        var source = Guid.Parse("018f1234-5678-7abc-9def-0123456789ab");
        var first = DeterministicId.Create(source, "line:1");
        var replay = DeterministicId.Create(source, "line:1");
        Span<byte> bytes = stackalloc byte[16];
        first.TryWriteBytes(bytes, bigEndian: true, out _);

        Assert.Equal(first, replay);
        Assert.Equal(8, bytes[6] >> 4);
        Assert.Equal(0x80, bytes[8] & 0xc0);
        Assert.NotEqual(source, first);
    }

    [Fact]
    public void Namespace_source_and_salt_all_participate_in_the_name()
    {
        var source = Guid.Parse("018f1234-5678-7abc-9def-0123456789ab");
        var firstNamespace = Guid.Parse("10000000-0000-8000-8000-000000000001");
        var secondNamespace = Guid.Parse("10000000-0000-8000-8000-000000000002");

        Assert.NotEqual(DeterministicId.Create(firstNamespace, source, 1),
            DeterministicId.Create(firstNamespace, source, 2));
        Assert.NotEqual(DeterministicId.Create(firstNamespace, source, 1),
            DeterministicId.Create(secondNamespace, source, 1));
    }
}
