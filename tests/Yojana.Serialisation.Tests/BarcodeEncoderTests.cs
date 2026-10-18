using System.Buffers.Binary;
using System.IO.Compression;
using Xunit;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Labels;

namespace Yojana.Serialisation.Tests;

public sealed class BarcodeEncoderTests
{
    private readonly ZxingBarcodeEncoder encoder = new();

    [Theory]
    [InlineData(LabelSymbology.Qr, 256, 256, true)]
    [InlineData(LabelSymbology.DataMatrix, 256, 256, true)]
    [InlineData(LabelSymbology.Code128, 512, 160, false)]
    [InlineData(LabelSymbology.Code39, 512, 160, false)]
    public async Task Fixed_input_produces_a_deterministic_png_with_the_requested_matrix_and_quiet_zone(
        string symbology, int width, int height, bool verticalQuietZone)
    {
        var first = await encoder.EncodeAsync(symbology, "AST-2610-0012", CancellationToken.None);
        var second = await encoder.EncodeAsync(symbology, "AST-2610-0012", CancellationToken.None);

        Assert.Equal(symbology, first.Symbology);
        Assert.Equal("image/png", first.MediaType);
        Assert.Equal(first.DataUrl, second.DataUrl);
        Assert.Equal((width, height), (first.Width, first.Height));
        var image = Png(first.DataUrl);
        Assert.Equal((width, height), (image.Width, image.Height));
        Assert.True(image.LeftWhite >= first.QuietZone, $"left quiet zone was {image.LeftWhite} pixels");
        Assert.True(image.RightWhite >= first.QuietZone, $"right quiet zone was {image.RightWhite} pixels");
        if (verticalQuietZone)
        {
            Assert.True(image.TopWhite >= first.QuietZone, $"top quiet zone was {image.TopWhite} pixels");
            Assert.True(image.BottomWhite >= first.QuietZone, $"bottom quiet zone was {image.BottomWhite} pixels");
        }
    }

    [Fact]
    public async Task Code39_refuses_the_asterisk_reserved_for_start_and_stop_markers()
    {
        var refusal = await Assert.ThrowsAsync<CommandRefusedException>(() =>
            encoder.EncodeAsync(LabelSymbology.Code39, "AB*CD", CancellationToken.None));

        Assert.Equal("barcode_code39_asterisk", refusal.Rule);
    }

    [Fact]
    public void Lookup_url_builder_escapes_the_serial_in_query_and_route_templates()
    {
        Assert.Equal("/api/units/lookup?q=AST%202610%2F12",
            new LabelLookupUrlBuilder("/api/units/lookup").ForSerial("AST 2610/12"));
        Assert.Equal("https://labels.example.test/u/AST%202610%2F12",
            new LabelLookupUrlBuilder("https://labels.example.test/u/{serial}").ForSerial("AST 2610/12"));
    }

    private static PngBounds Png(string dataUrl)
    {
        const string prefix = "data:image/png;base64,";
        Assert.StartsWith(prefix, dataUrl, StringComparison.Ordinal);
        var bytes = Convert.FromBase64String(dataUrl[prefix.Length..]);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes[..8]);
        var width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
        var compressed = new List<byte>();
        var offset = 8;
        while (offset < bytes.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4));
            var type = System.Text.Encoding.ASCII.GetString(bytes, offset + 4, 4);
            if (type == "IDAT") compressed.AddRange(bytes.AsSpan(offset + 8, length).ToArray());
            offset += 12 + length;
            if (type == "IEND") break;
        }

        using var source = new MemoryStream(compressed.ToArray());
        using var zlib = new ZLibStream(source, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        var scanlines = raw.ToArray();
        Assert.Equal((width + 1) * height, scanlines.Length);
        var left = width;
        var right = -1;
        var top = height;
        var bottom = -1;
        for (var y = 0; y < height; y++)
        {
            Assert.Equal(0, scanlines[y * (width + 1)]);
            for (var x = 0; x < width; x++)
            {
                if (scanlines[y * (width + 1) + x + 1] != 0) continue;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }
        Assert.True(right >= 0, "PNG contained no black symbol pixels");
        return new(width, height, left, width - right - 1, top, height - bottom - 1);
    }

    private sealed record PngBounds(int Width, int Height, int LeftWhite, int RightWhite, int TopWhite, int BottomWhite);
}
