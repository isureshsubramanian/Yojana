using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Yojana.Platform.Data;

/// <summary>
/// Every <see cref="DateTime"/> column holds UTC. Values read back are marked <see cref="DateTimeKind.Utc"/>, so JSON
/// carries the "Z" suffix; a local value written or used as a query parameter is converted to UTC first.
/// </summary>
/// <remarks>
/// Hidden coupling: the web client (web/src/components/tenantTime.ts) parses timestamps with <c>new Date(value)</c>,
/// which reads a value without "Z" as the browser's local time. Removing this converter shifts every time on screen by
/// the viewer's UTC offset.
/// </remarks>
public sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    value => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value,
    value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
