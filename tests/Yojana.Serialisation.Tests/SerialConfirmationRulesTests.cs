using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Documents;

namespace Yojana.Serialisation.Tests;

public sealed class SerialConfirmationRulesTests
{
    [Theory]
    [InlineData(null, SerialConfirmationMode.ScanRequired)]
    [InlineData("", SerialConfirmationMode.ScanRequired)]
    [InlineData("unexpected", SerialConfirmationMode.ScanRequired)]
    [InlineData(SerialConfirmationMode.ScanRequired, SerialConfirmationMode.ScanRequired)]
    [InlineData(SerialConfirmationMode.CopyAllowed, SerialConfirmationMode.CopyAllowed)]
    public void Setting_resolution_is_safe_and_accepts_only_the_two_contract_values(string? stored, string expected)
    {
        Assert.Equal(expected, SerialConfirmationSettings.Resolve(stored));
    }

    [Fact]
    public async Task Setting_round_trips_and_rejects_an_unknown_mode()
    {
        var store = new MemoryTenantSettings();
        var settings = new SerialConfirmationSettings(store);

        Assert.Equal(SerialConfirmationMode.ScanRequired, await settings.GetAsync(CancellationToken.None));
        await settings.SetAsync(new(SerialConfirmationMode.CopyAllowed), CancellationToken.None);
        Assert.Equal(SerialConfirmationMode.CopyAllowed, await settings.GetAsync(CancellationToken.None));

        var refusal = await Assert.ThrowsAsync<CommandRefusedException>(() => settings.SetAsync(new("sometimes"), CancellationToken.None));
        Assert.Equal("serial_confirmation_mode_invalid", refusal.Rule);
        Assert.NotNull(refusal.UnblockingStep);
    }

    [Fact]
    public void Missing_capture_method_is_scanned_for_backward_compatibility()
    {
        var decision = SerialConfirmationRules.Evaluate(SerialConfirmationMode.ScanRequired, null);

        Assert.True(decision.Accepted);
        Assert.Equal(SerialCaptureMethodValue.Scanned, decision.CaptureMethod);
        Assert.False(decision.SerialsCopiedWithoutScan);
    }

    [Theory]
    [InlineData(SerialCaptureMethodValue.Copied)]
    [InlineData(SerialCaptureMethodValue.Keyed)]
    public void Scan_required_refuses_every_non_scanned_capture_with_a_named_rule(string method)
    {
        var decision = SerialConfirmationRules.Evaluate(SerialConfirmationMode.ScanRequired, method);

        Assert.False(decision.Accepted);
        Assert.Equal(SerialConfirmationRules.ScanRequiredRule, decision.Refusal!.Rule);
        Assert.Equal(SerialConfirmationRules.ScanUnblockingStep, decision.Refusal.UnblockingStep);
    }

    [Theory]
    [InlineData(SerialCaptureMethodValue.Copied)]
    [InlineData(SerialCaptureMethodValue.Keyed)]
    public void Copy_allowed_accepts_non_scanned_capture_and_marks_the_audit_fact(string method)
    {
        var decision = SerialConfirmationRules.Evaluate(SerialConfirmationMode.CopyAllowed, method);

        Assert.True(decision.Accepted);
        Assert.True(decision.SerialsCopiedWithoutScan);
    }

    private sealed class MemoryTenantSettings : ITenantSettings
    {
        private readonly Dictionary<string, string> values = [];

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(values.GetValueOrDefault(key));

        public Task SetAsync(string key, string value, CancellationToken cancellationToken)
        {
            values[key] = value;
            return Task.CompletedTask;
        }
    }
}
