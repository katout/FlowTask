using System.Collections;
using Katout.FlowTask.Unity;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Katout.FlowTask.Samples.Tests.SampleTestUtil;
using Object = UnityEngine.Object;

namespace Katout.FlowTask.Samples.Tests
{
    /// <summary>Tests of the <see cref="Tutorial"/> sample.</summary>
    public class TutorialTests
    {
        TutorialOverlay _overlay;
        bool _moved;
        bool _jumped;

        [SetUp]
        public void CreateOverlay()
        {
            // On a DontDestroyOnLoad object, as in a game: a scene change does not hide what is on it.
            _overlay = TutorialOverlay.Create("TutorialOverlay");
            Object.DontDestroyOnLoad(_overlay.gameObject);
            _moved = false;
            _jumped = false;
        }

        [UnityTearDown]
        public IEnumerator CleanUp()
        {
            if (_overlay != null) Object.Destroy(_overlay.gameObject);
            return SampleHost.CleanUp();
        }

        TutorialStep[] Steps(SampleHost stage)
        {
            var moveButton = new GameObject("MoveButton", typeof(RectTransform)).GetComponent<RectTransform>();
            var jumpButton = new GameObject("JumpButton", typeof(RectTransform)).GetComponent<RectTransform>();
            moveButton.SetParent(stage.Transform, false);
            jumpButton.SetParent(stage.Transform, false);
            return new[]
            {
                new TutorialStep("Move", moveButton, () => _moved),
                new TutorialStep("Jump", jumpButton, () => _jumped),
            };
        }

        [UnityTest]
        public IEnumerator Tutorial_TheBalloonFollowsTheStepsAndHidesAtTheEnd()
        {
            var stage = SampleHost.Create("Stage", Ending.Destroy);
            var steps = Steps(stage);
            var h = stage.GameObject.RunWhileActive(Tutorial.Run(_overlay, steps));
            Assert.That(_overlay.BalloonText, Is.EqualTo("Move"));
            Assert.That(_overlay.Highlighted, Is.SameAs(steps[0].Target));

            _moved = true;
            yield return WaitUntil(() => _overlay.BalloonText == "Jump", what: "the second step");
            Assert.That(_overlay.Highlighted, Is.SameAs(steps[1].Target));
            _jumped = true;
            yield return WaitFor(h);
            Assert.That(h.Result, Is.EqualTo(2));
            Assert.That(_overlay.BalloonText, Is.Null);
            Assert.That(_overlay.Highlighted, Is.Null);
        }

        [UnityTest]
        public IEnumerator Tutorial_SkipEndsTheCurrentStepAndReturnsTheStepsDone()
        {
            var stage = SampleHost.Create("Stage", Ending.Destroy);
            var h = stage.GameObject.RunWhileActive(Tutorial.Run(_overlay, Steps(stage)));
            _moved = true;
            yield return WaitUntil(() => _overlay.BalloonText == "Jump", what: "the second step");

            _overlay.SkipButton.onClick.Invoke();
            yield return WaitFor(h);
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
            Assert.That(h.Result, Is.EqualTo(1));
            Assert.That(_overlay.BalloonText, Is.Null, "the skipped step's balloon was hidden");
            Assert.That(_overlay.Highlighted, Is.Null);
        }

        [UnityTest]
        public IEnumerator Tutorial_EndsWithTheObjectItIsBoundTo([Values] Ending ending)
        {
            var stage = SampleHost.Create("Stage", ending);
            var h = stage.GameObject.RunWhileActive(Tutorial.Run(_overlay, Steps(stage)));
            yield return null;
            Assert.That(_overlay.BalloonText, Is.EqualTo("Move"));

            yield return stage.End();
            yield return WaitFor(h);
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
            Assert.That(_overlay != null, Is.True, "the overlay outlives the scene");
            Assert.That(_overlay.BalloonText, Is.Null, "the balloon was hidden when the tutorial ended with its scene");
            Assert.That(_overlay.Highlighted, Is.Null);
        }
    }
}
