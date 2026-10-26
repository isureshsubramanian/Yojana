using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Data.Entities;

namespace Yojana.Integration.Relays;

internal static class RelayModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<SiteRelay>(b =>
        {
            b.ToTable("relay", t => t.HasCheckConstraint("ck_relay_status", "status IN ('active','revoked')"));
            b.HasKey(x => new { x.TenantId, x.RelayId });
            b.Property(x => x.DisplayName).HasMaxLength(160);
            b.Property(x => x.Status).HasMaxLength(16);
            b.Property(x => x.AgentVersion).HasMaxLength(80);
            b.Property(x => x.HostName).HasMaxLength(160);
            b.Property(x => x.SigningKeyEnvelope).HasMaxLength(4096);
            b.Property(x => x.PreviousSigningKeyEnvelope).HasMaxLength(4096);
            b.Property(x => x.ErpVersion).HasMaxLength(160);
            b.Property(x => x.ProbedErpVersion).HasMaxLength(160);
            b.HasIndex(x => new { x.TenantId, x.ConnectionId, x.Status, x.LastSeenAt, x.RelayId });
            b.HasIndex(x => new { x.TenantId, x.ConnectionId, x.DisplayName, x.RelayId });
            b.HasIndex(x => new { x.TenantId, x.ConnectionId, x.Status, x.RelayId });
            b.HasOne<Connection>().WithMany().HasForeignKey(x => new { x.TenantId, x.ConnectionId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<RelayRequestRow>(b =>
        {
            b.ToTable("relay_request", t =>
            {
                t.HasCheckConstraint("ck_relay_request_payload", "DATALENGTH(payload_json) <= 25165824");
                t.HasCheckConstraint("ck_relay_request_result", "DATALENGTH(result_json) <= 25165824");
            });
            b.HasKey(x => new { x.TenantId, x.RequestId });
            b.HasIndex(x => new { x.TenantId, x.ConnectionId, x.LeaseId, x.CreatedAt, x.RequestId });
            b.HasIndex(x => new { x.TenantId, x.ExpiresAt, x.RequestId });
            b.HasOne<Connection>().WithMany().HasForeignKey(x => new { x.TenantId, x.ConnectionId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<RelayNonce>(b =>
        {
            b.ToTable("relay_nonce");
            b.HasKey(x => new { x.TenantId, x.RelayId, x.Nonce });
            b.Property(x => x.Nonce).HasMaxLength(48).IsUnicode(false);
            b.HasIndex(x => new { x.TenantId, x.RelayId, x.SeenAt });
            b.HasIndex(x => new { x.TenantId, x.ExpiresAt });
        });
    }
}
