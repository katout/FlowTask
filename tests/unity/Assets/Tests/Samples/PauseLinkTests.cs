using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Katout.FlowTask.Samples.Tests.SampleTestUtil;
using Object = UnityEngine.Object;

namespace Katout.FlowTask.Samples.Tests
{
    /// <summary>
    /// The <see cref="PauseLink"/> sample (Time.timeScale follows a paused clock) and what Time.timeScale does to the
    /// sample's clocks.
    /// </summary>
    public class PauseLinkTests
    {
        GameClocks _clocks;
        GameObject _link;
        float _timeScale;

        [SetUp]
        public void CreateSession()
        {
            _clocks = Clocks;
            _timeScale = Time.timeScale;
        }

        [UnityTearDown]
        public IEnumerator CleanUp()
        {
            if (_link != null) Object.Destroy(_link);
            yield return null;
            Time.timeScale = _timeScale;
        }

        [UnityTest]
        public IEnumerator PauseLinkSample_TimeScaleIsZeroWhileTheGameClockIsPaused()
        {
            Time.timeScale = 0.5f; // slow motion, given back after the pause
            _link = new GameObject("PauseLink");
            _link.AddComponent<PauseLink>().Clock = _clocks.Game;

            var menu = _clocks.Game.Pause(); // outside flow code: no owner, disposed below
            try
            {
                var settings = _clocks.Game.Pause(); // a screen on top of the menu
                try
                {
                    yield return null;
                    Assert.That(Time.timeScale, Is.Zero, "Unity stopped with the clock");
                }
                finally
                {
                    settings.Dispose();
                }

                yield return null;
                Assert.That(Time.timeScale, Is.Zero, "still stopped while the menu's pause is held");
            }
            finally
            {
                menu.Dispose(); // the clocks are shared: never leave them paused
            }

            yield return null;
            Assert.That(Time.timeScale, Is.EqualTo(0.5f), "the last pause gave the time scale back");
        }

        [UnityTest]
        public IEnumerator PauseLinkSample_ADisabledLinkGivesTheTimeScaleBack()
        {
            _link = new GameObject("PauseLink");
            _link.AddComponent<PauseLink>().Clock = _clocks.Game;
            var pause = _clocks.Game.Pause(); // outside flow code: no owner, disposed below
            try
            {
                yield return null;
                Assert.That(Time.timeScale, Is.Zero);
                _link.SetActive(false); // the mistake the sample warns about: the link on an object a menu hides
                Assert.That(Time.timeScale, Is.EqualTo(_timeScale), "no one would give it back otherwise");
            }
            finally
            {
                pause.Dispose(); // the clocks are shared: never leave them paused
            }
        }

        [UnityTest]
        public IEnumerator GameClocksSample_TimeScaleZeroStopsGameButNotUi()
        {
            Time.timeScale = 0; // what PauseLink does: Unity stops DefaultClock and the Game clock under it
            yield return Frames(2);
            var game = _clocks.Game.Time;
            var ui = _clocks.Ui.Time;
            yield return new WaitForSecondsRealtime(0.1f);
            Assert.That(_clocks.Game.Time, Is.EqualTo(game), "Game follows Time.timeScale through DefaultClock");
            Assert.That(_clocks.Ui.Time, Is.GreaterThan(ui), "the UI clock runs on unscaled time");
            Assert.That(_clocks.Game.IsPausedInHierarchy, Is.False, "a time scale of 0 is not a pause: PauseLink does not react to it");
        }
    }
}
