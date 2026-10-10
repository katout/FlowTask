using System.Collections;
using System.Collections.Generic;
using Katout.FlowTask.Unity;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Katout.FlowTask.Samples.Tests.SampleTestUtil;
using Object = UnityEngine.Object;

namespace Katout.FlowTask.Samples.Tests
{
    /// <summary>The <see cref="BackKeyRouter"/> sample (the back key assembled in the app layer, not by the library).</summary>
    public class BackKeyRouterTests
    {
        [UnityTest]
        public IEnumerator BackKeyRouter_TopLayerWinsAndEntriesLeaveWithTheirScope()
        {
            var router = new BackKeyRouter();
            var log = new List<string>();

            async FlowTask Listen(string name, int layer)
            {
                await router.Next(layer);
                log.Add(name);
            }

            var screen = W.Run(Listen("screen", BackKeyRouter.Screen));
            var dialog = W.Run(Listen("dialog", BackKeyRouter.Dialog));
            var screen2 = W.Run(Listen("screen 2", BackKeyRouter.Screen));
            Assert.That(router.Count, Is.EqualTo(3));

            Assert.That(router.Press(), Is.True);
            yield return WaitFor(dialog);
            Assert.That(log, Is.EqualTo(new[] { "dialog" }), "the higher layer wins, whatever the order of the pushes");
            Assert.That(router.Count, Is.EqualTo(2), "the entry left with the wait");

            Assert.That(router.Press(), Is.True);
            yield return WaitFor(screen2);
            Assert.That(log, Is.EqualTo(new[] { "dialog", "screen 2" }), "within a layer, the entry pushed last wins");

            // A lost Race and a canceled flow remove their entries too.
            var raced = W.Run(FlowTask.Race(router.Next(BackKeyRouter.Dialog), FlowTask.NextFrame()));
            Assert.That(router.Count, Is.EqualTo(2));
            yield return WaitFor(raced);
            Assert.That(raced.Result.Index, Is.EqualTo(1));
            Assert.That(router.Count, Is.EqualTo(1), "the losing branch's entry left when the Race ended");
            screen.Cancel(); // the flow unwinds at the next flush point
            yield return WaitFor(screen);
            Assert.That(screen.Status, Is.EqualTo(FlowStatus.Canceled));
            Assert.That(router.Count, Is.Zero, "canceling the flow removed its entry");
            Assert.That(router.Press(), Is.False);
        }

        [UnityTest]
        public IEnumerator BackKeyRouter_BlockSwallowsTheKeyUntilDisposed()
        {
            var router = new BackKeyRouter();
            var log = new List<string>();

            async FlowTask Purchase()
            {
                using var block = router.Block(); // the key does nothing while talking to the store
                await FlowTask.WaitForSeconds(1.0, W.UnscaledClock);
                log.Add("purchased");
            }

            async FlowTask ShopDialog()
            {
                var r = await FlowTask.Race(Purchase(), router.Next(BackKeyRouter.Dialog));
                log.Add(r.Index == 0 ? "purchase done" : "backed out");
                await router.Next(BackKeyRouter.Dialog);
                log.Add("back works again");
            }

            var h = W.Run(ShopDialog());
            Assert.That(router.IsCaptured(BackKeyRouter.Critical), Is.True);
            Assert.That(router.Press(), Is.True, "a swallowed press is still delivered, to the block");
            yield return Frames(2);
            Assert.That(log, Is.Empty, "the press reached neither the dialog nor the purchase");
            yield return WaitUntil(() => log.Count == 2, what: "the purchase to end");
            Assert.That(log, Is.EqualTo(new[] { "purchased", "purchase done" }));
            Assert.That(router.IsCaptured(BackKeyRouter.Critical), Is.False, "the block was released with its scope");
            Assert.That(router.Press(), Is.True);
            yield return WaitFor(h);
            Assert.That(log, Is.EqualTo(new[] { "purchased", "purchase done", "back works again" }));
        }

        [Test]
        public void BackKeyRouter_PressWithNothingListeningReturnsFalse()
        {
            var router = new BackKeyRouter();
            Assert.That(router.Press(), Is.False);
            Assert.That(router.Count, Is.Zero);
        }

        [UnityTest]
        public IEnumerator BackKeyRouter_PressInUpdateResumesTheListenerInTheSameFrame()
        {
            var router = new BackKeyRouter();
            var go = new GameObject("BackKeyPresser");
            var presser = go.AddComponent<BackKeyPresser>();
            presser.Router = router;
            var resumedFrame = -1;

            async FlowTask Listen()
            {
                await router.Next(BackKeyRouter.Screen);
                resumedFrame = Time.frameCount;
            }

            try
            {
                var h = W.Run(Listen());
                yield return null;
                presser.PressNextUpdate = true;
                yield return WaitFor(h);
                Assert.That(presser.Delivered, Is.True);
                Assert.That(resumedFrame, Is.EqualTo(presser.PressedFrame), "the flush point at the end of Update resumed the flow");
            }
            finally
            {
                Object.Destroy(go);
            }
        }

        [Test]
        public void BackKeyRouter_DestroyingTheDialogObjectRemovesItsEntryRightAway()
        {
            var router = new BackKeyRouter();
            var dialog = new GameObject("Dialog");
            var closed = false;

            async FlowTask Dialog()
            {
                try
                {
                    await router.Next(BackKeyRouter.Dialog);
                }
                finally
                {
                    closed = true;
                }
            }

            var h = dialog.RunWhileActive(Dialog());
            Assert.That(router.Count, Is.EqualTo(1));
            Object.DestroyImmediate(dialog); // OnDisable, called before OnDestroy, cancels the flow and flushes its World right away
            Assert.That(router.Count, Is.Zero, "the entry left inside DestroyImmediate");
            Assert.That(closed, Is.True);
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
        }
    }
}
