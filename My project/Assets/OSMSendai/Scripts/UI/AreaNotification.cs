using OsmSendai.Data;
using OsmSendai.World;
using UnityEngine;
using UnityEngine.UI;

namespace OsmSendai.UI
{
    public sealed class AreaNotification : MonoBehaviour
    {
        private PlaceEntry[] _neighbourhoods;
        private PlaceEntry[] _quarters;

        private Canvas _canvas;
        private CanvasGroup _canvasGroup;
        private Text _text;
        private Image _background;

        private FloatingOrigin _floatingOrigin;
        private Transform _player;

        private string _currentArea = "";
        private float _nextCheckTime;

        // Animation state
        private enum AnimState { Idle, FadeIn, Hold, FadeOut }
        private AnimState _animState = AnimState.Idle;
        private float _animTimer;

        private const float FadeInDuration = 0.5f;
        private const float HoldDuration = 2.0f;
        private const float FadeOutDuration = 1.0f;
        private const float CheckInterval = 0.5f;

        public void Initialize(PlaceEntry[] places, FloatingOrigin floatingOrigin, Transform player)
        {
            _floatingOrigin = floatingOrigin;
            _player = player;

            // Split places into categories
            var nh = new System.Collections.Generic.List<PlaceEntry>();
            var qt = new System.Collections.Generic.List<PlaceEntry>();
            foreach (var p in places)
            {
                if (p.type == "neighbourhood") nh.Add(p);
                else if (p.type == "quarter" || p.type == "suburb" || p.type == "city") qt.Add(p);
            }
            _neighbourhoods = nh.ToArray();
            _quarters = qt.ToArray();

            CreateUI();
            _canvasGroup.alpha = 0f;
        }

        private void CreateUI()
        {
            // Canvas
            var canvasGo = new GameObject("AreaNotification_Canvas");
            canvasGo.transform.SetParent(transform, false);
            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 100;
            canvasGo.AddComponent<CanvasScaler>();
            canvasGo.AddComponent<GraphicRaycaster>();

            // CanvasGroup for fade
            _canvasGroup = canvasGo.AddComponent<CanvasGroup>();
            _canvasGroup.blocksRaycasts = false;
            _canvasGroup.interactable = false;

            // Background panel at bottom center
            var bgGo = new GameObject("Background");
            bgGo.transform.SetParent(canvasGo.transform, false);
            _background = bgGo.AddComponent<Image>();
            _background.color = new Color(0f, 0f, 0f, 0.5f);
            var bgRect = bgGo.GetComponent<RectTransform>();
            bgRect.anchorMin = new Vector2(0.3f, 0f);
            bgRect.anchorMax = new Vector2(0.7f, 0.08f);
            bgRect.offsetMin = Vector2.zero;
            bgRect.offsetMax = Vector2.zero;

            // Text
            var textGo = new GameObject("AreaText");
            textGo.transform.SetParent(bgGo.transform, false);
            _text = textGo.AddComponent<Text>();
            if (WorldBootstrap.JapaneseFont != null)
                _text.font = WorldBootstrap.JapaneseFont;
            _text.fontSize = 28;
            _text.alignment = TextAnchor.MiddleCenter;
            _text.color = Color.white;
            _text.horizontalOverflow = HorizontalWrapMode.Overflow;
            _text.verticalOverflow = VerticalWrapMode.Overflow;
            var textRect = textGo.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
        }

        private void Update()
        {
            if (_player == null || _neighbourhoods == null) return;

            // Check area at intervals
            if (Time.unscaledTime >= _nextCheckTime)
            {
                _nextCheckTime = Time.unscaledTime + CheckInterval;
                CheckArea();
            }

            // Animate
            UpdateAnimation();
        }

        private void CheckArea()
        {
            var offset = _floatingOrigin != null ? _floatingOrigin.accumulatedOffset : Vector3.zero;
            var globalX = (float)(_player.position.x + offset.x);
            var globalZ = (float)(_player.position.z + offset.z);

            var nearestNh = FindNearest(globalX, globalZ, _neighbourhoods);
            var nearestQt = FindNearest(globalX, globalZ, _quarters);

            string area;
            if (nearestQt != null && nearestNh != null)
                area = nearestQt.name + " \u00B7 " + nearestNh.name;
            else if (nearestQt != null)
                area = nearestQt.name;
            else if (nearestNh != null)
                area = nearestNh.name;
            else
                area = "";

            if (area != _currentArea && !string.IsNullOrEmpty(area))
            {
                _currentArea = area;
                _text.text = area;
                _animState = AnimState.FadeIn;
                _animTimer = 0f;
            }
        }

        private static PlaceEntry FindNearest(float x, float z, PlaceEntry[] entries)
        {
            PlaceEntry best = null;
            var bestDist = float.MaxValue;
            for (var i = 0; i < entries.Length; i++)
            {
                var dx = entries[i].x - x;
                var dy = entries[i].y - z;
                var d = dx * dx + dy * dy;
                if (d < bestDist) { bestDist = d; best = entries[i]; }
            }
            return best;
        }

        private void UpdateAnimation()
        {
            switch (_animState)
            {
                case AnimState.FadeIn:
                    _animTimer += Time.unscaledDeltaTime;
                    _canvasGroup.alpha = Mathf.Clamp01(_animTimer / FadeInDuration);
                    if (_animTimer >= FadeInDuration)
                    {
                        _animState = AnimState.Hold;
                        _animTimer = 0f;
                    }
                    break;
                case AnimState.Hold:
                    _animTimer += Time.unscaledDeltaTime;
                    _canvasGroup.alpha = 1f;
                    if (_animTimer >= HoldDuration)
                    {
                        _animState = AnimState.FadeOut;
                        _animTimer = 0f;
                    }
                    break;
                case AnimState.FadeOut:
                    _animTimer += Time.unscaledDeltaTime;
                    _canvasGroup.alpha = 1f - Mathf.Clamp01(_animTimer / FadeOutDuration);
                    if (_animTimer >= FadeOutDuration)
                    {
                        _animState = AnimState.Idle;
                    }
                    break;
            }
        }
    }
}
