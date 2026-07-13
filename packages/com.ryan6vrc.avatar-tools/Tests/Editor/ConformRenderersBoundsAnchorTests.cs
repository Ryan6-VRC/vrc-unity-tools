using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Ryan6Vrc.AvatarTools.Editor;

// Exercises SetBoundsAndAnchor's bounds/anchor branches. The owned root has no humanoid Animator, so the
// Hips anchor resolves via FindDescendantByName ("Hips") — the identical `if (hips != null)` write block
// the humanoid path reaches, so this covers the real write logic without a humanoid avatar asset. An
// empty source makes materials FAIL, but the bounds/anchor step (step 3) runs regardless, and these
// assertions read renderer state + the summary disposition tokens, not the PASS verdict.
public class ConformRenderersBoundsAnchorTests
{
    static GameObject Child(GameObject parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        return go;
    }

    // Owned root with a name-based "Hips" and `smrCount` SMRs. Each SMR gets an empty mesh assigned FIRST
    // so a localBounds the caller sets afterward sticks (assigning a mesh resets localBounds).
    static GameObject BuildOwned(int smrCount, out Transform hips, out SkinnedMeshRenderer[] smrs)
    {
        var root = new GameObject("Owned");
        hips = Child(root, "Hips").transform;
        smrs = new SkinnedMeshRenderer[smrCount];
        for (int i = 0; i < smrCount; i++)
        {
            var smr = Child(root, "Mesh" + i).AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = new Mesh();
            smrs[i] = smr;
        }
        return root;
    }

    static string Conform(GameObject owned, bool whatIf = false)
    {
        // The empty source always makes materials FAIL (see the type comment), and a FAIL summary is
        // emitted via Debug.LogError — expected, or the runner flags an unhandled error log.
        LogAssert.Expect(LogType.Error, new Regex("=> FAIL"));
        var src = new GameObject("Src");
        var summary = ConformRenderers.Run(owned, src, null, whatIf);
        Object.DestroyImmediate(src);
        return summary;
    }

    [Test]
    public void Bounds_grows_a_tight_box_to_the_standard()
    {
        var owned = BuildOwned(1, out _, out var smrs);
        smrs[0].localBounds = new Bounds(new Vector3(0.1f, 0.2f, 0f), new Vector3(0.6f, 0.6f, 0.6f)); // extents 0.3, inside standard
        Conform(owned);
        Assert.AreEqual(Vector3.zero, smrs[0].localBounds.center, "tight box grown to standard center");
        Assert.AreEqual(new Vector3(1f, 1f, 1f), smrs[0].localBounds.extents, "tight box grown to standard extents");
        Object.DestroyImmediate(owned);
    }

    [Test]
    public void Bounds_keeps_a_box_larger_than_the_standard()
    {
        var owned = BuildOwned(1, out _, out var smrs);
        smrs[0].localBounds = new Bounds(Vector3.zero, new Vector3(3f, 3f, 3f)); // extents 1.5 > standard
        var summary = Conform(owned);
        Assert.AreEqual(new Vector3(1.5f, 1.5f, 1.5f), smrs[0].localBounds.extents, "larger box preserved, not shrunk");
        StringAssert.Contains("boundsKeptLarger=1", summary);
        Object.DestroyImmediate(owned);
    }

    [Test]
    public void Anchor_null_is_set_to_hips()
    {
        var owned = BuildOwned(1, out var hips, out var smrs);
        Assert.IsNull(smrs[0].probeAnchor, "precondition: anchor starts null");
        var summary = Conform(owned);
        Assert.AreSame(hips, smrs[0].probeAnchor, "null anchor repaired to Hips");
        StringAssert.Contains("anchorsSet=1", summary);
        Object.DestroyImmediate(owned);
    }

    [Test]
    public void Anchor_valid_internal_is_preserved()
    {
        var owned = BuildOwned(1, out _, out var smrs);
        var chest = Child(owned, "Chest").transform; // internal, not Hips
        smrs[0].probeAnchor = chest;
        var summary = Conform(owned);
        Assert.AreSame(chest, smrs[0].probeAnchor, "valid internal anchor preserved, not rewritten to Hips");
        StringAssert.Contains("anchorsPreserved=1", summary);
        Object.DestroyImmediate(owned);
    }

    [Test]
    public void Anchor_external_is_repaired_to_hips()
    {
        var owned = BuildOwned(1, out var hips, out var smrs);
        var external = new GameObject("External"); // outside ownedRoot
        smrs[0].probeAnchor = external.transform;
        var summary = Conform(owned);
        Assert.AreSame(hips, smrs[0].probeAnchor, "external anchor repaired to Hips");
        StringAssert.Contains("anchorsSet=1", summary);
        Object.DestroyImmediate(external);
        Object.DestroyImmediate(owned);
    }

    [Test]
    public void WhatIf_reports_disposition_without_mutating()
    {
        var owned = BuildOwned(1, out _, out var smrs);
        smrs[0].localBounds = new Bounds(Vector3.zero, new Vector3(3f, 3f, 3f)); // would be kept-larger
        var summary = Conform(owned, whatIf: true);                              // anchor null → would be set
        Assert.IsNull(smrs[0].probeAnchor, "whatIf did not set the anchor");
        Assert.AreEqual(new Vector3(1.5f, 1.5f, 1.5f), smrs[0].localBounds.extents, "whatIf did not change bounds");
        StringAssert.Contains("anchorsSet=1", summary);
        StringAssert.Contains("boundsKeptLarger=1", summary);
        Object.DestroyImmediate(owned);
    }

    [Test]
    public void Unmatched_renderer_lands_in_the_envelope_offender_grammar()
    {
        var owned = BuildOwned(1, out _, out _); // "Mesh0" has no counterpart in the empty source
        var summary = Conform(owned);
        StringAssert.Contains("offenders=[", summary);
        StringAssert.Contains("unmatched: renderer 'Mesh0'", summary);

        // The written RunLog is the shared envelope, not the old bespoke shape.
        var path = summary.Substring(summary.IndexOf("log=") + 4);
        var json = System.IO.File.ReadAllText(path);
        StringAssert.Contains("\"kind\": \"conform-renderers\"", json);
        StringAssert.Contains("\"offenders\": [", json);
        StringAssert.Contains("unmatched: renderer 'Mesh0'", json);
        StringAssert.DoesNotContain("\"mismatches\"", json);
        UnityEditor.AssetDatabase.DeleteAsset(path);
        Object.DestroyImmediate(owned);
    }

    // G25: a mergeable (no humanoid rig, no 'Hips' transform) must PASS with an anchor NOTE, not FAIL on a
    // missing anchor. Source MATCHES the one renderer with a real material so the material step passes —
    // isolating the anchor's contribution: pre-fix this run FAILed via an AnchorWarning offender.
    [Test]
    public void Mergeable_without_Hips_passes_with_anchor_note_not_fail()
    {
        var owned = new GameObject("Hair"); // no Animator, no 'Hips' child
        var smr = Child(owned, "Mesh0").AddComponent<SkinnedMeshRenderer>();
        smr.sharedMesh = new Mesh();
        Assert.IsNull(smr.probeAnchor, "precondition: anchor starts null (as-authored)");

        var src = new GameObject("Src");
        var mat = new Material(Shader.Find("Unlit/Color")); // real material (not null / not Default-Material)
        Child(src, "Mesh0").AddComponent<MeshRenderer>().sharedMaterials = new[] { mat };

        var summary = ConformRenderers.Run(owned, src); // PASS logs at Log level — no LogAssert needed

        StringAssert.Contains("=> PASS", summary);
        StringAssert.Contains("probeAnchor left as-authored", summary); // the anchor note, in notes=[…]
        Assert.IsNull(smr.probeAnchor, "mergeable anchor left as-authored (repair skipped), never repaired");

        Object.DestroyImmediate(mat);
        Object.DestroyImmediate(src);
        Object.DestroyImmediate(owned);
    }

    [Test]
    public void Null_arg_fails_through_the_runlog_grammar_not_a_bare_line()
    {
        LogAssert.Expect(LogType.Error, new Regex("ownedRoot is null"));
        var summary = ConformRenderers.Run(null, null);
        StringAssert.Contains("error=ownedRoot is null => FAIL | log=", summary);
        var path = summary.Substring(summary.IndexOf("log=") + 4);
        Assert.IsTrue(System.IO.File.Exists(path), "guard FAIL must write a RunLog: " + path);
        UnityEditor.AssetDatabase.DeleteAsset(path);
    }
}
