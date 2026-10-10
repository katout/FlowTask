namespace Katout.FlowTask.Internal;

/// <summary>
/// Recycled instances of one type. Every pooled type keeps one in a static field, shared by all Worlds and threads: a
/// thread that finds the pool claimed by another does not wait (Rent returns null and the caller constructs; Return
/// leaves the instance to the GC). A struct without a static constructor, so IL2CPP needs no class-init check.
/// </summary>
internal struct NodePool<T> where T : class
{
    const int Limit = 1024;

    // Struct elements: storing into a T[] would check the element type on every Return.
    struct Slot
    {
        internal T Item;
    }

    Slot[] _slots;
    int _count;
    int _claimed;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T Rent()
    {
        // An unsynchronized read as a hint: an empty pool costs no atomic operation.
        if (_count == 0 || Interlocked.Exchange(ref _claimed, 1) != 0) return null;
        T item = null;
        var top = _count - 1;
        if (top >= 0)
        {
            item = _slots[top].Item;
            _slots[top].Item = null;
            _count = top;
        }

        Volatile.Write(ref _claimed, 0);
        return item;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Return(T item)
    {
        if (_count >= Limit || Interlocked.Exchange(ref _claimed, 1) != 0) return;
        var count = _count;
        if (count < Limit)
        {
            if (_slots == null) _slots = new Slot[16];
            else if (count == _slots.Length) Array.Resize(ref _slots, count * 2);
            _slots[count].Item = item;
            _count = count + 1;
        }

        Volatile.Write(ref _claimed, 0);
    }
}

/// <summary>Growable ring buffer used as a queue; allocation-free after warm-up.</summary>
internal sealed class Deque<T>
{
    T[] _items;
    int _head;
    int _count;

    internal Deque(int capacity) => _items = new T[capacity];

    internal int Count => _count;

    internal void PushBack(in T item)
    {
        if (_count == _items.Length) Grow();
        _items[(_head + _count) % _items.Length] = item;
        _count++;
    }

    internal void PushFront(in T item)
    {
        if (_count == _items.Length) Grow();
        _head = (_head - 1 + _items.Length) % _items.Length;
        _items[_head] = item;
        _count++;
    }

    internal T PopFront()
    {
        var item = _items[_head];
        _items[_head] = default;
        _head = (_head + 1) % _items.Length;
        _count--;
        return item;
    }

    internal void Clear()
    {
        Array.Clear(_items, 0, _items.Length);
        _head = 0;
        _count = 0;
    }

    void Grow()
    {
        var n = new T[_items.Length * 2];
        for (var i = 0; i < _count; i++) n[i] = _items[(_head + i) % _items.Length];
        _items = n;
        _head = 0;
    }
}

/// <summary>A node and the token it had: stale once the node is released.</summary>
internal readonly record struct NodeRef(FlowNode Node, uint Token)
{
    internal NodeRef(FlowNode node) : this(node, node.Token)
    {
    }

    internal bool IsLive => Node.Token == Token && Node.State == NodeState.Running;
}

internal enum InboxKind : byte
{
    /// <summary>An <see cref="IInboxItem"/>: a cross-thread emit or close, a posted action, a Task completion.</summary>
    Item,

    /// <summary>A FlowHandle.Cancel called on another thread.</summary>
    Cancel,
}

internal readonly record struct InboxEntry(object Target, uint Token, InboxKind Kind);

internal interface IInboxItem
{
    /// <summary>Step 1 (intake) of a Tick or Flush, on the World's thread.</summary>
    void ProcessInbox(uint token);

    /// <summary>The World was disposed with this entry queued: release what it holds without running it.</summary>
    void DiscardInbox(uint token);
}
