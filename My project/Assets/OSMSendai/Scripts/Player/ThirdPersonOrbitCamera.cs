using UnityEngine;

namespace OsmSendai.Player
{
    public sealed class ThirdPersonOrbitCamera : MonoBehaviour
    {
        [Header("References")]
        public Transform target;

        [Header("Follow")]
        public Vector3 targetOffset = new Vector3(0f, 1.6f, 0f);
        public float distance = 6f;
        public float height = 2f;
        public float followSmooth = 12f;

        [Header("Rotation")]
        public float minPitch = -20f;
        public float maxPitch = 70f;
        public float mouseSensitivity = 180f;
        
        [Header("Input")]
        public bool alwaysOrbitFromPointerDelta = true;
        public float orbitDeadzone = 0.01f;
        public bool allowLeftClickOrbitInput = true;
        public KeyCode orbitModifierKey = KeyCode.LeftAlt;

        private float _yaw;
        private float _pitch;

        private void Start()
        {
            var euler = transform.eulerAngles;
            _yaw = euler.y;
            _pitch = NormalizePitch(euler.x);
        }

        private void LateUpdate()
        {
            if (target == null)
            {
                return;
            }

            var mouseX = Input.GetAxis("Mouse X");
            var mouseY = Input.GetAxis("Mouse Y");
            if (IsOrbitInputActive(mouseX, mouseY))
            {
                _yaw += mouseX * mouseSensitivity * Time.deltaTime;
                _pitch -= mouseY * mouseSensitivity * Time.deltaTime;
                _pitch = Mathf.Clamp(_pitch, minPitch, maxPitch);
            }

            var pivot = target.position + targetOffset;
            var rotation = Quaternion.Euler(_pitch, _yaw, 0f);

            var cameraOffset = rotation * new Vector3(0f, 0f, -distance);
            cameraOffset.y += height;
            var desiredPosition = pivot + cameraOffset;

            var t = 1f - Mathf.Exp(-followSmooth * Time.deltaTime);
            transform.position = Vector3.Lerp(transform.position, desiredPosition, t);

            var desiredRotation = Quaternion.LookRotation(pivot - transform.position, Vector3.up);
            transform.rotation = Quaternion.Slerp(transform.rotation, desiredRotation, t);
        }

        private static float NormalizePitch(float angle)
        {
            if (angle > 180f)
            {
                angle -= 360f;
            }

            return angle;
        }

        private bool IsOrbitInputActive(float mouseX, float mouseY)
        {
            if (alwaysOrbitFromPointerDelta)
            {
                var hasDelta = Mathf.Abs(mouseX) > orbitDeadzone || Mathf.Abs(mouseY) > orbitDeadzone;
                if (hasDelta)
                {
                    return true;
                }
            }

            if (Input.GetMouseButton(1))
            {
                return true;
            }

            if (allowLeftClickOrbitInput && Input.GetMouseButton(0))
            {
                return true;
            }

            if (orbitModifierKey != KeyCode.None && Input.GetKey(orbitModifierKey))
            {
                return true;
            }

            return false;
        }
    }
}
