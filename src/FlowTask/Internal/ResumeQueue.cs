namespace Katout.FlowTask.Internal;

/// <summary>
/// Step 4 of a Tick or Flush: the queued resumes, processed in order. A resume whose clock is paused is held until the
/// pause is released.
/// </summary>
internal sealed class ResumeQueue
{
    /// <summary>Resumes one flush processes at most; the rest wait for the next one (a guard against livelocks).</summary>
    const int MaxResumesPerFlush = 65536;

    readonly Unwinder _unwinder;
    readonly Reporter _reporter;
    readonly Deque<NodeRef> _queue = new(64);
    readonly List<NodeRef> _held = new();
    readonly List<NodeRef> _released = new();
    bool _pauseReleased;

    internal ResumeQueue(Unwinder unwinder, Reporter reporter)
    {
        _unwinder = unwinder;
        _reporter = reporter;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Enqueue(FlowNode node) => _queue.PushBack(new NodeRef(node));

    /// <summary>A clock was unpaused or removed: the resumes it held go back to the queue at the next flush.</summary>
    internal void OnPauseReleased() => _pauseReleased = true;

    /// <summary>Processes the queued resumes in order, and those queued meanwhile, up to the limit.</summary>
    internal void Flush()
    {
        _unwinder.ProcessPending();
        var processed = 0;
        while (true)
        {
            // A Pause released during this flush lets its held resumes run in it, in their order.
            if (_pauseReleased) ReleaseHeld();
            if (_queue.Count == 0) return;
            if (processed >= MaxResumesPerFlush)
            {
                _reporter.WarnOnce(FlowWarningKind.FlushLimit, $"A flush processed {MaxResumesPerFlush} resumes; {_queue.Count} moved to the next one. Two flows that resume each other without end?", null);
                return;
            }

            var e = _queue.PopFront();
            var n = e.Node;
            if (n.Token != e.Token) continue;
            // A removed scope clock stays paused but never releases: its resumes are not held.
            var clock = n.ResumeClock;
            if (clock.EffectivelyPaused && clock.LiveWorld != null)
            {
                _held.Add(e);
                continue;
            }

            processed++;
            n.OnDequeued();
            // Back at the top level: the ancestors deferred while this entry ran can unwind now.
            _unwinder.DrainDeferred();
            if (_unwinder.HasPending) _unwinder.ProcessPending();
        }
    }

    /// <summary>Resumes a Pause held go to the front of the queue once it is released, in their original order.</summary>
    void ReleaseHeld()
    {
        _pauseReleased = false;
        var count = _held.Count;
        if (count == 0) return;
        var kept = 0;
        for (var i = 0; i < count; i++)
        {
            var e = _held[i];
            if (e.Node.Token != e.Token) continue;
            var clock = e.Node.ResumeClock;
            if (clock.EffectivelyPaused && clock.LiveWorld != null) _held[kept++] = e;
            else _released.Add(e);
        }

        _held.RemoveRange(kept, count - kept);
        for (var i = _released.Count - 1; i >= 0; i--) _queue.PushFront(_released[i]);
        _released.Clear();
    }

    /// <summary>World.Dispose: the flows have ended, and nothing more resumes.</summary>
    internal void Clear()
    {
        _queue.Clear();
        _held.Clear();
    }
}
