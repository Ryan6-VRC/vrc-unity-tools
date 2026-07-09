using System.Collections.Generic;
using System.Text;

namespace Ryan6Vrc.AvatarTools.Editor
{
    // Addressing-name escaping. A state/sub-machine NAME may legally contain the path separator '/' (or a
    // literal '\'). In a transition-target / default PATH context, escape per SEGMENT so the separator stays
    // unambiguous: '\' -> '\\', '/' -> '\/'. This lives in the BARE-SCALAR layer, BELOW YAML string-quoting:
    // the decoder escapes here first; if the escaped value still needs YAML double-quoting, Quote doubles the
    // '\' and the reader halves it, so the two escapings COMPOSE (no separate compound-hazard handling needed).
    // The compiler splits a path on UNescaped '/' then unescapes each segment. Bare names with no unescaped
    // '/' and the Task-2 'Sub/A' / leading-'/' forms are unaffected.
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

    // Typed model for the animator-write substrate. Plain POCOs (System.* only, no Unity API) so the
    // parser and later compiler stages can be unit-checked without the editor. Every type/field here is
    // the shared contract that AnimatorSchemaYaml (this task) and later ControllerEmit tasks reference
    // verbatim — do not rename.
    public enum BindingBasis { AvatarRoot, MountRoot }
    public enum ControllerRole { Fx, BaseFx, Gesture, Action, Sitting, TPose, IkPose, Additive, Base }
    public enum AnimParamType { Bool, Int, Float }
    public enum LayerBlend { Override, Additive }
    public enum TransitionInterruption { None, Source, Destination, SourceThenDestination, DestinationThenSource }
    public enum TreeKind { OneD, SimpleDirectional2D, FreeformDirectional2D, FreeformCartesian2D, Direct }
    public enum CondOp { Is, IsNot, Greater, Less, Equals, NotEqual }

    // Parameter names the compiler owns — an authored document must NOT declare these (SchemaValidation
    // refuses them), so emission can inject them without colliding with a user param.
    public static class ReservedNames
    {
        // The seconds-only carrier's scratch float: ControllerEmit declares it on the controller on first
        // use to give a duration-only clip an honest, resolvable Animator-property curve; it is never listed
        // in the emitted VRCExpressionParameters.
        public const string CarrierParam = "_CompilerNull";
    }

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
        public Dictionary<string, object> ReservedNotes = new Dictionary<string, object>();
        public string SourcePath;
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
    public sealed class VrcParamMeta { public bool Synced; public bool Saved; public bool Osc; public AnimParamType? VrcType; }

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
        public string DefaultState;             // name within this machine (state or submachine)
        public List<Transition> EntryLadder = new List<Transition>();   // ordered
        public List<Transition> AnyLadder = new List<Transition>();     // ordered; each has CanTransitionToSelf
        public List<Behaviour> Behaviours = new List<Behaviour>();
    }
    public sealed class SubMachine { public string Name; public StateMachine Machine = new StateMachine(); }

    // Schema-tree traversal shared by every consumer that flattens a layer's states across the whole
    // sub-machine tree (WD hoist, state-count budget checks, RunLog summaries). One walk, so a new
    // schema-tree consumer costs no fourth copy — the spec's one-edit-site health bar. Null-safe: skips a
    // null state or sub-machine rather than throwing (a well-formed document has none; malformed input
    // degrades instead of faulting).
    public static class SchemaTree
    {
        // Every state under this machine, recursing sub-machines at any depth, appended to `into`.
        public static void CollectStates(this StateMachine sm, List<State> into)
        {
            if (sm == null) return;
            foreach (var s in sm.States) if (s != null) into.Add(s);
            foreach (var sub in sm.Machines) if (sub != null && sub.Machine != null) sub.Machine.CollectStates(into);
        }

        // Count of the same traversal, without materializing the list.
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
        public bool CanTransitionToSelf;        // AnyState ladder only
        public bool Mute; public bool Solo;     // AnimatorStateTransition editor flags (state + AnyState ladders)
        // null == inherit Defaults:
        public float? Duration; public float? ExitTime; public bool? FixedDuration;
        public TransitionInterruption? Interruption; public bool? OrderedInterruption;
    }
    public struct Condition { public string Param; public CondOp Op; public float Value; }

    // Motion: exactly one of Clip / RefPath / RefGuid / Tree is set (Clip refers into AnimDocument.Clips by name).
    public sealed class MotionRef
    {
        public string Clip;                     // inline-clip name
        public string RefPath;                  // "Assets/.../Walk.anim"
        public GuidRef RefGuid;                 // { guid, fileID, unresolved }
        public BlendTreeSpec Tree;
    }
    public sealed class GuidRef { public string Guid; public long FileID; public bool Unresolved; }

    public sealed class BlendTreeSpec
    {
        public TreeKind Kind;
        public string Param;                    // blend param (X) - Direct uses per-child DirectWeight instead
        public string ParamY;                   // 2D only
        public bool? Normalized;                // Direct only: the "Normalized Blend Values" runtime toggle
                                                // (sum-to-1 vs raw additive). null ⇒ Unity's construction default.
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
    // Seconds != null with no Sets/Curves == duration-only (compiler emits an inert carrier later).
    public sealed class ClipSpec
    {
        public string Name;
        public float? Seconds;                  // declared length (duration-only or curve length override)
        public Dictionary<string, float> Sets = new Dictionary<string, float>();   // binding -> constant
        public List<CurveSpec> Curves = new List<CurveSpec>();
    }
    public sealed class CurveSpec { public string Binding; public List<Keyframe2> Keys = new List<Keyframe2>(); }
    public struct Keyframe2 { public float Time; public float Value; public Keyframe2(float t, float v){ Time=t; Value=v; } }

    public sealed class Behaviour
    {
        public string Kind;                     // "driver" | "tracking" | "playableLayer" | "locomotion" | "poseSpace" | "playAudio" | "layerControl"
        public Dictionary<string, object> Fields = new Dictionary<string, object>(); // decoded per-kind by ControllerEmit later
    }

    public sealed class SchemaException : System.Exception
    {
        public SchemaException(string message) : base(message) {}
    }
}
