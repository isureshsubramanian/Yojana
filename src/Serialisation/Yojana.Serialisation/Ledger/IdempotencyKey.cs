using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Yojana.Serialisation.Ledger;

/// <summary>
/// Deterministic idempotency key of an event (ADR-0001 UnitEvent): SHA-256 over (source system, document, line,
/// unit, event type, business time), lower-case hex, 64 characters. The business time is part of the hash so a
/// retried capture lands in the same partition and is rejected by the aligned unique index; a retry carrying a
/// different business time is, by definition, a new event.
/// </summary>
public static class IdempotencyKey
{
    public static string For(string source, string? documentType, Guid? documentId, int? documentLine, Guid unitId, string eventType, DateTime eventTime)
    {
        var text = string.Join('|', source, documentType ?? "", documentId?.ToString("D") ?? "", documentLine?.ToString(CultureInfo.InvariantCulture) ?? "",
            unitId.ToString("D"), eventType, eventTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>A second key tied to the first (the closing event of a transfer, the child side of an install).</summary>
    public static string Derive(string key, string suffix) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key + "|" + suffix)));

    /// <summary>For facts that carry no document (a correction, a flag): the command id stands in for the document.</summary>
    public static string ForCommand(string source, Guid commandId, Guid unitId, string eventType, DateTime eventTime) =>
        For(source, "command", commandId, null, unitId, eventType, eventTime);
}
