using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Ryan6Vrc.AvatarTools.Editor;

// The default-material gate, which FAILs a conform run on its own (`defaultMatSlots` is one of the ANDed
// conditions behind the PASS). It used to decide "this is Unity's built-in" by NAME, which any project
// material sharing that name satisfies — so the gate carried a false-FAIL vector, and nothing in the suite
// asserted the rule at all. The only prior reference deliberately avoided tripping it.
//
// Both polarities are the point. A negative measured over the vendor corpus proves nothing here: that corpus
// is the wrong population (ConformRenderers runs on OUR avatars after a transplant, which is exactly where an
// unremapped material leaves a built-in slot behind), and per docs/verify.md an instrument must be shown to
// see a positive before its negative is worth anything. These tests are that positive.
public class ConformRenderersDefaultMaterialTests
{
    // The real built-in must still be recognized — this is the "instrument can see a positive" half. If the
    // handle ever stops resolving, the production predicate falls back to GUID+localId and warns; if BOTH
    // routes failed, the gate would silently never fire and this is what catches that.
    [Test]
    public void Recognizes_the_real_builtin_default_material()
    {
        var builtin = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Material.mat");
        Assert.IsNotNull(builtin, "Unity's built-in Default-Material did not resolve — the gate's instrument is broken");
        Assert.IsTrue(ConformRenderers.IsBuiltinDefaultMaterial(builtin));
    }

    // The false-FAIL vector the name compare carried. A project material may legitimately be named
    // "Default-Material"; under the old rule it FAILed the whole conform run.
    [Test]
    public void Does_not_mistake_a_project_material_that_merely_shares_the_name()
    {
        var impostor = new Material(Shader.Find("Standard"));
        try
        {
            impostor.name = "Default-Material";
            Assert.AreEqual("Default-Material", impostor.name, "the impostor must satisfy the OLD name compare");
            Assert.IsFalse(ConformRenderers.IsBuiltinDefaultMaterial(impostor),
                "identity, not name, is what decides — this material is not Unity's built-in");
        }
        finally { Object.DestroyImmediate(impostor); }
    }

    // An ordinary material is neither, and a null slot is the OTHER branch entirely (null slots are counted
    // as null-slot offenders, never as default-material ones) — pinned so the two never merge.
    [Test]
    public void Ordinary_and_null_materials_are_not_the_builtin()
    {
        var ordinary = new Material(Shader.Find("Standard"));
        try
        {
            Assert.IsFalse(ConformRenderers.IsBuiltinDefaultMaterial(ordinary));
            Assert.IsFalse(ConformRenderers.IsBuiltinDefaultMaterial(null));
        }
        finally { Object.DestroyImmediate(ordinary); }
    }
}
