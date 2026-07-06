using UnityEngine;

// A trivial StateMachineBehaviour used only by SweepControllerTests to plant an orphan SMB sub-asset.
// SMBs are ScriptableObjects (minted via ScriptableObject.CreateInstance), so the tests' AddOrphan<T>
// new() helper cannot make one. It lives in its own same-named file because Unity requires a
// StateMachineBehaviour subclass to be filed that way to resolve its MonoScript asset (otherwise a
// "No script asset for …" console error fires whenever an instance is serialized).
public class SweepTestDummySmb : StateMachineBehaviour { }
