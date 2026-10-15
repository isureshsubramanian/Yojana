using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Yojana.Platform.Contracts.Identifiers;

/// <summary>
/// Generates surrogate keys that are time-ordered under SQL Server's <c>uniqueidentifier</c>
/// comparison, so clustered keys stay append-friendly (MOD-01 invariant 2: no random GUID as a
/// clustered key).
/// </summary>
/// <remarks>
/// SQL Server does not compare a <c>uniqueidentifier</c> left to right. It compares the last six bytes
/// first (bytes 10-15 of the stored layout), then bytes 8-9, 6-7, 4-5 and finally 0-3. A standard
/// UUIDv7 (<see cref="Guid.CreateVersion7()"/>) puts its timestamp in the first six bytes, which SQL
/// Server compares last, so UUIDv7 keys scatter across the clustered index exactly like random GUIDs.
/// This generator therefore writes the 48-bit millisecond timestamp into bytes 10-15 and a 16-bit
/// monotonic counter into bytes 8-9 of the stored layout, which is the same ordering strategy as
/// <c>NEWSEQUENTIALID()</c> and EF Core's SQL Server sequential GUID generator. The remaining eight
/// bytes are random. Verified in P0-T08 (see ADR-0001 Action Item 54).
/// </remarks>
public static class TimeOrderedId
{
    private static readonly object Gate = new();
    private static long lastMilliseconds;
    private static ushort counter;

    /// <summary>A new id ordered by generation time under SQL Server comparison.</summary>
    public static Guid New() => New(DateTime.UtcNow);

    /// <summary>A new id whose SQL Server ordering position is the given UTC instant.</summary>
    public static Guid New(DateTime utcNow)
    {
        var ms = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        ushort seq;
        lock (Gate)
        {
            if (ms > lastMilliseconds)
            {
                // Normal path: a new millisecond.
                lastMilliseconds = ms;
                counter = 0;
                seq = 0;
            }
            else if (ms == lastMilliseconds)
            {
                // Same millisecond: the counter keeps generation order; on overflow borrow the next millisecond.
                if (counter == ushort.MaxValue)
                {
                    ms = ++lastMilliseconds;
                    counter = 0;
                }
                else
                {
                    counter++;
                }

                seq = counter;
            }
            else
            {
                // Back-dated explicit instant (an id positioned at a business time): keep the instant as given;
                // ordering among ids of that same instant is not meaningful, so the sequence is random.
                seq = (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue + 1);
            }
        }

        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes[..8]);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.Slice(8, 2), seq);
        // 48-bit big-endian timestamp in the bytes SQL Server compares first.
        bytes[10] = (byte)(ms >> 40);
        bytes[11] = (byte)(ms >> 32);
        bytes[12] = (byte)(ms >> 24);
        bytes[13] = (byte)(ms >> 16);
        bytes[14] = (byte)(ms >> 8);
        bytes[15] = (byte)ms;
        // Guid(ReadOnlySpan<byte>) takes the same byte layout that Guid.ToByteArray() produces, which
        // is the layout SQL Server stores and compares.
        return new Guid(bytes);
    }

    /// <summary>The UTC instant encoded in an id produced by <see cref="New()"/>.</summary>
    public static DateTime TimestampOf(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        long ms = ((long)bytes[10] << 40) | ((long)bytes[11] << 32) | ((long)bytes[12] << 24)
                  | ((long)bytes[13] << 16) | ((long)bytes[14] << 8) | bytes[15];
        return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
    }
}
