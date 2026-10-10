using System;
using System.Threading;
using System.Threading.Tasks;

namespace Katout.FlowTask.Samples
{
    /// <summary>What a store returns for a completed purchase.</summary>
    public sealed class Receipt
    {
        public Receipt(string itemId, string transactionId)
        {
            ItemId = itemId;
            TransactionId = transactionId;
        }

        public string ItemId { get; }
        public string TransactionId { get; }
    }

    /// <summary>An expected failure of the store. <see cref="Retryable"/>: trying again may pass (network, busy server).</summary>
    public sealed class StoreException : Exception
    {
        public StoreException(string message, bool retryable)
            : base(message)
        {
            Retryable = retryable;
        }

        public bool Retryable { get; }
    }

    /// <summary>The store as Task-based code sees it (an IAP SDK, a web API). Expected failures fault with <see cref="StoreException"/>.</summary>
    public interface IStore
    {
        /// <summary>
        /// Buys <paramref name="itemId"/>. <paramref name="ct"/> is canceled when the player leaves; the store may still
        /// complete the purchase (it was already charged), so the caller has to handle a receipt that arrives late.
        /// </summary>
        Task<Receipt> PurchaseAsync(string itemId, CancellationToken ct);
    }

    public enum PurchaseError
    {
        /// <summary>The store refused (payment declined, item unavailable).</summary>
        Declined,

        /// <summary>Retryable failures that did not pass in time.</summary>
        Unavailable,
    }

    /// <summary>
    /// A purchase failed in a way the game expects (<see cref="Error"/> says how), and the screen tells the player. The
    /// store's <see cref="StoreException"/> is the inner exception.
    /// </summary>
    public sealed class PurchaseFailedException : Exception
    {
        public PurchaseFailedException(PurchaseError error, Exception innerException)
            : base($"Purchase failed: {error}", innerException)
        {
            Error = error;
        }

        public PurchaseError Error { get; }
    }
}
