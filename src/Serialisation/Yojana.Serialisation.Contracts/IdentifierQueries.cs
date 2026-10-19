namespace Yojana.Serialisation.Contracts;

/// <summary>Narrow identity lookup used by the integration resequencer; no serialisation table shape crosses the boundary.</summary>
public sealed record ResolveKnownSerials(IReadOnlyList<string> Serials);
public sealed record ResolveKnownSerialsResult(IReadOnlySet<string> Known, IReadOnlySet<string> Unknown);
