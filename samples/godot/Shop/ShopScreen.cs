using System;
using Katout.FlowTask.Godot;
using Godot;

namespace Katout.FlowTask.Samples;

/// <summary>
/// Sample: the screen around <see cref="Shop"/>. Each press of Buy runs one purchase; the back key leaves the screen.
/// Bind it to the screen node (<c>screen.RunWhileInTree(ShopScreen.Run(buy, status, shop, router, CrashReport.Send))</c>).
/// </summary>
public static class ShopScreen
{
    /// <param name="reportBug">Receives a purchase that failed with a bug, as a crash reporter would.</param>
    public static async FlowTask Run(Button buy, Label status, Shop shop, BackKeyRouter router, Action<Exception> reportBug, string itemId = "gem_pack")
    {
        using var pressed = buy.PressedSignal();
        while (true)
        {
            // Next, not a subscription: a press while a purchase runs reaches nobody and is dropped, which is what a
            // Buy button wants (no second purchase queued behind the first).
            var r = await FlowTask.Race(pressed.Next(), router.Next(BackKeyRouter.Screen));
            if (r.Index == 1) return;

            status.Text = "Purchasing";
            try
            {
                var receipt = await shop.Purchase(itemId);
                status.Text = "Bought " + receipt.ItemId;
            }
            catch (PurchaseFailedException e)
            {
                status.Text = "Failed: " + e.Error; // expected: the store declined, or stayed busy
            }
            catch (Exception bug) when (bug is not FlowCanceledException)
            {
                // A bug in one purchase ends that purchase, handled here, instead of unwinding the screen and the flows
                // around it.
                reportBug(bug);
                status.Text = "Something went wrong";
            }
        }
    }
}
