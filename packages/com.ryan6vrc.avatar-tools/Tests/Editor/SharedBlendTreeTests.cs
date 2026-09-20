using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;
using Ryan6Vrc.AvatarTools.Tests;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.TestTools;

// ── Shared (named) blend trees: `trees:` + `shared:` ────────────────────────────────────────────────
//
// One tree authored once compiles to ONE BlendTree sub-asset that several motion slots reference, and a
// decompile RECOVERS that sharing instead of inlining a copy per parent.
//
// Read the emit and read halves as genuinely separate claims. Every test that reaches decompile THROUGH a
// compile can only ever observe the compiler's own memo; DecompileRecoversSharingFromHandBuiltController is
// the one that builds the sharing with the Unity API directly and so pins that the refcount pre-pass counts
// the LIVE graph. That distinction is the whole reason the read side can be wrong while everything else is
// green — see the vacuous-pass note on the fixpoint test below.
public class SharedBlendTreeTests
{
    private const string TestRoot = "Assets/Agent/Scratch/shared_tree_tests";

    [SetUp]
    public void SetUp() => AnimatorTestHelpers.EnsureFolder(TestRoot);

    [TearDown]
    public void TearDown() => AssetDatabase.DeleteAsset(TestRoot);

    // Two states and one nested tree-child all naming the same shared tree — the three-parent shape the
    // construct exists for, and the shape the FT driver rig has (two tree children plus one state motion).
    private const string SharedDoc = @"schema: 1
controller: SharedTree_Fx
basis: avatar-root
role: fx

parameters:
  W:   { type: float, default: 1.0 }
  Out: { type: float, aap: true }

clips:
  low:  { set: { Out: 0.0 } }
  high: { set: { Out: 1.0 } }

trees:
  Driver:
    tree: direct
    normalized: false
    children:
      - { clip: low,  directWeight: W }
      - { clip: high, directWeight: W }

layers:
  - name: L
    states:
      A:
        motion: { shared: Driver }
      B:
        motion:
          tree: direct
          normalized: false
          children:
            - { clip: low, directWeight: W }
            - { shared: Driver, directWeight: W }
      C:
        motion: { shared: Driver }
    default: A
";

    // ── emit ────────────────────────────────────────────────────────────────────────────────────────

    // REFERENCE IDENTITY, not a count. EmitResult.Trees is rebuilt from LoadAllAssetsAtPath by
    // ReloadFromDisk, which already collapses duplicates — so a `Trees.Count` assertion passes whether or
    // not the memo exists, and would prove nothing about sharing.
    [Test]
    public void SharedTreeEmitsOneObjectReferencedFromEveryParent()
    {
        var c = FixpointOracle.CompileTo(TestRoot, SharedDoc, "SharedTree_Fx", "emit");
        var states = c.layers[0].stateMachine.states.ToDictionary(s => s.state.name, s => s.state);

        var fromA = states["A"].motion as BlendTree;
        var fromC = states["C"].motion as BlendTree;
        var parentB = states["B"].motion as BlendTree;
        Assert.IsNotNull(fromA, "state A plays a blend tree");
        Assert.IsNotNull(parentB, "state B plays a blend tree");
        var fromB = parentB.children[1].motion as BlendTree;

        Assert.AreSame(fromA, fromC, "states A and C must play the SAME BlendTree object, not copies");
        Assert.AreSame(fromA, fromB, "a tree CHILD naming the shared tree resolves to that same object");
        Assert.AreEqual("Driver", fromA.name, "the shared tree is named by its trees: key, not by a positional auto-name");

        // The whole point, measured the way the target controller was measured: one serialized document.
        var trees = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(c))
            .OfType<BlendTree>().Where(t => t.name == "Driver").ToList();
        Assert.AreEqual(1, trees.Count, "exactly one 'Driver' BlendTree sub-asset exists");
    }

    // A shared tree's children auto-name off the KEY, so none of them needs an explicit name: on the way
    // back. If BuildTree took the caller's positional default instead, the built name would depend on which
    // reference site populated the memo first and every child would gain a spurious name:.
    [Test]
    public void SharedTreeChildrenCarryNoSpuriousNames()
    {
        var c = FixpointOracle.CompileTo(TestRoot, SharedDoc, "SharedTree_Fx", "childnames");
        string yaml = FixpointOracle.Decode(c);
        StringAssert.DoesNotContain("name: Driver_", yaml, "a shared tree's children must not decompile explicit names");
        StringAssert.DoesNotContain("_BlendTree", yaml, "no positional auto-name may leak into a shared tree's naming");
    }

    // ── round trip ──────────────────────────────────────────────────────────────────────────────────

    // NOTE ON VACUITY: the text-equality half of this test passes even with the feature entirely inert —
    // three unshared trees would inline on BOTH decodes and compare equal. The shape assertions are what
    // make it a test of sharing rather than of determinism, so do not drop them to make a failure go away.
    [Test]
    public void SharedTreeDocumentIsAFixpoint()
    {
        var c1 = FixpointOracle.CompileTo(TestRoot, SharedDoc, "SharedTree_Fx", "fx1");
        string d1 = FixpointOracle.Decode(c1);

        var doc1 = ControllerDecompile.Walk(c1).Doc;
        Assert.AreEqual(1, doc1.Trees.Count, "the decode carries exactly one trees: entry");
        Assert.AreEqual("Driver", doc1.Trees[0].Name);
        StringAssert.Contains("trees:", d1, "the serialized document carries a trees: block");
        Assert.AreEqual(3, CountOccurrences(d1, "shared: Driver"),
            "all three reference sites decompile as shared: references, not inlined bodies");

        var c2 = FixpointOracle.CompileTo(TestRoot, d1, "SharedTree_Fx", "fx2");
        string d2 = FixpointOracle.Decode(c2);
        Assert.AreEqual(d1, d2, "decode(C1) == decode(compile(decode(C1))) — the shared form round-trips exactly");
    }

    // The construct must not disturb the ordinary case: a tree with ONE parent stays inline, so every
    // existing document and fixture decompiles byte-identically to before.
    [Test]
    public void SinglyReferencedTreeStaysInline()
    {
        const string doc = @"schema: 1
controller: Inline_Fx
basis: avatar-root
role: fx
parameters:
  W: { type: float, default: 1.0 }
  Out: { type: float, aap: true }
clips:
  low: { set: { Out: 0.0 } }
trees:
  Solo:
    tree: direct
    normalized: false
    children:
      - { clip: low, directWeight: W }
layers:
  - name: L
    states:
      A:
        motion: { shared: Solo }
    default: A
";
        // A single reference COMPILES — it is not an error — and the decompile re-inlines it, which is the
        // documented asymmetry: sharing is a property of the built graph, and one parent is not sharing.
        var c = FixpointOracle.CompileTo(TestRoot, doc, "Inline_Fx", "solo");
        var walked = ControllerDecompile.Walk(c).Doc;
        Assert.AreEqual(0, walked.Trees.Count, "a one-parent tree decompiles inline, with no trees: entry");

        string yaml = FixpointOracle.Decode(c);
        StringAssert.DoesNotContain("shared:", yaml);
        StringAssert.DoesNotContain("trees:", yaml);
    }

    // ── read side, built WITHOUT the compiler ───────────────────────────────────────────────────────

    // The only test that pins the refcount pre-pass against Unity's own sharing rather than against the
    // emitter's memo. Also the committed stand-in for the live acceptance run on the legacy FT controller.
    [Test]
    public void DecompileRecoversSharingFromHandBuiltController()
    {
        string path = TestRoot + "/HandBuilt.controller";
        var ac = AnimatorController.CreateAnimatorControllerAtPath(path);
        ac.AddParameter("W", AnimatorControllerParameterType.Float);

        var shared = new BlendTree { name = "HandShared", blendType = BlendTreeType.Direct, hideFlags = HideFlags.HideInHierarchy };
        AssetDatabase.AddObjectToAsset(shared, ac);
        var parent = new BlendTree { name = "HandParent", blendType = BlendTreeType.Direct, hideFlags = HideFlags.HideInHierarchy };
        AssetDatabase.AddObjectToAsset(parent, ac);
        parent.AddChild(shared);

        var sm = ac.layers[0].stateMachine;
        sm.AddState("S1").motion = shared;     // parent 1: a state motion
        sm.AddState("S2").motion = shared;     // parent 2: another state motion
        sm.AddState("S3").motion = parent;     // parent 3: a tree child
        EditorUtility.SetDirty(ac);
        AssetDatabase.SaveAssets();

        var doc = ControllerDecompile.Walk(ac).Doc;
        Assert.AreEqual(1, doc.Trees.Count, "the multi-parent tree becomes one trees: entry");
        Assert.AreEqual("HandShared", doc.Trees[0].Name);

        string yaml = AnimatorSchemaEmit.Serialize(doc);
        Assert.AreEqual(3, CountOccurrences(yaml, "shared: HandShared"),
            "all three parent slots emit a shared: reference");
        // The trees: entry takes its name from its KEY. An emitted `name:` inside the body is exactly what
        // BindTrees refuses, i.e. decompile output that cannot recompile.
        StringAssert.DoesNotContain("name: HandShared", yaml,
            "a trees: entry must not emit a name: line beside its key");
    }

    // The same motion in two child slots of ONE parent is two parent edges, so it is shared. Named here
    // because it is the case a node-counting pre-pass silently gets wrong.
    [Test]
    public void SameTreeTwiceUnderOneParentCountsAsShared()
    {
        string path = TestRoot + "/TwiceUnderOne.controller";
        var ac = AnimatorController.CreateAnimatorControllerAtPath(path);
        ac.AddParameter("W", AnimatorControllerParameterType.Float);

        var shared = new BlendTree { name = "Twice", blendType = BlendTreeType.Direct, hideFlags = HideFlags.HideInHierarchy };
        AssetDatabase.AddObjectToAsset(shared, ac);
        var parent = new BlendTree { name = "OneParent", blendType = BlendTreeType.Direct, hideFlags = HideFlags.HideInHierarchy };
        AssetDatabase.AddObjectToAsset(parent, ac);
        parent.AddChild(shared);
        parent.AddChild(shared);

        ac.layers[0].stateMachine.AddState("S").motion = parent;
        EditorUtility.SetDirty(ac);
        AssetDatabase.SaveAssets();

        var doc = ControllerDecompile.Walk(ac).Doc;
        Assert.AreEqual(1, doc.Trees.Count, "two child slots on one parent are two parent edges — shared");
        Assert.AreEqual("Twice", doc.Trees[0].Name);
    }

    // A name collision between two shared trees INLINES both rather than refusing the document. "Blend Tree"
    // is Unity's default name for every UI-created tree, so a refusal here would block a read door on
    // content we do not own, with renaming a vendor asset as the only remedy.
    [Test]
    public void CollidingSharedTreeNamesInlineRatherThanRefuse()
    {
        string path = TestRoot + "/Collide.controller";
        var ac = AnimatorController.CreateAnimatorControllerAtPath(path);
        ac.AddParameter("W", AnimatorControllerParameterType.Float);

        var a = new BlendTree { name = "Blend Tree", blendType = BlendTreeType.Direct, hideFlags = HideFlags.HideInHierarchy };
        var b = new BlendTree { name = "Blend Tree", blendType = BlendTreeType.Direct, hideFlags = HideFlags.HideInHierarchy };
        AssetDatabase.AddObjectToAsset(a, ac);
        AssetDatabase.AddObjectToAsset(b, ac);

        var sm = ac.layers[0].stateMachine;
        sm.AddState("A1").motion = a;
        sm.AddState("A2").motion = a;
        sm.AddState("B1").motion = b;
        sm.AddState("B2").motion = b;
        EditorUtility.SetDirty(ac);
        AssetDatabase.SaveAssets();

        var w = ControllerDecompile.Walk(ac);
        Assert.IsEmpty(w.Refusals, "a shared-name collision must not refuse the document");
        Assert.AreEqual(0, w.Doc.Trees.Count, "both colliding trees inline instead");
        Assert.IsTrue(w.Notes.Any(n => n.Contains("Blend Tree")), "the inlining is reported as a note");
    }

    // ── refusals ────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void CyclicSharedTreesRefuse()
    {
        const string doc = @"schema: 1
controller: Cycle_Fx
basis: avatar-root
role: fx
parameters:
  W: { type: float, default: 1.0 }
  Out: { type: float, aap: true }
clips:
  low: { set: { Out: 0.0 } }
trees:
  A:
    tree: direct
    children:
      - { shared: B, directWeight: W }
  B:
    tree: direct
    children:
      - { shared: A, directWeight: W }
layers:
  - name: L
    states:
      SA: { motion: { shared: A } }
      SB: { motion: { shared: B } }
    default: SA
";
        // Driven through the real door: a cycle must be a named FAIL, not a stack overflow.
        LogAssert.ignoreFailingMessages = true;   // the FAIL logs at error level — expected here
        try
        {
            string y = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "shared_cycle.yaml");
            System.IO.File.WriteAllText(y, doc);
            string outDir = TestRoot + "/out_cycle";
            AnimatorTestHelpers.EnsureFolder(outDir);
            string res = CompileController.Run(y, outDir, whatIf: false);
            StringAssert.Contains("=> FAIL", res, "a cyclic shared tree fails the compile");
            StringAssert.Contains("contains itself", res, "the refusal names the loop");
        }
        finally { LogAssert.ignoreFailingMessages = false; }
    }

    [Test]
    public void UnreferencedTreeEntryIsRefused()
    {
        const string doc = @"schema: 1
controller: Dead_Fx
basis: avatar-root
role: fx
parameters:
  W: { type: float, default: 1.0 }
  Out: { type: float, aap: true }
clips:
  low: { set: { Out: 0.0 } }
trees:
  Dead:
    tree: direct
    children:
      - { clip: low, directWeight: W }
layers:
  - name: L
    states:
      A: { motion: { clip: low } }
    default: A
";
        var errors = SchemaValidation.Validate(AnimatorSchemaYaml.Parse(doc, "dead.yaml"));
        Assert.IsTrue(errors.Any(e => e.Contains("unreferenced-tree") && e.Contains("Dead")),
            "a trees: entry nothing references is refused by name — got: " + string.Join(" | ", errors));
    }

    // LIVENESS IS TRANSITIVE. An entry referenced only from another `trees:` body is reachable only if that
    // body is itself reachable — otherwise a self-edge or a mutually-referencing clique sustains its own use
    // count, escapes `unreferenced-tree`, and is then never built (emit is lazy and driven from state
    // motions), so a declared object leaves the controller with no diagnostic at all.
    [Test]
    public void SelfReferencingDeadTreeIsRefused()
    {
        const string doc = @"schema: 1
controller: SelfDead_Fx
basis: avatar-root
role: fx
parameters:
  W: { type: float, default: 1.0 }
  Out: { type: float, aap: true }
clips:
  low: { set: { Out: 0.0 } }
trees:
  A:
    tree: direct
    children:
      - { shared: A, directWeight: W }
layers:
  - name: L
    states:
      S: { motion: { clip: low } }
    default: S
";
        var errors = SchemaValidation.Validate(AnimatorSchemaYaml.Parse(doc, "selfdead.yaml"));
        Assert.IsTrue(errors.Any(e => e.Contains("unreferenced-tree") && e.Contains("'A'")),
            "a tree whose only reference is its own self-edge is not live — got: " + string.Join(" | ", errors));
    }

    [Test]
    public void MutuallyReferencingDeadTreesAreRefused()
    {
        const string doc = @"schema: 1
controller: ClqDead_Fx
basis: avatar-root
role: fx
parameters:
  W: { type: float, default: 1.0 }
  Out: { type: float, aap: true }
clips:
  low: { set: { Out: 0.0 } }
trees:
  A:
    tree: direct
    children:
      - { shared: B, directWeight: W }
  B:
    tree: direct
    children:
      - { shared: A, directWeight: W }
layers:
  - name: L
    states:
      S: { motion: { clip: low } }
    default: S
";
        var errors = SchemaValidation.Validate(AnimatorSchemaYaml.Parse(doc, "clqdead.yaml"));
        Assert.IsTrue(errors.Any(e => e.Contains("unreferenced-tree") && e.Contains("'A'")), "A is dead");
        Assert.IsTrue(errors.Any(e => e.Contains("unreferenced-tree") && e.Contains("'B'")), "B is dead");
    }

    // A tree reached transitively through a LIVE shared tree is live, and must not be refused.
    [Test]
    public void TransitivelyReachedTreeIsNotRefused()
    {
        const string doc = @"schema: 1
controller: Chain_Fx
basis: avatar-root
role: fx
parameters:
  W: { type: float, default: 1.0 }
  Out: { type: float, aap: true }
clips:
  low: { set: { Out: 0.0 } }
trees:
  Outer:
    tree: direct
    children:
      - { shared: Inner, directWeight: W }
  Inner:
    tree: direct
    children:
      - { clip: low, directWeight: W }
layers:
  - name: L
    states:
      S1: { motion: { shared: Outer } }
      S2: { motion: { shared: Outer } }
    default: S1
";
        var errors = SchemaValidation.Validate(AnimatorSchemaYaml.Parse(doc, "chain.yaml"));
        Assert.IsFalse(errors.Any(e => e.Contains("unreferenced-tree")),
            "a tree reached through a live shared tree is live — got: " + string.Join(" | ", errors));
    }

    // NO CYCLE GUARD ON THE READ SIDE, deliberately — Unity will not let one exist. Its importer breaks a
    // BlendTree cycle on load, nulling the offending child and logging "BlendTree cycle detected"; measured
    // both through AddChild and with the cycle written straight into the asset text, which is the stronger
    // case since hand edits and vendor tooling write .controller YAML directly. So the decoder can never
    // observe a cycle, and a guard there would be unreachable code. This test is what keeps that true: if a
    // future Unity preserves the edge, it fails and the read side owes a guard.
    //
    // The COMPILE side is a different story and does carry one (CyclicSharedTreesRefuse): an authored
    // document can express a cycle that never reaches Unity at all.
    [Test]
    public void UnityItselfRefusesToBuildABlendTreeCycle()
    {
        string path = TestRoot + "/UnityGuard.controller";
        var ac = AnimatorController.CreateAnimatorControllerAtPath(path);
        var a = new BlendTree { name = "CycA", blendType = BlendTreeType.Direct, hideFlags = HideFlags.HideInHierarchy };
        var b = new BlendTree { name = "CycB", blendType = BlendTreeType.Direct, hideFlags = HideFlags.HideInHierarchy };
        var f1 = new BlendTree { name = "F1", blendType = BlendTreeType.Direct, hideFlags = HideFlags.HideInHierarchy };
        var f2 = new BlendTree { name = "F2", blendType = BlendTreeType.Direct, hideFlags = HideFlags.HideInHierarchy };
        foreach (var t in new[] { a, b, f1, f2 }) AssetDatabase.AddObjectToAsset(t, ac);
        a.AddChild(f1);
        b.AddChild(f2);
        var sm = ac.layers[0].stateMachine;
        sm.AddState("SA").motion = a;
        sm.AddState("SB").motion = b;
        EditorUtility.SetDirty(ac);
        AssetDatabase.SaveAssets();

        long aId, bId, f1Id, f2Id;
        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(a, out _, out aId);
        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(b, out _, out bId);
        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(f1, out _, out f1Id);
        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(f2, out _, out f2Id);

        string abs = System.IO.Path.GetFullPath(path);
        string text = System.IO.File.ReadAllText(abs);
        string from1 = "m_Motion: {fileID: " + f1Id + "}";
        string from2 = "m_Motion: {fileID: " + f2Id + "}";
        Assert.IsTrue(text.Contains(from1) && text.Contains(from2), "both placeholder edges are in the text");
        text = text.Replace(from1, "m_Motion: {fileID: " + bId + "}")    // CycA -> CycB
                   .Replace(from2, "m_Motion: {fileID: " + aId + "}");   // CycB -> CycA
        System.IO.File.WriteAllText(abs, text);

        LogAssert.ignoreFailingMessages = true;   // the importer logs its refusal at error level
        try { AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate); }
        finally { LogAssert.ignoreFailingMessages = false; }

        var reloaded = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
        var rSm = reloaded.layers[0].stateMachine;
        var ra = rSm.states.First(s => s.state.name == "SA").state.motion as BlendTree;
        Assert.AreEqual(1, ra.children.Length, "the child slot survives the import");
        Assert.IsNull(ra.children[0].motion, "but Unity broke the cycle, so the decoder never sees one");

        var w = ControllerDecompile.Walk(reloaded);
        Assert.IsEmpty(w.Refusals, "with the cycle broken the walk is clean");
    }

    [Test]
    public void DanglingSharedReferenceIsRefused()
    {
        const string doc = @"schema: 1
controller: Dangling_Fx
basis: avatar-root
role: fx
parameters:
  W: { type: float, default: 1.0 }
  Out: { type: float, aap: true }
clips:
  low: { set: { Out: 0.0 } }
layers:
  - name: L
    states:
      A: { motion: { shared: Ghost } }
    default: A
";
        var errors = SchemaValidation.Validate(AnimatorSchemaYaml.Parse(doc, "dangling.yaml"));
        Assert.IsTrue(errors.Any(e => e.Contains("dangling-shared") && e.Contains("Ghost")),
            "a shared: naming no entry is refused by name — got: " + string.Join(" | ", errors));
    }

    // The key IS the name. A body carrying one too would give the tree two names that could disagree, and a
    // decompile never writes one — so it is a parse refusal rather than a silent shadow.
    [Test]
    public void NameFieldInsideATreesEntryIsRefused()
    {
        const string doc = @"schema: 1
controller: Named_Fx
basis: avatar-root
role: fx
parameters:
  W: { type: float, default: 1.0 }
trees:
  T:
    tree: direct
    name: Something
    children: []
layers:
  - name: L
    states:
      A: { motion: { shared: T } }
    default: A
";
        var ex = Assert.Throws<SchemaException>(() => AnimatorSchemaYaml.Parse(doc, "named.yaml"));
        StringAssert.Contains("takes its name from its key", ex.Message);
    }

    // `shared:` reaches DecodeMotionRef's dispatch BEFORE the unguarded tree fallthrough. If it did not, a
    // shared map would land in BindTree and throw KeyNotFoundException — a crash, not a named refusal.
    [Test]
    public void SharedMotionWithSiblingKeysIsANamedRefusal()
    {
        const string doc = @"schema: 1
controller: Sib_Fx
basis: avatar-root
role: fx
parameters:
  W: { type: float, default: 1.0 }
trees:
  T:
    tree: direct
    children: []
layers:
  - name: L
    states:
      A: { motion: { shared: T, param: W } }
    default: A
";
        var ex = Assert.Throws<SchemaException>(() => AnimatorSchemaYaml.Parse(doc, "sib.yaml"));
        StringAssert.Contains("shared", ex.Message);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, System.StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }
}
