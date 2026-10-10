using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using Katout.FlowTask.Unity;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Katout.FlowTask.Samples.Tests.SampleTestUtil;

namespace Katout.FlowTask.Samples.Tests
{
    /// <summary>
    /// Tests of the <see cref="PauseMenu"/> sample (reference-counted pauses; the settings saved in a finally block).
    /// </summary>
    public class PauseMenuTests
    {
        GameClocks _clocks;
        BackKeyRouter _router;
        FakeSettingsStore _store;
        GameSettings _settings;

        [SetUp]
        public void CreateSession()
        {
            _clocks = Clocks;
            _router = new BackKeyRouter();
            _store = new FakeSettingsStore();
            _settings = new GameSettings(_store);
        }

        [UnityTearDown]
        public IEnumerator CleanUp() => SampleHost.CleanUp();

        PauseMenuView StartHud(SampleHost host, out FlowHandle hud)
        {
            var view = PauseMenuView.Create(host.Transform);
            // The HUD runs on the Game clock, like the gameplay it sits on; the menu moves itself to the UI clock.
            hud = host.GameObject.RunWhileActive(PauseMenu.Hud(_clocks, view, _router, _settings), _clocks.Game);
            return view;
        }

        IEnumerator OpenSettings(PauseMenuView view)
        {
            if (!view.IsMenuOpen)
            {
                view.PauseButton.onClick.Invoke();
                yield return WaitUntil(() => view.IsMenuOpen, what: "the menu");
            }

            view.SettingsButton.onClick.Invoke();
            yield return WaitUntil(() => view.IsSettingsOpen, what: "the settings");
        }

        [UnityTest]
        public IEnumerator PauseSample_NestedPausesReleaseInOrder()
        {
            var view = StartHud(SampleHost.Create("Hud", Ending.Destroy), out _);
            view.PauseButton.onClick.Invoke();
            yield return WaitUntil(() => view.IsMenuOpen, what: "the menu");
            Assert.That(_clocks.Game.PauseCount, Is.EqualTo(1));
            Assert.That(_clocks.Ui.IsPausedInHierarchy, Is.False, "the UI keeps running");

            view.SettingsButton.onClick.Invoke();
            yield return WaitUntil(() => view.IsSettingsOpen, what: "the settings");
            Assert.That(_clocks.Game.PauseCount, Is.EqualTo(2), "the settings pause too");

            Assert.That(_router.Press(), Is.True); // closes the settings: the back key goes to the top screen
            yield return WaitUntil(() => !view.IsSettingsOpen, what: "the settings to close");
            Assert.That(view.IsMenuOpen, Is.True);
            Assert.That(_clocks.Game.IsPausedInHierarchy, Is.True, "still paused by the menu");

            view.ResumeButton.onClick.Invoke();
            yield return WaitUntil(() => !view.IsMenuOpen, what: "the menu to close");
            Assert.That(_clocks.Game.PauseCount, Is.Zero);
            Assert.That(_router.Count, Is.Zero);
        }

        [UnityTest]
        public IEnumerator PauseSample_TheMenuFreezesTheGameAndItsEventsWaitForTheResume()
        {
            var pausedOwnClock = 0;
            void OnWarning(FlowWarning w)
            {
                if (w.Kind == FlowWarningKind.PausedOwnClock) pausedOwnClock++;
            }

            W.OnWarning += OnWarning;
            try
            {
                var view = StartHud(SampleHost.Create("Hud", Ending.Destroy), out _);
                var enemy = SampleHost.Create("Enemy", Ending.Destroy).GameObject.AddComponent<Enemy>();
                enemy.Clocks = _clocks;
                yield return WaitUntil(() => enemy.transform.position.x > 0.1f, what: "the patrol to move");

                view.PauseButton.onClick.Invoke();
                yield return WaitUntil(() => view.IsMenuOpen, what: "the menu");
                var x = enemy.transform.position.x;
                enemy.TakeHit(1); // a world event during the pause
                yield return Frames(5);
                Assert.That(enemy.transform.position.x, Is.EqualTo(x), "the enemy is frozen");
                Assert.That(enemy.Flinches, Is.Zero, "the hit waits in the enemy's subscription");

                view.ResumeButton.onClick.Invoke();
                yield return WaitUntil(() => enemy.Flinches == 1, what: "the hit after the resume");
                Assert.That(pausedOwnClock, Is.Zero, "the menu does not run on the clock it pauses");
            }
            finally
            {
                W.OnWarning -= OnWarning;
            }
        }

        [UnityTest]
        public IEnumerator PauseSample_EndsWithTheObjectItIsBoundTo([Values] Ending ending)
        {
            var host = SampleHost.Create("Hud", ending);
            var view = StartHud(host, out var hud);
            view.PauseButton.onClick.Invoke();
            yield return WaitUntil(() => view.IsMenuOpen, what: "the menu");
            view.SettingsButton.onClick.Invoke();
            yield return WaitUntil(() => view.IsSettingsOpen, what: "the settings");
            Assert.That(_clocks.Game.PauseCount, Is.EqualTo(2));

            yield return host.End();
            yield return WaitFor(hud);
            Assert.That(hud.Status, Is.EqualTo(FlowStatus.Canceled));
            Assert.That(_clocks.Game.PauseCount, Is.Zero, "both pauses were released: the game does not stay frozen");
            Assert.That(_router.Count, Is.Zero);
        }

        [UnityTest]
        public IEnumerator PauseSample_ClosingTheSettingsSavesThemAndOnlyACompletedSaveMakesThemClean()
        {
            var view = StartHud(SampleHost.Create("Hud", Ending.Destroy), out _);
            yield return OpenSettings(view);
            view.SettingsCloseButton.onClick.Invoke();
            yield return WaitUntil(() => !view.IsSettingsOpen, what: "the settings to close");
            yield return Frames(2);
            Assert.That(_store.Saves, Is.Zero, "nothing changed, nothing to save");

            yield return OpenSettings(view);
            _settings.Volume = 0.5f; // what the screen's slider does
            view.SettingsCloseButton.onClick.Invoke();
            yield return WaitUntil(() => _store.IsSaving, what: "the save");
            Assert.That(view.IsSettingsOpen, Is.False);
            Assert.That(_clocks.Game.PauseCount, Is.EqualTo(1), "the settings' pause ended with the screen, before the save");
            Assert.That(_settings.IsDirty, Is.True, "not written yet");

            _store.Finish();
            yield return WaitUntil(() => !_settings.IsDirty, what: "the completed save to make the settings clean");
            Assert.That(_store.SavedVolume, Is.EqualTo(0.5f));
            view.ResumeButton.onClick.Invoke(); // the menu goes on once the save is done
            yield return WaitUntil(() => !view.IsMenuOpen, what: "the menu to close");
            Assert.That(_clocks.Game.PauseCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator PauseSample_TheSaveRunsToItsEndWhenTheHudIsDestroyed()
        {
            var host = SampleHost.Create("Hud", Ending.Destroy);
            var view = StartHud(host, out var hud);
            yield return OpenSettings(view);
            _settings.Volume = 0.25f;

            yield return host.End(); // the settings unwind and start the save in their finally block
            Assert.That(hud.Status, Is.EqualTo(FlowStatus.Running), "the HUD ends after the save in it");
            Assert.That(hud.CancelCause, Is.Not.EqualTo(CancelCause.None));
            Assert.That(_store.IsSaving, Is.True);
            Assert.That(_clocks.Game.PauseCount, Is.EqualTo(1), "the settings' pause was released; the menu's is released after the save, as through a call stack");
            yield return Frames(3);
            Assert.That(_store.Token.IsCancellationRequested, Is.False, "a save started in a finally block is not canceled by the destruction");

            _store.Finish();
            yield return WaitUntil(() => !_settings.IsDirty, what: "the settings to be clean after the save completed");
            Assert.That(_store.SavedVolume, Is.EqualTo(0.25f));
            yield return WaitUntil(() => hud.IsCompleted, what: "the HUD to end after the save");
            Assert.That(hud.Status, Is.EqualTo(FlowStatus.Canceled));
            Assert.That(_clocks.Game.PauseCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator PauseSample_ASaveCutByItsTimeLimitLeavesTheSettingsDirty()
        {
            var view = StartHud(SampleHost.Create("Hud", Ending.Destroy), out _);
            yield return OpenSettings(view);
            _settings.Volume = 0.75f;
            view.SettingsCloseButton.onClick.Invoke();
            yield return WaitUntil(() => _store.IsSaving, what: "the save");

            // The store never finishes: the Race gives the save up after SaveTimeoutSeconds of the UI clock.
            yield return WaitUntil(() => _store.Token.IsCancellationRequested, (float)PauseMenu.SaveTimeoutSeconds + 3, "the time limit to give the save up");
            yield return Frames(2);
            Assert.That(_settings.IsDirty, Is.True, "the code after the Race ran, but a save given up does not make the settings clean");

            yield return OpenSettings(view); // the menu goes on; the next close saves again
            view.SettingsCloseButton.onClick.Invoke();
            yield return WaitUntil(() => _store.Saves == 2 && _store.IsSaving, what: "the second save");
            _store.Finish();
            yield return WaitUntil(() => !_settings.IsDirty, what: "the second save to make the settings clean");
            Assert.That(_store.SavedVolume, Is.EqualTo(0.75f));
        }

        /// <summary>A settings store whose save completes when the test calls <see cref="Finish"/>, and never otherwise.</summary>
        sealed class FakeSettingsStore : ISettingsStore
        {
            TaskCompletionSource<bool> _pending;
            float _volume;

            public int Saves { get; private set; }
            public float SavedVolume { get; private set; } = float.NaN;
            public CancellationToken Token { get; private set; }
            public bool IsSaving => _pending != null && !_pending.Task.IsCompleted;

            public Task SaveAsync(float volume, CancellationToken ct)
            {
                Saves++;
                Token = ct;
                _volume = volume;
                var pending = new TaskCompletionSource<bool>();
                _pending = pending;
                ct.Register(() => pending.TrySetCanceled(ct));
                return pending.Task;
            }

            public void Finish()
            {
                SavedVolume = _volume;
                _pending.TrySetResult(true);
            }
        }
    }
}
