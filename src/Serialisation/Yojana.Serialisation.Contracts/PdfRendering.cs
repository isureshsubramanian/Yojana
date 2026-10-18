namespace Yojana.Serialisation.Contracts;

/// <summary>The complete print contract crossing the certificate-to-render-worker boundary.</summary>
public sealed record PdfPageSpec(string Size, string Orientation, decimal MarginTopMm, decimal MarginRightMm,
    decimal MarginBottomMm, decimal MarginLeftMm, string? HeaderHtml, string? FooterHtml, bool ShowPageNumbers = true);

public sealed record PdfRenderRequest(string Html, PdfPageSpec Page);

/// <summary>Swappable PDF boundary; the certificate module never knows which browser engine fulfils it.</summary>
public interface IPdfRenderer
{
    Task<byte[]> RenderAsync(PdfRenderRequest request, CancellationToken cancellationToken);
}

/// <summary>Optional composite-output seam used by selection runs without changing per-certificate rendering.</summary>
public interface ICertificateBundleRenderer
{
    Task<CertificateRenderResult> RenderBundleAsync(IReadOnlyList<CertificateRenderRequest> requests,
        CancellationToken cancellationToken);
}
