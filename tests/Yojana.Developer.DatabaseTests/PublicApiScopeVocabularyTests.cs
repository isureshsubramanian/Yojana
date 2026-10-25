using DeveloperScopes = Yojana.Developer.Contracts.PublicApiScopes;
using SerialisationScopes = Yojana.Serialisation.Contracts.PublicApiScopes;

namespace Yojana.Developer.DatabaseTests;

// Pins the hidden coupling documented on both PublicApiScopes classes: the scopes a client can be granted are exactly
// the scopes the read and write ports know.
public sealed class PublicApiScopeVocabularyTests
{
    [Fact]
    public void Client_scopes_and_port_scopes_are_the_same_vocabulary() =>
        Assert.Equal(SerialisationScopes.All.Order(StringComparer.Ordinal),
            DeveloperScopes.All.Order(StringComparer.Ordinal));
}
