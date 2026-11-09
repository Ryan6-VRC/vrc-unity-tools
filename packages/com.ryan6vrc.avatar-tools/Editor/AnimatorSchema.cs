using System.Collections.Generic;
using System.Text;

namespace Ryan6Vrc.AvatarTools.Editor
{
    // Addressing-name escaping. A state/sub-machine NAME may legally contain '/' or '\', so a transition-target
    // or default PATH escapes per SEGMENT: '\' -> '\\', '/' -> '\/'. This is the BARE-SCALAR layer, BELOW YAML
    // string-quoting: Quote doubles the '\' and the reader halves it, so the two escapings compose.
    public static class AddressPath
    {
        public static string EscapeSegment(string name)
            => name.Replace("\\", "\\\\").Replace("/", "\\/");

        public static string UnescapeSegment(string seg)
        {
            var sb = new StringBuilder(seg.Length);
            for (int i = 0; i < seg.Length; i++)
            {
                if (seg[i] == '\\' && i + 1 < seg.Length) sb.Append(seg[++i]);
                else sb.Append(seg[i]);
            }
            return sb.ToString();
        }

        // Split on UNescaped '/' (a '/' not part of a '\x' escape pair). Segments keep their escapes; the
        // caller unescapes each. A leading absolute-'/' anchor is the caller's concern (stripped first).
        public static List<string> Split(string path)
        {
            var segs = new List<string>();
            var cur = new StringBuilder();
            for (int i = 0; i < path.Length; i++)
            {
                char c = path[i];
                if (c == '\\' && i + 1 < path.Length) { cur.Append(c).Append(path[++i]); }
                else if (c == '/') { segs.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(c);
            }
            segs.Add(cur.ToString());
            return segs;
        }
    }

    // Typed model for the animator-write substrate. Plain POCOs: System.* only, no Unity API.
    public enum BindingBasis { AvatarRoot, MountRoot }
    public enum ControllerRole { Fx, BaseFx, Gesture, Action, Sitting, TPose, IkPose, Additive, Base }
    public enum AnimParamType { Bool, Int, Float }
    public enum LayerBlend { Override, Additive }
    public enum TransitionInterruption { None, Source, Destination, SourceThenDestination, DestinationThenSource }
    public enum TreeKind { OneD, SimpleDirectional2D, FreeformDirectional2D, FreeformCartesian2D, Direct }
    public enum CondOp { Is, IsNot, Greater, Less, Equals, NotEqual }
    // The SDK's TwoAxisPuppet/FourAxisPuppet are out of vocabulary (docs/animator-schema.md §Not yet in the
    // schema); the parser refuses an unknown kind.
    public enum MenuControlKind { Button, Toggle, SubMenu, Radial }

    // Parameter names the compiler owns.
    public static class ReservedNames
    {
        // The seconds-only carrier's scratch float (docs/animator-schema.md §clips).
        public const string CarrierParam = "_CompilerNull";
    }

    // The SDK's per-menu control cap, mirrored here because SchemaValidation is System.*-only. ControllerEmit
    // asserts this against VRCExpressionsMenu.MAX_CONTROLS on every menu emit. Exceeding it is destructive: the
    // SDK's own menu inspector silently truncates `controls` to the cap on open
    // (VRCExpressionsMenuEditor.RefreshListSizeView), so an over-long menu loses controls the first time a
    // human looks at it.
    public static class MenuLimits { public const int MaxControlsPerMenu = 8; }

    public sealed class AnimDocument
    {
        public int Schema;
        public string ControllerName;
        public BindingBasis Basis;
        public ControllerRole Role = ControllerRole.Fx;
        public Defaults Defaults = new Defaults();
        public List<ParamSpec> Parameters = new List<ParamSpec>();
        public List<Layer> Layers = new List<Layer>();
        public List<ClipSpec> Clips = new List<ClipSpec>();
        // Named blend trees: each emits one BlendTree sub-asset however many motion slots reference it
        // (MotionRef.Shared).
        public List<BlendTreeSpec> Trees = new List<BlendTreeSpec>();
        public List<MenuControl> Menu;          // null == no menu: block (emit writes no menu asset)
        public Dictionary<string, object> ReservedNotes = new Dictionary<string, object>();
        public string SourcePath;
    }

    // One expression-menu control. The schema holds an ordered LIST, not a name-keyed map: display order is what
    // the wearer sees, and two controls may share a name.
    public sealed class MenuControl
    {
        public string Name;
        public MenuControlKind Kind;
        public string Param;                    // the driven parameter; null for a bare SubMenu
        public float Value = 1f;                // Button/Toggle: the value written while active
        public List<MenuControl> Controls;      // SubMenu only; null otherwise
        // The control's icon, as authored: a project path (it starts `Assets/` or `Packages/`) or a path
        // relative to THIS DOCUMENT's own directory. ControllerEmit.ResolveIcon owns the outcomes.
        public string Icon;
    }

    public sealed class Defaults
    {
        public bool WriteDefaults = true;
        public float TransitionDuration = 0f;
        public bool TransitionHasExitTime = false;
        public TransitionInterruption Interruption = TransitionInterruption.None;
    }

    public sealed class ParamSpec
    {
        public string Name; public AnimParamType Type; public float Default;
        public bool Aap; public bool Scratch; // Scratch == internal/working param, kept out of the emitted VRCExpressionParameters
        public VrcParamMeta Vrc;               // null unless a vrc: block was declared
    }
    public sealed class VrcParamMeta
    {
        public bool Synced; public bool Saved; public bool Osc; public AnimParamType? VrcType;
        // Wire encoding, null = not declared = the SDK's own default (8 bits, the type's default representation, default lerp).
        public int? Bits;        // 2..8, int/float wire types only
        public bool? Signed;     // true = signed range, false = unsigned; the SDK's default is signed for a float, unsigned for an int
        public float? Lerp;      // seconds; sets a custom lerp duration, 0 included
        public bool HasEncoding => Bits.HasValue || Signed.HasValue || Lerp.HasValue;
    }

    public sealed class Layer
    {
        public string Name; public float Weight = 1f; public string Mask; // asset path/ref or null
        public LayerBlend Blend = LayerBlend.Override;
        public bool? WriteDefaults;             // null -> inherit Defaults
        public StateMachine Root = new StateMachine();
    }

    public sealed class StateMachine
    {
        public List<State> States = new List<State>();
        public List<SubMachine> Machines = new List<SubMachine>();
        public string DefaultState;             // BARE local name within this machine (state or submachine)
        // A default state nested below this machine: a ROOT-RELATIVE address, always naming a state. Separate
        // from DefaultState because the two CO-EXIST in Unity (docs/animator-schema.md §`defaultState:`).
        public string DefaultStatePath;
        public List<Transition> EntryLadder = new List<Transition>();   // ordered
        public List<Transition> AnyLadder = new List<Transition>();     // ordered; each has CanTransitionToSelf
        public List<Behaviour> Behaviours = new List<Behaviour>();
        public MachineLayout Layout;
    }
    // OnExit == the sub-machine's OUTGOING transitions, fired when it reaches its own Exit. Unity models these
    // as AnimatorTransition (the entry-rung type), so they carry conditions + target and NO timing surface.
    // They live on the PARENT's model because that is the machine whose scope their targets resolve in.
    public sealed class SubMachine
    {
        public string Name;
        public StateMachine Machine = new StateMachine();
        public List<Transition> OnExit = new List<Transition>();
    }

    // Per-machine node layout. Null unless authored or captured. Contract: docs/animator-schema.md §layout.
    public sealed class MachineLayout
    {
        public Dictionary<string, float[]> Nodes = new Dictionary<string, float[]>();
        public float[] Entry, Any, Exit, Parent;   // [x, y] or null
    }

    // Traversal shared by every consumer that flattens a layer's states across the whole sub-machine tree.
    // Null-safe: skips a null state or sub-machine rather than throwing.
    public static class SchemaTree
    {
        public static void CollectStates(this StateMachine sm, List<State> into)
        {
            if (sm == null) return;
            foreach (var s in sm.States) if (s != null) into.Add(s);
            foreach (var sub in sm.Machines) if (sub != null && sub.Machine != null) sub.Machine.CollectStates(into);
        }

        public static int CountStates(this StateMachine sm)
        {
            if (sm == null) return 0;
            int n = 0;
            foreach (var s in sm.States) if (s != null) n++;
            foreach (var sub in sm.Machines) if (sub != null && sub.Machine != null) n += sub.Machine.CountStates();
            return n;
        }
    }

    public sealed class State
    {
        public string Name;
        public MotionRef Motion;                // null == deliberate empty state (motion: ~)
        public float Speed = 1f;
        public string SpeedParam;               // null unless speed-parameter binding
        public string MotionTimeParam;          // null unless motion-time binding
        public bool Mirror;
        public bool? WriteDefaults;             // per-state override (rare)
        public List<Transition> Transitions = new List<Transition>();
        public List<Behaviour> Behaviours = new List<Behaviour>();
    }

    public sealed class Transition
    {
        public string To;                       // bare == local; "Sub/State" == from layer root; "/Top" == absolute from root; null == "to Exit"
        public bool ToExit;
        public List<Condition> When = new List<Condition>();
        public bool CanTransitionToSelf;        // authored on the AnyState ladder only; a state rung's is derived from `To`
        public string Name;                     // null ⇒ Unity's empty default; state/AnyState only (refused on entry)
        public bool Mute; public bool Solo;     // AnimatorStateTransition editor flags (state + AnyState ladders)
        // Normalized time in the DESTINATION state's motion where playback begins (Unity m_TransitionOffset).
        // null == Unity's 0. A layer whose states share one clip at speed 0 and differ only by their incoming
        // offset is a POSE PICKER, so zeroing it collapses every pose onto frame 0. State + AnyState only; an
        // entry rung is an AnimatorTransition and has no timing surface.
        public float? Offset;
        // null == inherit Defaults:
        public float? Duration; public float? ExitTime; public bool? FixedDuration;
        public TransitionInterruption? Interruption; public bool? OrderedInterruption;
    }
    public struct Condition { public string Param; public CondOp Op; public float Value; }

    // Motion: exactly one field is set. Shared is the one form that does NOT own its motion: every MotionRef
    // carrying the same Shared name resolves to a single BlendTree sub-asset.
    public sealed class MotionRef
    {
        public string Clip;                     // inline-clip name
        public string RefPath;                  // "Assets/.../Walk.anim"
        public GuidRef RefGuid;                 // { guid, fileID, unresolved }
        public BlendTreeSpec Tree;
        public string Shared;                   // named tree under AnimDocument.Trees
    }
    public sealed class GuidRef { public string Guid; public long FileID; public bool Unresolved; }

    public sealed class BlendTreeSpec
    {
        public TreeKind Kind;
        public string Param;                    // blend param (X) - Direct uses per-child DirectWeight instead
        public string ParamY;                   // 2D only
        public bool? Normalized;                // Direct only: the "Normalized Blend Values" runtime toggle
                                                // (sum-to-1 vs raw additive). null ⇒ Unity's construction default.
        public string Name;                     // null ⇒ compiler auto-generates (<State>_BlendTree / <parent>_<i>)
        public List<TreeChild> Children = new List<TreeChild>();
    }
    public sealed class TreeChild
    {
        public MotionRef Motion;                // clip | ref | nested tree
        public float Threshold;                 // 1D
        public float PosX, PosY;                // 2D
        public string DirectWeight;             // Direct only
        public float TimeScale = 1f;            // negative legal
        public bool Mirror; public float CycleOffset;
    }

    // Inline clip: exactly one authoring form. Sets == constant single-key writes; Curves == keyframed;
    // Seconds != null with no Sets/Curves == duration-only (compiler emits an inert carrier).
    public sealed class ClipSpec
    {
        public string Name;
        public float? Seconds;                  // declared length (duration-only or curve length override)
        public bool Loop;                       // AnimationClipSettings.loopTime: the state replays the clip seamlessly
        public Dictionary<string, float> Sets = new Dictionary<string, float>();   // binding -> constant
        public List<CurveSpec> Curves = new List<CurveSpec>();
    }
    public enum CurveTangent { Flat, Linear, Stepped }
    public sealed class CurveSpec { public string Binding; public CurveTangent Tangents = CurveTangent.Flat; public List<Keyframe2> Keys = new List<Keyframe2>(); }
    public struct Keyframe2 { public float Time; public float Value; public Keyframe2(float t, float v){ Time=t; Value=v; } }

    public sealed class Behaviour
    {
        public string Kind;                     // "driver" | "tracking" | "playableLayer" | "locomotion" | "poseSpace" | "playAudio" | "layerControl" | "faceTrackingSets"
        public Dictionary<string, object> Fields = new Dictionary<string, object>(); // decoded per-kind by ControllerEmit
    }

    public sealed class SchemaException : System.Exception
    {
        public SchemaException(string message) : base(message) {}
    }
}
