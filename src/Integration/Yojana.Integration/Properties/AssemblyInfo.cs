using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Yojana.Integration.Tests")]
[assembly: InternalsVisibleTo("Yojana.Integration.DatabaseTests")]
// The rehearsal tool is deliberately coupled to Integration internals so it drives the production source protocol
// and pipeline. Removing this friendship without a public rehearsal port would let the benchmark schema drift.
[assembly: InternalsVisibleTo("Yojana.Bench")]
