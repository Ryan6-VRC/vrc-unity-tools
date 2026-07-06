using System;
using UnityEngine;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.Dynamics;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>Copy-order topo group for a deep-tier component. <c>Conservative</c> is the null-lookup
    /// case (no table row); never assigned to a descriptor.</summary>
    public enum TopoGroup { Collider, Contact, PhysBone, Constraint, Conservative }

    /// <summary>
    /// One deep-tier descriptor row: how the transplant engine treats one VRC component family.
    /// Field paths are SerializedProperty names (probed in order for the casing the SDK actually
    /// serializes). The token convention "Collection[].Field" means "iterate the Collection array
    /// and follow Field on each element" (used for VRC constraint Sources).
    /// </summary>
    public sealed class VrcComponentDescriptor
    {
        /// <summary>The type this row is keyed on (matched up a queried component's base chain).</summary>
        public readonly Type type;
        /// <summary>Anchor field(s), probed in order (first found wins). Used by Copy's leaf placement and Relocate's pin-and-rewire.</summary>
        public readonly string[] anchorFieldPaths;
        /// <summary>Hard dependencies — pull the referent or null+flag it (e.g. physbone colliders).</summary>
        public readonly string[] hardDepFieldPaths;
        /// <summary>Soft dependencies — drop the entry silently if its referent is absent (e.g. ignoreTransforms).</summary>
        public readonly string[] softDepFieldPaths;
        /// <summary>True for offset-anchor leaves (collider/contact) whose missing host GO may be auto-recreated under its parent bone.</summary>
        public readonly bool leafRecreateEligible;
        /// <summary>Copy-order topo group this row belongs to (drives the planner's seed partition / topo order).</summary>
        public readonly TopoGroup group;

        public VrcComponentDescriptor(Type type, string[] anchorFieldPaths, string[] hardDepFieldPaths,
                                      string[] softDepFieldPaths, bool leafRecreateEligible, TopoGroup group)
        {
            this.type = type;
            this.anchorFieldPaths = anchorFieldPaths ?? Array.Empty<string>();
            this.hardDepFieldPaths = hardDepFieldPaths ?? Array.Empty<string>();
            this.softDepFieldPaths = softDepFieldPaths ?? Array.Empty<string>();
            this.leafRecreateEligible = leafRecreateEligible;
            this.group = group;
        }
    }

    /// <summary>
    /// The closed, owned deep-tier table. Looked up by walking a component's <c>GetType()</c> base
    /// chain so a concrete subclass (e.g. <c>VRCPhysBoneCollider</c>, a concrete contact, a concrete
    /// <c>VRCConstraint</c>) resolves to its base row. A null lookup means the type is conservative-tier
    /// (copied type-blind, no dependency-follow / recreate / relocate). Kept VRC-SDK-only on purpose;
    /// MA/VRCFury/NDMF never get a row.
    /// </summary>
    public static class VrcComponentTable
    {
        public static readonly VrcComponentDescriptor[] Rows =
        {
            new VrcComponentDescriptor(typeof(VRCPhysBone),
                anchorFieldPaths:     new[] { "rootTransform" },
                hardDepFieldPaths:    new[] { "colliders" },
                softDepFieldPaths:    new[] { "ignoreTransforms" },
                leafRecreateEligible: false,
                group:                TopoGroup.PhysBone),

            new VrcComponentDescriptor(typeof(VRCPhysBoneColliderBase),
                anchorFieldPaths:     new[] { "rootTransform" },
                hardDepFieldPaths:    Array.Empty<string>(),
                softDepFieldPaths:    Array.Empty<string>(),
                leafRecreateEligible: true,
                group:                TopoGroup.Collider),

            new VrcComponentDescriptor(typeof(ContactBase),
                anchorFieldPaths:     new[] { "rootTransform" },
                hardDepFieldPaths:    Array.Empty<string>(),
                softDepFieldPaths:    Array.Empty<string>(),
                leafRecreateEligible: true,
                group:                TopoGroup.Contact),

            new VrcComponentDescriptor(typeof(VRCConstraintBase),
                anchorFieldPaths:     new[] { "TargetTransform", "targetTransform", "m_TargetTransform" },
                hardDepFieldPaths:    new[] { "Sources[].SourceTransform" },
                softDepFieldPaths:    Array.Empty<string>(),
                leafRecreateEligible: false,
                group:                TopoGroup.Constraint),
        };

        /// <summary>Descriptor for <paramref name="type"/>, walking its base chain; null if conservative-tier.</summary>
        public static VrcComponentDescriptor Lookup(Type type)
        {
            for (var t = type; t != null; t = t.BaseType)
                foreach (var row in Rows)
                    if (row.type == t) return row;
            return null;
        }

        /// <summary>Descriptor for a component instance's runtime type; null if conservative-tier.</summary>
        public static VrcComponentDescriptor Lookup(Component component)
            => component == null ? null : Lookup(component.GetType());

        public static bool IsDeepTier(Type type) => type != null && Lookup(type) != null;
        public static bool IsDeepTier(Component component) => Lookup(component) != null;
    }
}
