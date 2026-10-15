using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.AgentTools.Editor;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;

/// <summary>
/// The built-menu section reports the clone's expressions menu control by control. The cases pin what a reader
/// acts on without a second look: a submenu the build shows twice is listed twice, a cycle and the depth cap
/// are named rather than silently cut, an unread menu publishes no table, and <c>paramFilter</c> narrows the
/// rows but never the summary count. Menus are built in memory, so no bake is needed.
/// </summary>
public class CompositionBuiltMenuTests
{
    private static VRCExpressionsMenu Menu(params VRCExpressionsMenu.Control[] controls)
    {
        var m = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
        m.controls = controls.ToList();
        return m;
    }

    private static VRCExpressionsMenu.Control Toggle(string name, string param, float value = 1f) =>
        new VRCExpressionsMenu.Control
        {
            name = name, type = VRCExpressionsMenu.Control.ControlType.Toggle, value = value,
            parameter = new VRCExpressionsMenu.Control.Parameter { name = param },
        };

    private static VRCExpressionsMenu.Control Sub(string name, VRCExpressionsMenu menu) =>
        new VRCExpressionsMenu.Control { name = name, type = VRCExpressionsMenu.Control.ControlType.SubMenu, subMenu = menu };

    private static string Row(List<string> lines, string path) => lines.Single(l => l.StartsWith("| `" + path + "` |"));

    [Test]
    public void ANestedToggle_isRowedUnderItsBuiltPath_withItsBuiltParameterAndValue()
    {
        var root = Menu(Sub("Outfit", Menu(Toggle("Jacket", "__MA/AutoParam/Jacket$8028c65bc2d4"), Toggle("Mode B", "Mode", 2f))));
        string key;
        var lines = CompositionBake.BuiltMenuSection(CompositionBake.ReadMenu(root), null, out key);

        Assert.AreEqual("builtMenuControls=3", key);
        Assert.AreEqual("| `Outfit` | SubMenu | — | — | — |", Row(lines, "Outfit"));
        Assert.AreEqual("| `Outfit/Jacket` | Toggle | `__MA/AutoParam/Jacket$8028c65bc2d4` | 1 | — |", Row(lines, "Outfit/Jacket"));
        StringAssert.Contains("| `Mode` | 2 |", Row(lines, "Outfit/Mode B"));
    }

    [Test]
    public void ARadial_carriesItsSubParameters()
    {
        var radial = new VRCExpressionsMenu.Control
        {
            name = "Size", type = VRCExpressionsMenu.Control.ControlType.RadialPuppet,
            parameter = new VRCExpressionsMenu.Control.Parameter { name = "" },
            subParameters = new[] { new VRCExpressionsMenu.Control.Parameter { name = "Bust" } },
        };
        string key;
        var lines = CompositionBake.BuiltMenuSection(CompositionBake.ReadMenu(Menu(radial)), null, out key);
        Assert.AreEqual("| `Size` | RadialPuppet | — | — | `Bust` |", Row(lines, "Size"));
    }

    [Test]
    public void ASubmenuReachedFromTwoParents_isListedTwice()
    {
        var shared = Menu(Toggle("Socks", "Socks"));
        var root = Menu(Sub("A", shared), Sub("B", shared));
        var read = CompositionBake.ReadMenu(root);

        CollectionAssert.AreEqual(new[] { "A", "A/Socks", "B", "B/Socks" }, read.Rows.Select(r => r.Path).ToArray());
        Assert.IsEmpty(read.Notes, "a repeat is not a cycle");
    }

    [Test]
    public void ACycle_isCutOnce_andNamed()
    {
        var inner = Menu(Toggle("T", "T"));
        var root = Menu(Sub("Loop", inner));
        inner.controls.Add(Sub("Back", root));
        var read = CompositionBake.ReadMenu(root);

        CollectionAssert.AreEqual(new[] { "Loop", "Loop/T", "Loop/Back" }, read.Rows.Select(r => r.Path).ToArray());
        StringAssert.Contains("`Loop/Back` opens a menu already open above it", read.Notes.Single());
    }

    [Test]
    public void TheDepthCap_isNamed()
    {
        var menu = Menu(Toggle("Leaf", "Leaf"));
        for (int i = 0; i < 14; i++) menu = Menu(Sub("L" + i, menu));
        var read = CompositionBake.ReadMenu(menu);
        StringAssert.Contains("stopped at depth 12", read.Notes.Single());
        Assert.IsFalse(read.Rows.Any(r => r.Path.EndsWith("/Leaf")));
    }

    [Test]
    public void AnUnreadMenu_publishesNoTable()
    {
        string key;
        var lines = CompositionBake.BuiltMenuSection(new CompositionBake.BuiltMenuRead { Error = "the clone descriptor's expressionsMenu is null" }, null, out key);
        Assert.AreEqual("builtMenuControls=unread", key);
        Assert.IsFalse(lines.Any(l => l.StartsWith("| path |")));
        StringAssert.Contains("expressionsMenu is null", lines.Single(l => l.StartsWith("**The built menu was not read")));
    }

    [Test]
    public void ParamFilter_narrowsTheRows_butNotTheCount()
    {
        var radial = new VRCExpressionsMenu.Control
        {
            name = "Size", type = VRCExpressionsMenu.Control.ControlType.RadialPuppet,
            subParameters = new[] { new VRCExpressionsMenu.Control.Parameter { name = "Hair_Length" } },
        };
        var root = Menu(Toggle("Ears", "Kemono_Ear"), Toggle("Twin", "Hair_TwinTail"), radial);
        string key;
        var lines = CompositionBake.BuiltMenuSection(CompositionBake.ReadMenu(root), "hair", out key);

        Assert.AreEqual("builtMenuControls=3", key);
        Assert.IsFalse(lines.Any(l => l.StartsWith("| `Ears` |")));
        Row(lines, "Twin");
        Row(lines, "Size");
    }
}
