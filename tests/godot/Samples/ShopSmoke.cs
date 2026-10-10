using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Katout.FlowTask.Godot;
using Godot;

namespace Katout.FlowTask.Samples.Tests;

/// <summary>The smoke test's checks of <see cref="Shop"/> and <see cref="ShopScreen"/>.</summary>
public static class ShopSmoke
{
    /// <summary>A store that answers after a real delay on the thread pool (Task.Delay), as a network would.</summary>
    sealed class DelayedStore : IStore
    {
        readonly Queue<Func<CancellationToken, Task<Receipt>>> _answers = new();

        public int Calls { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public TaskCompletionSource<Receipt> Pending { get; private set; }

        public void Fail(bool retryable) => _answers.Enqueue(async ct =>
        {
            await Task.Delay(10, ct).ConfigureAwait(false);
            throw new StoreException("store failure", retryable);
        });

        public void Succeed(string transactionId) => _answers.Enqueue(async ct =>
        {
            await Task.Delay(10, ct).ConfigureAwait(false);
            return new Receipt("gem_pack", transactionId);
        });

        public void Break() => _answers.Enqueue(_ => Task.FromException<Receipt>(new InvalidOperationException("bug in the store code (expected)")));

        public Task<Receipt> PurchaseAsync(string itemId, CancellationToken ct)
        {
            Calls++;
            LastToken = ct;
            if (_answers.Count > 0) return _answers.Dequeue()(ct);
            Pending = new TaskCompletionSource<Receipt>(TaskCreationOptions.RunContinuationsAsynchronously);
            return Pending.Task; // answered by the check, ignoring the token: the store had already charged
        }
    }

    public static async FlowTask<Verdict> Run(Node host)
    {
        var ui = FlowWorldNode.Default.UnscaledClock;
        var router = new BackKeyRouter();
        var store = new DelayedStore();
        var late = new List<Receipt>();
        var shop = new Shop(store, router, ui, late.Add, retryDelaySeconds: 0.05);

        // Retries: two retryable failures, then a receipt.
        store.Fail(retryable: true);
        store.Fail(retryable: true);
        store.Succeed("t-1");
        var bought = await shop.Purchase("gem_pack");
        var retries = store.Calls;

        // A declined purchase throws PurchaseFailedException at once, caught here as the screen catches it.
        store.Fail(retryable: false);
        PurchaseError? declined = null;
        try
        {
            await shop.Purchase("gem_pack");
        }
        catch (PurchaseFailedException e)
        {
            declined = e.Error;
        }

        var declinedCalls = store.Calls - retries;

        // On the screen: the back key and a second press are ignored while buying; a bug stays in its catch.
        var screenNode = NodeLifetime.Own(new VBoxContainer { Name = "ShopScreen" });
        var buy = new Button { Name = "Buy", Text = "Buy" };
        var status = new Label { Name = "Status" };
        screenNode.AddChild(buy);
        screenNode.AddChild(status);
        host.AddChild(screenNode);
        var rootExceptions = 0;
        Exception reported = null;
        void OnUnhandledException(FlowExceptionInfo p) => rootExceptions++;
        FlowWorldNode.Instance.UnhandledException += OnUnhandledException;
        try
        {
            var screen = screenNode.RunWhileInTree(ShopScreen.Run(buy, status, shop, router, bug => reported = bug));
            var calls = store.Calls;
            buy.EmitSignal(BaseButton.SignalName.Pressed);
            await FlowTask.WaitUntil(() => store.Pending != null);
            var blocked = router.Press();
            buy.EmitSignal(BaseButton.SignalName.Pressed);
            await FlowTask.DelayFrames(3);
            var ignored = screen.Status == FlowStatus.Running && store.Calls == calls + 1;
            store.Pending.SetResult(new Receipt("gem_pack", "t-2"));
            await FlowTask.WaitUntil(() => status.Text == "Bought gem_pack");

            store.Break();
            buy.EmitSignal(BaseButton.SignalName.Pressed);
            await FlowTask.WaitUntil(() => status.Text == "Something went wrong");
            var contained = screen.Status == FlowStatus.Running && reported is InvalidOperationException && rootExceptions == 0;

            // Leaving during a purchase: the store's token is canceled and the receipt that comes anyway is kept.
            var pendingBefore = store.Pending;
            buy.EmitSignal(BaseButton.SignalName.Pressed);
            await FlowTask.WaitUntil(() => store.Pending != pendingBefore);
            var token = store.LastToken;
            screenNode.QueueFree();
            await FlowTask.WaitUntil(() => screen.IsCompleted);
            var canceled = token.IsCancellationRequested;
            store.Pending.SetResult(new Receipt("gem_pack", "t-late"));
            await FlowTask.Race(FlowTask.WaitUntil(() => late.Count == 1), FlowTask.WaitForSeconds(2, ui));

            return Verdict.Of($"retried: {bought.TransactionId} after {retries} calls; declined: {declined?.ToString() ?? "no exception"} after {declinedCalls} calls; blocked back key {blocked}, second press ignored {ignored}; bug contained {contained}; left: {screen.Status} ({screen.Result}), token canceled {canceled}, late receipts {late.Count}, entries left {router.Count}",
                (bought.TransactionId == "t-1" && retries == 3, "retried twice, then bought"),
                (declined == PurchaseError.Declined && declinedCalls == 1, "a declined purchase threw PurchaseFailedException and was not retried"),
                (blocked && ignored, "the back key and a second press did nothing during the purchase"),
                (contained, "a bug in a purchase stayed in its catch"),
                (screen.Status == FlowStatus.Succeeded && !screen.Result && canceled, "leaving canceled the store's token"),
                (late.Count == 1 && late[0].TransactionId == "t-late", "the late receipt went to onLateReceipt"),
                (router.Count == 0, "no back-key entry left"));
        }
        finally
        {
            FlowWorldNode.Instance.UnhandledException -= OnUnhandledException;
        }
    }
}
