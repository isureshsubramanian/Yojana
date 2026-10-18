namespace Yojana.Serialisation.Certificates;

/// <summary>Bounded synchronous certificate-run settings (section <c>Serialisation:Certificates</c>).</summary>
public sealed class CertificateRunOptions
{
    public const string SectionName = "Serialisation:Certificates";

    /// <summary>Selections above this count are refused before a run receipt or certificate is written.</summary>
    public int MaximumSynchronousUnits { get; set; } = 2_000;

    /// <summary><c>html</c> keeps the dependency-free fallback; <c>pdf</c> sends inert HTML to the configured worker.</summary>
    public string Renderer { get; set; } = "html";

    public string? PdfWorkerBaseUrl { get; set; }

    public int PdfRequestTimeoutSeconds { get; set; } = 60;
}

/// <summary>Chunk lifecycle seam for diagnostics; observers do not own transaction or retry policy.</summary>
public interface ICertificateRunChunkObserver
{
    Task StartingAsync(Guid commandId, int chunkNumber, int unitCount, CancellationToken cancellationToken);
}

internal sealed class NullCertificateRunChunkObserver : ICertificateRunChunkObserver
{
    public Task StartingAsync(Guid commandId, int chunkNumber, int unitCount, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
