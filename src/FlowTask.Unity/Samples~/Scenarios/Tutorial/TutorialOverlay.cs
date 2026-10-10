using System;
using UnityEngine;
using UnityEngine.UI;

namespace Katout.FlowTask.Samples
{
    /// <summary>
    /// Sample view of <see cref="Tutorial"/>: a balloon, a highlight and a Skip button. In a game it often lives on a canvas
    /// that survives scene changes (DontDestroyOnLoad), so what the tutorial shows must be hidden by the tutorial itself: a
    /// scene change does not hide it.
    /// </summary>
    public sealed class TutorialOverlay : MonoBehaviour
    {
        static readonly Color HighlightColor = new Color(1f, 0.85f, 0.2f, 0.35f);

        RectTransform _balloon;
        Text _balloonLabel;
        Image _highlight;
        string _balloonText;
        RectTransform _highlighted;

        public Button SkipButton { get; private set; }

        /// <summary>The text of the balloon on screen; null when hidden.</summary>
        public string BalloonText
        {
            get => _balloonText;
            private set
            {
                _balloonText = value;
                if (_balloonLabel != null) _balloonLabel.text = value ?? "";
                SampleUi.SetVisible(_balloon, value != null);
            }
        }

        /// <summary>The highlighted UI element; null when none.</summary>
        public RectTransform Highlighted
        {
            get => _highlighted;
            private set
            {
                _highlighted = value;
                if (_highlight == null) return;
                _highlight.color = value != null ? HighlightColor : Color.clear;
                if (value == null) return;
                // Over the target, which is on the same canvas (same scale).
                _highlight.rectTransform.position = value.TransformPoint(value.rect.center);
                _highlight.rectTransform.sizeDelta = value.rect.size;
            }
        }

        public static TutorialOverlay Create(string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var overlay = go.AddComponent<TutorialOverlay>();
            var root = (RectTransform)go.transform;
            root.anchorMin = Vector2.zero;
            root.anchorMax = Vector2.one;
            root.sizeDelta = Vector2.zero;

            overlay._highlight = new GameObject("Highlight", typeof(RectTransform), typeof(Image)).GetComponent<Image>();
            overlay._highlight.transform.SetParent(root, false);
            overlay._highlight.raycastTarget = false; // the highlighted button stays clickable
            overlay._highlight.color = Color.clear;

            overlay._balloon = SampleUi.Panel(root, "Balloon", 360);
            overlay._balloon.anchorMin = overlay._balloon.anchorMax = overlay._balloon.pivot = new Vector2(1, 0);
            overlay._balloon.anchoredPosition = new Vector2(-16, 16);
            overlay._balloonLabel = SampleUi.Label(overlay._balloon, "");
            overlay.SkipButton = SampleUi.Button(overlay._balloon, "Skip");
            SampleUi.SetVisible(overlay._balloon, false);
            return overlay;
        }

        /// <summary>Shows the balloon until the result is disposed (<c>using var balloon = overlay.ShowBalloon(text);</c>).</summary>
        public Shown ShowBalloon(string text)
        {
            BalloonText = text;
            return new Shown(this, false);
        }

        /// <summary>Highlights <paramref name="target"/> until the result is disposed.</summary>
        public Shown Highlight(RectTransform target)
        {
            Highlighted = target;
            return new Shown(this, true);
        }

        public readonly struct Shown : IDisposable
        {
            readonly TutorialOverlay _overlay;
            readonly bool _highlight;

            internal Shown(TutorialOverlay overlay, bool highlight)
            {
                _overlay = overlay;
                _highlight = highlight;
            }

            public void Dispose()
            {
                if (_overlay == null) return; // the overlay itself is gone
                if (_highlight) _overlay.Highlighted = null;
                else _overlay.BalloonText = null;
            }
        }
    }
}
