using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Ryan6Vrc.AgentTools.Editor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.TestTools;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.Constraint.Components;

/// <summary>
/// The bake's reads of the built clone that are not a parameter, a triangle or a byte: constraint references
/// the build left missing, each mesh's UV layout, the caller's probe, and the one-back copy of the artifact.
/// Each makes a claim a reader acts on without a second look, so the cases pin the line between the states
/// that look alike: a destroyed reference against one never assigned, a probe that returned nothing against
/// one that threw, a finished artifact against a stub.
///
/// Objects are left for the next test's <c>NewScene(Single)</c> to take rather than destroyed —
/// <c>docs/verify.md</c> §Test venue. The one destroy below runs on a clone inside
/// <c>AvatarBakeScope</c>'s injectable seam, the way the build itself removes an object.
/// </summary>
public class CompositionBuiltReadsTests
{
    [SetUp]
    public void SetUp() => UnityEditor.SceneManagement.EditorSceneManager.NewScene(
        UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
        UnityEditor.SceneManagement.NewSceneMode.Single);

    // ── Constraints ─────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void AReferenceTheBuildTookAway_isARow_howeverTheBuildLeftTheSlot()
    {
        var source = new GameObject("Avatar");
        var live = Child(source, "Live");
        var doomed = Child(source, "Doomed");

        var vrc = Child(source, "VrcHost").AddComponent<VRCParentConstraint>();
        var sources = vrc.Sources;
        sources.Add(new VRCConstraintSource(live.transform, 1f));
        sources.Add(new VRCConstraintSource(null, 1f));          // empty before the build: legal, never a row
        sources.Add(new VRCConstraintSource(doomed.transform, 1f));
        vrc.Sources = sources;

        var unity = Child(source, "UnityHost").AddComponent<ParentConstraint>();
        unity.AddSource(new ConstraintSource { sourceTransform = live.transform, weight = 1f });
        unity.AddSource(new ConstraintSource { sourceTransform = doomed.transform, weight = 1f });

        var moved = Child(source, "MovedHost").AddComponent<ParentConstraint>();
        moved.AddSource(new ConstraintSource { sourceTransform = doomed.transform, weight = 1f });

        Child(source, "GoneHost").AddComponent<ParentConstraint>();

        // The seam stands in for the build, and leaves a lost reference in each state a real pass does: the
        // VRC slot keeps the destroyed object's id (nothing swept it), the Unity slot is overwritten with a
        // plain null (AAO's re-map after its passes), which is byte-identical to a slot never assigned. One
        // source is re-pointed, one constraint destroyed, one made. The scope is NOT disposed.
        CompositionBake.ConstraintSnapshot before = null;
        var scope = new AvatarBakeScope(source, "Avatar (bake)",
            clone =>
            {
                var liveOnClone = clone.transform.Find("Live");
                clone.transform.Find("MovedHost").GetComponent<ParentConstraint>()
                     .SetSource(0, new ConstraintSource { sourceTransform = liveOnClone, weight = 1f });
                Object.DestroyImmediate(clone.transform.Find("Doomed").gameObject);
                clone.transform.Find("UnityHost").GetComponent<ParentConstraint>()
                     .SetSource(1, new ConstraintSource { sourceTransform = null, weight = 1f });
                Object.DestroyImmediate(clone.transform.Find("GoneHost").GetComponent<ParentConstraint>());
                clone.transform.Find("Live").gameObject.AddComponent<ParentConstraint>();
                return true;
            },
            () => { },
            clone => before = CompositionBake.SnapshotConstraintRefs(clone));
        Assert.IsTrue(scope.Ok, scope.DescribeFailure());

        var read = CompositionBake.ReadLostConstraintRefs(scope.Clone, before);

        CollectionAssert.AreEquivalent(
            new[] { "VrcHost|VRCParentConstraint|Sources.source2.SourceTransform|Doomed",
                    "UnityHost|ParentConstraint|m_Sources.Array.data[1].sourceTransform|Doomed" },
            read.Rows.Select(r => r.Path + "|" + r.Type + "|" + r.Property + "|" + r.Was).ToArray(),
            "both lost sources are rows; the re-pointed source and the slot empty from the start are not");
        Assert.IsTrue(read.Rows.All(r => r.Caveat == null), "a slot inside the source count carries no caveat");
        Assert.AreEqual(1, read.Removed);
        Assert.AreEqual(1, read.Added);

        string key;
        var lines = CompositionBake.ConstraintSection(read, null, out key);
        Assert.AreEqual("lostConstraintRefs=2", key);
        Assert.IsTrue(lines.Any(l => l.StartsWith("| `VrcHost` | VRCParentConstraint | `Sources.source2.SourceTransform` | `Doomed` |")));
        Assert.IsTrue(lines.Any(l => l.Contains("removed 1 constraint component(s) and made 1")));
    }

    [Test]
    public void TheSection_saysNoneForAnEmptyRead_andUnreadWhenThereWasNoSnapshot()
    {
        string key;
        var empty = CompositionBake.ConstraintSection(
            new CompositionBake.ConstraintRead { Rows = new List<CompositionBake.LostRefRow>() }, "Hair/", out key);
        Assert.AreEqual("lostConstraintRefs=0", key);
        Assert.IsTrue(empty.Any(l => l.StartsWith("| _(none)_ |")));
        StringAssert.Contains("Hair/", empty[0]);

        // No snapshot means nothing was compared, which must never read as nothing lost.
        var unread = CompositionBake.ConstraintSection(
            CompositionBake.ReadLostConstraintRefs(new GameObject("Clone"), null), null, out key);
        Assert.AreEqual("lostConstraintRefs=unread", key);
        Assert.IsFalse(unread.Any(l => l.StartsWith("| _(none)_ |")));
        Assert.IsTrue(unread.Any(l => l.Contains("did not complete")));
    }

    // ── Geometry: the UV column ─────────────────────────────────────────────────────────────────────

    [Test]
    public void TheUvColumn_isTheBuiltLayout_andAnAuthoredLayoutThatDiffersIsNamed()
    {
        var root = new GameObject("Geo");
        Skinned(root, "TwoSets", UvMesh(uv0: true, uv1Wide: true));
        Skinned(root, "Bare", UvMesh(uv0: false, uv1Wide: false));

        var rows = CompositionBake.ReadGeometry(root);
        Assert.AreEqual("0:2 1:4", rows.Single(r => r.Path == "TwoSets").Uv);
        Assert.AreEqual("none", rows.Single(r => r.Path == "Bare").Uv, "no channel is `none`; `—` is no row on this side");

        // The same renderer, authored with one narrow set and built with a wide second one.
        var authored = new List<CompositionBake.GeoRow> { new CompositionBake.GeoRow { Path = "Body", Skinned = true, Active = true, Tris = 3, Uv = "0:2" } };
        var built = new List<CompositionBake.GeoRow> { new CompositionBake.GeoRow { Path = "Body", Skinned = true, Active = true, Tris = 3, Uv = "0:2 1:4" } };
        string keys;
        var lines = CompositionBake.GeometrySection(authored, built, null, out keys);
        var row = lines.Single(l => l.StartsWith("| `Body` |"));
        StringAssert.Contains("| 3 | 3 | 0:2 1:4 |", row);
        StringAssert.Contains("authored uv: 0:2", row);

        var gone = CompositionBake.GeometrySection(authored, new List<CompositionBake.GeoRow>(), null, out keys);
        StringAssert.Contains("| 3 | — | — |", gone.Single(l => l.StartsWith("| `Body` |")));
    }

    // ── Probe ───────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void AProbe_isHandedTheClone_andItsTextIsFencedWhole()
    {
        var clone = new GameObject("Clone");
        string key;
        var lines = CompositionBake.ProbeSection(go => go.name + "\n```\nstatus: pending", clone, out key);

        Assert.AreEqual("probe=ok", key);
        int open = lines.IndexOf("````");   // one longer than the three the text carries
        int close = lines.LastIndexOf("````");
        Assert.Greater(close, open, "the probe's text is fenced, with a fence its own backticks cannot close");
        Assert.AreEqual("Clone\n```\nstatus: pending", string.Join("\n", lines.Skip(open + 1).Take(close - open - 1)));
    }

    [Test]
    public void AProbeThatReturnsNothing_isToldApartFromOneThatThrew()
    {
        var clone = new GameObject("Clone");
        string key;

        var nul = CompositionBake.ProbeSection(go => null, clone, out key);
        Assert.AreEqual("probe=ok", key);
        CollectionAssert.Contains(nul, "(returned null)");

        var empty = CompositionBake.ProbeSection(go => "", clone, out key);
        Assert.AreEqual("probe=ok", key);
        CollectionAssert.Contains(empty, "(returned empty)");

        var threw = CompositionBake.ProbeSection(go => throw new System.InvalidOperationException("boom"), clone, out key);
        Assert.AreEqual("probe=threw", key);
        Assert.IsTrue(threw.Any(l => l.Contains("InvalidOperationException") && l.Contains("boom")));
    }

    [Test]
    public void AProbeWithoutBake_isRefusedBeforeTheAvatarIsResolved()
    {
        LogAssert.Expect(LogType.Error, new Regex("probe"));
        string result = ReportComposition.Run("NoSuchAvatarAnywhere", bake: false, probe: go => "x");

        StringAssert.StartsWith("[ReportComposition] FAIL: probe", result);
        StringAssert.Contains("bake:true", result);
    }

    // ── The artifact: finished, and one back ────────────────────────────────────────────────────────

    [Test]
    public void OnlyAFinishedBake_isKeptAsThePreviousOne()
    {
        string dir = Path.Combine(Path.GetTempPath(), "b1-prev-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "composition-bake_X.md");
            string prev = path + CompositionBake.PreviousSuffix;

            Assert.IsFalse(CompositionBake.KeepPrevious(path), "no artifact, nothing to keep");

            File.WriteAllText(path, "# ReportComposition (bake): X\n\nstatus: pending\n");
            Assert.IsFalse(CompositionBake.KeepPrevious(path), "a pending stub is not a bake");
            File.WriteAllText(path, "# ReportComposition (bake)\n\nstatus: FAILED\n\nsummary: … => FAIL (x)\n");
            Assert.IsFalse(CompositionBake.KeepPrevious(path), "a refusal is not a bake");
            Assert.IsFalse(File.Exists(prev));

            File.WriteAllText(path, "summary: first => OK\n");
            Assert.IsTrue(CompositionBake.KeepPrevious(path));
            Assert.AreEqual("summary: first => OK\n", File.ReadAllText(prev));

            // A failed re-bake must not cost the last good one, and the next good one replaces it.
            File.WriteAllText(path, "# ReportComposition (bake)\n\nstatus: FAILED\n");
            Assert.IsFalse(CompositionBake.KeepPrevious(path));
            Assert.AreEqual("summary: first => OK\n", File.ReadAllText(prev));
            File.WriteAllText(path, "summary: second => OK\n");
            Assert.IsTrue(CompositionBake.KeepPrevious(path));
            Assert.AreEqual("summary: second => OK\n", File.ReadAllText(prev));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void AFinishedArtifact_isNotPending_whateverItsBodyQuotes()
    {
        Assert.IsTrue(CompositionBake.IsFinished("summary: [ReportComposition] X => OK\n\n## Probe\n\nstatus: pending\n"));
        Assert.IsFalse(CompositionBake.IsFinished("# ReportComposition (bake): X\n\nstatus: pending\n"));
        Assert.IsFalse(CompositionBake.IsFinished(null));
    }

    // ── Fixture ─────────────────────────────────────────────────────────────────────────────────────

    private static GameObject Child(GameObject root, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(root.transform);
        return go;
    }

    private static void Skinned(GameObject root, string name, Mesh mesh) =>
        Child(root, name).AddComponent<SkinnedMeshRenderer>().sharedMesh = mesh;

    private static Mesh UvMesh(bool uv0, bool uv1Wide)
    {
        var m = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
        if (uv0) m.SetUVs(0, new List<Vector2> { Vector2.zero, Vector2.zero, Vector2.zero });
        if (uv1Wide) m.SetUVs(1, new List<Vector4> { Vector4.one, Vector4.one, Vector4.one });
        return m;
    }
}
