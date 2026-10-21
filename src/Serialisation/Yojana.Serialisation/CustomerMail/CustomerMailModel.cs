using Microsoft.EntityFrameworkCore;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.CustomerMail;

internal static class CustomerMailModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<MailTemplate>(b =>
        {
            b.ToTable("mail_template");
            b.HasKey(x => new { x.TenantId, x.TemplateId });
            b.Property(x => x.Kind).HasMaxLength(60).IsUnicode(false);
            b.Property(x => x.Name).HasMaxLength(120);
            b.Property(x => x.Description).HasMaxLength(300);
            b.Property(x => x.StartedFrom).HasMaxLength(120);
            b.Property(x => x.CreatedBy).HasMaxLength(200);
            b.HasIndex(x => new { x.TenantId, x.Kind, x.Name }).IsUnique();
        });
        model.Entity<MailTemplateVersion>(b =>
        {
            b.ToTable("mail_template_version", t =>
            {
                t.HasCheckConstraint("ck_mail_template_version_status", "status IN ('draft','published','retired')");
                t.HasCheckConstraint("ck_mail_template_version_content", "ISJSON(content) = 1");
            });
            b.HasKey(x => new { x.TenantId, x.VersionId });
            b.Property(x => x.Status).HasMaxLength(12).IsUnicode(false);
            b.Property(x => x.Content).HasColumnType("nvarchar(max)");
            b.Property(x => x.CreatedBy).HasMaxLength(200);
            b.Property(x => x.UpdatedBy).HasMaxLength(200);
            b.Property(x => x.PublishedBy).HasMaxLength(200);
            b.Property(x => x.RetiredBy).HasMaxLength(200);
            b.Property(x => x.RowVersion).IsRowVersion();
            b.HasIndex(x => new { x.TenantId, x.TemplateId, x.Version }).IsUnique();
            b.HasOne<MailTemplate>().WithMany().HasForeignKey(x => new { x.TenantId, x.TemplateId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<MailTemplateDefault>(b =>
        {
            b.ToTable("mail_template_default");
            b.HasKey(x => new { x.TenantId, x.Kind, x.Language });
            b.Property(x => x.Kind).HasMaxLength(60).IsUnicode(false);
            b.Property(x => x.Language).HasMaxLength(20).IsUnicode(false);
            b.Property(x => x.UpdatedBy).HasMaxLength(200);
            b.HasIndex(x => new { x.TenantId, x.TemplateId });
            b.HasOne<MailTemplate>().WithMany().HasForeignKey(x => new { x.TenantId, x.TemplateId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<CustomerMailMessage>(b =>
        {
            b.ToTable("customer_mail_message", t =>
            {
                t.HasCheckConstraint("ck_customer_mail_message_status", "status IN ('queued','retrying','sent','failed')");
                t.HasCheckConstraint("ck_customer_mail_message_cc", "ISJSON(cc) = 1");
            });
            b.HasKey(x => new { x.TenantId, x.MessageId });
            b.Property(x => x.Kind).HasMaxLength(60).IsUnicode(false);
            b.Property(x => x.TemplateName).HasMaxLength(120);
            b.Property(x => x.Language).HasMaxLength(20).IsUnicode(false);
            b.Property(x => x.AboutLabel).HasMaxLength(200);
            b.Property(x => x.CustomerName).HasMaxLength(200);
            b.Property(x => x.ToAddress).HasMaxLength(320);
            b.Property(x => x.ToName).HasMaxLength(200);
            b.Property(x => x.Cc).HasColumnType("nvarchar(max)");
            b.Property(x => x.Subject).HasMaxLength(300);
            b.Property(x => x.HtmlBody).HasColumnType("nvarchar(max)");
            b.Property(x => x.TextBody).HasColumnType("nvarchar(max)");
            b.Property(x => x.AttachmentName).HasMaxLength(150);
            b.Property(x => x.SentBy).HasMaxLength(200);
            b.Property(x => x.Status).HasMaxLength(12).IsUnicode(false);
            b.Property(x => x.LastError).HasMaxLength(400);
            b.Property(x => x.Transport).HasMaxLength(20).IsUnicode(false);
            // The sent log pages newest first by (queued_at, message_id); filters narrow by status, kind and sender.
            b.HasIndex(x => new { x.TenantId, x.QueuedAt, x.MessageId }).IsDescending(false, true, true);
            b.HasIndex(x => new { x.TenantId, x.Status, x.QueuedAt });
            b.HasIndex(x => new { x.TenantId, x.UnitId, x.QueuedAt });
            b.HasIndex(x => new { x.TenantId, x.TemplateVersionId });
            b.HasIndex(x => new { x.TenantId, x.CommandId });
        });
    }
}
