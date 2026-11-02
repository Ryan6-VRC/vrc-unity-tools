// The AnimationUtility rebind idiom (read the curve, clear the old binding, write the curve on the new path)
// is extracted from hfcRed/Animation-Repathing (Editor/ARManual.cs: ScanInvalidPaths, RenameInvalidPaths) and
// reimplemented here, with a segment-safe prefix match in place of that project's Contains/Replace prefix mode.
//
// hfcRed/Animation-Repathing © hfcRed — MIT License (https://github.com/hfcRed/Animation-Repathing).

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>Repath the binding paths of the clips a controller references
    /// (<see cref="AnimatorClipWalk.CollectClips"/>), rewriting the shared <c>.anim</c> assets in place from
    /// caller-supplied segment-safe moves. Frame-blind: express the moves in this controller's binding frame,
    /// because a sweep assuming avatar-root-relative paths silently corrupts mount-relative controllers.
    /// Contract: docs/animator.md.</summary>
    [AgentTool]
    public static class RepathClips
    {
        /// <summary>One repath: rewrite bindings whose path equals <see cref="oldPath"/> or is a
        /// <see cref="oldPath"/><c>/…</c> descendant, replacing the matched prefix with <see cref="newPath"/>.</summary>
        public struct Move
        {
            public string oldPath;
            public string newPath;
            public Move(string oldPath, string newPath) { this.oldPath = oldPath; this.newPath = newPath; }
        }

        /// <summary>The door for execute_code: parallel arrays in place of <see cref="Move"/> literals.</summary>
        public static string Run(AnimatorController controller, string[] oldPaths, string[] newPaths, bool force = false, bool whatIf = false)
        {
            string doorLabel = controller != null ? TransplantCore.Sanitize(controller.name) : "null-controller";
            if (oldPaths == null || newPaths == null) return ArgFail(doorLabel, "oldPaths/newPaths is null");
            if (oldPaths.Length != newPaths.Length)
                return ArgFail(doorLabel, "oldPaths.Length (" + oldPaths.Length + ") != newPaths.Length (" + newPaths.Length + ")");
            var moves = new Move[oldPaths.Length];
            for (int i = 0; i < oldPaths.Length; i++) moves[i] = new Move(oldPaths[i], newPaths[i]);
            return Run(controller, moves, force, whatIf);
        }

        public static string Run(AnimatorController controller, Move[] moves, bool force = false, bool whatIf = false)
        {
            if (controller == null) return ArgFail("null-controller", "controller is null");
            if (moves == null || moves.Length == 0)
                return ArgFail(TransplantCore.Sanitize(controller.name), "moves is null or empty");

            var log = new RunLog("repath-clips")
            {
                whatIf = whatIf,
                instance = controller.name,
                source = AssetDatabase.GetAssetPath(controller),
            };
            string label = TransplantCore.Sanitize(controller.name);

            try
            {
                // An empty oldPath would match and corrupt the path=="" muscle/root bindings (type=Animator).
                var seenOld = new HashSet<string>(StringComparer.Ordinal);
                foreach (var mv in moves)
                {
                    if (string.IsNullOrEmpty(mv.oldPath))
                        log.Offender("move has empty/null oldPath — root/muscle bindings are not repath targets");
                    else if (string.IsNullOrEmpty(mv.newPath))
                        log.Offender("move '" + mv.oldPath + "' has empty/null newPath — would malform binding paths");
                    else if (!seenOld.Add(mv.oldPath))
                        log.Offender("duplicate oldPath in moves[]: '" + mv.oldPath + "'");
                }
                if (log.offenders.Count > 0)
                {
                    log.result = "FAIL";
                    log.error = "invalid moves[] (empty/null/duplicate path)";
                    return TransplantCore.Finish(log, label);
                }

                var clips = AnimatorClipWalk.CollectClips(controller);
                log.Count("clipsScanned", clips.Count);

                string ctrlDir = System.IO.Path.GetDirectoryName(log.source ?? "")?.Replace((char)92, '/');
                var strays = new List<string>();
                if (!string.IsNullOrEmpty(ctrlDir))
                    foreach (string guid in AssetDatabase.FindAssets("t:AnimationClip", new[] { ctrlDir }))
                    {
                        string cpath = AssetDatabase.GUIDToAssetPath(guid);
                        if (!cpath.EndsWith(".anim", StringComparison.OrdinalIgnoreCase) || cpath == log.source) continue;
                        if (System.IO.Path.GetDirectoryName(cpath).Replace((char)92, '/') != ctrlDir) continue;
                        var c = AssetDatabase.LoadAssetAtPath<AnimationClip>(cpath);
                        if (c != null && !clips.Contains(c)) strays.Add(TransplantCore.Leaf(cpath));
                    }
                log.Count("clipsUnreferenced", strays.Count);
                if (strays.Count > 0)
                    log.Warning("clip in controller folder not referenced by the controller, not repathed: " +
                        string.Join(", ", strays.ToArray()));

                // Per binding the longest oldPath wins, matched once against the original path, so moves do not cascade.
                var moveMatched = new int[moves.Length];
                var allBindingPaths = new HashSet<string>(StringComparer.Ordinal);
                var plan = new List<ClipPlan>();

                foreach (var clip in clips)
                {
                    var floatB = AnimationUtility.GetCurveBindings(clip);
                    var objB = AnimationUtility.GetObjectReferenceCurveBindings(clip);

                    var originalKeys = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var b in floatB) { allBindingPaths.Add(b.path); originalKeys.Add(Key(b)); }
                    foreach (var b in objB) { allBindingPaths.Add(b.path); originalKeys.Add(Key(b)); }

                    var rewrites = new List<Rewrite>();
                    System.Action<EditorCurveBinding, bool> consider = (b, isObj) =>
                    {
                        int best = -1, bestLen = -1;
                        for (int i = 0; i < moves.Length; i++)
                        {
                            if (!SegmentMatch(b.path, moves[i].oldPath)) continue;
                            int len = moves[i].oldPath.Length;
                            if (len > bestLen) { bestLen = len; best = i; }
                        }
                        if (best < 0) return;
                        moveMatched[best]++;
                        string rewritten = moves[best].newPath + b.path.Substring(moves[best].oldPath.Length);
                        if (rewritten == b.path) return;
                        var nb = b; nb.path = rewritten;
                        rewrites.Add(new Rewrite { oldB = b, newB = nb, isObj = isObj, moveIdx = best });
                    };
                    foreach (var b in floatB) consider(b, false);
                    foreach (var b in objB) consider(b, true);

                    if (rewrites.Count == 0) continue;
                    plan.Add(new ClipPlan
                    {
                        clip = clip,
                        path = AssetDatabase.GetAssetPath(clip),
                        rewrites = rewrites,
                        originalKeys = originalKeys,
                    });
                }

                foreach (var cp in plan)
                {
                    if (TransplantCore.IsWritableAsset(cp.path)) continue;
                    string leaf = TransplantCore.Leaf(cp.path);
                    if (force)
                        log.Note("read-only clip override (force): '" + leaf + "' (" + cp.path + ")");
                    else
                        log.Offender("read-only clip '" + leaf + "' (" + cp.path +
                            "): materialize an owned copy first (OwnControllerClips), or pass force=true");
                }

                foreach (var cp in plan)
                {
                    string leaf = TransplantCore.Leaf(cp.path);
                    var srcKeys = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var r in cp.rewrites) srcKeys.Add(Key(r.oldB));
                    var dstGroups = new Dictionary<string, List<Rewrite>>(StringComparer.Ordinal);
                    foreach (var r in cp.rewrites)
                    {
                        string dk = Key(r.newB);
                        List<Rewrite> g;
                        if (!dstGroups.TryGetValue(dk, out g)) { g = new List<Rewrite>(); dstGroups[dk] = g; }
                        g.Add(r);
                    }
                    foreach (var kv in dstGroups)
                    {
                        bool occupiedByStayer = cp.originalKeys.Contains(kv.Key) && !srcKeys.Contains(kv.Key);
                        bool multiSource = kv.Value.Count > 1;
                        if (!occupiedByStayer && !multiSource) continue;
                        var r0 = kv.Value[0];
                        string why = multiSource ? "2+ sources collapse onto it" : "target already occupied";
                        string msg = leaf + ": " + r0.oldB.path + " -> " + r0.newB.path +
                                     " [" + r0.newB.propertyName + "] (" + why + ")";
                        if (force) log.Note("curve collision override (force): " + msg);
                        else log.Offender("curve collision: " + msg);
                    }
                }

                // A move an earlier run already applied matches nothing but finds its newPath present, so it does not warn.
                int movesUnmatched = 0;
                for (int i = 0; i < moves.Length; i++)
                {
                    if (moveMatched[i] > 0) continue;
                    movesUnmatched++;
                    bool newPresent = false;
                    foreach (var p in allBindingPaths)
                        if (SegmentMatch(p, moves[i].newPath)) { newPresent = true; break; }
                    if (!newPresent)
                        log.Warning("move '" + moves[i].oldPath + "' -> '" + moves[i].newPath +
                            "' matched 0 bindings and newPath is absent (stale move?)");
                }

                long bindingsRewritten = 0;
                foreach (var cp in plan) bindingsRewritten += cp.rewrites.Count;
                log.Count("clipsMutated", plan.Count);
                log.Count("bindingsRewritten", bindingsRewritten);
                log.Count("movesUnmatched", movesUnmatched);

                foreach (var cp in plan)
                {
                    var pairs = new List<string>();
                    var pairSeen = new HashSet<int>();
                    foreach (var r in cp.rewrites)
                        if (pairSeen.Add(r.moveIdx))
                            pairs.Add(moves[r.moveIdx].oldPath + "->" + moves[r.moveIdx].newPath);
                    log.Note(TransplantCore.Leaf(cp.path) + ": " + string.Join(", ", pairs.ToArray()) +
                             " (" + cp.rewrites.Count + " binding(s))");
                }

                // These guards sit above the whatIf return so a preview reports the guard verdict an execute would.
                if (log.offenders.Count > 0)
                {
                    log.result = "FAIL";
                    log.error = "ownership/collision guard (pass force=true to override)";
                    return TransplantCore.Finish(log, label);
                }

                if (whatIf)
                {
                    log.result = "PASS";
                    return TransplantCore.Finish(log, label);
                }

                AssetDatabase.StartAssetEditing();
                try
                {
                    foreach (var cp in plan)
                    {
                        foreach (var r in cp.rewrites)
                        {
                            if (r.isObj) r.objCurve = AnimationUtility.GetObjectReferenceCurve(cp.clip, r.oldB);
                            else r.floatCurve = AnimationUtility.GetEditorCurve(cp.clip, r.oldB);
                        }
                        foreach (var r in cp.rewrites)
                        {
                            if (r.isObj) AnimationUtility.SetObjectReferenceCurve(cp.clip, r.oldB, null);
                            else AnimationUtility.SetEditorCurve(cp.clip, r.oldB, null);
                        }
                        foreach (var r in cp.rewrites)
                        {
                            if (r.isObj) AnimationUtility.SetObjectReferenceCurve(cp.clip, r.newB, r.objCurve);
                            else AnimationUtility.SetEditorCurve(cp.clip, r.newB, r.floatCurve);
                        }
                        EditorUtility.SetDirty(cp.clip);
                    }
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                }
                foreach (var cp in plan) AssetDatabase.SaveAssetIfDirty(cp.clip);

                // Read back under force too: Set…Curve is void, so a write forced onto an immutable clip can no-op
                // silently. Compare reloaded curve content, because a forced swap (A↔B) leaves every expected key
                // present with nothing written. An EditMode test cannot stage the no-op on this Unity and OS:
                // SaveAssetIfDirty bypasses the OS read-only attribute.
                int landedFailures = 0;
                foreach (var cp in plan)
                {
                    AssetDatabase.ImportAsset(cp.path, ImportAssetOptions.ForceUpdate);
                    var disk = AssetDatabase.LoadAssetAtPath<AnimationClip>(cp.path) ?? cp.clip;
                    var actual = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var b in AnimationUtility.GetCurveBindings(disk)) actual.Add(Key(b));
                    foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(disk)) actual.Add(Key(b));

                    var dstKeys = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var r in cp.rewrites) dstKeys.Add(Key(r.newB));

                    string leaf = TransplantCore.Leaf(cp.path);
                    foreach (var r in cp.rewrites)
                    {
                        if (!actual.Contains(Key(r.newB)))
                        {
                            landedFailures++;
                            log.Offender("write did not land in '" + leaf + "': expected binding absent after reimport (" +
                                r.newB.path + " [" + r.newB.propertyName + "]) — immutable/unwritable asset?");
                            continue;
                        }
                        bool contentOk = r.isObj
                            ? ObjCurvesEqual(AnimationUtility.GetObjectReferenceCurve(disk, r.newB), r.objCurve)
                            : FloatCurvesEqual(AnimationUtility.GetEditorCurve(disk, r.newB), r.floatCurve);
                        if (!contentOk)
                        {
                            landedFailures++;
                            log.Offender("write did not land (content mismatch) in '" + leaf + "': " +
                                r.newB.path + " [" + r.newB.propertyName + "] — reloaded curve differs from written (immutable/unwritable asset?)");
                        }
                    }
                    foreach (var r in cp.rewrites)
                    {
                        string sk = Key(r.oldB);
                        if (!dstKeys.Contains(sk) && actual.Contains(sk))
                        {
                            landedFailures++;
                            log.Offender("old binding still present in '" + leaf + "' after rewrite (" +
                                r.oldB.path + " [" + r.oldB.propertyName + "])");
                        }
                    }
                }
                log.Count("writeLandedFailures", landedFailures);

                log.result = log.offenders.Count > 0 ? "FAIL" : "PASS";
                if (log.result == "FAIL") log.error = "write-landed read-back mismatch";
            }
            catch (Exception ex)
            {
                log.result = "FAIL";
                log.error = ex.GetType().Name + ": " + ex.Message;
            }

            return TransplantCore.Finish(log, label);
        }

        static bool SegmentMatch(string bindingPath, string oldPath)
        {
            if (oldPath == null || bindingPath == null) return false;
            if (bindingPath == oldPath) return true;
            return bindingPath.StartsWith(oldPath + "/", StringComparison.Ordinal);
        }

        /// <summary>Identity of a binding within a clip: two bindings collide iff their keys are equal.</summary>
        static string Key(EditorCurveBinding b)
            => b.path + "\0" + (b.type != null ? b.type.FullName : "?") + "\0" + b.propertyName + "\0" + (b.isPPtrCurve ? "1" : "0");

        static string ArgFail(string label, string msg)
        {
            var log = new RunLog("repath-clips") { result = "FAIL", error = msg };
            log.Offender(msg);
            return TransplantCore.Finish(log, label);
        }

        static bool FloatCurvesEqual(AnimationCurve a, AnimationCurve b)
        {
            if (a == null || b == null) return a == b;
            if (a.length != b.length) return false;
            if (a.preWrapMode != b.preWrapMode || a.postWrapMode != b.postWrapMode) return false;
            for (int i = 0; i < a.length; i++)
            {
                Keyframe ka = a[i], kb = b[i];
                if (!FloatEq(ka.time, kb.time) || !FloatEq(ka.value, kb.value) ||
                    !FloatEq(ka.inTangent, kb.inTangent) || !FloatEq(ka.outTangent, kb.outTangent) ||
                    !FloatEq(ka.inWeight, kb.inWeight) || !FloatEq(ka.outWeight, kb.outWeight) ||
                    ka.weightedMode != kb.weightedMode)
                    return false;
            }
            return true;
        }

        /// <summary>Exact, because Unity serialization round-trips a written float bit-stably; NaN-tolerant (raw
        /// <c>NaN != NaN</c>) so a NaN keyframe that round-trips is not a false content mismatch.</summary>
        static bool FloatEq(float x, float y) => (float.IsNaN(x) && float.IsNaN(y)) || x == y;

        static bool ObjCurvesEqual(ObjectReferenceKeyframe[] a, ObjectReferenceKeyframe[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i].time != b[i].time || a[i].value != b[i].value) return false;
            return true;
        }

        sealed class ClipPlan
        {
            public AnimationClip clip;
            public string path;
            public List<Rewrite> rewrites;
            public HashSet<string> originalKeys;
        }

        sealed class Rewrite
        {
            public EditorCurveBinding oldB;
            public EditorCurveBinding newB;
            public bool isObj;
            public int moveIdx;
            public AnimationCurve floatCurve;
            public ObjectReferenceKeyframe[] objCurve;
        }
    }
}
