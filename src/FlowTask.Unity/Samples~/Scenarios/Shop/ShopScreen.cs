using System;
using Katout.FlowTask.Unity;

namespace Katout.FlowTask.Samples
{
    /// <summary>
    /// Sample: the screen around <see cref="Shop"/>. Each press of Buy runs one purchase; the back key leaves the screen.
    /// Bind it to the screen's GameObject (<c>screen.RunWhileActive(ShopScreen.Run(view, shop, router))</c>).
    /// </summary>
    public static class ShopScreen
    {
        public static async FlowTask Run(ShopView view, Shop shop, BackKeyRouter router)
        {
            using var buy = view.BuyButton.ClickedSignal();
            while (true)
            {
                // Next, not a subscription: a press while a purchase runs reaches nobody and is dropped, which is what a
                // Buy button wants (no second purchase queued behind the first).
                var r = await FlowTask.Race(buy.Next(), router.Next(BackKeyRouter.Screen));
                if (r.Index == 1) return;

                view.Status = "Purchasing";
                try
                {
                    var receipt = await shop.Purchase(view.ItemId);
                    view.Status = "Bought " + receipt.ItemId;
                }
                catch (PurchaseFailedException e)
                {
                    view.Status = "Failed: " + e.Error; // expected: the store declined, or stayed busy
                }
                catch (Exception bug) when (bug is not FlowCanceledException)
                {
                    // A bug in one purchase ends that purchase, handled here, instead of unwinding the screen and the flows
                    // around it.
                    view.LastBug = bug; // e.g. send it to the crash reporter
                    view.Status = "Something went wrong";
                }
            }
        }
    }
}
