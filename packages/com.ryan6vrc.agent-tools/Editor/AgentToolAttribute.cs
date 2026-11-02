using System;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// Marks a class as a curated workspace tool: one row in the meta-repo tool inventory
    /// (TOOLS.md), keyed by the type's name however many doors it grows. Presence is the whole
    /// signal; the tool's purpose is authored in TOOLS.md. One tool per class.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class AgentToolAttribute : Attribute
    {
    }
}
