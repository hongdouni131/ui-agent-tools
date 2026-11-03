using UnityEngine;

namespace UIAgent.Tools
{
    /// <summary>Stable identity for generated UI nodes, independent of editable GameObject names.</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class UiNodeIdentity : MonoBehaviour
    {
        [SerializeField] private string id;
        public string Id => id;
    }
}
