using Katout.FlowTask.Testing;
using Katout.FlowTask.Testing.NUnit;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Katout.FlowTask.Samples.Tests
{
    /// <summary>
    /// The samples' flows tested without the PlayerLoop. Each test owns a World and advances it in
    /// virtual time (FlowTask.Testing), so ten seconds of timeouts and retries pass at once, and an unhandled exception
    /// fails the test ([FailOnUnhandledFlowException] with FlowNUnit.CreateWorld). Fake Tasks are made completed
    /// already (Task.FromResult, Task.FromException): a Task that completes later on a real thread would make the test
    /// wait for it.
    /// </summary>
    public class EngineFreeSampleTests
    {
        [Test, FailOnUnhandledFlowException]
        public void EngineFreeSampleTest_ShopRetriesUntilTheStoreSucceeds()
        {
            using var tw = FlowNUnit.CreateWorld();
            var store = new FakeStore();
            store.FailNext(retryable: true);
            store.FailNext(retryable: true);
            store.SucceedNext("t-1");
            var shop = new Shop(store, new BackKeyRouter(), tw.World.UnscaledClock, _ => { }); // real 1 s and 2 s waits

            var receipt = tw.World.RunUntilComplete(shop.Purchase("gem_pack"));

            Assert.That(receipt.TransactionId, Is.EqualTo("t-1"));
            Assert.That(store.Calls, Is.EqualTo(3));
            Assert.That(tw.World.UnscaledClock.Time, Is.GreaterThanOrEqualTo(3.0), "3 s of retry waits passed in virtual time");
        }

        [Test, FailOnUnhandledFlowException]
        public void EngineFreeSampleTest_ShopScreenBuysOnAPressAndLeavesOnTheBackKey()
        {
            using var tw = FlowNUnit.CreateWorld();
            var parent = new GameObject("ShopScreen");
            try
            {
                var store = new FakeStore();
                store.SucceedNext("t-1");
                var router = new BackKeyRouter();
                var view = ShopView.Create(parent.transform);

                // Start, inject the input, run until done: the three lines of an engine-free test.
                var screen = tw.World.Run(ShopScreen.Run(view, new Shop(store, router, tw.World.UnscaledClock, _ => { }), router));
                view.BuyButton.onClick.Invoke();
                tw.World.TickUntil(() => view.Status == "Bought gem_pack");

                router.Press();
                tw.World.TickUntil(() => screen.IsCompleted);
                Assert.That(screen.Status, Is.EqualTo(FlowStatus.Succeeded));
            }
            finally
            {
                Object.Destroy(parent);
            }
        }

        [Test, FailOnUnhandledFlowException]
        public void EngineFreeSampleTest_ConfirmDialogTimesOutInVirtualTime()
        {
            using var tw = FlowNUnit.CreateWorld();
            var parent = new GameObject("Screen");
            try
            {
                var router = new BackKeyRouter();
                var answer = tw.World.RunUntilComplete(ConfirmDialog.Show(parent.transform, "Buy?", router, tw.World.UnscaledClock));
                Assert.That(answer, Is.False, "no answer within 10 s of virtual time");
                Assert.That(router.Count, Is.Zero);
            }
            finally
            {
                Object.Destroy(parent);
            }
        }

        [Test, FailOnUnhandledFlowException]
        public void EngineFreeSampleTest_LoadingStopsAtTheFirstFailure()
        {
            using var tw = FlowNUnit.CreateWorld();
            var parent = new GameObject("LoadingScreen");
            try
            {
                var loader = new FakeLoader();
                var view = LoadingView.Create(parent.transform);

                // The expected failure is caught, as a caller catches it: one that ended the root flow would be unhandled.
                async FlowTask<string> FailedKey()
                {
                    try
                    {
                        await Loading.LoadAll(loader, new[] { "stage", "music" }, view);
                        return null;
                    }
                    catch (AssetLoadException e)
                    {
                        return e.Key;
                    }
                }

                var h = tw.World.Run(FailedKey());
                loader.Requests[1].Fail();
                tw.World.TickUntil(() => h.IsCompleted);
                Assert.That(h.Result, Is.EqualTo("music"));
                Assert.That(loader.Requests[0].Releases + loader.Requests[1].Releases, Is.EqualTo(2));
            }
            finally
            {
                Object.Destroy(parent);
            }
        }

        [Test]
        public void EngineFreeSampleTest_AnUnhandledExceptionFailsTheTest()
        {
            // A plain TestWorld shows what [FailOnUnhandledFlowException] relies on: it records the report and throws when disposed.
            var tw = new TestWorld();
            var store = new FakeStore();
            store.BreakNext();
            var shop = new Shop(store, new BackKeyRouter(), tw.World.UnscaledClock, _ => { });
            _ = tw.World.Run(shop.Purchase("gem_pack")); // no catch around it: the bug reaches the root
            tw.World.TickFrames(2);

            Assert.That(tw.Exceptions, Has.Count.EqualTo(1));
            Assert.That(tw.Exceptions[0].Exception, Is.InstanceOf<System.InvalidOperationException>());
            Assert.Throws<FlowUnhandledExceptionAssertionException>(() => tw.Dispose());
        }
    }
}
