namespace Yojana.Platform.Contracts.Time;

/// <summary>
/// The source of "now". Both business time (when something happened) and recorded time (when it was
/// written) are UTC <c>datetime2(3)</c> values; conversions to a site's local calendar live with the
/// rule that needs them (ADR-0001 case 112), never here.
/// </summary>
public interface IClock
{
    DateTime UtcNow { get; }
}
