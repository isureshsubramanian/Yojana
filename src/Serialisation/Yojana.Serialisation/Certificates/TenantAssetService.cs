using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Certificates;

public sealed class TenantAssetService(SerialisationDbContext db, ITenantContext tenant, IClock clock)
{
    public const int MaximumBytes = 1024 * 1024;
    public const int MaximumDimension = 4096;

    public async Task<TenantAssetUploadResult> UploadLogoAsync(Guid commandId, byte[] input, Guid? legalEntityId, string actorRef,
        string? originalFileName,
        CancellationToken cancellationToken)
    {
        var existing = await db.TenantAssets.AsNoTracking().FirstOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.AssetId == commandId, cancellationToken);
        if (existing is not null) return new TenantAssetUploadResult(Row(existing), true);
        if (input.Length == 0) throw Refused("tenant_asset_empty", "The uploaded logo is empty.");
        if (input.Length > MaximumBytes) throw Refused("tenant_asset_too_large", "Logos may be at most 1 MB.");
        var inspected = Inspect(input);
        if (inspected.Width is > MaximumDimension || inspected.Height is > MaximumDimension)
            throw Refused("tenant_asset_dimensions_too_large", $"Logo dimensions may be at most {MaximumDimension} × {MaximumDimension} pixels.");
        var now = clock.UtcNow;
        var entity = new TenantAsset
        {
            TenantId = tenant.TenantId,
            AssetId = commandId,
            Kind = TenantAssetKind.Logo,
            LegalEntityId = legalEntityId,
            MediaType = inspected.MediaType,
            OriginalFileName = FileName(originalFileName),
            Bytes = inspected.Bytes,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(inspected.Bytes)),
            Width = inspected.Width,
            Height = inspected.Height,
            UploadedBy = actorRef,
            UploadedAt = now,
        };
        db.TenantAssets.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return new TenantAssetUploadResult(Row(entity), false);
    }

    public async Task<IReadOnlyList<TenantAssetRow>> ListLogosAsync(Guid? legalEntityId,
        CancellationToken cancellationToken)
    {
        var query = db.TenantAssets.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
            && row.Kind == TenantAssetKind.Logo);
        if (legalEntityId is { } entityId)
            query = query.Where(row => row.LegalEntityId == null || row.LegalEntityId == entityId);
        return (await query.OrderByDescending(row => row.UploadedAt).ThenByDescending(row => row.AssetId)
            .ToListAsync(cancellationToken)).Select(Row).ToList();
    }

    public async Task<TenantAssetContent?> ContentAsync(Guid assetId, CancellationToken cancellationToken)
    {
        var row = await db.TenantAssets.AsNoTracking().FirstOrDefaultAsync(item => item.TenantId == tenant.TenantId
            && item.AssetId == assetId, cancellationToken);
        return row is null ? null : new TenantAssetContent(row.AssetId, row.MediaType, row.Bytes, row.Sha256, row.UploadedAt);
    }

    internal static InspectedAsset Inspect(byte[] input)
    {
        if (IsPng(input))
        {
            if (input.Length < 24 || !input.AsSpan(12, 4).SequenceEqual("IHDR"u8))
                throw Refused("tenant_asset_invalid_png", "The PNG header is incomplete.");
            return Raster("image/png", input, checked((int)BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(16, 4))),
                checked((int)BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(20, 4))));
        }
        if (IsJpeg(input))
        {
            var dimensions = JpegDimensions(input);
            return Raster("image/jpeg", input, dimensions.Width, dimensions.Height);
        }
        if (LooksLikeSvg(input)) return SanitiseSvg(input);
        throw Refused("tenant_asset_media_type_unknown", "Only PNG, JPEG and SVG logos are accepted; the file signature was not recognised.");
    }

    internal static InspectedAsset SanitiseSvg(byte[] input)
    {
        try
        {
            using var stream = new MemoryStream(input, writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumBytes,
                IgnoreComments = true,
            });
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            var root = document.Root;
            if (root is null || root.Name.LocalName != "svg" || root.Name.NamespaceName != SvgNamespace)
                throw Refused("tenant_asset_invalid_svg", "The XML root must be an SVG element in the SVG namespace.");

            foreach (var element in root.DescendantsAndSelf().ToList())
            {
                if (!AllowedElements.Contains(element.Name.LocalName))
                {
                    if (element != root) element.Remove();
                    continue;
                }
                foreach (var attribute in element.Attributes().ToList())
                {
                    if (attribute.IsNamespaceDeclaration) continue;
                    var name = attribute.Name.LocalName;
                    if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase) || !AllowedAttributes.Contains(name))
                    {
                        attribute.Remove();
                        continue;
                    }
                    if (name == "href" && !SafeHref(attribute.Value)) attribute.Remove();
                    else if (UrlAttributes.Contains(name) && !SafePaint(attribute.Value)) attribute.Remove();
                }
            }

            var dimensions = SvgDimensions(root);
            using var output = new MemoryStream();
            using (var writer = XmlWriter.Create(output, new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false),
                OmitXmlDeclaration = true,
                Indent = false,
            })) document.Save(writer);
            return new InspectedAsset("image/svg+xml", output.ToArray(), dimensions.Width, dimensions.Height);
        }
        catch (CommandRefusedException)
        {
            throw;
        }
        catch (Exception exception) when (exception is XmlException or DecoderFallbackException or OverflowException)
        {
            throw Refused("tenant_asset_invalid_svg", "The SVG is not well-formed or has invalid dimensions.");
        }
    }

    private static InspectedAsset Raster(string mediaType, byte[] bytes, int width, int height)
    {
        if (width <= 0 || height <= 0) throw Refused("tenant_asset_invalid_dimensions", "Logo dimensions must be positive.");
        return new InspectedAsset(mediaType, bytes, width, height);
    }

    private static (int Width, int Height) JpegDimensions(byte[] bytes)
    {
        var offset = 2;
        while (offset + 4 <= bytes.Length)
        {
            while (offset < bytes.Length && bytes[offset] != 0xff) offset++;
            while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
            if (offset >= bytes.Length) break;
            var marker = bytes[offset++];
            if (marker is 0xd8 or 0xd9 || marker is >= 0xd0 and <= 0xd7) continue;
            if (offset + 2 > bytes.Length) break;
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));
            if (length < 2 || offset + length > bytes.Length) break;
            if (SofMarkers.Contains(marker))
            {
                if (length < 7) break;
                var height = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 3, 2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 5, 2));
                if (width == 0 || height == 0) break;
                return (width, height);
            }
            offset += length;
        }
        throw Refused("tenant_asset_invalid_jpeg", "The JPEG dimensions could not be read from its headers.");
    }

    private static (int Width, int Height) SvgDimensions(XElement root)
    {
        var width = SvgLength(root.Attribute("width")?.Value);
        var height = SvgLength(root.Attribute("height")?.Value);
        if (width is not null && height is not null) return (width.Value, height.Value);
        var viewBox = root.Attribute("viewBox")?.Value.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        if (viewBox is not { Length: 4 }
            || !decimal.TryParse(viewBox[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var boxWidth)
            || !decimal.TryParse(viewBox[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var boxHeight)
            || boxWidth <= 0 || boxHeight <= 0)
            throw Refused("tenant_asset_invalid_dimensions", "An SVG logo needs positive width and height attributes or a viewBox.");
        return (checked((int)Math.Ceiling(boxWidth)), checked((int)Math.Ceiling(boxHeight)));
    }

    private static int? SvgLength(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.EndsWith('%')) return null;
        var text = value.EndsWith("px", StringComparison.OrdinalIgnoreCase) ? value[..^2] : value;
        return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number > 0
            ? checked((int)Math.Ceiling(number)) : null;
    }

    private static bool IsPng(byte[] bytes) => bytes.AsSpan().StartsWith(PngSignature);
    private static bool IsJpeg(byte[] bytes) => bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff;
    private static bool LooksLikeSvg(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.AsSpan().IndexOf((byte)0) >= 0) return false;
        var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        return text.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase);
    }
    private static bool SafeHref(string value) => value.StartsWith('#')
        || value.StartsWith("data:image/png;base64,", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("data:image/jpeg;base64,", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("data:image/gif;base64,", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("data:image/webp;base64,", StringComparison.OrdinalIgnoreCase);
    private static bool SafePaint(string value)
    {
        var text = value.Trim();
        var firstUrl = text.IndexOf("url(", StringComparison.OrdinalIgnoreCase);
        if (firstUrl < 0) return true;
        return firstUrl == 0 && text.StartsWith("url(#", StringComparison.OrdinalIgnoreCase) && text.EndsWith(')')
            && text.IndexOf("url(", 4, StringComparison.OrdinalIgnoreCase) < 0;
    }

    private static TenantAssetRow Row(TenantAsset row) => new(row.AssetId, row.Kind, row.LegalEntityId, row.MediaType,
        row.Sha256, row.Width, row.Height, row.Bytes.Length, row.UploadedBy, row.UploadedAt, row.OriginalFileName);
    private static string? FileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var name = Path.GetFileName(value.Trim());
        return name.Length <= 255 ? name : name[..255];
    }
    private static CommandRefusedException Refused(string rule, string message) => new(rule, message);

    private const string SvgNamespace = "http://www.w3.org/2000/svg";
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly HashSet<byte> SofMarkers = [0xc0, 0xc1, 0xc2, 0xc3, 0xc5, 0xc6, 0xc7, 0xc9, 0xca, 0xcb, 0xcd, 0xce, 0xcf];
    private static readonly HashSet<string> AllowedElements = new(StringComparer.Ordinal)
    {
        "svg", "g", "path", "rect", "circle", "ellipse", "line", "polyline", "polygon", "text", "tspan",
        "defs", "linearGradient", "radialGradient", "stop", "clipPath", "mask", "image", "use", "title", "desc",
    };
    private static readonly HashSet<string> AllowedAttributes = new(StringComparer.Ordinal)
    {
        "id", "class", "x", "y", "x1", "y1", "x2", "y2", "cx", "cy", "r", "rx", "ry", "width", "height",
        "viewBox", "preserveAspectRatio", "d", "points", "transform", "fill", "stroke", "stroke-width", "stroke-linecap",
        "stroke-linejoin", "opacity", "fill-opacity", "stroke-opacity", "font-family", "font-size", "font-weight",
        "text-anchor", "dominant-baseline", "offset", "stop-color", "stop-opacity", "clip-path", "mask", "href",
    };
    private static readonly HashSet<string> UrlAttributes = new(StringComparer.Ordinal) { "fill", "stroke", "clip-path", "mask" };
}

internal sealed record InspectedAsset(string MediaType, byte[] Bytes, int Width, int Height);

public static class TenantAssetUploadHandler
{
    public static Task<TenantAssetUploadResult> Handle(UploadTenantLogo command, TenantAssetService service,
        CancellationToken cancellationToken) => service.UploadLogoAsync(command.CommandId, command.Bytes,
        command.LegalEntityId, command.ActorRef, command.OriginalFileName, cancellationToken);
}
