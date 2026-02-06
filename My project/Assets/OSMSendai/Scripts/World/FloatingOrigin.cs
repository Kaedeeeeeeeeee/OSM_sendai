using UnityEngine;

namespace OsmSendai.World
{
    public sealed class FloatingOrigin : MonoBehaviour
    {
        [Tooltip("When the camera/player is farther than this distance (meters) from origin, shift the world back.")]
        public float shiftThresholdMeters = 5000f;

        [Tooltip("Root to shift. If empty, shifts this GameObject.")]
        public Transform worldRoot;

        [Tooltip("Camera to track. If empty, uses Camera.main.")]
        public Transform trackedTransform;

        public Vector3 accumulatedOffset { get; private set; }

        private void Awake()
        {
            if (worldRoot == null) worldRoot = transform;
        }

        private void LateUpdate()
        {
            var t = trackedTransform != null ? trackedTransform : (Camera.main != null ? Camera.main.transform : null);
            if (t == null) return;

            var pos = t.position;
            var planar = new Vector2(pos.x, pos.z);
            if (planar.magnitude < shiftThresholdMeters) return;

            var shift = new Vector3(-pos.x, 0f, -pos.z);
            worldRoot.position += shift;
            accumulatedOffset -= shift;
        }
    }
}

