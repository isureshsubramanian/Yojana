using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.DeadLetters;
using Yojana.Integration.Outbox;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class OutboundReviewTests(IntegrationHostFixture fixture)
{
    [DatabaseFact]
    public async Task Held_record_is_never_created_twice_and_completes_when_the_reviewer_posts_it()
    {
        var (tenant, connection, commandId) = await HeldCommandAsync("rvpost");
        using var scope = fixture.ScopeFor(tenant);
        var relay = scope.ServiceProvider.GetRequiredService<OutboundRelay>();
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();

        var command = await db.OutboundCommands.SingleAsync(x => x.OutboundCommandId == commandId);
        Assert.Equal(OutboundCommandStatus.AwaitingReview, command.Status);
        Assert.Equal(OutboundConnectorOutcome.AwaitingReview, command.Outcome);
        Assert.Equal("HELD-" + command.ErpReferenceText, command.PendingRemoteKey);
        var task = await db.ManualOutboundTasks.SingleAsync(x => x.OutboundCommandId == commandId);
        Assert.Equal(ManualTaskStatus.Open, task.Status);
        Assert.Null(task.DeadLetterItemId);
        Assert.Equal("A fictional reviewer must add the held record.", task.Note);
        Assert.Empty(await db.DeadLetterItems.Where(x => x.SourceId == commandId).ToListAsync());

        // Not due yet: no remote read, and the held command is never claimed for create again.
        Assert.Equal(new OutboundRelayResult(0, 0, 0), await relay.RelayConnectionAsync(connection, default));
        Assert.Equal(0, fixture.Connector.ReviewChecksFor(connection));

        fixture.Clock.UtcNow += OutboundRelay.ReviewCheckInterval + TimeSpan.FromSeconds(1);
        Assert.Equal(new OutboundRelayResult(0, 0, 0), await relay.RelayConnectionAsync(connection, default));
        Assert.Equal(1, fixture.Connector.ReviewChecksFor(connection));
        await db.Entry(command).ReloadAsync();
        Assert.Equal(OutboundCommandStatus.AwaitingReview, command.Status);
        Assert.Equal(fixture.Clock.UtcNow + OutboundRelay.ReviewCheckInterval, command.NextAttemptAt!.Value,
            TimeSpan.FromMilliseconds(1));

        fixture.Connector.Review(command.PendingRemoteKey!, OutboundReviewState.Posted(new("ERP-77", null)));
        fixture.Clock.UtcNow += OutboundRelay.ReviewCheckInterval + TimeSpan.FromSeconds(1);
        Assert.Equal(1, (await relay.RelayConnectionAsync(connection, default)).Completed);
        await db.Entry(command).ReloadAsync();
        Assert.Equal(OutboundCommandStatus.Done, command.Status);
        Assert.Equal(OutboundConnectorOutcome.PostedAfterReview, command.Outcome);
        Assert.Equal("ERP-77", command.ErpExternalId);
        Assert.Null(command.ErpChangeMarker);
        Assert.Single(await db.ExternalIdMaps.Where(x => x.ConnectionId == connection && x.ExternalId == "ERP-77")
            .ToListAsync());
        await db.Entry(task).ReloadAsync();
        Assert.Equal(ManualTaskStatus.Completed, task.Status);
        Assert.Equal("ERP-77", task.ErpExternalId);
        Assert.StartsWith("system:connection:", task.CompletedBy, StringComparison.Ordinal);
        Assert.Equal(1, fixture.Connector.CreateCallsFor(connection));
    }

    [DatabaseFact]
    public async Task Rejected_review_parks_the_command_and_keeps_one_task_tied_to_the_dead_letter()
    {
        var (tenant, connection, commandId) = await HeldCommandAsync("rvrej");
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var command = await db.OutboundCommands.SingleAsync(x => x.OutboundCommandId == commandId);
        fixture.Connector.Review(command.PendingRemoteKey!, OutboundReviewState.Rejected("Fictional approver declined."));
        fixture.Clock.UtcNow += OutboundRelay.ReviewCheckInterval + TimeSpan.FromSeconds(1);

        Assert.Equal(1, (await scope.ServiceProvider.GetRequiredService<OutboundRelay>()
            .RelayConnectionAsync(connection, default)).Parked);
        await db.Entry(command).ReloadAsync();
        Assert.Equal(OutboundCommandStatus.Parked, command.Status);
        Assert.Equal(OutboundConnectorOutcome.Rejected, command.Outcome);
        var letter = await db.DeadLetterItems.SingleAsync(x => x.SourceId == commandId);
        Assert.Equal("rejected_in_remote_review", letter.ReasonCode);
        Assert.Equal("Fictional approver declined.", letter.Failure);
        var task = await db.ManualOutboundTasks.SingleAsync(x => x.OutboundCommandId == commandId);
        Assert.Equal(ManualTaskStatus.Open, task.Status);
        Assert.Equal(letter.DeadLetterItemId, task.DeadLetterItemId);
        Assert.Equal("Fictional approver declined.", task.Note);
    }

    [DatabaseFact]
    public async Task Withdrawn_review_parks_and_a_failed_check_only_waits_for_the_next_one()
    {
        var (tenant, connection, commandId) = await HeldCommandAsync("rvwd");
        using var scope = fixture.ScopeFor(tenant);
        var relay = scope.ServiceProvider.GetRequiredService<OutboundRelay>();
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var command = await db.OutboundCommands.SingleAsync(x => x.OutboundCommandId == commandId);

        fixture.Connector.FailReviewCheck = true;
        fixture.Clock.UtcNow += OutboundRelay.ReviewCheckInterval + TimeSpan.FromSeconds(1);
        Assert.Equal(new OutboundRelayResult(0, 0, 0), await relay.RelayConnectionAsync(connection, default));
        await db.Entry(command).ReloadAsync();
        Assert.Equal(OutboundCommandStatus.AwaitingReview, command.Status);
        Assert.StartsWith("HttpRequestException", command.LastFailure, StringComparison.Ordinal);
        Assert.True(command.NextAttemptAt > fixture.Clock.UtcNow);

        fixture.Connector.FailReviewCheck = false;
        fixture.Connector.Review(command.PendingRemoteKey!, OutboundReviewState.Withdrawn("Fictional draft deleted."));
        fixture.Clock.UtcNow += OutboundRelay.ReviewCheckInterval + TimeSpan.FromSeconds(1);
        Assert.Equal(1, (await relay.RelayConnectionAsync(connection, default)).Parked);
        Assert.Equal("withdrawn_in_remote_review",
            (await db.DeadLetterItems.SingleAsync(x => x.SourceId == commandId)).ReasonCode);
    }

    [DatabaseFact]
    public async Task Operator_can_complete_a_held_record_posted_outside_the_review()
    {
        var (tenant, _, commandId) = await HeldCommandAsync("rvman");
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var task = await db.ManualOutboundTasks.SingleAsync(x => x.OutboundCommandId == commandId);
        var view = await scope.ServiceProvider.GetRequiredService<DeadLetterService>().CompleteManualTaskAsync(
            task.ManualOutboundTaskId, new("ERP-HAND-1", null, true), Guid.NewGuid().ToString("D"), default);
        Assert.Equal(ManualTaskStatus.Completed, view.Status);
        Assert.Equal("A fictional reviewer must add the held record.", view.Note);
        var command = await db.OutboundCommands.SingleAsync(x => x.OutboundCommandId == commandId);
        Assert.Equal(OutboundCommandStatus.Done, command.Status);
        Assert.Equal("ERP-HAND-1", command.ErpExternalId);
    }

    private async Task<(TenantId Tenant, Guid Connection, Guid CommandId)> HeldCommandAsync(string prefix)
    {
        fixture.Clock.UtcNow = new DateTime(2026, 9, 12, 9, 0, 0, DateTimeKind.Utc);
        fixture.Connector.Configure(FakeOutboundBehaviour.HoldForReview);
        var tenant = await fixture.NewTenantAsync(prefix);
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid(),
            IntegrationHostFixture.FakeConnectorCode);
        var id = TimeOrderedId.New(fixture.Clock.UtcNow);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        db.OutboundCommands.Add(new OutboundCommandRow
        {
            TenantId = tenant,
            OutboundCommandId = id,
            ConnectionId = connection,
            CommandType = "CreateServiceOrder",
            IdempotencyKey = Guid.NewGuid(),
            ModuleDocumentRef = $"service/{Guid.NewGuid():N}",
            ErpReferenceText = $"YOJ-{Guid.NewGuid():N}",
            Payload = "{\"customer_ref\":\"FIC-CUSTOMER\",\"problem_description\":\"Fictional repair\"}",
            Status = OutboundCommandStatus.Pending,
            CreatedAt = fixture.Clock.UtcNow.AddHours(-1),
        });
        await db.SaveChangesAsync();
        var held = await scope.ServiceProvider.GetRequiredService<OutboundRelay>()
            .RelayConnectionAsync(connection, default);
        Assert.Equal(new OutboundRelayResult(0, 0, 0, 1), held);
        return (tenant, connection, id);
    }
}
