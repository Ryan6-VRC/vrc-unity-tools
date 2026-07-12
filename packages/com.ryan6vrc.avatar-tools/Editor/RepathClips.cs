// RepathClips — deterministic, segment-safe repath of an AnimatorController's OWNED clip bindings.
//
// The stock AnimationUtility rebind idiom (read curve → clear the old binding → set binding.path →
// write the curve on the new binding) is EXTRACTED from hfcRed/Animation-Repathing
// (Editor/ARManual.cs — ScanInvalidPaths / RenameInvalidPaths), MIT-licensed. It is reimplemented here
// against TransplantCore with: a SEGMENT-SAFE prefix match replacing that project's naive
// Contains/Replace prefix mode; an owned-clips-only ownership guard; a curve-collision guard; and a
// write-landed read-back. This file does NOT depend on or call com.hfcred.animationrepathing.
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
    /// <summary>
    /// Repath the EditorCurveBinding paths of the clips an <see cref="AnimatorController"/> references,
    /// applying a caller-supplied set of segment-safe <c>oldPath → newPath</c> moves. Directed (the caller
    /// already knows the moves — this is not a discovery tool); on-disk-<c>.anim</c>-asset-only; owned-clips
    /// -only (a read-only clip a move would touch FAILs loud unless <c>force</c>). Both float curves
    /// (<see cref="AnimationUtility.GetCurveBindings"/>) and objectReference curves
    /// (<see cref="AnimationUtility.GetObjectReferenceCurveBindings"/>) are rewritten. The clip set is the
    /// shared <see cref="AnimatorClipWalk.CollectClips"/> traversal (states + nested blend trees +
    /// sub-state-machines at any depth + synced-layer overrides; deduped).
    ///
    /// A clip is mutated as a shared <c>.anim</c> ASSET IN PLACE — every referrer of that clip sees the
    /// rewrite; each mutated clip is named on the RunLog. Idempotency is PASS with 0 rewrites (a re-run
    /// finds no matching oldPath). Call <see cref="Run(AnimatorController, Move[], bool, bool)"/> from MCP
    /// execute_code (the flat <c>string[] oldPaths, string[] newPaths</c> overload is the ergonomic door).
    ///
    /// <para><b>Frame boundary — the CALLER owns frame-correctness.</b> This is a single-controller,
    /// literal-string, FRAME-BLIND rewriter: it matches and rewrites <c>oldPath</c> as a raw string and has
    /// no avatar root. Across a real avatar the same bone lives under DIFFERENT path frames per animator —
    /// descriptor layers are avatar-root-relative; a Modular Avatar MergeAnimator carries its own
    /// <c>pathMode</c> (Relative/Absolute) + <c>relativePathRoot</c>; a VRCFury FullController is
    /// mount-relative with its own rewrite settings (<c>rootBindingsApplyToAvatar</c> / <c>removePrefixes</c>
    /// / <c>addPrefix</c> / <c>rootObjOverride</c>), and VRCFury may even MIX absolute + relative bindings
    /// within one controller (relative wins on ambiguity). The caller must iterate the avatar's animators and
    /// express each controller's moves in THAT controller's frame; this tool cannot resolve
    /// same-string-different-frame bindings and rewrites them together (the curve-collision guard fails loud
    /// on any same-target collision, unless <c>force</c>). Driving it blindly across a multi-animator avatar assuming
    /// avatar-root-relative paths will silently corrupt the mount-relative controllers.</para>
    /// </summary>
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

        // ── Public API ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Ergonomic door for execute_code (parallel arrays, fully-qualified, fail-loud — no custom-struct literals needed):
        /// two parallel arrays zipped into <see cref="Move"/>[]. FAILs loud on a length mismatch.
        /// </summary>
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

        /// <summary>
        /// The core. Returns a one-line PASS/FAIL summary ending with the RunLog path
        /// (<c>… => RESULT | log=&lt;path&gt;</c>); also Debug.Log/LogError it.
        /// </summary>
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
                // ── Precondition: empty/null/duplicate oldPath in moves[] (static, before any read).
                //    An empty oldPath would match the path=="" muscle/root bindings (type=Animator) and
                //    corrupt them; an empty newPath malforms paths ("/Hips"). Neither is a repath target. ──
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

                // ── Deduped clip set from the shared motion-slot walk (one grammar with OwnControllerClips) ──
                var clips = AnimatorClipWalk.CollectClips(controller);
                log.Count("clipsScanned", clips.Count);

                // ── Build the plan: per clip, the longest-oldPath-wins rewrite of each matching binding
                //    (matched ONCE against the ORIGINAL path — no cascade) ──
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
                        if (rewritten == b.path) return; // degenerate (oldPath == newPath) — no rewrite
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

                // ── Ownership guard (per touched clip) ──
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

                // ── Curve-collision guard (per touched clip): a rewritten target already occupied by a
                //    curve that stays, or two moves collapsing two sources onto one target ──
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

                // ── Stale-move warnings (still PASS): a move that matched nothing AND whose newPath is
                //    absent from the walked clips. A zero-match move whose newPath already resolves is
                //    idempotent-satisfied → suppressed. ──
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

                // Per-clip audit: name every mutated clip + its segment-level rewrites (whatIf and execute).
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

                // ── Blocking guards FAIL the same for whatIf and execute (preview == execute verdict) ──
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

                // ── Execute: rebind idiom, batched; capture-all → clear-all → write-all per clip so
                //    swaps/permutations don't clobber an unread source curve ──
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
                AssetDatabase.SaveAssets();

                // ── Write-landed read-back (force NEVER bypasses this): reimport each touched clip from
                //    disk and assert the rewritten CURVE CONTENT actually landed — not just that a binding key
                //    is present. Key-presence alone is defeated by a cyclic permutation (A↔B) forced onto an
                //    immutable clip: every old key is also a new key and every new key pre-existed, so nothing
                //    written still "passes." Comparing the reloaded curve to exactly what we captured/wrote is
                //    deterministic and catches the silent no-op (Set…Curve is void and never throws on an
                //    immutable Packages/ write). ──
                int landedFailures = 0;
                foreach (var cp in plan)
                {
                    // Reload a fresh handle from disk so the read-back is unambiguously disk-sourced (and
                    // robust if a hard reimport re-mints the native object).
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

        // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

        /// <summary>Segment-safe prefix match: <paramref name="bindingPath"/> equals
        /// <paramref name="oldPath"/> or is a <c><paramref name="oldPath"/>/…</c> descendant. Renaming
        /// <c>Armature/Hips</c> matches <c>Armature/Hips</c> and <c>Armature/Hips/Spine</c>, never
        /// <c>Armature/HipsFoo</c>.</summary>
        static bool SegmentMatch(string bindingPath, string oldPath)
        {
            if (oldPath == null || bindingPath == null) return false;
            if (bindingPath == oldPath) return true;
            return bindingPath.StartsWith(oldPath + "/", StringComparison.Ordinal);
        }

        /// <summary>Stable identity of a binding within a clip: path + declaring type + property +
        /// float/PPtr discriminant. Two bindings collide iff their keys are equal.</summary>
        static string Key(EditorCurveBinding b)
            => b.path + "\0" + (b.type != null ? b.type.FullName : "?") + "\0" + b.propertyName + "\0" + (b.isPPtrCurve ? "1" : "0");

        /// <summary>Route an argument-guard failure through the house RunLog grammar (summary + RunLog +
        /// LogError), like the sibling transplant tools — never a bare trailer-less line.</summary>
        static string ArgFail(string label, string msg)
        {
            var log = new RunLog("repath-clips") { result = "FAIL", error = msg };
            log.Offender(msg);
            return TransplantCore.Finish(log, label);
        }

        /// <summary>Deterministic equality of the reloaded float curve against exactly what we wrote:
        /// keyframe count then each key's time/value/tangents/weights/weightedMode. Round-tripping the same
        /// values through Unity serialization is bit-stable, so exact comparison is correct (not float-guessing).</summary>
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

        /// <summary>Exact equality, but NaN-tolerant (raw <c>NaN != NaN</c>) so a genuine NaN keyframe that
        /// round-trips a landed write is not a false content-mismatch. We compare against exactly what we
        /// wrote, so bit-exact is otherwise correct.</summary>
        static bool FloatEq(float x, float y) => (float.IsNaN(x) && float.IsNaN(y)) || x == y;

        /// <summary>Deterministic equality of the reloaded object-reference curve: keyframe count then each
        /// key's time + referenced object.</summary>
        static bool ObjCurvesEqual(ObjectReferenceKeyframe[] a, ObjectReferenceKeyframe[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i].time != b[i].time || a[i].value != b[i].value) return false;
            return true;
        }

        // ── Plan types ──────────────────────────────────────────────────────────────────────────────

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
