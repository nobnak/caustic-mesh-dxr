using UnityEngine;

namespace CausticMeshDxr
{
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-100)]
    public sealed class ReceiverRotation : MonoBehaviour
    {
        [Tooltip("Local-space rotation speed in degrees per second for the X, Y, and Z axes.")]
        [SerializeField] Vector3 degreesPerSecond;

        public Vector3 DegreesPerSecond => degreesPerSecond;

        public bool IsRotating => degreesPerSecond.sqrMagnitude > 1e-8f;

        void Update()
        {
            if (!IsRotating)
                return;

            transform.Rotate(degreesPerSecond * Time.deltaTime, Space.Self);
        }
    }
}
