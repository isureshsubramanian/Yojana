namespace Yojana.Platform.Contracts.Security;

/// <summary>
/// Deployment boundary for global user secrets. The returned value is a self-describing envelope; callers persist it
/// unchanged and bind each use to a stable purpose.
/// </summary>
public interface IUserSecretProtector
{
    Task<byte[]> ProtectAsync(ReadOnlyMemory<byte> plaintext, string purpose,
        CancellationToken cancellationToken);

    Task<byte[]> UnprotectAsync(ReadOnlyMemory<byte> sealedValue, string purpose,
        CancellationToken cancellationToken);
}

/// <summary>Renders an enrolment value without exposing Platform to a barcode implementation.</summary>
public interface IQrCodeRenderer
{
    /// <summary>Returns a self-contained SVG document safe to place in an image data URL.</summary>
    Task<string> RenderSvgAsync(string content, CancellationToken cancellationToken);
}
