using System.Collections;
using Katout.FlowTask.Unity;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Katout.FlowTask.Samples.Tests.SampleTestUtil;
using Object = UnityEngine.Object;

namespace Katout.FlowTask.Samples.Tests
{
    /// <summary>Tests of the <see cref="ConfirmDialog"/> sample.</summary>
    public class ConfirmDialogTests
    {
        float _maximumDeltaTime;

        // The close animation lasts 0.25 s of UnscaledClock time, and some tests check that it is still playing a frame
        // later. A player's first frames can take longer than that, so the frame time is capped for these tests: the
        // UnscaledClock advances by at most Time.maximumDeltaTime per frame.
        [SetUp]
        public void CapFrameTime()
        {
            _maximumDeltaTime = Time.maximumDeltaTime;
            Time.maximumDeltaTime = 0.1f;
        }

        [TearDown]
        public void RestoreFrameTime() => Time.maximumDeltaTime = _maximumDeltaTime;

        [UnityTearDown]
        public IEnumerator CleanUp() => SampleHost.CleanUp();

        static ConfirmDialogView ViewUnder(SampleHost host) => host.GameObject.GetComponentInChildren<ConfirmDialogView>(true);

        [UnityTest]
        public IEnumerator ConfirmDialogSample_OkResolvesTrueAndTheViewCloses()
        {
            var router = new BackKeyRouter();
            var host = SampleHost.Create("Screen", Ending.Destroy);
            var h = host.GameObject.RunWhileActive(ConfirmDialog.Show(host.Transform, "Buy?", router, W.UnscaledClock));
            var view = ViewUnder(host);
            Assert.That(view, Is.Not.Null);
            Assert.That(router.Count, Is.EqualTo(1), "the dialog owns the back key while it is open");

            view.OkButton.onClick.Invoke();
            view.CancelButton.onClick.Invoke(); // the same frame: the first branch wins, the other tap reaches nobody
            yield return WaitUntil(() => view.IsClosing, what: "the close animation");
            Assert.That(router.Count, Is.Zero, "the back-key entry left with its losing branch");
            Assert.That(h.IsCompleted, Is.False, "the flow waits for the close animation its finally block awaits");
            yield return WaitFor(h);
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
            Assert.That(h.Result, Is.True);
            yield return null; // Destroy takes effect at the end of the frame
            Assert.That(view == null, Is.True, "the dialog was destroyed after its close animation");
        }

        [UnityTest]
        public IEnumerator ConfirmDialogSample_CancelBackKeyAndTimeoutResolveFalse()
        {
            var router = new BackKeyRouter();

            var host = SampleHost.Create("Cancel", Ending.Destroy);
            var canceled = host.GameObject.RunWhileActive(ConfirmDialog.Show(host.Transform, "Buy?", router, W.UnscaledClock));
            ViewUnder(host).CancelButton.onClick.Invoke();
            yield return WaitFor(canceled);
            Assert.That(canceled.Result, Is.False, "Cancel");

            host = SampleHost.Create("Back", Ending.Destroy);
            var back = host.GameObject.RunWhileActive(ConfirmDialog.Show(host.Transform, "Buy?", router, W.UnscaledClock));
            Assert.That(router.Press(), Is.True);
            yield return WaitFor(back);
            Assert.That(back.Result, Is.False, "the back key");

            host = SampleHost.Create("Timeout", Ending.Destroy);
            var timeout = host.GameObject.RunWhileActive(ConfirmDialog.Show(host.Transform, "Buy?", router, W.UnscaledClock, timeoutSeconds: 0.2));
            yield return WaitFor(timeout);
            Assert.That(timeout.Result, Is.False, "no answer in time");
            Assert.That(router.Count, Is.Zero);
        }

        [UnityTest]
        public IEnumerator ConfirmDialogSample_TheCloseAnimationPlaysWhenTheCallerIsCanceled()
        {
            var router = new BackKeyRouter();
            var host = SampleHost.Create("Screen", Ending.Destroy);
            var h = W.Run(ConfirmDialog.Show(host.Transform, "Buy?", router, W.UnscaledClock));
            var view = ViewUnder(host);
            yield return null;

            h.Cancel();
            yield return null;
            Assert.That(view != null && view.IsClosing, Is.True, "the finally block started the close animation");
            Assert.That(router.Count, Is.Zero);
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Running), "the canceled flow waits for the close animation its finally block awaits");
            Assert.That(h.CancelCause, Is.EqualTo(CancelCause.Explicit));
            yield return WaitFor(h);
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
            yield return null;
            Assert.That(view == null, Is.True, "destroyed after the animation");
        }

        [UnityTest]
        public IEnumerator ConfirmDialogSample_DestroyedDuringTheCloseAnimationEndsQuietly()
        {
            var router = new BackKeyRouter();
            var host = SampleHost.Create("Screen", Ending.Destroy);
            var h = host.GameObject.RunWhileActive(ConfirmDialog.Show(host.Transform, "Buy?", router, W.UnscaledClock));
            var view = ViewUnder(host);
            view.OkButton.onClick.Invoke();
            yield return WaitUntil(() => view.IsClosing, what: "the close animation");

            Object.Destroy(host.GameObject); // the dialog goes with it, halfway through the animation
            yield return WaitFor(h);
            // A MissingReferenceException from the animation would be logged as an error and fail the test.
            Assert.That(h.Status, Is.Not.EqualTo(FlowStatus.Faulted));
            Assert.That(view == null, Is.True);
        }

        [UnityTest]
        public IEnumerator ConfirmDialogSample_EndsWithTheObjectItIsBoundTo([Values] Ending ending)
        {
            var router = new BackKeyRouter();
            var host = SampleHost.Create("Screen", ending);
            var h = host.GameObject.RunWhileActive(ConfirmDialog.Show(host.Transform, "Buy?", router, W.UnscaledClock));
            var view = ViewUnder(host);
            Assert.That(router.Count, Is.EqualTo(1));

            yield return host.End();
            yield return WaitFor(h);
            yield return null; // a view the dialog destroyed itself (Deactivate) is gone at the end of the frame
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
            Assert.That(router.Count, Is.Zero, "the back-key entry left");
            Assert.That(view == null, Is.True, "the dialog went with the object");
        }
    }
}
