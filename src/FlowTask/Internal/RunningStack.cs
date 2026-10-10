namespace Katout.FlowTask.Internal;

/// <summary>The state machines whose code is on the call stack, innermost last.</summary>
internal sealed class RunningStack
{
    Entry[] _entries = new Entry[16];
    int _count;

    internal bool IsEmpty => _count == 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Push(FlowNode node)
    {
        if (_count == _entries.Length) Array.Resize(ref _entries, _entries.Length * 2);
        _entries[_count++].Node = node;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Pop() => _entries[--_count].Node = null;

    /// <summary>True when <paramref name="node"/> runs, or is an ancestor of a node that runs.</summary>
    internal bool IsOnPath(FlowNode node)
    {
        for (var i = 0; i < _count; i++)
        {
            for (var r = _entries[i].Node; r != null; r = r.Parent)
            {
                if (ReferenceEquals(r, node)) return true;
            }
        }

        return false;
    }

    /// <summary>An element of the stack: a struct, since a store into a FlowNode[] checks the element type.</summary>
    struct Entry
    {
        internal FlowNode Node;
    }
}
