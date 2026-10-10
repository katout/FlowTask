namespace Katout.FlowTask.Internal;

/// <summary>
/// Step 1 of a Tick or Flush: the World's inbox, which takes the sends of other threads (emits, closes, cancels, posted
/// actions, Task completions).
/// </summary>
internal sealed class Inbox
{
    readonly Unwinder _unwinder;
    readonly Reporter _reporter;

    // Every cross-thread send goes through this one queue under this one lock, so the sends of one thread are processed
    // in the order it made them, whatever their kind. A signal queues its values under the same lock.
    readonly object _lock = new();
    Queue<InboxEntry> _entries = new();
    Queue<InboxEntry> _swap = new();
    volatile bool _hasItems;
    bool _closed; // under _lock; set by Close, after which sends are dropped
    bool _draining; // a posted action that called Tick or Flush would drain the inbox under Drain

    internal Inbox(Unwinder unwinder, Reporter reporter)
    {
        _unwinder = unwinder;
        _reporter = reporter;
    }

    internal object Lock => _lock;

    /// <summary>True while <see cref="Drain"/> processes the entries: a posted action runs then.</summary>
    internal bool IsDraining => _draining;

    /// <summary>Queues an entry; the caller holds <see cref="Lock"/>. False, queuing nothing, once the World is disposed.</summary>
    internal bool TryEnqueueLocked(object target, uint token, InboxKind kind)
    {
        if (_closed) return false;
        _entries.Enqueue(new InboxEntry(target, token, kind));
        _hasItems = true;
        return true;
    }

    internal bool Post(IInboxItem item, uint token)
    {
        lock (_lock) return TryEnqueueLocked(item, token, InboxKind.Item);
    }

    internal bool PostCancel(FlowNode node, uint token)
    {
        lock (_lock) return TryEnqueueLocked(node, token, InboxKind.Cancel);
    }

    /// <summary>FlowWorld.Post: runs <paramref name="action"/> outside every scope; its exception is unhandled.</summary>
    internal void PostAction(Action action) => Post(new PostedAction(action, _reporter), 0);

    sealed class PostedAction : IInboxItem
    {
        readonly Action _action;
        readonly Reporter _reporter;

        internal PostedAction(Action action, Reporter reporter)
        {
            _action = action;
            _reporter = reporter;
        }

        public void ProcessInbox(uint token)
        {
            try
            {
                _action();
            }
#pragma warning disable CA1031 // a posted action runs outside every scope: its failure is an unhandled exception
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _reporter.Report(new FlowExceptionInfo(ex, "<FlowWorld.Post>", FlowExceptionKind.Unhandled));
            }
        }

        public void DiscardInbox(uint token)
        {
        }
    }

    internal void Drain()
    {
        if (!_hasItems) return;
        Queue<InboxEntry> batch;
        lock (_lock)
        {
            batch = _entries;
            _entries = _swap;
            _swap = batch;
            _hasItems = false;
        }

        // A posted action runs here: IsExecuting is true meanwhile, so it cannot Flush and drain the queue under this loop.
        _draining = true;
        try
        {
            while (batch.Count > 0)
            {
                var e = batch.Dequeue();
                if (e.Kind == InboxKind.Item)
                {
                    ((IInboxItem)e.Target).ProcessInbox(e.Token);
                    continue;
                }

                // A Cancel from another thread: confirmed now, unwound at the head of this flush.
                var target = (FlowNode)e.Target;
                if (target.Token == e.Token) _unwinder.CancelNode(target, CancelCause.Explicit);
            }
        }
        finally
        {
            _draining = false;
        }
    }

    /// <summary>World.Dispose: later sends are dropped, and those still queued are discarded (posted actions do not run).</summary>
    internal void Close()
    {
        lock (_lock)
        {
            _closed = true;
            _hasItems = false;
        }

        // Nothing is queued any more, so the queue can be read without the lock.
        while (_entries.Count > 0)
        {
            var e = _entries.Dequeue();
            if (e.Kind == InboxKind.Item) ((IInboxItem)e.Target).DiscardInbox(e.Token);
        }
    }
}
