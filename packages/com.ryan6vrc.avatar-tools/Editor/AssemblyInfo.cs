using System.Runtime.CompilerServices;

// The tool-owned layout constants/grid on ControllerEmit are internal. The separate test assembly
// (Ryan6VRC.AvatarTools.Tests) asserts against those internal grid/constant members, so this IVT
// grants it access; same-assembly code needs no IVT.
[assembly: InternalsVisibleTo("Ryan6VRC.AvatarTools.Tests")]
