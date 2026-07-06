using System;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// Marks a class as a curated workspace tool — one row in the meta-repo tool inventory
    /// (TOOLS.md), keyed by the decorated type's name. Presence is the whole signal: the tool's
    /// one-line purpose is authored in TOOLS.md, not here. Menu / CLI / MCP / UI are doors, never
    /// the key — a tool is tagged once and keeps its identity no matter how many doors it grows.
    /// One tool per class; a type that would host two distinct tools should be split instead.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class AgentToolAttribute : Attribute
    {
    }
}
