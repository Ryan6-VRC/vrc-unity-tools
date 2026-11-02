using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Ryan6VRC.AgentTools.Tests")]

// avatar-tools' RenderThumbnail reaches the internal AvatarBake through this; keep AvatarBakeScope's
// read-then-close protocol off the public surface.
[assembly: InternalsVisibleTo("Ryan6VRC.AvatarTools.Editor")]
