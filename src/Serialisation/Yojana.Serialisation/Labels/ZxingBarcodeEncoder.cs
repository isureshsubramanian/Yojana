using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Yojana.Serialisation.Contracts;
using ZXing;
using ZXing.Common;

namespace Yojana.Serialisation.Labels;

/// <summary>ZXing supplies only the symbol matrix; this type owns the dependency-free PNG rendering.</summary>
public sealed class ZxingBarcodeEncoder : IBarcodeEncoder
{
    public Task<BarcodeEncoding> EncodeAsync(string symbology, string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(value))
            throw new CommandRefusedException("barcode_value_required", "A non-empty value is required to generate a barcode.");
        if (symbology == LabelSymbology.Code39 && value.Contains('*', StringComparison.Ordinal))
            throw new CommandRefusedException("barcode_code39_asterisk", "An asterisk cannot be encoded in a Code 39 serial.");

        var (format, width, height, quietZone) = symbology switch
        {
            LabelSymbology.Qr => (BarcodeFormat.QR_CODE, 256, 256, 4),
            LabelSymbology.DataMatrix => (BarcodeFormat.DATA_MATRIX, 256, 256, 4),
            LabelSymbology.Code128 => (BarcodeFormat.CODE_128, 512, 160, 10),
            LabelSymbology.Code39 => (BarcodeFormat.CODE_39, 512, 160, 10),
            _ => throw new CommandRefusedException("barcode_symbology_unsupported", $"Symbology '{symbology}' is not supported."),
        };
        try
        {
            var hints = new Dictionary<EncodeHintType, object>
            {
                [EncodeHintType.MARGIN] = quietZone,
                [EncodeHintType.CHARACTER_SET] = "UTF-8",
            };
            var matrix = new MultiFormatWriter().encode(value, format, width, height, hints);
            var png = BarcodePng.Render(matrix);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new BarcodeEncoding(symbology, "image/png",
                "data:image/png;base64," + Convert.ToBase64String(png), matrix.Width, matrix.Height, quietZone, value));
        }
        catch (Exception ex) when (ex is ArgumentException or WriterException)
        {
            throw new CommandRefusedException("barcode_value_invalid", ex.Message);
        }
    }
}

internal static class BarcodePng
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static byte[] Render(BitMatrix matrix)
    {
        var scanlines = new byte[(matrix.Width + 1) * matrix.Height];
        for (var y = 0; y < matrix.Height; y++)
        {
            var offset = y * (matrix.Width + 1);
            scanlines[offset] = 0;
            for (var x = 0; x < matrix.Width; x++)
                scanlines[offset + x + 1] = matrix[x, y] ? (byte)0 : (byte)255;
        }

        byte[] compressed;
        using (var payload = new MemoryStream())
        {
            using (var zlib = new ZLibStream(payload, CompressionLevel.SmallestSize, leaveOpen: true))
                zlib.Write(scanlines);
            compressed = payload.ToArray();
        }

        using var png = new MemoryStream();
        png.Write(Signature);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, matrix.Width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], matrix.Height);
        header[8] = 8;
        header[9] = 0;
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed);
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream destination, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        destination.Write(number);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        destination.Write(typeBytes);
        destination.Write(data);
        var crc = Crc32(typeBytes, data);
        BinaryPrimitives.WriteUInt32BigEndian(number, crc);
        destination.Write(number);
    }

    private static uint Crc32(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        var crc = uint.MaxValue;
        foreach (var value in first) crc = Step(crc, value);
        foreach (var value in second) crc = Step(crc, value);
        return ~crc;
    }

    private static uint Step(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
            crc = (crc & 1) == 0 ? crc >> 1 : 0xedb88320u ^ (crc >> 1);
        return crc;
    }
}
