using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Katout.FlowTask.Unity;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Katout.FlowTask.Samples.Tests.SampleTestUtil;

namespace Katout.FlowTask.Samples.Tests
{
    /// <summary>A store whose answers the test gives: scripted ones first, then a pending Task the test completes.</summary>
    public sealed class FakeStore : IStore
    {
        readonly Queue<Func<Task<Receipt>>> _scripted = new Queue<Func<Task<Receipt>>>();

        public int Calls { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public TaskCompletionSource<Receipt> Pending { get; private set; }

        /// <summary>The next call faults with a <see cref="StoreException"/>, completed already (as a fast failure would).</summary>
        public void FailNext(bool retryable) => _scripted.Enqueue(() => Task.FromException<Receipt>(new StoreException("store failure", retryable)));

        /// <summary>The next call succeeds, completed already: the recipe for fake Tasks in engine-free tests.</summary>
        public void SucceedNext(string transactionId) => _scripted.Enqueue(() => Task.FromResult(new Receipt("gem_pack", transactionId)));

        /// <summary>The next call throws what no purchase expects: a bug in the store code.</summary>
        public void BreakNext() => _scripted.Enqueue(() => Task.FromException<Receipt>(new InvalidOperationException("bug in the store code")));

        public Task<Receipt> PurchaseAsync(string itemId, CancellationToken ct)
        {
            Calls++;
            LastToken = ct;
            if (_scripted.Count > 0) return _scripted.Dequeue()();
            Pending = new TaskCompletionSource<Receipt>();
            return Pending.Task;
        }
    }

    /// <summary>Tests of the <see cref="Shop"/> and <see cref="ShopScreen"/> sample.</summary>
    public class ShopTests
    {
        FakeStore _store;
        BackKeyRouter _router;
        List<Receipt> _lateReceipts;
        Shop _shop;

        [SetUp]
        public void CreateShop()
        {
            _store = new FakeStore();
            _router = new BackKeyRouter();
            _lateReceipts = new List<Receipt>();
            _shop = new Shop(_store, _router, W.UnscaledClock, _lateReceipts.Add, retryDelaySeconds: 0.05);
        }

        [UnityTearDown]
        public IEnumerator CleanUp() => SampleHost.CleanUp();

        [UnityTest]
        public IEnumerator ShopSample_RetriesAFailureThatMayPassAndThenBuys()
        {
            _store.FailNext(retryable: true);
            _store.FailNext(retryable: true);
            var h = W.Run(_shop.Purchase("gem_pack"));
            yield return WaitUntil(() => _store.Pending != null, what: "the third attempt");
            Assert.That(_store.Calls, Is.EqualTo(3));
            Assert.That(_router.IsCaptured(BackKeyRouter.Critical), Is.True, "the back key is blocked during the purchase");

            _store.Pending.SetResult(new Receipt("gem_pack", "t-1"));
            yield return WaitFor(h);
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
            Assert.That(h.Result.TransactionId, Is.EqualTo("t-1"));
            Assert.That(_router.Count, Is.Zero, "the block was released");
        }

        // A purchase whose expected failure is caught, as the screen catches it: an exception that ended a root flow would
        // reach the World's OnUnhandledException, which the default handler logs as an error (failing the test). Null when it bought.
        async FlowTask<PurchaseError?> FailureOfAPurchase()
        {
            try
            {
                await _shop.Purchase("gem_pack");
                return null;
            }
            catch (PurchaseFailedException e)
            {
                return e.Error;
            }
        }

        [UnityTest]
        public IEnumerator ShopSample_ADeclinedPurchaseThrowsAndIsNotRetried()
        {
            _store.FailNext(retryable: false);
            var h = W.Run(FailureOfAPurchase());
            yield return WaitFor(h);
            Assert.That(h.Result, Is.EqualTo(PurchaseError.Declined));
            Assert.That(_store.Calls, Is.EqualTo(1));
            Assert.That(_router.Count, Is.Zero, "the block was released");

            for (var i = 0; i < 3; i++) _store.FailNext(retryable: true);
            h = W.Run(FailureOfAPurchase());
            yield return WaitFor(h);
            Assert.That(h.Result, Is.EqualTo(PurchaseError.Unavailable), "three retryable failures in a row");
            Assert.That(_store.Calls, Is.EqualTo(4));
        }

        ShopView OpenScreen(SampleHost host, out FlowHandle screen)
        {
            var view = ShopView.Create(host.Transform);
            screen = host.GameObject.RunWhileActive(ShopScreen.Run(view, _shop, _router));
            return view;
        }

        [UnityTest]
        public IEnumerator ShopSample_TheBackKeyAndBuyAreIgnoredWhilePurchasing()
        {
            var view = OpenScreen(SampleHost.Create("ShopScreen", Ending.Destroy), out var screen);
            view.BuyButton.onClick.Invoke();
            yield return WaitUntil(() => _store.Pending != null, what: "the purchase");
            Assert.That(view.Status, Is.EqualTo("Purchasing"));

            Assert.That(_router.Press(), Is.True, "swallowed by the block");
            view.BuyButton.onClick.Invoke();
            view.BuyButton.onClick.Invoke();
            yield return Frames(3);
            Assert.That(screen.Status, Is.EqualTo(FlowStatus.Running), "the back key did not leave the screen");
            Assert.That(_store.Calls, Is.EqualTo(1), "the presses during the purchase were dropped");

            _store.Pending.SetResult(new Receipt("gem_pack", "t-1"));
            yield return WaitUntil(() => view.Status == "Bought gem_pack", what: "the purchase to end");
            Assert.That(_router.Press(), Is.True);
            yield return WaitFor(screen);
            Assert.That(screen.Status, Is.EqualTo(FlowStatus.Succeeded), "the back key works again and leaves the screen");
        }

        [UnityTest]
        public IEnumerator ShopSample_ABugInAPurchaseIsCaughtByTheScreen()
        {
            var view = OpenScreen(SampleHost.Create("ShopScreen", Ending.Destroy), out var screen);
            _store.BreakNext();
            view.BuyButton.onClick.Invoke();
            // An unhandled exception would be logged as an error by the default handler and fail the test.
            yield return WaitUntil(() => view.LastBug != null, what: "the failed purchase");
            Assert.That(view.LastBug, Is.InstanceOf<InvalidOperationException>());
            Assert.That(view.Status, Is.EqualTo("Something went wrong"));
            Assert.That(screen.Status, Is.EqualTo(FlowStatus.Running), "the screen stays");
            Assert.That(_router.IsCaptured(BackKeyRouter.Critical), Is.False, "the failed purchase released its block");

            view.BuyButton.onClick.Invoke();
            yield return WaitUntil(() => _store.Pending != null, what: "the next purchase");
            _store.Pending.SetResult(new Receipt("gem_pack", "t-2"));
            yield return WaitUntil(() => view.Status == "Bought gem_pack", what: "the next purchase to end");
        }

        [UnityTest]
        public IEnumerator ShopSample_LeavingCancelsTheStoreAndKeepsTheLateReceipt([Values] Ending ending)
        {
            var host = SampleHost.Create("ShopScreen", ending);
            var view = OpenScreen(host, out var screen);
            view.BuyButton.onClick.Invoke();
            yield return WaitUntil(() => _store.Pending != null, what: "the purchase");

            yield return host.End();
            yield return WaitFor(screen);
            Assert.That(screen.Status, Is.EqualTo(FlowStatus.Canceled));
            Assert.That(_store.LastToken.IsCancellationRequested, Is.True, "the store's token was canceled");
            Assert.That(_router.Count, Is.Zero, "the block and the screen's back-key entry were released");

            // The store had already charged: its receipt arrives after the flow is gone and goes to onLateReceipt.
            _store.Pending.SetResult(new Receipt("gem_pack", "t-late"));
            yield return WaitUntil(() => _lateReceipts.Count == 1, what: "the late receipt");
            Assert.That(_lateReceipts[0].TransactionId, Is.EqualTo("t-late"));
        }
    }
}
