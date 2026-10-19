namespace Katout.FlowTask.Internal;

/// <summary>
/// Step 3 of a Tick: the waits that the Tick evaluates (time, frames, conditions), in the order they began. Conditions
/// (WaitUntil) are in one bucket evaluated every Tick. Time and frame waits are in buckets per clock and target: at the head
/// of a Tick only the due ones are taken out, and the walk merges them with the conditions by the order they began (a
/// number each wait takes when it joins), so the order, and the effect of a condition on the waits after it, are those of
/// a walk of every wait.
/// </summary>
internal sealed class TickList
{
    // The conditions, in the order they began.
    readonly DeadlineBucket _conditions = new();

    // The order in which the waits began: every wait that joins takes the next number.
    long _nextSeq = 1;

    // The clocks that have time or frame waits.
    ClockQueue[] _queues = new ClockQueue[4];
    int _queueCount;

    // Due waits the last walk skipped (their clock was paused, or they were canceled): the next walk takes them again.
    readonly DeadlineBucket _carried = new();
    DeadlineBucket[] _pool = new DeadlineBucket[8];
    int _poolCount;

    // The walk of one Evaluate: the conditions copied at its head, and the due waits sorted by the order they began.
    NodeRef[] _conds = new NodeRef[16];
    long[] _condSeqs = new long[16];
    NodeRef[] _due = new NodeRef[16];
    long[] _dueSeqs = new long[16];
    int _dueCount;
    int _dueIndex;
    bool _dueUnsorted;
    bool _evaluating;
    bool _walked;
    long _walkSeq;
    long _walkEndSeq;

    /// <summary>A condition: evaluated in every Tick in which its clock runs.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Add(FlowNode node)
    {
        if ((node.Flags & NodeFlags.InTickList) != 0) return;
        node.Flags |= NodeFlags.InTickList;
        node.TickSeq = _nextSeq++;
        _conditions.Append(node);
    }

    /// <summary>
    /// A time or frame wait on <paramref name="clock"/>, satisfied once the clock's Time (or FrameCount) reaches
    /// <paramref name="target"/>. The target is in the clock's own time, which only step 2 moves, so a change of time
    /// scale or pause (of the clock or of a parent) leaves it as it is.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void AddDeadline(FlowNode node, Clock clock, bool frames, double target)
    {
        if ((node.Flags & NodeFlags.InTickList) != 0) return;
        node.Flags |= NodeFlags.InTickList;
        node.TickSeq = _nextSeq++;
        // Waits that begin together usually share a target (the flows resumed in one Tick that wait a frame): they join
        // the bucket the last wait joined.
        var q = clock.TickQueue;
        var b = q == null ? null : frames ? q.Frames.Last : q.Time.Last;
        if (b == null || b.Key != target) b = NewBucket(clock, frames, target);
        b.Append(node);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    DeadlineBucket NewBucket(Clock clock, bool frames, double target)
    {
        var q = clock.TickQueue ??= new ClockQueue(clock);
        if (q.Index < 0) Register(q);
        var heap = frames ? q.Frames : q.Time;
        var b = RentBucket();
        b.Key = target;
        heap.Push(b);
        heap.Last = b;
        return b;
    }

    internal void Remove(FlowNode node)
    {
        if ((node.Flags & NodeFlags.InTickList) == 0) return;
        node.Flags &= ~NodeFlags.InTickList;
        var b = node.TickBucket;
        // Null: a time or frame wait taken out for the walk of this Evaluate, which skips it at its turn.
        if (b == null) return;
        node.TickBucket = null;
        b.RemoveAt(node.TickSlot);
        if (b == _conditions) return;
        if (b.Live == 0 && b.Heap != null)
        {
            b.Heap.Remove(b);
            ReturnBucket(b);
        }

        // Should a state machine wait again within the walk, a walk of every wait would evaluate the new wait at the old
        // place: the walk keeps that place. (A cancel by a condition unwinds after step 3, so this is only a safeguard.)
        if (_evaluating && node.IsStateMachine && node.TickSeq > _walkSeq && node.TickSeq < _walkEndSeq) AddDue(new NodeRef(node), node.TickSeq);
    }

    /// <summary>
    /// Evaluates the waits in the order they began, and a satisfied one reserves its resume (so waits satisfied in one Tick
    /// resume in that order, not by deadline). It walks a copy: a WaitUntil condition can end other waits.
    /// </summary>
    internal void Evaluate()
    {
        if (_conditions.Count == 0 && _queueCount == 0 && _carried.Count == 0) return;
        var condCount = CopyConditions();

        TakeDue();
        if (condCount == 0 && _dueCount == 0) return;
        _walkEndSeq = _nextSeq;
        _evaluating = true;
        _walked = false;
        try
        {
            Walk(condCount);
        }
        finally
        {
            _evaluating = false;
            if (!_walked) AbandonWalk(condCount);
            _dueCount = _dueIndex = 0;
            _dueUnsorted = false;
        }
    }

    /// <summary>Copies the conditions for the walk, and closes the holes the removed ones left.</summary>
    int CopyConditions()
    {
        var c = _conditions;
        if (_conds.Length < c.Live)
        {
            var size = Math.Max(c.Live, _conds.Length * 2);
            _conds = new NodeRef[size];
            _condSeqs = new long[size];
        }

        var conds = _conds;
        var condSeqs = _condSeqs;
        var slots = c.Slots;
        var w = 0;
        for (var i = 0; i < c.Count; i++)
        {
            var n = slots[i].Node;
            if (n == null) continue;
            if (w != i)
            {
                slots[w].Node = n;
                slots[i].Node = null;
                n.TickSlot = w;
            }

            conds[w] = new NodeRef(n);
            condSeqs[w++] = n.TickSeq;
        }

        c.Count = w;
        return w;
    }

    void Walk(int condCount)
    {
        var conds = _conds;
        var condSeqs = _condSeqs;
        var ci = 0;
        while (true)
        {
            if (_dueUnsorted) SortDue();
            NodeRef r;
            var di = _dueIndex;
            if (di < _dueCount && (ci >= condCount || _dueSeqs[di] < condSeqs[ci]))
            {
                r = _due[di];
                _due[di] = default;
                _walkSeq = _dueSeqs[di];
                _dueIndex = di + 1;
            }
            else if (ci < condCount)
            {
                r = conds[ci];
                conds[ci] = default;
                _walkSeq = condSeqs[ci++];
            }
            else
            {
                break;
            }

            var n = r.Node;
            if (!r.IsLive || (n.Flags & NodeFlags.InTickList) == 0 || (n.IsCancelConfirmed && !n.RunsCleanup))
            {
                Carry(n, r.Token);
                continue;
            }

            var clock = n.TickClock;
            if (clock.EffectivelyPaused)
            {
                // A removed clock stays paused, so only the waits on paused clocks pay for this check.
                if (clock.LiveWorld == null) FailRemovedClockWait(n, clock);
                else Carry(n, r.Token);
                continue;
            }

            if (!n.EvaluateTick())
            {
                Carry(n, r.Token);
                continue;
            }

            Remove(n);
            n.OnTickSatisfied();
        }

        _walked = true;
    }

    /// <summary>A wait threw out of the walk: the waits it did not reach stay in the list.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    void AbandonWalk(int condCount)
    {
        for (var i = _dueIndex; i < _dueCount; i++)
        {
            if (_due[i].Node is { } n) Carry(n, _due[i].Token);
            _due[i] = default;
        }

        Array.Clear(_conds, 0, condCount);
    }

    /// <summary>
    /// The due waits at the head of a Tick: those the last walk skipped, and those whose clock reached their target. Every
    /// wait of a removed clock is taken: its turn fails it.
    /// </summary>
    void TakeDue()
    {
        _dueCount = _dueIndex = 0;
        _dueUnsorted = false;
        if (_carried.Count > 0)
        {
            Take(_carried);
            _carried.Clear();
        }

        for (var i = _queueCount - 1; i >= 0; i--)
        {
            var q = _queues[i];
            var clock = q.Clock;
            if (clock.LiveWorld == null)
            {
                TakeUpTo(q.Time, double.PositiveInfinity);
                TakeUpTo(q.Frames, double.PositiveInfinity);
            }
            else
            {
                if (q.Time.Count > 0 && q.Time.Min.Key <= clock.Time) TakeUpTo(q.Time, clock.Time);
                if (q.Frames.Count > 0 && q.Frames.Min.Key <= clock.FrameCount) TakeUpTo(q.Frames, clock.FrameCount);
            }

            if (q.Time.Count == 0 && q.Frames.Count == 0) Unregister(q);
        }

        if (_dueUnsorted) SortDue();
    }

    void TakeUpTo(BucketHeap heap, double now)
    {
        while (heap.Count > 0 && heap.Min.Key <= now)
        {
            var b = heap.PopMin();
            Take(b);
            ReturnBucket(b);
        }
    }

    void Take(DeadlineBucket b)
    {
        var need = _dueCount + b.Live;
        if (need > _due.Length)
        {
            var size = Math.Max(need, _due.Length * 2);
            Array.Resize(ref _due, size);
            Array.Resize(ref _dueSeqs, size);
        }

        var due = _due;
        var dueSeqs = _dueSeqs;
        var count = _dueCount;
        // A bucket is in the order its waits began: only its first wait can come before the waits taken so far.
        var last = count > 0 ? dueSeqs[count - 1] : 0;
        var slots = b.Slots;
        for (var i = 0; i < b.Count; i++)
        {
            var n = slots[i].Node;
            if (n == null) continue;
            slots[i].Node = null;
            n.TickBucket = null;
            due[count] = new NodeRef(n);
            dueSeqs[count++] = n.TickSeq;
        }

        if (count > _dueCount && dueSeqs[_dueCount] < last) _dueUnsorted = true;
        _dueCount = count;
    }

    /// <summary>
    /// A scope clock removed within the walk: the waits on the clock after the current one fail at their turn, as in a walk
    /// of every wait. (A scope a condition cancels ends after step 3, so this is only a safeguard.)
    /// </summary>
    internal void OnClockRemoved(Clock clock)
    {
        if (!_evaluating || clock.TickQueue is not { Index: >= 0 } q) return;
        TakeLater(q.Time);
        TakeLater(q.Frames);
    }

    void TakeLater(BucketHeap heap)
    {
        // Emptied buckets stay in the heap: the next Evaluate takes every bucket of a removed clock.
        for (var i = 0; i < heap.Count; i++)
        {
            var b = heap.At(i);
            var slots = b.Slots;
            for (var s = 0; s < b.Count; s++)
            {
                var n = slots[s].Node;
                if (n == null || n.TickSeq <= _walkSeq || n.TickSeq >= _walkEndSeq) continue;
                n.TickBucket = null;
                b.RemoveAt(s);
                AddDue(new NodeRef(n), n.TickSeq);
            }
        }
    }

    /// <summary>A due wait skipped at its turn stays due: the next walk takes it again.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void Carry(FlowNode n, uint token)
    {
        if (n.TickBucket == null && n.Token == token && (n.Flags & NodeFlags.InTickList) != 0) _carried.Append(n);
    }

    void AddDue(NodeRef r, long seq)
    {
        if (_dueCount == _due.Length)
        {
            Array.Resize(ref _due, _dueCount * 2);
            Array.Resize(ref _dueSeqs, _dueCount * 2);
        }

        if (_dueCount > _dueIndex && seq < _dueSeqs[_dueCount - 1]) _dueUnsorted = true;
        _due[_dueCount] = r;
        _dueSeqs[_dueCount++] = seq;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void SortDue()
    {
        Array.Sort<long, NodeRef>(_dueSeqs, _due, _dueIndex, _dueCount - _dueIndex);
        _dueUnsorted = false;
    }

    void Register(ClockQueue q)
    {
        if (_queueCount == _queues.Length) Array.Resize(ref _queues, _queueCount * 2);
        q.Index = _queueCount;
        _queues[_queueCount++] = q;
    }

    void Unregister(ClockQueue q)
    {
        var last = _queues[--_queueCount];
        _queues[q.Index] = last;
        last.Index = q.Index;
        _queues[_queueCount] = null;
        q.Index = -1;
    }

    DeadlineBucket RentBucket() => _poolCount > 0 ? _pool[--_poolCount] : new DeadlineBucket();

    void ReturnBucket(DeadlineBucket b)
    {
        b.Clear();
        if (_poolCount == _pool.Length) Array.Resize(ref _pool, _poolCount * 2);
        _pool[_poolCount++] = b;
    }

    /// <summary>A wait still on a scope clock that was removed (a task outside the owner used it) would never end: it fails.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    void FailRemovedClockWait(FlowNode n, Clock clock)
    {
        Remove(n);
        var ex = Errors.RemovedClock(clock);
        if (n.IsStateMachine) n.FailPark(ex);
        else ((ClockWait)n).FailAsync(ex);
    }
}

/// <summary>The time and frame waits of one clock, in buckets ordered by target.</summary>
internal sealed class ClockQueue
{
    internal readonly Clock Clock;
    internal readonly BucketHeap Time = new();
    internal readonly BucketHeap Frames = new();

    /// <summary>The place in the tick list's clocks; -1 while it has no waits.</summary>
    internal int Index = -1;

    internal ClockQueue(Clock clock) => Clock = clock;
}

/// <summary>
/// Waits in the order they began: those of one clock with the same target, the conditions, or the due waits a walk
/// skipped. A removed wait leaves a hole.
/// </summary>
internal sealed class DeadlineBucket
{
    internal double Key;
    // In a struct, so that a store needs no check of the array's type (FlowNode is not sealed).
    internal NodeSlot[] Slots = new NodeSlot[4];
    internal int Count;
    internal int Live;

    /// <summary>The heap the bucket is in; null in the pool, and for the conditions and the skipped waits.</summary>
    internal BucketHeap Heap;
    internal int HeapIndex = -1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Append(FlowNode node)
    {
        if (Count == Slots.Length) Array.Resize(ref Slots, Count * 2);
        node.TickBucket = this;
        node.TickSlot = Count;
        Slots[Count++].Node = node;
        Live++;
    }

    internal void RemoveAt(int slot)
    {
        Slots[slot].Node = null;
        if (--Live == 0) Clear();
    }

    /// <summary>Every slot is empty: taking a wait out empties its slot, and so does removing it.</summary>
    internal void Clear()
    {
        Count = 0;
        Live = 0;
    }
}

internal struct NodeSlot
{
    internal FlowNode Node;
}

/// <summary>A min-heap of buckets by target.</summary>
internal sealed class BucketHeap
{
    DeadlineBucket[] _items = new DeadlineBucket[4];

    internal int Count;

    /// <summary>The bucket a wait joined last, while it is in the heap: the next wait with the same target joins it too.</summary>
    internal DeadlineBucket Last;

    internal DeadlineBucket Min => _items[0];

    internal DeadlineBucket At(int index) => _items[index];

    internal void Push(DeadlineBucket b)
    {
        if (Count == _items.Length) Array.Resize(ref _items, Count * 2);
        b.Heap = this;
        SiftUp(b, Count++);
    }

    internal DeadlineBucket PopMin()
    {
        var b = _items[0];
        RemoveAt(0);
        return b;
    }

    internal void Remove(DeadlineBucket b) => RemoveAt(b.HeapIndex);

    void RemoveAt(int index)
    {
        var removed = _items[index];
        removed.Heap = null;
        removed.HeapIndex = -1;
        if (ReferenceEquals(Last, removed)) Last = null;
        var last = _items[--Count];
        _items[Count] = null;
        if (index == Count) return;
        if (index > 0 && last.Key < _items[(index - 1) >> 1].Key) SiftUp(last, index);
        else SiftDown(last, index);
    }

    void SiftUp(DeadlineBucket b, int i)
    {
        while (i > 0)
        {
            var p = (i - 1) >> 1;
            var parent = _items[p];
            if (!(b.Key < parent.Key)) break;
            _items[i] = parent;
            parent.HeapIndex = i;
            i = p;
        }

        _items[i] = b;
        b.HeapIndex = i;
    }

    void SiftDown(DeadlineBucket b, int i)
    {
        while (true)
        {
            var c = 2 * i + 1;
            if (c >= Count) break;
            if (c + 1 < Count && _items[c + 1].Key < _items[c].Key) c++;
            var child = _items[c];
            if (!(child.Key < b.Key)) break;
            _items[i] = child;
            child.HeapIndex = i;
            i = c;
        }

        _items[i] = b;
        b.HeapIndex = i;
    }
}
