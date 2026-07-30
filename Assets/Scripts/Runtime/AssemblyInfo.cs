using System.Runtime.CompilerServices;

// The avatar layer keeps its moving parts internal — scene phases, the
// expression accumulator, the VRM driver. Tests reach them through here rather
// than by widening the public surface for the sake of coverage.
[assembly: InternalsVisibleTo("NeonCompanion.EditModeTests")]
