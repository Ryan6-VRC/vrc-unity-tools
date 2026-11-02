using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;

namespace Ryan6Vrc.AvatarTools.Tests
{
    // A second reading of a built controller's graph, independent of FixpointOracle. The round-trip gates compare
    // decode(X) == decode(Y) through ControllerDecompile, so a property it does not model drops identically on
    // both sides and the comparison stays green. The digest therefore compares ReportController's rendering
    // (agent-tools, a separately written walker) and must not reuse any part of ControllerDecompile.
    internal static class ControllerGraphDigest
    {
        // Compares ReportController's Snapshot body, not its one-line summary. The asset path (a scratch folder
        // per compile) and the controller's name (the two sides are different assets) vary with the host rather
        // than the graph, so they are normalized out.
        internal static string Of(AnimatorController c)
        {
            string summary = Ryan6Vrc.AgentTools.Editor.ReportController.Run(c);
            var m = Regex.Match(summary, @"log=(\S+)");
            Assert.IsTrue(m.Success, "ReportController did not report a log path — got: " + summary);
            string path = m.Groups[1].Value;

            string body = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), path));
            body = Regex.Replace(body, @"^asset: .*$", "asset: <normalized>", RegexOptions.Multiline);
            body = Regex.Replace(body, @"^# ReportController: .*$", "# ReportController: <normalized>", RegexOptions.Multiline);
            body = body.Replace(c.name, "<controller>");
            return body + DefaultStatePaths(c);
        }

        // ReportController renders a default as the carrying machine's path plus the target's bare name
        // ("Entry → `Input RightHand/Neutral` (default)"), so a rebuild booting `Preset 3/Neutral` instead of
        // `Preset 0/Neutral` would render byte-identically. The digest therefore appends each default's full
        // owner path.
        private static string DefaultStatePaths(AnimatorController c)
        {
            var sb = new System.Text.StringBuilder("\n## Default state paths\n");
            foreach (var layer in c.layers)
                Walk(layer.stateMachine, layer.name, layer.name);
            return sb.ToString();

            void Walk(AnimatorStateMachine sm, string path, string layerName)
            {
                if (sm == null) return;
                sb.Append(path).Append(" -> ").Append(OwnerPath(sm.defaultState, c, layerName) ?? "<none>").Append('\n');
                foreach (var child in sm.stateMachines)
                    if (child.stateMachine != null)
                        Walk(child.stateMachine, path + "/" + child.stateMachine.name, layerName);
            }
        }

        private static string OwnerPath(AnimatorState target, AnimatorController c, string layerName)
        {
            if (target == null) return null;
            foreach (var layer in c.layers)
            {
                if (layer.name != layerName) continue;
                string found = Search(layer.stateMachine, layer.name);
                if (found != null) return found;
            }
            return "<outside this layer>";

            string Search(AnimatorStateMachine sm, string path)
            {
                if (sm == null) return null;
                foreach (var cs in sm.states)
                    if (cs.state == target) return path + "/" + cs.state.name;
                foreach (var child in sm.stateMachines)
                {
                    if (child.stateMachine == null) continue;
                    var r = Search(child.stateMachine, path + "/" + child.stateMachine.name);
                    if (r != null) return r;
                }
                return null;
            }
        }

        internal static void AssertSameGraph(AnimatorController expected, AnimatorController actual, string what)
        {
            string a = Of(expected), b = Of(actual);
            if (a == b) return;

            var la = a.Split('\n');
            var lb = b.Split('\n');
            int i = 0;
            while (i < la.Length && i < lb.Length && la[i] == lb[i]) i++;
            string firstA = i < la.Length ? la[i] : "<end of digest>";
            string firstB = i < lb.Length ? lb[i] : "<end of digest>";
            Assert.Fail($"graph digest differs across {what} at line {i + 1} — the two controllers are not the "
                + $"same graph even if their decoded YAML agrees.\n  expected: {firstA}\n  actual:   {firstB}");
        }
    }
}
