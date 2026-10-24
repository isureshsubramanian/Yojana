using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data;
using Yojana.Platform.Security;
using Yojana.Platform.Tenancy;

namespace Yojana.Platform.Tests;

public sealed class ConventionTests
{
    [Theory]
    [InlineData("TenantId", "tenant_id")]
    [InlineData("UnitEvent", "unit_event")]
    [InlineData("LiveEntitlementIds", "live_entitlement_ids")]
    [InlineData("SmokeLog", "smoke_log")]
    [InlineData("Id", "id")]
    [InlineData("RecordedAt", "recorded_at")]
    [InlineData("tenant_setting", "tenant_setting")]
    public void Names_become_snake_case(string input, string expected) => Assert.Equal(expected, Naming.ToSnakeCase(input));

    [Fact]
    public void Password_hash_round_trips_and_rejects_wrong_password()
    {
        var hash = PasswordHasher.Hash("correct horse");
        Assert.StartsWith("pbkdf2-sha256$", hash, StringComparison.Ordinal);
        Assert.True(PasswordHasher.Verify("correct horse", hash));
        Assert.False(PasswordHasher.Verify("wrong", hash));
        Assert.False(PasswordHasher.Verify("correct horse", null));
    }

    [Fact]
    public void Tenant_context_serves_one_tenant_per_scope()
    {
        var context = new TenantContext();
        Assert.False(context.HasTenant);
        Assert.Throws<NoTenantException>(() => context.TenantId);
        var a = new TenantId(Guid.NewGuid());
        context.Set(a);
        context.Set(a);
        Assert.Equal(a, context.TenantId);
        Assert.Throws<InvalidOperationException>(() => context.Set(new TenantId(Guid.NewGuid())));
        Assert.Throws<ArgumentException>(() => new TenantContext().Set(default));
    }

    [Fact]
    public void Tenant_id_parses_only_non_empty_guids()
    {
        Assert.False(TenantId.TryParse(Guid.Empty.ToString(), out _));
        Assert.False(TenantId.TryParse("not a guid", out _));
        Assert.True(TenantId.TryParse("11111111-1111-1111-1111-111111111111", out var id));
        Assert.Equal("11111111-1111-1111-1111-111111111111", id.ToString());
    }

    [Fact]
    public void Sign_in_request_limit_defaults_to_one_hundred_and_twenty_per_address()
    {
        Assert.Equal(120, new SignInOptions().RequestLimitPerMinute);
    }
}
