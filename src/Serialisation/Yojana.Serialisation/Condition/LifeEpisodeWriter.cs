using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Condition;

public sealed class LifeEpisodeWriter(SerialisationDbContext db, ITenantContext tenant) : ILifeEpisodeWriter
{
    public async Task<Guid> OpenAsync(OpenLifeEpisode episode, CancellationToken cancellationToken)
    {
        if (!LifeEpisodeKind.All.Contains(episode.Kind)) throw new CommandRefusedException("life_episode_kind_unknown", $"'{episode.Kind}' is not a life episode kind.");
        await ConditionCommandRules.RequireUnitAsync(db, tenant.TenantId, episode.UnitId, cancellationToken);
        if (await db.LifeEpisodes.AnyAsync(e => e.TenantId == tenant.TenantId && e.UnitId == episode.UnitId && e.Kind == episode.Kind && e.EndedAt == null, cancellationToken))
            throw new CommandRefusedException("life_episode_already_open", $"Unit {episode.UnitId} already has an open {episode.Kind} life episode.");
        var id = TimeOrderedId.New(episode.StartedAt);
        db.LifeEpisodes.Add(new LifeEpisode
        {
            TenantId = tenant.TenantId, LifeEpisodeId = id, UnitId = episode.UnitId, Kind = episode.Kind,
            StartedAt = episode.StartedAt, WorkOrderId = episode.WorkOrderId, CycleNumber = episode.CycleNumber,
            ActorRef = episode.ActorRef, OpeningEventId = episode.OpeningEventId,
        });
        await db.SaveChangesAsync(cancellationToken);
        return id;
    }

    public async Task CloseAsync(CloseLifeEpisode episode, CancellationToken cancellationToken)
    {
        var row = await db.LifeEpisodes.FirstOrDefaultAsync(e => e.TenantId == tenant.TenantId && e.LifeEpisodeId == episode.LifeEpisodeId, cancellationToken)
            ?? throw new CommandRefusedException("life_episode_unknown", $"Life episode {episode.LifeEpisodeId} does not exist.");
        if (row.EndedAt is not null) return;
        if (episode.EndedAt < row.StartedAt) throw new CommandRefusedException("life_episode_dates_invalid", "A life episode cannot end before it starts.");
        row.EndedAt = episode.EndedAt;
        row.ClosingEventId = episode.ClosingEventId;
        await db.SaveChangesAsync(cancellationToken);
    }
}
