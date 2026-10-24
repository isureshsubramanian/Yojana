using System.Text.Json;
using Xunit;
using Yojana.Web;

namespace Yojana.Platform.DatabaseTests;

public sealed class TenantCurrencyCompatibilityTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Previous_client_accepted_field_is_deserialised_and_ignored()
    {
        var request = JsonSerializer.Deserialize<TenantCurrencyRequest>(
            """{"currency":"USD","accepted":["EUR","GBP"]}""",
            WebJson);

        Assert.NotNull(request);
        Assert.Equal("USD", request.Currency);
        Assert.Equal(["EUR", "GBP"], request.Accepted);
    }
}
