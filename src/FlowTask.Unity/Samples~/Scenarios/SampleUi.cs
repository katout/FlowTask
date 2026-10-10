using UnityEngine;
using UnityEngine.UI;

namespace Katout.FlowTask.Samples
{
    /// <summary>
    /// The plain uGUI of the sample views, built in code with DefaultControls (what the GameObject &gt; UI menu makes) so that
    /// the samples need no prefab. A game makes its views as prefabs; the flows only use their buttons and the values they
    /// show.
    /// </summary>
    public static class SampleUi
    {
        /// <summary>A box at the center of <paramref name="parent"/>: its children stack from the top, and it is as tall as they are.</summary>
        public static RectTransform Panel(Transform parent, string name, float width)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(CanvasGroup),
                typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.sizeDelta = new Vector2(width, 0);
            go.GetComponent<Image>().color = new Color(0.16f, 0.18f, 0.23f);
            var layout = go.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(16, 16, 16, 16);
            layout.spacing = 8;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            go.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            return rect;
        }

        public static Button Button(Transform parent, string label)
        {
            var go = DefaultControls.CreateButton(new DefaultControls.Resources());
            go.name = label;
            go.transform.SetParent(parent, false);
            go.AddComponent<LayoutElement>().minHeight = 36;
            var text = go.GetComponentInChildren<Text>();
            text.text = label;
            text.fontSize = 16;
            return go.GetComponent<Button>();
        }

        public static Text Label(Transform parent, string text)
        {
            var go = DefaultControls.CreateText(new DefaultControls.Resources());
            go.transform.SetParent(parent, false);
            var label = go.GetComponent<Text>();
            label.text = text;
            label.color = Color.white;
            label.fontSize = 16;
            label.raycastTarget = false;
            return label;
        }

        /// <summary>
        /// Shows or hides a <see cref="Panel"/> through its CanvasGroup. Not SetActive: a flow that unwinds because Unity
        /// deactivates its GameObject changes its views inside that deactivation, where SetActive is not allowed.
        /// </summary>
        public static void SetVisible(Component panel, bool visible)
        {
            if (panel == null) return; // destroyed with its scene
            var group = panel.GetComponent<CanvasGroup>();
            group.alpha = visible ? 1 : 0;
            group.interactable = visible;
            group.blocksRaycasts = visible;
        }
    }
}
