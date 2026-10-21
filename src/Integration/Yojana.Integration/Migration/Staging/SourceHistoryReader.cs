using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Data;
using Yojana.Integration.LegacyMigration.Canonicalisation;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.LegacyMigration.Staging;

internal sealed record SourceHistoryMovementRow(
    Guid CandidateId,
    Guid EventCandidateId,
    string SerialMasterSourceKey,
    string TransactionSourceKey,
    string Kind,
    string DocumentLabel,
    string CancellationFamily,
    string? BaseCancellationFamily,
    bool SuppressWhenBasedOnCustomerReturn,
    int? DocumentEntry,
    int? DocumentNumber,
    int? BaseEntry,
    DateTime? DocumentDate,
    DateTime? CreatedDate,
    int? CreatedTime,
    int? LogEntry,
    string? PartyCode,
    string? WarehouseCode,
    decimal Quantity);

/// <summary>
/// Adapts incumbent transaction facts at the staging boundary. Its result is the narrow, source-agnostic contract
/// consumed by reconstruction; numeric document types do not cross that seam.
/// </summary>
public sealed class SourceHistoryReader(IntegrationDbContext db, ITenantContext tenant)
{
    internal async Task<IReadOnlyList<SourceHistoryMovementRow>> ReadAsync(Guid canonicalisationRunId,
        IReadOnlyList<Guid> candidateIds, CancellationToken cancellationToken)
    {
        var rows = await (from candidateEvent in db.MigrationEventCandidates.AsNoTracking()
                          join line in db.Set<TransactionLogLineStagingRow>().AsNoTracking()
                              on new { candidateEvent.TenantId, SourceKey = candidateEvent.TransactionSourceKey }
                              equals new { line.TenantId, SourceKey = line.SourcePk }
                          join header in db.Set<TransactionLogHeaderStagingRow>().AsNoTracking()
                              on new { line.TenantId, line.BatchId, line.SourceSystem, line.LogEntry }
                              equals new { header.TenantId, header.BatchId, header.SourceSystem, header.LogEntry }
                          where candidateEvent.TenantId == tenant.TenantId &&
                                candidateEvent.RunId == canonicalisationRunId &&
                                candidateIds.Contains(candidateEvent.CandidateId)
                          select new SourceInputRow
                          {
                              CandidateId = candidateEvent.CandidateId,
                              EventCandidateId = candidateEvent.EventCandidateId,
                              SerialMasterSourceKey = candidateEvent.SerialMasterSourceKey,
                              TransactionSourceKey = candidateEvent.TransactionSourceKey,
                              DocumentType = header.DocumentType,
                              DocumentEntry = header.DocumentEntry,
                              DocumentNumber = header.DocumentNumber,
                              BaseType = header.BaseType,
                              BaseEntry = header.BaseEntry,
                              DocumentDate = header.DocumentDate,
                              CreatedDate = header.CreatedDate,
                              CreatedTime = header.CreatedTime,
                              LogEntry = header.LogEntry,
                              StockEffect = header.StockEffect,
                              PartyCode = header.PartyCode,
                              WarehouseCode = header.WarehouseCode,
                              Quantity = line.Quantity ?? 0m,
                          }).ToListAsync(cancellationToken);
        return rows.Select(Adapt).ToList();
    }

    private static SourceHistoryMovementRow Adapt(SourceInputRow row)
    {
        var movement = SourceDocumentTypes.Map(row.DocumentType, row.Quantity, row.StockEffect, row.BaseType);
        return new(row.CandidateId, row.EventCandidateId, row.SerialMasterSourceKey,
            row.TransactionSourceKey, movement.Kind, movement.DocumentLabel, movement.CancellationFamily,
            movement.BaseCancellationFamily, movement.SuppressWhenBasedOnCustomerReturn,
            row.DocumentEntry, row.DocumentNumber, row.BaseEntry, row.DocumentDate, row.CreatedDate,
            row.CreatedTime, row.LogEntry, row.PartyCode, row.WarehouseCode, row.Quantity);
    }

    // A member-init class is required here: EF cannot filter or order a positional record projection.
    private sealed class SourceInputRow
    {
        public Guid CandidateId { get; init; }
        public Guid EventCandidateId { get; init; }
        public required string SerialMasterSourceKey { get; init; }
        public required string TransactionSourceKey { get; init; }
        public int? DocumentType { get; init; }
        public int? DocumentEntry { get; init; }
        public int? DocumentNumber { get; init; }
        public int? BaseType { get; init; }
        public int? BaseEntry { get; init; }
        public DateTime? DocumentDate { get; init; }
        public DateTime? CreatedDate { get; init; }
        public int? CreatedTime { get; init; }
        public int? LogEntry { get; init; }
        public int? StockEffect { get; init; }
        public string? PartyCode { get; init; }
        public string? WarehouseCode { get; init; }
        public decimal Quantity { get; init; }
    }
}
