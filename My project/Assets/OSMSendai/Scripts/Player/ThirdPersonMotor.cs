using UnityEngine;
using UnityEngine.Rendering;

namespace OsmSendai.Player
{
    public sealed class ThirdPersonMotor : MonoBehaviour
    {
        [Header("References")]
        public Transform cameraTransform;

        [Header("Movement")]
        public float moveSpeed = 14f;
        public float sprintMultiplier = 1.6f;
        public float rotationSpeed = 540f;
        public float gravity = -25f;
        public float groundedStickForce = -2f;

        [Header("Facing")]
        public bool faceCameraWhenRightMouseHeld = true;
        public bool alwaysFaceCameraFromPointerDelta = true;
        public float facingOrbitDeadzone = 0.01f;
        public bool allowLeftClickOrbitInput = true;
        public KeyCode orbitModifierKey = KeyCode.LeftAlt;

        [Header("Flight")]
        public KeyCode toggleFlightKey = KeyCode.F;
        public float flightSpeed = 35f;
        public float flightSprintMultiplier = 2.2f;
        public KeyCode ascendKey = KeyCode.E;
        public KeyCode descendKey = KeyCode.Q;
        public bool autoEnterFlightWhenAscendPressed = true;

        private CharacterController _controller;
        private float _verticalVelocity;
        private bool _isFlying;

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
            if (_controller == null)
            {
                _controller = gameObject.AddComponent<CharacterController>();
            }

            if (toggleFlightKey == KeyCode.None) toggleFlightKey = KeyCode.F;
            if (ascendKey == KeyCode.None) ascendKey = KeyCode.E;
            if (descendKey == KeyCode.None) descendKey = KeyCode.Q;
            if (flightSpeed <= 0f) flightSpeed = 35f;
            if (flightSprintMultiplier < 1f) flightSprintMultiplier = 2.2f;

            _controller.height = Mathf.Max(1.6f, _controller.height);
            _controller.radius = Mathf.Max(0.3f, _controller.radius);
            _controller.center = new Vector3(0f, _controller.height * 0.5f, 0f);
            _controller.stepOffset = Mathf.Clamp(_controller.stepOffset, 0.2f, 0.5f);

            EnsureDebugVisual();
        }

        private void Update()
        {
            if (cameraTransform == null && Camera.main != null)
            {
                cameraTransform = Camera.main.transform;
            }

            if (Input.GetKeyDown(toggleFlightKey))
            {
                _isFlying = !_isFlying;
                _verticalVelocity = 0f;
            }

            var inputX = Input.GetAxisRaw("Horizontal");
            var inputZ = Input.GetAxisRaw("Vertical");
            var planarInput = new Vector3(inputX, 0f, inputZ);
            planarInput = Vector3.ClampMagnitude(planarInput, 1f);
            var sprint = Input.GetKey(KeyCode.LeftShift);
            var ascendHeld = Input.GetKey(ascendKey) || Input.GetKey(KeyCode.Space);

            if (!_isFlying && autoEnterFlightWhenAscendPressed && ascendHeld)
            {
                _isFlying = true;
                _verticalVelocity = 0f;
            }

            if (_isFlying)
            {
                HandleFlying(planarInput, sprint);
                return;
            }

            HandleGrounded(planarInput, sprint);
        }

        private void HandleGrounded(Vector3 planarInput, bool sprint)
        {
            var moveDirection = planarInput;
            if (cameraTransform != null)
            {
                var camForward = cameraTransform.forward;
                camForward.y = 0f;
                camForward.Normalize();

                var camRight = cameraTransform.right;
                camRight.y = 0f;
                camRight.Normalize();

                moveDirection = camForward * planarInput.z + camRight * planarInput.x;
            }

            if (_controller.isGrounded && _verticalVelocity < 0f)
            {
                _verticalVelocity = groundedStickForce;
            }

            _verticalVelocity += gravity * Time.deltaTime;

            var speed = moveSpeed * (sprint ? sprintMultiplier : 1f);
            var horizontalVelocity = moveDirection * speed;
            var velocity = horizontalVelocity + Vector3.up * _verticalVelocity;

            _controller.Move(velocity * Time.deltaTime);

            RotateTowardsCameraOrMovement(moveDirection);
        }

        private void HandleFlying(Vector3 planarInput, bool sprint)
        {
            _verticalVelocity = 0f;

            var upInput = 0f;
            if (Input.GetKey(ascendKey) || Input.GetKey(KeyCode.Space))
            {
                upInput += 1f;
            }
            if (Input.GetKey(descendKey) || Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
            {
                upInput -= 1f;
            }

            var moveDirection = planarInput + Vector3.up * upInput;
            if (cameraTransform != null)
            {
                var camForward = cameraTransform.forward;
                var camRight = cameraTransform.right;
                var worldUp = Vector3.up;
                moveDirection = camForward * planarInput.z + camRight * planarInput.x + worldUp * upInput;
            }

            moveDirection = Vector3.ClampMagnitude(moveDirection, 1f);
            var speed = flightSpeed * (sprint ? flightSprintMultiplier : 1f);
            _controller.Move(moveDirection * speed * Time.deltaTime);

            RotateTowardsCameraOrMovement(moveDirection);
        }

        private void RotateTowardsCameraOrMovement(Vector3 moveDirection)
        {
            var mouseX = Input.GetAxis("Mouse X");
            var mouseY = Input.GetAxis("Mouse Y");
            if (faceCameraWhenRightMouseHeld && cameraTransform != null && IsOrbitInputActive(mouseX, mouseY))
            {
                var cameraForward = cameraTransform.forward;
                cameraForward.y = 0f;
                if (cameraForward.sqrMagnitude > 0.0001f)
                {
                    var look = Quaternion.LookRotation(cameraForward.normalized, Vector3.up);
                    transform.rotation = Quaternion.RotateTowards(transform.rotation, look, rotationSpeed * Time.deltaTime);
                    return;
                }
            }

            if (moveDirection.sqrMagnitude > 0.0001f)
            {
                var targetRotation = Quaternion.LookRotation(moveDirection, Vector3.up);
                transform.rotation = Quaternion.RotateTowards(
                    transform.rotation,
                    targetRotation,
                    rotationSpeed * Time.deltaTime);
            }
        }

        private bool IsOrbitInputActive(float mouseX, float mouseY)
        {
            if (alwaysFaceCameraFromPointerDelta)
            {
                var hasDelta = Mathf.Abs(mouseX) > facingOrbitDeadzone || Mathf.Abs(mouseY) > facingOrbitDeadzone;
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

        private void EnsureDebugVisual()
        {
            const string visualName = "VisualCapsule";
            var existing = transform.Find(visualName);
            if (existing != null)
            {
                ConfigureVisualRenderer(existing.gameObject);
                return;
            }

            var visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            visual.name = visualName;
            visual.transform.SetParent(transform, false);
            visual.transform.localPosition = new Vector3(0f, 1f, 0f);
            visual.transform.localRotation = Quaternion.identity;
            visual.transform.localScale = new Vector3(0.8f, 1f, 0.8f);

            var visualCollider = visual.GetComponent<Collider>();
            if (visualCollider != null)
            {
                Destroy(visualCollider);
            }

            ConfigureVisualRenderer(visual);
        }

        private static void ConfigureVisualRenderer(GameObject visual)
        {
            var renderer = visual.GetComponent<MeshRenderer>();
            if (renderer == null)
            {
                return;
            }

            renderer.shadowCastingMode = ShadowCastingMode.On;
            renderer.receiveShadows = true;
        }
    }
}
