using System.Collections.Generic;
using Katout.FlowTask.Unity;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if FLOWTASK_SAMPLES_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

namespace Katout.FlowTask.Samples
{
    /// <summary>
    /// The demo scene (Demo.unity): one screen to try the samples. Press Play and use the buttons; Escape is the back key.
    /// The session's objects (clocks, back-key router, settings) are made when the demo starts and passed to the flows, and
    /// the demo's flow is bound to this GameObject, so leaving Play mode or unloading the scene ends every sample it runs.
    /// The clocks belong to that flow and leave the World with it, so loading the scene again adds no clocks to the World.
    /// </summary>
    public sealed class Demo : MonoBehaviour
    {
        static readonly string[] AssetKeys = { "Forest", "Castle", "Music", "Enemies" };

        readonly Queue<string> _log = new Queue<string>();
        Text _logLabel;
        int _hits;

        void Start()
        {
            var router = new BackKeyRouter();
            var input = gameObject.AddComponent<BackKeyInput>();
            input.Router = router;
            input.Unhandled += () => Log("Back key: nothing to close (a game would ask whether to quit)");
            gameObject.RunWhileActive(Run(router, CreateCanvas()));
        }

        async FlowTask Run(BackKeyRouter router, RectTransform canvas)
        {
            // Owned by this flow: the clocks leave the World when the demo ends.
            var clocks = GameClocks.ForCurrentScope();
            gameObject.AddComponent<PauseLink>().Clock = clocks.Game;
            // The demo runs on the UI clock: it goes on while the game is paused.
            await Flow.WithClock(clocks.Ui, RunSamples(clocks, router, canvas));
        }

        async FlowTask RunSamples(GameClocks clocks, BackKeyRouter router, RectTransform canvas)
        {
            var menu = SampleUi.Panel(canvas, "Samples", 260);
            menu.anchorMin = menu.anchorMax = menu.pivot = new Vector2(0, 1); // top left
            menu.anchoredPosition = new Vector2(16, -16);
            SampleUi.Label(menu, "FlowTask samples\nEscape: the back key");
            var confirm = SampleUi.Button(menu, "Confirm dialog");
            var tutorial = SampleUi.Button(menu, "Tutorial");
            var shop = SampleUi.Button(menu, "Shop");
            var loading = SampleUi.Button(menu, "Parallel loading");
            var hit = SampleUi.Button(menu, "Hit the enemy");

            _logLabel = SampleUi.Label(canvas, "");
            var log = _logLabel.rectTransform;
            log.anchorMin = log.anchorMax = log.pivot = Vector2.zero; // bottom left
            log.anchoredPosition = new Vector2(16, 16);
            log.sizeDelta = new Vector2(560, 140);
            _logLabel.alignment = TextAnchor.LowerLeft;

            var pauseView = PauseMenuView.Create(canvas);
            var settings = new GameSettings(new DemoSettingsStore(Log));
            pauseView.VolumeSlider.onValueChanged.AddListener(v => settings.Volume = v);
            var overlay = TutorialOverlay.Create("Tutorial");
            overlay.transform.SetParent(canvas, false);
            var steps = new[]
            {
                new TutorialStep("Hit the enemy.", (RectTransform)hit.transform, () => _hits > 0),
                new TutorialStep("Pause the game.", (RectTransform)pauseView.PauseButton.transform, () => clocks.Game.IsPausedInHierarchy),
                new TutorialStep("Resume it.", (RectTransform)pauseView.ResumeButton.transform, () => !clocks.Game.IsPausedInHierarchy),
            };

            Log("Pick a sample. Pausing freezes the enemy; the menus keep running.");
            await FlowTask.WhenAll(
                PauseMenu.Hud(clocks, pauseView, router, settings),
                Enemies(clocks, hit),
                OneSampleAtATime(clocks, router, canvas, confirm, tutorial, shop, loading, overlay, steps));
        }

        // One sample at a time: a press of another sample's button meanwhile reaches nobody.
        async FlowTask OneSampleAtATime(GameClocks clocks, BackKeyRouter router, RectTransform canvas, Button confirm, Button tutorial,
            Button shop, Button loading, TutorialOverlay overlay, TutorialStep[] steps)
        {
            using var confirmPressed = confirm.ClickedSignal();
            using var tutorialPressed = tutorial.ClickedSignal();
            using var shopPressed = shop.ClickedSignal();
            using var loadingPressed = loading.ClickedSignal();
            var store = new Shop(new DemoStore(), router, clocks.Ui, r => Log($"Shop: receipt {r.TransactionId} came after leaving; granted"));
            var loader = new DemoLoader();
            while (true)
            {
                var chosen = await FlowTask.Race(new[]
                {
                    confirmPressed.Next().WithoutResult(),
                    tutorialPressed.Next().WithoutResult(),
                    shopPressed.Next().WithoutResult(),
                    loadingPressed.Next().WithoutResult(),
                });
                if (chosen == 0)
                {
                    var ok = await ConfirmDialog.Show(canvas, "Buy the gem pack?", router, clocks.Ui);
                    Log(ok ? "Confirm dialog: OK" : "Confirm dialog: no (Cancel, the back key or 10 s)");
                }
                else if (chosen == 1)
                {
                    _hits = 0;
                    var done = await Tutorial.Run(overlay, steps);
                    Log($"Tutorial: {done} of {steps.Length} steps done");
                }
                else if (chosen == 2)
                {
                    await OpenShop(canvas, store, router);
                }
                else
                {
                    await LoadAssets(canvas, loader);
                }
            }
        }

        async FlowTask OpenShop(RectTransform canvas, Shop store, BackKeyRouter router)
        {
            var view = ShopView.Create(canvas);
            try
            {
                await ShopScreen.Run(view, store, router);
                Log("Shop: left with the back key");
            }
            finally
            {
                if (view != null) Destroy(view.gameObject);
            }
        }

        async FlowTask LoadAssets(RectTransform canvas, IAssetLoader loader)
        {
            var view = LoadingView.Create(canvas);
            try
            {
                var requests = await Loading.LoadAll(loader, AssetKeys, view);
                if (requests == null)
                {
                    Log("Loading: canceled");
                }
                else
                {
                    Log($"Loading: {requests.Length} assets loaded");
                    foreach (var request in requests) request.Release(); // the demo does not use them
                }
            }
            catch (AssetLoadException e)
            {
                Log("Loading: " + e.Message);
            }
            finally
            {
                if (view != null) Destroy(view.gameObject);
            }
        }

        // An enemy at a time; the next one comes a second of game time after the last one died.
        async FlowTask Enemies(GameClocks clocks, Button hit)
        {
            using var hitPressed = hit.ClickedSignal();
            var label = hit.GetComponentInChildren<Text>();
            var sprite = Sprite.Create(Texture2D.whiteTexture, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4);
            Enemy enemy = null;
            try
            {
                while (true)
                {
                    enemy = SpawnEnemy(clocks, sprite);
                    while (enemy != null) // destroyed after its death animation
                    {
                        var r = await FlowTask.Race(hitPressed.Next(), ShowEnemy(enemy, label));
                        if (r.Index != 0 || enemy == null) continue;
                        _hits++;
                        enemy.TakeHit(1); // like a bullet: while the game is paused, the hit waits for the resume
                    }

                    Log("Enemy: defeated; the next one comes in a second");
                    await FlowTask.WaitForSeconds(1, clocks.Game);
                }
            }
            finally
            {
                if (enemy != null) Destroy(enemy.gameObject);
                Destroy(sprite);
            }
        }

        static Enemy SpawnEnemy(GameClocks clocks, Sprite sprite)
        {
            var go = new GameObject("Enemy", typeof(SpriteRenderer));
            go.GetComponent<SpriteRenderer>().sprite = sprite;
            go.transform.position = new Vector3(-3, 0, 0);
            go.transform.localScale = Vector3.one * 1.5f;
            var enemy = go.AddComponent<Enemy>();
            enemy.Clocks = clocks; // before its Start, which starts the brain
            enemy.Waypoints = new[] { new Vector3(-3, 0, 0), new Vector3(3, 0, 0) };
            return enemy;
        }

        // Shows what the enemy's brain does: white while patrolling, red while flinching, grey once dead.
        static async FlowTask ShowEnemy(Enemy enemy, Text label)
        {
            var sprite = enemy.GetComponent<SpriteRenderer>();
            while (enemy != null)
            {
                sprite.color = enemy.IsDead ? Color.grey : enemy.Hp == null || enemy.IsPatrolling ? Color.white : new Color(1f, 0.35f, 0.35f);
                if (enemy.Hp != null) label.text = $"Hit the enemy (HP {enemy.Hp.Value})";
                await FlowTask.NextFrame();
            }
        }

        RectTransform CreateCanvas()
        {
            var go = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            go.transform.SetParent(transform, false);
            go.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            scaler.matchWidthOrHeight = 0.5f;
            if (FindAnyObjectByType<EventSystem>() == null)
            {
                var events = new GameObject("EventSystem", typeof(EventSystem));
                events.transform.SetParent(transform, false);
#if FLOWTASK_SAMPLES_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
                events.AddComponent<InputSystemUIInputModule>();
#else
                events.AddComponent<StandaloneInputModule>();
#endif
            }

            return (RectTransform)go.transform;
        }

        void Log(string line)
        {
            _log.Enqueue(line);
            while (_log.Count > 6) _log.Dequeue();
            if (_logLabel != null) _logLabel.text = string.Join("\n", _log);
        }
    }
}
