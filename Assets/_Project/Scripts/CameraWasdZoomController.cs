using UnityEngine;
using UnityEngine.InputSystem;

namespace Project
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    public sealed class CameraWasdZoomController : MonoBehaviour
    {
        [SerializeField] private float _panSpeed = 12f;
        [SerializeField] private float _fastPanMultiplier = 2f;
        [SerializeField] private float _zoomSpeed = 8f;
        [SerializeField] private float _minOrthoSize = 2f;
        [SerializeField] private float _maxOrthoSize = 30f;
        [SerializeField] private float _focusPlaneZ = 0f;
        [SerializeField] private bool _useUnscaledTime = true;

        private Camera _camera;

        private void Awake()
        {
            _camera = GetComponent<Camera>();
        }

        private void Update()
        {
            float dt = _useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
            if (dt <= 0f)
            {
                return;
            }

            UpdatePan(dt);
            UpdateZoomTowardMouse();
        }

        private void UpdatePan(float dt)
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return;
            }

            float x = 0f;
            float y = 0f;

            if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed)
            {
                x -= 1f;
            }

            if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed)
            {
                x += 1f;
            }

            if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed)
            {
                y -= 1f;
            }

            if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed)
            {
                y += 1f;
            }

            Vector2 move = new Vector2(x, y);
            if (move.sqrMagnitude > 1f)
            {
                move = move.normalized;
            }

            float speed = _panSpeed;
            if (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed)
            {
                speed *= _fastPanMultiplier;
            }

            Vector3 delta = new Vector3(move.x, move.y, 0f) * (speed * dt);
            transform.position += delta;
        }

        private void UpdateZoomTowardMouse()
        {
            if (_camera == null || !_camera.orthographic)
            {
                return;
            }

            Mouse mouse = Mouse.current;
            if (mouse == null)
            {
                return;
            }

            float scroll = mouse.scroll.ReadValue().y / 120f;
            if (Mathf.Abs(scroll) < 0.0001f)
            {
                return;
            }

            float planeDistance = Mathf.Abs(transform.position.z - _focusPlaneZ);
            Vector2 mousePosition = mouse.position.ReadValue();
            Vector3 worldBefore = _camera.ScreenToWorldPoint(new Vector3(mousePosition.x, mousePosition.y, planeDistance));

            float zoomFactor = Mathf.Exp(-scroll * _zoomSpeed * 0.05f);
            float newSize = Mathf.Clamp(_camera.orthographicSize * zoomFactor, _minOrthoSize, _maxOrthoSize);
            _camera.orthographicSize = newSize;

            Vector3 worldAfter = _camera.ScreenToWorldPoint(new Vector3(mousePosition.x, mousePosition.y, planeDistance));
            Vector3 delta = worldBefore - worldAfter;
            transform.position += new Vector3(delta.x, delta.y, 0f);
        }
    }
}
