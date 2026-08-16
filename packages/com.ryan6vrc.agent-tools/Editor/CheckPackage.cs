using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// Deterministic check-package for the AI-assisted VRChat workflow.
    ///
    /// Walks prefabs (under a folder) or selected objects and reports four classes of broken
    /// reference a vendor import can leave behind:
    ///   - material slots: resolved / empty / MISSING
    ///   - renderer meshes: present / MISSING
    ///   - MonoBehaviours: missing script references
    ///   - FBX external-material remap, in two classes: entries that RESOLVE while the model imports empty
    ///     (a stale import — force-reimport), and entries that resolve to NOTHING (their .mat targets are
    ///     absent — restore or import those, then force-reimport). The remedies run in opposite order.
    ///
    /// The load-bearing distinction is MISSING vs EMPTY. A slot whose serialized reference has a
    /// non-zero instance id that fails to resolve is broken; a clean-zero slot is an intentional
    /// empty (e.g. an unused submesh). Counting raw nulls is a false-alarm trap: the empty slots that
    /// exist are almost entirely authored that way, so a null-counting verdict would fail healthy
    /// packages and catch nothing. This counts only the broken ones, so PASS/FAIL is meaningful.
    /// (The trap is real; the magnitude this comment used to claim for it — "hundreds per costume" —
    /// is not, and the census below is what an argument about empty slots should cite instead.)
    ///
    /// A MISSING count alone says how many references broke, not how many things broke: one vendor-side
    /// mistake can dangle a thousand slots at two targets. So each MISSING offender names the target its
    /// reference still points at, and the summary counts the distinct targets behind them — keyed on
    /// serialized identity (guid + fileID), which is what the file records, spanning material slots and
    /// meshes alike.
    ///
    /// The remap checks catch what the empty-vs-MISSING rule deliberately ignores: an FBX carrying an
    /// external-material remap is bound to .mat assets only at import time. Import it before those
    /// materials exist (e.g. costume package before a separate MaterialPack) and it caches empty slots
    /// that no later import re-applies — the model reads as "intentionally empty" yet renders untextured.
    /// These are the one place an empty slot is evidence, and only because the remap says something was
    /// supposed to fill it; everywhere else the empty count stays deliberately silent.
    ///
    /// That silence is a decision, measured 2026-08-15 rather than inherited. `empty=` rides every summary
    /// and Finish's predicate never reads it, which looked like a gap once R25 retired the all-bound remap
    /// verdict — so the population was censused across two projects: 163 models (1023 renderer slots) and
    /// 723 prefabs (14712 slots).
    ///
    ///   empty slots, model side     19   3 models (17 in a vendor FBX, 1 in a package FBX in both
    ///                                    projects) — every one already caught as the UNRESOLVED class
    ///   empty slots, prefab side    56   45 ParticleSystemRenderer, 10 optimizer merge targets, 1 other
    ///   map-free models with any     0   of 111
    ///
    /// So the whole population is two authoring idioms plus one residue. A particle renderer's unused trail
    /// slot is routine (ScanModelRemap already excludes ParticleSystemRenderer from its own empty count for
    /// exactly this reason; ScanMaterials does not, and should not — it reports rather than judges). A
    /// mesh-merging optimizer's destination renderer is authored empty AND meshless, to be filled at build.
    /// The one residue is a marker mesh left materialless so it draws nothing while switched on — which is
    /// indistinguishable, in serialized state, from a submesh someone forgot to bind. That is the finding:
    /// not that empty slots are too noisy to gate, but that the question a gate would answer — SHOULD this
    /// slot draw — is not recorded anywhere a static read can reach. Hence the render-sheet routing in
    /// docs/unity-tools.md, and hence no verdict here.
    ///
    /// Do not add one without a measured instance of a slot that should have drawn. It would also have to be
    /// genuinely per-slot: `notBound` above is a model-wide material-identity test and would miss the same
    /// class the retired verdict did.
    ///
    /// Prefab assets are inspected via LoadPrefabContents, which composes variant overrides in an
    /// isolated preview scene without touching the open scene. INSPECTION ONLY — never mutates.
    /// </summary>
    [AgentTool]
    public static class CheckPackage
    {
        private const string RunLogDir = RunLogFormat.RunLogDir;

        // ----- Public API (callable from execute_code / the import skill) ---------------------

        /// <summary>Verify every prefab under an asset folder. Returns a one-line PASS/FAIL summary;
        /// when a verification run was performed it ends with the RunLog path (<c>… => RESULT | log=&lt;path&gt;</c>) —
        /// a bad-input early return is a bare <c>[CheckPackage] FAIL: …</c> with no trailer.</summary>
        public static string Run(string assetFolderPath)
        {
            if (string.IsNullOrEmpty(assetFolderPath) || !AssetDatabase.IsValidFolder(assetFolderPath))
                return "[CheckPackage] FAIL: not a valid asset folder: " + assetFolderPath;

            var r = new Report { Target = assetFolderPath, Mode = "folder" };
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { assetFolderPath }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) continue;
                ScanPrefabAsset(path, r);
            }
            // A model asset gets BOTH scans, matching VerifySelection's FBX branch. It used to get only the
            // remap probe, so an FBX carrying a broken material reference on its own renderers passed here and
            // FAILed under VerifySelection on the identical asset — a false PASS, and the divergence was
            // invisible because neither mode said which scans it had run.
            foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { assetFolderPath }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (model != null) { r.Scanned++; ScanHierarchy(model, path, r); }
                ScanModelRemap(path, r);
            }
            return Finish(r, Leaf(assetFolderPath));
        }

        /// <summary>Verify selected prefab assets and/or scene GameObjects.</summary>
        public static string VerifySelection()
        {
            var objs = Selection.gameObjects;
            if (objs == null || objs.Length == 0)
            {
                Debug.LogWarning("[CheckPackage] Nothing selected.");
                return "[CheckPackage] FAIL: nothing selected.";
            }

            var r = new Report { Target = "selection", Mode = "selection" };
            foreach (var go in objs)
            {
                if (EditorUtility.IsPersistent(go))
                {
                    var path = AssetDatabase.GetAssetPath(go);
                    if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                        ScanPrefabAsset(path, r);
                    else { r.Scanned++; ScanHierarchy(go, path, r); ScanModelRemap(path, r); } // model prefab / FBX asset
                }
                else { r.Scanned++; ScanHierarchy(go, "scene:" + go.scene.name, r); }
            }
            return Finish(r, "selection");
        }

        // ----- Scanning -----------------------------------------------------------------------

        private static void ScanPrefabAsset(string assetPath, Report r)
        {
            r.Scanned++;
            GameObject root;
            try { root = PrefabUtility.LoadPrefabContents(assetPath); }
            catch (Exception e)
            {
                r.LoadErrors++;
                r.Offenders.Add(new Offender { Location = assetPath, ObjectPath = "", Kind = "load-error", Detail = e.Message });
                return;
            }
            try { ScanHierarchy(root, assetPath, r); }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        private static void ScanHierarchy(GameObject root, string location, Report r)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                var go = t.gameObject;
                string goPath = HierarchyPath(t);

                int missingScripts = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
                if (missingScripts > 0)
                {
                    r.ScriptsMissing += missingScripts;
                    r.Offenders.Add(new Offender { Location = location, ObjectPath = goPath, Kind = "script-missing", Detail = missingScripts + " missing MonoBehaviour(s)" });
                }

                foreach (var rend in go.GetComponents<Renderer>())
                {
                    ScanMaterials(rend, location, goPath, r);
                    var smr = rend as SkinnedMeshRenderer;
                    if (smr != null) ScanMeshRef(new SerializedObject(smr), location, goPath, "SkinnedMeshRenderer", r);
                }
                var mf = go.GetComponent<MeshFilter>();
                if (mf != null) ScanMeshRef(new SerializedObject(mf), location, goPath, "MeshFilter", r);
            }
        }

        private static void ScanMaterials(Renderer rend, string location, string goPath, Report r)
        {
            var arr = new SerializedObject(rend).FindProperty("m_Materials");
            if (arr == null || !arr.isArray) return;
            for (int i = 0; i < arr.arraySize; i++)
            {
                var el = arr.GetArrayElementAtIndex(i);
                if (el.objectReferenceValue != null) r.MatResolved++;
                else if (el.objectReferenceInstanceIDValue != 0)
                {
                    r.MatMissing++;
                    r.Offenders.Add(new Offender { Location = location, ObjectPath = goPath, Kind = "material-missing", Detail = "material slot " + i + " holds a dangling reference: " + DanglingTarget(el.objectReferenceInstanceIDValue, r) });
                }
                else r.MatEmpty++; // intentional empty submesh slot
            }
        }

        private static void ScanMeshRef(SerializedObject so, string location, string goPath, string compName, Report r)
        {
            var p = so.FindProperty("m_Mesh");
            if (p == null || p.objectReferenceValue != null) return;     // no slot, or mesh present
            if (p.objectReferenceInstanceIDValue == 0) return;           // intentionally no mesh
            r.MeshesMissing++;
            r.Offenders.Add(new Offender { Location = location, ObjectPath = goPath, Kind = "mesh-missing", Detail = compName + " mesh holds a dangling reference: " + DanglingTarget(p.objectReferenceInstanceIDValue, r) });
        }

        // Names what a broken reference still points at, which decides the remedy: a guid that resolves
        // to a real file means the reference aims at a sub-object that file does not contain (an FBX set
        // to external materials never creates the material sub-objects its prefab variants override, so
        // the fix is at the referencing end), while an absent guid means the asset was never imported.
        //
        // Also tallies the target, so the run can report how many things broke rather than only how many
        // slots did. Memoized per instance id: the case this exists for — a thousand slots aiming at two
        // targets — would otherwise pay a thousand AssetDatabase round-trips to build two strings.
        private static string DanglingTarget(int instanceId, Report r)
        {
            if (!r.TargetCache.TryGetValue(instanceId, out var t))
            {
                // Only the long-localId overload is usable; the int one throws unconditionally.
                bool mapped = AssetDatabase.TryGetGUIDAndLocalFileIdentifier(instanceId, out string guid, out long fileId);
                t = new TargetInfo
                {
                    Mapped = mapped,
                    // Serialized identity, not the in-memory handle: counting distinct targets is only
                    // meaningful against what the file records. An unmapped id has nothing else to key on.
                    Key = mapped ? guid + "/" + fileId : "instanceID:" + instanceId,
                    Detail = DescribeTarget(instanceId, mapped, guid, fileId,
                        mapped ? AssetDatabase.GUIDToAssetPath(guid) : null,
                        mapped && RunLogFormat.AssetGuidResolves(guid))
                };
                r.TargetCache[instanceId] = t;
            }
            r.DanglingTargets.Add(t.Key);
            if (!t.Mapped) r.UnidentifiedTargets.Add(t.Key);
            return t.Detail;
        }

        /// <summary>Pure wording for the four shapes the answer takes, so each is assertable without
        /// having to provoke a real dangling reference. A guid mapping does survive for an instance id
        /// whose object fails to load, so the unmapped branch is rare residue rather than the normal
        /// path — and it is the only one whose target key can over-count, having nothing but the
        /// in-memory handle to key on.
        ///
        /// <paramref name="assetExists"/> is why a path alone cannot word this: a deleted asset's GUID
        /// keeps resolving to its old path (<see cref="RunLogFormat.AssetGuidResolves"/>), so wording every
        /// non-empty path as "holds no object with fileID N" hunts a sub-asset in a file that is gone.</summary>
        internal static string DescribeTarget(int instanceId, bool mapped, string guid, long fileId, string assetPath,
                                              bool assetExists)
        {
            if (!mapped) return "instanceID " + instanceId + " has no guid mapping";
            if (string.IsNullOrEmpty(assetPath)) return "guid " + guid + " is absent from this project";
            return assetExists
                ? "guid " + guid + " resolves to " + assetPath + ", which holds no object with fileID " + fileId
                : "guid " + guid + " was " + assetPath + ", which no longer resolves (deleted, moved, or failed to import)";
        }

        // An FBX's external-material remap is applied only at import time, so a model imported before its
        // materials existed caches empty slots that no later import re-applies — it reads "intentionally
        // empty" yet renders untextured.
        //
        // This used to gate on `materialLocation == External`, which is the LEGACY extract-to-files mode.
        // Modern Unity remaps through the external-object map while materialLocation stays `InPrefab`, so
        // that gate silently disabled the whole check: measured 2026-08-08, all 69 map-carrying models in
        // AvatarProject's vendor tree are `InPrefab`, i.e. nothing below had ever run. The external-object
        // map is what this check actually depends on, so the map is what it gates on.
        //
        // Two classes, because their remedies run in OPPOSITE ORDER and doing the wrong one is a no-op:
        //   entries resolve, slots still empty -> stale import; force-reimport the FBX.
        //   entries resolve to nothing         -> the material pack was never imported; import it FIRST,
        //                                         then force-reimport.
        //
        // Scope bound, stated because the message cannot: this asserts "the model carries an external-material
        // map AND has empty slots", not "this particular slot is the one the map should have filled" — the
        // importer's source-material list does not index-align with renderer slots (measured: 2 entries against
        // 10 empty slots), so a per-slot attribution would be an assumption, not a reading. The 15/69 census
        // taken when this check first ran was one untextured outfit family, every member the UNRESOLVED class.
        //
        // What IS readable is the entry: a mapped material either appears on some renderer of this model or it
        // does not, and the walk below already has both halves in hand. So the message names the entries that
        // landed nowhere rather than every entry in the map — with a dozen entries and two genuine misses, the
        // undifferentiated list left the reader to re-derive the partition the scan had already computed.
        //
        // An all-bound model — every resolvable entry landed somewhere, yet slots are still empty — is NOT a
        // class here, because the empty slots are then other submeshes and the remedy is a no-op. It was
        // measured before being retired: zero instances across 119 map-carrying models in three projects, and
        // no importer-driven path constructs it (binding is applied by NAME at import, so a key whose name
        // exists binds and a key whose name does not has no submesh to leave empty; the staleness above is
        // all-or-nothing per import, which is why a real one lands in the not-bound partition). Those empty
        // slots are still reported — ScanHierarchy runs on the same model asset and counts every one into
        // `empty=` — they simply do not carry a verdict. Do not re-add the class without a measured instance:
        // `notBound` is a model-wide material-identity test, so on a map aiming several keys at one material
        // one bound occurrence marks them all bound, and a per-slot check is what such an instance would need.
        //
        // `materialImportMode == None` is excluded: materials are deliberately not imported there, so every
        // slot is empty by design and an empty count carries no signal at all.
        private static void ScanModelRemap(string assetPath, Report r)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer == null || importer.materialImportMode == ModelImporterMaterialImportMode.None) return;

            var mapped = new List<string>();
            var unresolved = new List<string>();
            // The material each entry points at, kept so the walk below can ask whether it actually landed.
            // Indexer, not Add: a duplicate identifier name would throw, and a throw here would take down a
            // whole-folder scan over one malformed importer entry.
            var target = new Dictionary<string, Material>();
            foreach (var kv in importer.GetExternalObjectMap())
            {
                if (kv.Key.type != typeof(Material)) continue;
                mapped.Add(kv.Key.name);
                if (kv.Value == null) unresolved.Add(kv.Key.name);
                else target[kv.Key.name] = kv.Value as Material;
            }
            if (mapped.Count == 0) return;

            var model = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (model == null) return;
            int empty = 0;
            // Non-null slots are collected, not just counted: a mapped material PRESENT on some renderer
            // bound fine, so naming it beside the genuine misses buries them. This is the one attribution
            // the API supports — the entry, never the slot.
            var boundHere = new HashSet<Material>();
            foreach (var rend in model.GetComponentsInChildren<Renderer>(true))
            {
                // The ParticleSystemRenderer exclusion applies to the EMPTY count only. Its null slot
                // is routine and carries no signal, but a material bound THERE is still bound on this
                // model — excluding it from the set would report a remap entry as "landed nowhere" and
                // prescribe a reimport that cannot change anything.
                bool slotsAreEvidence = !(rend is ParticleSystemRenderer);
                foreach (var m in rend.sharedMaterials)
                {
                    if (m == null) { if (slotsAreEvidence) empty++; }
                    else boundHere.Add(m);
                }
            }
            if (empty == 0) return;

            // Partition first: the verdict now depends on it, since an all-bound model is not a class.
            var notBound = new List<string>();
            foreach (var name in mapped)
            {
                Material t;
                if (target.TryGetValue(name, out t) && t != null && !boundHere.Contains(t)) notBound.Add(name);
            }

            var verdict = ClassifyRemap(mapped.Count, unresolved.Count, empty, notBound.Count);
            if (verdict == RemapVerdict.None) return;

            if (verdict == RemapVerdict.Unresolved) r.RemapUnresolved += empty; else r.RemapStale += empty;
            r.Offenders.Add(new Offender
            {
                Location = assetPath,
                ObjectPath = "",
                Kind = verdict == RemapVerdict.Unresolved ? "fbx-remap-unresolved" : "fbx-remap-stale",
                Detail = DescribeRemap(verdict, mapped.Count, unresolved, empty, mapped, notBound)
            });
        }

        internal enum RemapVerdict { None, Stale, Unresolved }

        /// <summary>The remap decision, pure so every branch is asserted directly rather than by hunting a
        /// binary FBX whose importer happens to produce it. A partly-missing pack classifies as
        /// <c>Unresolved</c>: import-first is the remedy that fixes it, and force-reimporting alone would
        /// leave the unresolved half exactly as it was.
        ///
        /// <paramref name="notBoundCount"/> is what makes the stale class say something: empty slots on a model
        /// whose every resolvable entry already landed are other submeshes, so there is nothing to prescribe
        /// and no verdict to carry. It defaults to 0 — a caller that has not partitioned gets <c>None</c> for
        /// the stale case rather than a FAIL it cannot word.</summary>
        internal static RemapVerdict ClassifyRemap(int mappedCount, int unresolvedCount, int emptySlots,
                                                   int notBoundCount = 0)
        {
            if (mappedCount == 0 || emptySlots == 0) return RemapVerdict.None;
            if (unresolvedCount > 0) return RemapVerdict.Unresolved;
            return notBoundCount > 0 ? RemapVerdict.Stale : RemapVerdict.None;
        }

        /// <summary>Pure wording for the two remap classes, kept beside the decision so a reader comparing
        /// the remedies sees they run in opposite order.
        ///
        /// <paramref name="notBound"/> is the stale class's whole readability: a model can carry a dozen
        /// remap entries where two never landed, and naming all twelve leaves the reader to find the two.
        /// An entry is "not bound" when its material appears on NO renderer of this model — an observation,
        /// unlike a per-slot attribution, which the importer's list cannot support (see ScanModelRemap).
        /// A stale verdict now implies a non-empty partition, so the wording states it unconditionally; a
        /// direct call with an empty one degenerates to a vacuous "0 of N" and is not a live case.</summary>
        internal static string DescribeRemap(RemapVerdict verdict, int mappedCount, List<string> unresolved, int emptySlots,
                                            List<string> mapped = null, List<string> notBound = null)
        {
            if (verdict == RemapVerdict.Unresolved)
                return emptySlots + " empty renderer slot(s); " + unresolved.Count + " of " + mappedCount
                     + " external-material remap entries resolve to nothing (" + NameList(unresolved)
                     + ") — restore or import those material assets, THEN force-reimport the FBX. A reimport alone "
                     + "cannot fix this: the targets do not exist yet";

            // No partition supplied: say only what an unpartitioned caller established. Claiming
            // "all N are already bound" here would assert a reading nobody took.
            if (notBound == null)
                return emptySlots + " empty renderer slot(s) despite " + mappedCount
                     + " resolvable external-material remap entries (" + NameList(mapped ?? new List<string>())
                     + ") — force-reimport the FBX. If a reimport does not clear it, the empty slots are "
                     + "not the mapped ones and this model is fine";

            return emptySlots + " empty renderer slot(s); " + notBound.Count + " of " + mappedCount
                 + " resolvable external-material remap entries are on no renderer of this model ("
                 + NameList(notBound) + ") — force-reimport the FBX. The other "
                 + (mappedCount - notBound.Count) + " bound fine and are not the problem. If a reimport "
                 + "does not clear it, those entries name no submesh this mesh carries and this model is fine";
        }

        /// <summary>Comma-joined names, capped so a many-material FBX names a readable few and counts the rest
        /// rather than emitting a wall the reader skips.</summary>
        internal static string NameList(List<string> names)
        {
            const int Cap = 6;
            if (names.Count <= Cap) return string.Join(", ", names.ToArray());
            return string.Join(", ", names.GetRange(0, Cap).ToArray()) + ", +" + (names.Count - Cap) + " more";
        }

        // ----- Output -------------------------------------------------------------------------

        private static string Finish(Report r, string label)
        {
            bool pass = r.MatMissing == 0 && r.MeshesMissing == 0 && r.ScriptsMissing == 0
                     && r.RemapStale == 0 && r.RemapUnresolved == 0 && r.LoadErrors == 0;
            r.Result = pass ? "PASS" : "FAIL";
            string logPath = WriteRunLog(r, label);

            // The refs-to-targets ratio is the verdict's character: a thousand slots at two targets is one
            // vendor-side mistake, not a thousand. Both numbers ride together so the ratio needs no
            // arithmetic, and it covers both dangling classes (material slots and meshes), so it sits
            // after them rather than beside either one.
            int danglingRefs = r.MatMissing + r.MeshesMissing;
            string dangling = danglingRefs == 0 ? "" :
                " | dangling: " + danglingRefs + " ref(s) at " + r.DanglingTargets.Count + " distinct target(s)"
                + (r.UnidentifiedTargets.Count > 0 ? ", " + r.UnidentifiedTargets.Count + " identified by instance id only (may over-count)" : "");

            string summary = string.Format(CultureInfo.InvariantCulture,
                "[CheckPackage] {0} ({1}, {2} scanned): materials resolved={3} empty={4} MISSING={5} | meshMISSING={6} | scriptMISSING={7} | remapSTALE={8} | remapUNRESOLVED={9}{10}{11} => {12} | log={13}",
                label, r.Mode, r.Scanned, r.MatResolved, r.MatEmpty, r.MatMissing, r.MeshesMissing, r.ScriptsMissing,
                r.RemapStale, r.RemapUnresolved, dangling,
                r.LoadErrors > 0 ? " | loadErrors=" + r.LoadErrors : "", r.Result, logPath);

            if (pass) Debug.Log(summary); else Debug.LogError(summary);
            return summary;
        }

        private static string WriteRunLog(Report r, string label)
        {
            Directory.CreateDirectory(RunLogDir);
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"kind\": \"check-package\",\n");
            sb.Append("  \"unityVersion\": ").Append(Q(Application.unityVersion)).Append(",\n");
            sb.Append("  \"timestampUtc\": ").Append(Q(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))).Append(",\n");
            sb.Append("  \"target\": ").Append(Q(r.Target)).Append(",\n");
            sb.Append("  \"mode\": ").Append(Q(r.Mode)).Append(",\n");
            sb.Append("  \"scanned\": ").Append(r.Scanned).Append(",\n");
            sb.Append("  \"materials\": { \"resolved\": ").Append(r.MatResolved)
              .Append(", \"empty\": ").Append(r.MatEmpty)
              .Append(", \"missing\": ").Append(r.MatMissing).Append(" },\n");
            sb.Append("  \"meshesMissing\": ").Append(r.MeshesMissing).Append(",\n");
            // Spans material slots and meshes both, so it sits outside "materials".
            sb.Append("  \"danglingDistinctTargets\": ").Append(r.DanglingTargets.Count).Append(",\n");
            sb.Append("  \"danglingUnidentifiedTargets\": ").Append(r.UnidentifiedTargets.Count).Append(",\n");
            sb.Append("  \"scriptsMissing\": ").Append(r.ScriptsMissing).Append(",\n");
            sb.Append("  \"remapStale\": ").Append(r.RemapStale).Append(",\n");
            sb.Append("  \"remapUnresolved\": ").Append(r.RemapUnresolved).Append(",\n");
            sb.Append("  \"loadErrors\": ").Append(r.LoadErrors).Append(",\n");
            sb.Append("  \"result\": ").Append(Q(r.Result)).Append(",\n");
            sb.Append("  \"offenders\": [");
            for (int i = 0; i < r.Offenders.Count; i++)
            {
                var o = r.Offenders[i];
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append("    { \"location\": ").Append(Q(o.Location))
                  .Append(", \"objectPath\": ").Append(Q(o.ObjectPath))
                  .Append(", \"kind\": ").Append(Q(o.Kind))
                  .Append(", \"detail\": ").Append(Q(o.Detail)).Append(" }");
            }
            sb.Append(r.Offenders.Count > 0 ? "\n  ]\n}" : "]\n}");

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            // Kind-prefixed filename matching the JSON "kind" (was verify_<label> — the drift #28
            // accepted "until touched"; nothing globs the old prefix).
            var path = $"{RunLogDir}/check-package_{Sanitize(label)}_{stamp}.json";
            File.WriteAllText(path, sb.ToString());
            AssetDatabase.Refresh();
            return path;
        }

        // ----- Helpers ------------------------------------------------------------------------

        private static string HierarchyPath(Transform t)
        {
            var sb = new StringBuilder(t.name);
            while (t.parent != null) { t = t.parent; sb.Insert(0, t.name + "/"); }
            return sb.ToString();
        }

        private static string Leaf(string assetPath) => RunLogFormat.Leaf(assetPath);

        private static string Sanitize(string s) => RunLogFormat.Sanitize(s);

        private static string Q(string s) => RunLogFormat.Q(s);

        // ----- Types --------------------------------------------------------------------------

        private class Report
        {
            public string Target;
            public string Mode;
            public int Scanned;
            public int MatResolved, MatEmpty, MatMissing;
            // Serialized identities behind every dangling reference found, material or mesh. The
            // unidentified subset is tracked apart because its key is an in-memory handle, not identity.
            public readonly HashSet<string> DanglingTargets = new HashSet<string>();
            public readonly HashSet<string> UnidentifiedTargets = new HashSet<string>();
            public readonly Dictionary<int, TargetInfo> TargetCache = new Dictionary<int, TargetInfo>();
            public int MeshesMissing;
            public int ScriptsMissing;
            public int RemapStale;
            public int RemapUnresolved;
            public int LoadErrors;
            public string Result;
            public readonly List<Offender> Offenders = new List<Offender>();
        }

        private struct TargetInfo
        {
            public string Key;      // serialized identity, or the instance id when none is available
            public string Detail;
            public bool Mapped;
        }

        private struct Offender
        {
            public string Location;
            public string ObjectPath;
            public string Kind;
            public string Detail;
        }
    }
}
