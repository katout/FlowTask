using System;

namespace Katout.FlowTask.Samples;

/// <summary>
/// Sample: a purchase through a Task-based store, with retries, the back key disabled while it runs, and a receipt that
/// arrives after the player left kept rather than lost. Start purchases from a button through <see cref="ShopScreen"/>,
/// which contains a failure of one purchase (try/catch). It uses no engine API.
/// </summary>
public sealed class Shop
{
    const int MaxAttempts = 3;

    readonly IStore _store;
    readonly BackKeyRouter _router;
    readonly Clock _ui;
    readonly Action<Receipt> _onLateReceipt;
    readonly double _retryDelaySeconds;

    /// <param name="ui">The clock of the retry waits: a UI clock, since the shop may be opened over a paused game.</param>
    /// <param name="onLateReceipt">
    /// Receives a receipt that the store delivered after the purchase flow was canceled (the player left, the screen was
    /// destroyed): the item was paid for, so grant it (or record it and grant it at the next launch).
    /// </param>
    public Shop(IStore store, BackKeyRouter router, Clock ui, Action<Receipt> onLateReceipt, double retryDelaySeconds = 1)
    {
        _store = store;
        _router = router;
        _ui = ui;
        _onLateReceipt = onLateReceipt;
        _retryDelaySeconds = retryDelaySeconds;
    }

    /// <summary>
    /// Buys <paramref name="itemId"/>. Expected failures of the store throw <see cref="PurchaseFailedException"/>, which the
    /// screen shows to the player. Anything else is a bug and is thrown as it is, to the catch around the button
    /// (<see cref="ShopScreen"/>).
    /// </summary>
    public async FlowTask<Receipt> Purchase(string itemId)
    {
        using var block = _router.Block(); // the back key does nothing while the store is at work
        for (var attempt = 1; ; attempt++)
        {
            // The token is canceled when this flow is. Only StoreException is caught here; other exceptions go on up.
            try
            {
                return await FlowBridge.FromTask(ct => _store.PurchaseAsync(itemId, ct), _onLateReceipt);
            }
            catch (StoreException e)
            {
                if (!e.Retryable) throw new PurchaseFailedException(PurchaseError.Declined, e);
                if (attempt == MaxAttempts) throw new PurchaseFailedException(PurchaseError.Unavailable, e);
            }

            await FlowTask.WaitForSeconds(_retryDelaySeconds * (1 << (attempt - 1)), _ui); // 1 s, then 2 s
        }
    }
}
