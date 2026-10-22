using Microsoft.EntityFrameworkCore;
using Yojana.Knowledge.Contracts;
using Yojana.Knowledge.Data;
using Yojana.Knowledge.Data.Entities;
using Yojana.Platform.Contracts.Files;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Knowledge.Questions;

public sealed record QuestionAttachmentResult(Guid FileId, string FileName, string ContentType,
    long ContentLength, string Sha256, Guid UploadedBy, DateTime UploadedAt);

public sealed class QuestionAttachmentService(KnowledgeDbContext db, ITenantFileStore files,
    ITenantFileLifecycle lifecycle, ITenantContext tenant)
{
    public async Task<QuestionAttachmentResult> UploadAsync(Guid questionId, Guid? replyId,
        TenantFileUpload upload, TenantFileCaller caller, CancellationToken cancellationToken)
    {
        var question = await db.Questions.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.QuestionId == questionId, cancellationToken)
            ?? throw new QuestionRuleException("question_not_found", "The question does not exist.");
        if (question.Status != QuestionStatus.Open)
            throw new QuestionRuleException("question_not_open", "Attachments can be added only while the question is open.");
        if (replyId is { } expected && !await db.Replies.AsNoTracking().AnyAsync(x => x.TenantId == tenant.TenantId
            && x.QuestionId == questionId && x.ReplyId == expected, cancellationToken))
            throw new QuestionRuleException("reply_not_found", "The reply does not belong to this question.");
        var stored = await files.PutAsync(new(question.RecordKind, question.RecordId), upload, caller,
            cancellationToken);
        db.Attachments.Add(new QuestionAttachment
        {
            TenantId = tenant.TenantId, FileId = stored.FileId, QuestionId = questionId,
            ReplyId = replyId, QuestionBody = replyId is null, FileName = stored.FileName,
            ContentType = stored.ContentType, ContentLength = stored.ContentLength, Sha256 = stored.Sha256,
            UploadedBy = stored.UploadedBy, UploadedAt = stored.UploadedAt,
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await lifecycle.PurgeAsync([stored.FileId], cancellationToken);
            throw;
        }
        return new(stored.FileId, stored.FileName, stored.ContentType, stored.ContentLength, stored.Sha256,
            stored.UploadedBy, stored.UploadedAt);
    }

    public async Task<OpenTenantFile?> OpenAsync(Guid questionId, Guid fileId, TenantFileCaller caller,
        CancellationToken cancellationToken)
    {
        if (!await db.Attachments.AsNoTracking().AnyAsync(x => x.TenantId == tenant.TenantId
            && x.QuestionId == questionId && x.FileId == fileId, cancellationToken)) return null;
        return await files.OpenReadAsync(fileId, caller, cancellationToken);
    }
}
