using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;

namespace Ryan6Vrc.AvatarTools.Tests
{
    // A SECOND, INDEPENDENT reading of a built controller's graph — the counterweight to FixpointOracle.
    //
    // Every existing round-trip gate is decode(X) == decode(Y) through the one oracle, so a property
    // ControllerDecompile does not model is dropped IDENTICALLY on both sides and the comparison stays green.
    // That is not a weak spot in the assertions; it is structural, and it is how a cross-machine defaultState
    // reached a perfect textual fixpoint while the two controllers booted different states. One lens agreeing
    // with itself is not a theorem.
    //
    // So the digest deliberately does NOT reuse any part of ControllerDecompile. It runs ReportController
    // (agent-tools) — a separately-authored walker of the same AnimatorController, written for humans reading a
    // digest rather than for round-tripping — and compares its rendering. The value is precisely that nothing
    // in this file knows what the schema models: a field the schema forgets tomorrow still shows up here.
    //
    // Read it as: "two unrelated readers of this graph describe it the same way." Where they disagree, one of
    // them is losing something, and which one is the interesting question.
    internal static class ControllerGraphDigest
    {
        // ReportController returns its one-line summary and writes the body to a Snapshot; the body is the part
        // worth comparing. Two lines vary with the HOST rather than the graph and are normalized out: the asset
        // path (a scratch folder that differs per compile) and the controller's own name (C0 and C1 are
        // deliberately different assets). Everything else — layers, states, motions, ladders, behaviours, and
        // the default-state line this exists for — is graph content.
        internal static string Of(AnimatorController c)
        {
            string summary = Ryan6Vrc.AgentTools.Editor.ReportController.Report(c);
            var m = Regex.Match(summary, @"log=(\S+)");
            Assert.IsTrue(m.Success, "ReportController did not report a log path — got: " + summary);
            string path = m.Groups[1].Value;

            string body = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), path));
            body = Regex.Replace(body, @"^asset: .*$", "asset: <normalized>", RegexOptions.Multiline);
            body = Regex.Replace(body, @"^# ReportController: .*$", "# ReportController: <normalized>", RegexOptions.Multiline);
            // The controller name also appears inside the params-asset / menu references some fixtures carry.
            body = body.Replace(c.name, "<controller>");
            return body;
        }

        // Assert two controllers describe the same graph. `what` names the step under test (e.g. "raw -> owned"),
        // because a digest diff is only meaningful against the step that produced it.
        internal static void AssertSameGraph(AnimatorController expected, AnimatorController actual, string what)
        {
            string a = Of(expected), b = Of(actual);
            if (a == b) return;

            // Name the first differing line rather than dumping two multi-kilobyte digests: on the defect this
            // gate was built for, that line IS the finding ("Entry -> `Neutral` (default)" vs "`Disable`").
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
