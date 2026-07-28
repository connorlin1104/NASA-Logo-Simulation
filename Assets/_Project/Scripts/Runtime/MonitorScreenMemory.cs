using UnityEngine;

namespace NasaSim
{
    /// <summary>
    /// Remembers the material the monitors were wearing before the screens were repainted, so
    /// "put the model's own screens back" has something to put back.
    ///
    /// It has to be stored in the SCENE, not in the tool: the original is a sub-asset inside
    /// SettingEnvo.fbx, and there is no way to ask the FBX which of its materials used to be on a
    /// particular slot. An editor window's own fields do not survive a domain reload either, so a
    /// serialized reference on a scene object is the only thing that outlives a script recompile.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MonitorScreenMemory : MonoBehaviour
    {
        [Tooltip("What the screen slots held before the Monitor Screens tool first touched them.")]
        public Material originalScreenMaterial;
    }
}
