using System.Collections.Generic;

namespace Ryan6Vrc.AvatarTools.Editor
{
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
        public bool Aap; public VrcParamMeta Vrc; // null unless a vrc: block was declared
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
        public string To;                       // target state/submachine name; null == "to Exit"
        public bool ToExit;
        public List<Condition> When = new List<Condition>();
        public bool CanTransitionToSelf;        // AnyState ladder only
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
