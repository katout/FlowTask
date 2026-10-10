using UnityEngine;
using UnityEngine.UI;

namespace Katout.FlowTask.Samples
{
    /// <summary>
    /// Sample view of <see cref="PauseMenu"/>: the HUD's pause button, the menu and the settings screen, built in code. The
    /// settings screen's volume slider is wired to <see cref="GameSettings.Volume"/> by whoever owns both (the demo).
    /// </summary>
    public sealed class PauseMenuView : MonoBehaviour
    {
        RectTransform _menu;
        RectTransform _settings;
        bool _menuOpen;
        bool _settingsOpen;

        public Button PauseButton { get; private set; }
        public Button ResumeButton { get; private set; }
        public Button SettingsButton { get; private set; }
        public Button SettingsCloseButton { get; private set; }
        public Slider VolumeSlider { get; private set; }

        public bool IsMenuOpen
        {
            get => _menuOpen;
            set
            {
                _menuOpen = value;
                SampleUi.SetVisible(_menu, value);
            }
        }

        public bool IsSettingsOpen
        {
            get => _settingsOpen;
            set
            {
                _settingsOpen = value;
                SampleUi.SetVisible(_settings, value);
            }
        }

        public static PauseMenuView Create(Transform parent)
        {
            var go = new GameObject("PauseMenu", typeof(RectTransform));
            var root = (RectTransform)go.transform;
            root.SetParent(parent, false);
            root.anchorMin = Vector2.zero;
            root.anchorMax = Vector2.one;
            root.sizeDelta = Vector2.zero;
            var view = go.AddComponent<PauseMenuView>();

            view.PauseButton = SampleUi.Button(root, "Pause");
            var pause = (RectTransform)view.PauseButton.transform;
            pause.anchorMin = pause.anchorMax = pause.pivot = Vector2.one; // top right
            pause.anchoredPosition = new Vector2(-16, -16);

            view._menu = SampleUi.Panel(root, "Menu", 280);
            SampleUi.Label(view._menu, "Paused").alignment = TextAnchor.MiddleCenter;
            view.ResumeButton = SampleUi.Button(view._menu, "Resume");
            view.SettingsButton = SampleUi.Button(view._menu, "Settings");

            view._settings = SampleUi.Panel(root, "Settings", 280);
            SampleUi.Label(view._settings, "Volume");
            view.VolumeSlider = DefaultControls.CreateSlider(new DefaultControls.Resources()).GetComponent<Slider>();
            view.VolumeSlider.transform.SetParent(view._settings, false);
            view.VolumeSlider.gameObject.AddComponent<LayoutElement>().minHeight = 24;
            view.VolumeSlider.value = 1;
            view.SettingsCloseButton = SampleUi.Button(view._settings, "Close");

            view.IsMenuOpen = false;
            view.IsSettingsOpen = false;
            return view;
        }
    }
}
