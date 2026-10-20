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

    [Theory]
    [InlineData("short", "person@example.invalid", "Fictional Person", "password_too_short")]
    [InlineData("person@example.invalid", "person@example.invalid", "Fictional Person", "password_matches_email")]
    [InlineData("Fictional Person", "person@example.invalid", "Fictional Person", "password_matches_display_name")]
    public void Password_policy_returns_stable_reason_codes(string password, string email, string displayName,
        string reason)
    {
        var result = new PasswordPolicy(new SignInOptions()).Validate(password, email, displayName);
        Assert.False(result.IsValid);
        Assert.Contains(result.Reasons, item => item.Code == reason && !string.IsNullOrWhiteSpace(item.Message));
    }

    [Fact]
    public void Password_policy_accepts_twelve_to_one_hundred_and_twenty_eight_characters_without_expiry()
    {
        var policy = new PasswordPolicy(new SignInOptions());
        Assert.True(policy.Validate("twelve-chars", "person@example.invalid", "Fictional Person").IsValid);
        Assert.True(policy.Validate(new string('x', 128), "person@example.invalid", "Fictional Person").IsValid);
        var tooLong = policy.Validate(new string('x', 129), "person@example.invalid", "Fictional Person");
        Assert.Equal(["password_too_long"], tooLong.Reasons.Select(reason => reason.Code));
    }

    [Fact]
    public void Sign_in_request_limit_defaults_to_one_hundred_and_twenty_per_address()
    {
        Assert.Equal(120, new SignInOptions().RequestLimitPerMinute);
    }
}
