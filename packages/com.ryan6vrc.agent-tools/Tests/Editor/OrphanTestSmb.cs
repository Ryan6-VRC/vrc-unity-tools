using UnityEngine;

// A trivial StateMachineBehaviour used by CheckAnimatorTests to plant an orphan SMB sub-asset.
// Unity resolves a StateMachineBehaviour's MonoScript through its same-named file; otherwise a
// "No script asset for …" console error fires whenever an instance is serialized.
public class OrphanTestSmb : StateMachineBehaviour { }
