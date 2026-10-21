using Yojana.Platform.Contracts.Security;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Labels;

/// <summary>
/// Platform-facing QR adapter. ZXing and its symbol contract remain inside Serialisation; the SVG only embeds the
/// existing self-contained PNG so account enrolment does not gain a second barcode implementation.
/// </summary>
public sealed class QrCodeRenderer(IBarcodeEncoder encoder) : IQrCodeRenderer
{
    public async Task<string> RenderSvgAsync(string content, CancellationToken cancellationToken)
    {
        var encoded = await encoder.EncodeAsync(LabelSymbology.Qr, content, cancellationToken);
        return $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {encoded.Width} {encoded.Height}\" " +
               $"role=\"img\" aria-label=\"Authenticator enrolment QR code\"><image width=\"{encoded.Width}\" " +
               $"height=\"{encoded.Height}\" href=\"{encoded.DataUrl}\"/></svg>";
    }
}
