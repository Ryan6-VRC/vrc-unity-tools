using System.Runtime.CompilerServices;

// The tool-owned layout constants/grid on ControllerEmit are internal (same-assembly Task 3 decompile
// reads them as its baseline); the test assembly asserts against them too, so bridge them across.
[assembly: InternalsVisibleTo("Ryan6VRC.AvatarTools.Tests")]
