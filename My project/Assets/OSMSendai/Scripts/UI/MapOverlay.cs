using System;
using OsmSendai.Data;
using OsmSendai.World;
using UnityEngine;
using UnityEngine.UI;
using Shadow = UnityEngine.UI.Shadow;
using Outline = UnityEngine.UI.Outline;

namespace OsmSendai.UI
{
    public sealed class MapOverlay : MonoBehaviour
    {
        private Canvas _canvas;
        private RawImage _mapImage;
        private RectTransform _viewportRect;
        private RectTransform _mapContent;
        private Image _playerMarker;
        private RectTransform _playerMarkerRect;

        private MapMetadata _meta;
        private FloatingOrigin _floatingOrigin;
        private Transform _player;
        private PlacesData _placesData;
        private Texture2D _mapTexture;

        private bool _isOpen;

        // Zoom / pan state
        private float _zoom = 1f;
        private Vector2 _panOffset;
        private float _fitWidth, _fitHeight;
        private Vector2 _lastViewportSize;

        // Drag tracking
        private bool _isDragging;
        private bool _wasDrag;
        private Vector2 _dragStartMouse;
        private Vector2 _dragStartPan;

        // Label LOD
        private struct LabelInfo
        {
            public RectTransform Rect;
            public float MinZoom;
            public float MaxZoom;
        }
        private LabelInfo[] _allLabels;
        private float _lastLabelZoom;

        // Constants
        const float MinZoom = 1f;
        const float MaxZoom = 12f;
        const float ZoomSpeed = 0.15f;
        const float DragThreshold = 5f;
        const float InitialZoom = 3f;

        public void Initialize(byte[] bmpBytes, MapMetadata meta, PlacesData placesData,
                               FloatingOrigin floatingOrigin, Transform player)
        {
            _meta = meta;
            _floatingOrigin = floatingOrigin;
            _player = player;
            _placesData = placesData;

            _mapTexture = new Texture2D(2, 2, TextureFormat.RGB24, false);
            ImageConversion.LoadImage(_mapTexture, bmpBytes);

            CreateUI();
            _canvas.gameObject.SetActive(false);
        }

        private void CreateUI()
        {
            // --- Canvas ---
            var canvasGo = new GameObject("MapOverlay_Canvas");
            canvasGo.transform.SetParent(transform, false);
            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 200;

            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            canvasGo.AddComponent<GraphicRaycaster>();

            // --- Dark background ---
            var bgGo = new GameObject("Background");
            bgGo.transform.SetParent(canvasGo.transform, false);
            var bgImage = bgGo.AddComponent<Image>();
            bgImage.color = new Color(0f, 0f, 0f, 0.7f);
            var bgRect = bgGo.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = Vector2.zero;
            bgRect.offsetMax = Vector2.zero;

            // --- MapViewport (clipping area) ---
            var viewportGo = new GameObject("MapViewport");
            viewportGo.transform.SetParent(canvasGo.transform, false);
            viewportGo.AddComponent<RectMask2D>();
            _viewportRect = viewportGo.GetComponent<RectTransform>();
            _viewportRect.anchorMin = new Vector2(0.05f, 0.05f);
            _viewportRect.anchorMax = new Vector2(0.95f, 0.95f);
            _viewportRect.offsetMin = Vector2.zero;
            _viewportRect.offsetMax = Vector2.zero;

            // --- MapContent (zoom/pan container) ---
            var contentGo = new GameObject("MapContent");
            _mapContent = contentGo.AddComponent<RectTransform>();
            _mapContent.SetParent(viewportGo.transform, false);
            _mapContent.anchorMin = new Vector2(0.5f, 0.5f);
            _mapContent.anchorMax = new Vector2(0.5f, 0.5f);
            _mapContent.pivot = new Vector2(0.5f, 0.5f);
            // sizeDelta set by RecalculateFitSize()

            // --- Map image (fills content) ---
            var mapGo = new GameObject("MapImage");
            mapGo.transform.SetParent(contentGo.transform, false);
            _mapImage = mapGo.AddComponent<RawImage>();
            _mapImage.texture = _mapTexture;
            _mapImage.raycastTarget = false;
            var imgRect = mapGo.GetComponent<RectTransform>();
            imgRect.anchorMin = Vector2.zero;
            imgRect.anchorMax = Vector2.one;
            imgRect.offsetMin = Vector2.zero;
            imgRect.offsetMax = Vector2.zero;

            // --- Player marker ---
            var markerGo = new GameObject("PlayerMarker");
            markerGo.transform.SetParent(contentGo.transform, false);
            _playerMarker = markerGo.AddComponent<Image>();
            _playerMarker.color = new Color(1f, 0.3f, 0.1f, 1f);
            _playerMarker.raycastTarget = false;
            _playerMarkerRect = markerGo.GetComponent<RectTransform>();
            _playerMarkerRect.anchorMin = new Vector2(0.5f, 0.5f);
            _playerMarkerRect.anchorMax = new Vector2(0.5f, 0.5f);
            _playerMarkerRect.pivot = new Vector2(0.5f, 0.5f);
            _playerMarkerRect.sizeDelta = new Vector2(12f, 12f);

            // --- Place labels ---
            CreateLabels();

            // Initial fit calculation (will be recalculated on open)
            RecalculateFitSize();
        }

        private void CreateLabels()
        {
            if (_placesData == null || _placesData.places == null)
            {
                _allLabels = Array.Empty<LabelInfo>();
                return;
            }

            // Count valid labels first
            int count = 0;
            foreach (var p in _placesData.places)
            {
                if (string.IsNullOrEmpty(p.name)) continue;
                if (string.IsNullOrEmpty(p.type)) continue;
                var minZ = MinZoomForType(p.type);
                if (minZ < 0f) continue; // unknown type
                var uv = WorldToUV(p.x, p.y);
                if (uv.x < 0f || uv.x > 1f || uv.y < 0f || uv.y > 1f) continue;
                count++;
            }

            _allLabels = new LabelInfo[count];
            int idx = 0;

            foreach (var p in _placesData.places)
            {
                if (string.IsNullOrEmpty(p.name)) continue;
                if (string.IsNullOrEmpty(p.type)) continue;
                var minZoom = MinZoomForType(p.type);
                if (minZoom < 0f) continue;
                var uv = WorldToUV(p.x, p.y);
                if (uv.x < 0f || uv.x > 1f || uv.y < 0f || uv.y > 1f) continue;

                var labelGo = new GameObject($"MapLabel_{p.name}");
                labelGo.transform.SetParent(_mapContent, false);
                var txt = labelGo.AddComponent<Text>();
                if (WorldBootstrap.JapaneseFont != null)
                    txt.font = WorldBootstrap.JapaneseFont;
                txt.text = p.name;
                txt.fontSize = FontSizeForType(p.type);
                txt.alignment = TextAnchor.MiddleCenter;
                txt.color = new Color(1f, 1f, 0.95f, 1f);
                txt.horizontalOverflow = HorizontalWrapMode.Overflow;
                txt.verticalOverflow = VerticalWrapMode.Overflow;
                txt.raycastTarget = false;

                var outline = labelGo.AddComponent<Outline>();
                outline.effectColor = new Color(0f, 0f, 0f, 0.8f);
                outline.effectDistance = new Vector2(1.5f, -1.5f);

                var shadow = labelGo.AddComponent<Shadow>();
                shadow.effectColor = new Color(0f, 0f, 0f, 0.6f);
                shadow.effectDistance = new Vector2(2f, -2f);

                var labelRect = labelGo.GetComponent<RectTransform>();
                labelRect.anchorMin = new Vector2(0.5f, 0.5f);
                labelRect.anchorMax = new Vector2(0.5f, 0.5f);
                labelRect.pivot = new Vector2(0.5f, 0.5f);
                labelRect.sizeDelta = new Vector2(200f, 30f);
                // Position in content-local space: (uv - 0.5) * fitSize
                // Will be set in RecalculateFitSize / RepositionLabels
                labelRect.anchoredPosition = Vector2.zero; // placeholder

                labelGo.SetActive(false); // will be set by UpdateLabelVisibility

                _allLabels[idx] = new LabelInfo
                {
                    Rect = labelRect,
                    MinZoom = minZoom,
                    MaxZoom = MaxZoomForType(p.type)
                };
                idx++;
            }
        }

        private void RecalculateFitSize()
        {
            if (_viewportRect == null) return;

            var vpSize = _viewportRect.rect.size;
            if (vpSize.x <= 0f || vpSize.y <= 0f) return;

            _lastViewportSize = vpSize;

            float imageAspect = (float)_mapTexture.width / _mapTexture.height;
            float viewportAspect = vpSize.x / vpSize.y;

            if (imageAspect > viewportAspect)
            {
                // Image wider than viewport — fit to width
                _fitWidth = vpSize.x;
                _fitHeight = vpSize.x / imageAspect;
            }
            else
            {
                // Image taller than viewport — fit to height
                _fitHeight = vpSize.y;
                _fitWidth = vpSize.y * imageAspect;
            }

            _mapContent.sizeDelta = new Vector2(_fitWidth, _fitHeight);

            RepositionLabelsAndMarker();
        }

        private void RepositionLabelsAndMarker()
        {
            if (_placesData == null || _placesData.places == null) return;

            int idx = 0;
            foreach (var p in _placesData.places)
            {
                if (string.IsNullOrEmpty(p.name)) continue;
                if (string.IsNullOrEmpty(p.type)) continue;
                var minZ = MinZoomForType(p.type);
                if (minZ < 0f) continue;
                var uv = WorldToUV(p.x, p.y);
                if (uv.x < 0f || uv.x > 1f || uv.y < 0f || uv.y > 1f) continue;

                if (idx < _allLabels.Length)
                {
                    _allLabels[idx].Rect.anchoredPosition = new Vector2(
                        (uv.x - 0.5f) * _fitWidth,
                        (uv.y - 0.5f) * _fitHeight
                    );
                }
                idx++;
            }
        }

        private static float MinZoomForType(string type)
        {
            switch (type)
            {
                case "city": return 1.0f;
                case "suburb": return 2.5f;
                case "quarter": return 2.5f;
                case "neighbourhood": return 5.0f;
                default: return -1f;
            }
        }

        private static float MaxZoomForType(string type)
        {
            switch (type)
            {
                case "city": return 2.5f;
                case "suburb": return 5.0f;
                default: return float.MaxValue;
            }
        }

        private static int FontSizeForType(string type)
        {
            switch (type)
            {
                case "city": return 28;
                case "suburb": return 24;
                case "quarter": return 22;
                case "neighbourhood": return 18;
                default: return 18;
            }
        }

        private Vector2 WorldToUV(float wx, float wz)
        {
            var u = (wx - _meta.worldMinX) / (_meta.worldMaxX - _meta.worldMinX);
            var v = (wz - _meta.worldMinZ) / (_meta.worldMaxZ - _meta.worldMinZ);
            return new Vector2(u, v);
        }

        private Vector2 UVToWorld(Vector2 uv)
        {
            var wx = _meta.worldMinX + uv.x * (_meta.worldMaxX - _meta.worldMinX);
            var wz = _meta.worldMinZ + uv.y * (_meta.worldMaxZ - _meta.worldMinZ);
            return new Vector2(wx, wz);
        }

        // ---- Zoom / Pan ----

        private void ClampPan()
        {
            var vpSize = _viewportRect.rect.size;
            float halfContentW = _fitWidth * _zoom * 0.5f;
            float halfContentH = _fitHeight * _zoom * 0.5f;
            float halfVpW = vpSize.x * 0.5f;
            float halfVpH = vpSize.y * 0.5f;

            float maxPanX = Mathf.Max(0f, halfContentW - halfVpW);
            float maxPanY = Mathf.Max(0f, halfContentH - halfVpH);

            _panOffset.x = Mathf.Clamp(_panOffset.x, -maxPanX, maxPanX);
            _panOffset.y = Mathf.Clamp(_panOffset.y, -maxPanY, maxPanY);
        }

        private void ApplyTransform()
        {
            _mapContent.localScale = new Vector3(_zoom, _zoom, 1f);
            _mapContent.anchoredPosition = _panOffset;

            UpdateLabelVisibility();
            UpdateLabelScale();
            UpdatePlayerMarker();
        }

        private void HandleZoom()
        {
            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(scroll) < 0.001f) return;

            // Get mouse position relative to viewport
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _viewportRect, Input.mousePosition, null, out var viewportPt))
                return;

            // Convert viewport-local to content-local
            var contentPt = (viewportPt - _panOffset) / _zoom;

            // Apply zoom
            _zoom *= 1f + scroll / ZoomSpeed;
            _zoom = Mathf.Clamp(_zoom, MinZoom, MaxZoom);

            // Adjust pan so the content point under cursor stays fixed
            _panOffset = viewportPt - contentPt * _zoom;

            ClampPan();
            ApplyTransform();
        }

        private void HandlePan()
        {
            if (Input.GetMouseButtonDown(0))
            {
                _isDragging = true;
                _wasDrag = false;
                _dragStartMouse = Input.mousePosition;
                _dragStartPan = _panOffset;
            }

            if (_isDragging && Input.GetMouseButton(0))
            {
                Vector2 delta = (Vector2)Input.mousePosition - _dragStartMouse;

                if (!_wasDrag && delta.magnitude > DragThreshold)
                    _wasDrag = true;

                if (_wasDrag)
                {
                    // Scale delta from screen pixels to canvas pixels
                    // The CanvasScaler uses ScaleWithScreenSize, so we need to account for the scale factor
                    float scaleFactor = _canvas.scaleFactor;
                    if (scaleFactor <= 0f) scaleFactor = 1f;
                    _panOffset = _dragStartPan + delta / scaleFactor;
                    ClampPan();
                    ApplyTransform();
                }
            }

            if (Input.GetMouseButtonUp(0))
            {
                _isDragging = false;
            }
        }

        // ---- Labels ----

        private void UpdateLabelVisibility()
        {
            if (_allLabels == null) return;

            // Only update when zoom crosses a threshold
            // Use a small epsilon to avoid flicker
            if (Mathf.Approximately(_lastLabelZoom, _zoom)) return;
            _lastLabelZoom = _zoom;

            for (int i = 0; i < _allLabels.Length; i++)
            {
                bool shouldShow = _zoom >= _allLabels[i].MinZoom && _zoom < _allLabels[i].MaxZoom;
                if (_allLabels[i].Rect.gameObject.activeSelf != shouldShow)
                    _allLabels[i].Rect.gameObject.SetActive(shouldShow);
            }
        }

        private void UpdateLabelScale()
        {
            if (_allLabels == null) return;

            float invZoom = 1f / _zoom;
            var scale = new Vector3(invZoom, invZoom, 1f);

            for (int i = 0; i < _allLabels.Length; i++)
            {
                if (_allLabels[i].Rect.gameObject.activeSelf)
                    _allLabels[i].Rect.localScale = scale;
            }
        }

        // ---- Player marker ----

        private void UpdatePlayerMarker()
        {
            if (_player == null || _playerMarkerRect == null) return;

            var offset = _floatingOrigin != null ? _floatingOrigin.accumulatedOffset : Vector3.zero;
            var gx = (float)(_player.position.x + offset.x);
            var gz = (float)(_player.position.z + offset.z);

            var uv = WorldToUV(gx, gz);

            // Position in content-local coords
            _playerMarkerRect.anchoredPosition = new Vector2(
                (uv.x - 0.5f) * _fitWidth,
                (uv.y - 0.5f) * _fitHeight
            );

            // Counter-scale to keep constant screen size
            float invZoom = 1f / _zoom;
            _playerMarkerRect.localScale = new Vector3(invZoom, invZoom, 1f);
        }

        // ---- Update loop ----

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.M))
            {
                if (_isOpen)
                    CloseMap();
                else
                    OpenMap();
            }

            if (!_isOpen) return;

            CheckViewportResize();
            HandleZoom();
            HandlePan();

            // Click-to-teleport (only if mouse up without drag)
            if (Input.GetMouseButtonUp(0) && !_wasDrag)
            {
                TryTeleport();
            }

            // Update marker every frame (player may be moving)
            UpdatePlayerMarker();
        }

        private void CheckViewportResize()
        {
            var vpSize = _viewportRect.rect.size;
            if (vpSize != _lastViewportSize && vpSize.x > 0f && vpSize.y > 0f)
            {
                RecalculateFitSize();
                ClampPan();
                ApplyTransform();
            }
        }

        // ---- Open / Close ----

        private void OpenMap()
        {
            _isOpen = true;
            _canvas.gameObject.SetActive(true);

            // Force layout rebuild so viewport rect is valid
            Canvas.ForceUpdateCanvases();
            RecalculateFitSize();

            // Center on player at initial zoom
            _zoom = InitialZoom;

            if (_player != null)
            {
                var offset = _floatingOrigin != null ? _floatingOrigin.accumulatedOffset : Vector3.zero;
                var gx = (float)(_player.position.x + offset.x);
                var gz = (float)(_player.position.z + offset.z);
                var uv = WorldToUV(gx, gz);

                var playerContentPos = new Vector2(
                    (uv.x - 0.5f) * _fitWidth,
                    (uv.y - 0.5f) * _fitHeight
                );
                _panOffset = -playerContentPos * _zoom;
            }
            else
            {
                _panOffset = Vector2.zero;
            }

            _lastLabelZoom = -1f; // force label update
            ClampPan();
            ApplyTransform();

            // Disable player controls
            if (_player != null)
            {
                var motor = _player.GetComponentInParent<OsmSendai.Player.ThirdPersonMotor>();
                if (motor != null) motor.enabled = false;
                var cam = _player.GetComponent<OsmSendai.Player.ThirdPersonOrbitCamera>();
                if (cam != null) cam.enabled = false;
            }

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void CloseMap()
        {
            _isOpen = false;
            _canvas.gameObject.SetActive(false);

            // Re-enable player controls
            if (_player != null)
            {
                var motor = _player.GetComponentInParent<OsmSendai.Player.ThirdPersonMotor>();
                if (motor != null) motor.enabled = true;
                var cam = _player.GetComponent<OsmSendai.Player.ThirdPersonOrbitCamera>();
                if (cam != null) cam.enabled = true;
            }

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        // ---- Teleport ----

        private void TryTeleport()
        {
            // Convert mouse position to viewport-local
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _viewportRect, Input.mousePosition, null, out var viewportPt))
                return;

            // Check if click is within viewport bounds
            var vpRect = _viewportRect.rect;
            if (!vpRect.Contains(viewportPt)) return;

            // Viewport-local → content-local → UV
            var contentLocal = (viewportPt - _panOffset) / _zoom;
            var uv = new Vector2(
                contentLocal.x / _fitWidth + 0.5f,
                contentLocal.y / _fitHeight + 0.5f
            );

            if (uv.x < 0f || uv.x > 1f || uv.y < 0f || uv.y > 1f) return;

            var world = UVToWorld(uv);

            // Account for floating origin offset
            var offset = _floatingOrigin != null ? _floatingOrigin.accumulatedOffset : Vector3.zero;
            var localX = world.x - (float)offset.x;
            var localZ = world.y - (float)offset.z;

            // Teleport player
            if (_player != null)
            {
                var cc = _player.GetComponentInParent<CharacterController>();
                if (cc != null) cc.enabled = false;
                _player.position = new Vector3(localX, 50f, localZ);
                if (cc != null) cc.enabled = true;
            }

            CloseMap();
        }
    }
}
