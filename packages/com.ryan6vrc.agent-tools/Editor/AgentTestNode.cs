using UnityEngine;

namespace Ryan6Vrc.AgentTools.Editor
{
    // Test-only fixture for AgentSelfTest's followAssets cases. Its own file so Unity generates a
    // MonoScript — a nested type has none, and AssetDatabase can't persist/reload a MonoScript-less
    // ScriptableObject. The self-referential children array builds cycles, chains, and fan-outs.
    internal sealed class AgentTestNode : ScriptableObject
    {
        public string label;
        public AgentTestNode[] children;
    }
}
