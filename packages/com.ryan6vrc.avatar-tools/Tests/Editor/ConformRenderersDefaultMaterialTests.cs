using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Ryan6Vrc.AvatarTools.Editor;

// The default-material gate FAILs a conform run on its own and decides by identity, not name; these tests pin
// both polarities. A negative measured over the vendor corpus proves nothing here: ConformRenderers runs on our
// avatars after a transplant, which is where an unremapped material leaves a built-in slot behind.
public class ConformRenderersDefaultMaterialTests
{
    // If the handle and the production predicate's GUID+localId fallback both failed, the gate would silently
    // never fire; this is what catches that.
    [Test]
    public void Recognizes_the_real_builtin_default_material()
    {
        var builtin = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Material.mat");
        Assert.IsNotNull(builtin, "Unity's built-in Default-Material did not resolve — the gate's instrument is broken");
        Assert.IsTrue(ConformRenderers.IsBuiltinDefaultMaterial(builtin));
    }

    [Test]
    public void Does_not_mistake_a_project_material_that_merely_shares_the_name()
    {
        var impostor = new Material(Shader.Find("Standard"));
        try
        {
            impostor.name = "Default-Material";
            Assert.AreEqual("Default-Material", impostor.name, "the impostor must satisfy a name-only compare");
            Assert.IsFalse(ConformRenderers.IsBuiltinDefaultMaterial(impostor),
                "identity, not name, is what decides — this material is not Unity's built-in");
        }
        finally { Object.DestroyImmediate(impostor); }
    }

    // A null slot is the other branch: it counts as a null-slot offender, never a default-material one.
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
