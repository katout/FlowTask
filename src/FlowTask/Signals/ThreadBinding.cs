namespace Katout.FlowTask.Internal;

/// <summary>
/// The World and thread a signal, FlowProperty or Once belongs to. The first World that uses the object binds it (a wait
/// or a subscription in one of its flows, the object's construction in one, or Signal(FlowWorld)); from then on a write
/// from another thread, or a use by a World on another thread, throws <see cref="FlowThreadException"/>, and a use by
/// another World once the first is disposed throws <see cref="FlowMisuseException"/> (the object was kept from an
/// earlier session). Before a World uses it, nothing is checked, as for any .NET object. A derived signal
/// (FlowProperty.Changed) shares its owner's binding. The first binding happens under <see cref="Gate"/>, as does a
/// close from another thread of a signal that no World uses yet.
/// </summary>
internal sealed class ThreadBinding
{
    FlowWorld _world; // set once, under Gate
    int _threadId;    // the bound World's thread; 0 while unbound

    internal readonly object Gate = new();

    internal ThreadBinding()
    {
    }

    /// <summary>Signal(FlowWorld): bound to that World's thread, whatever thread runs the constructor.</summary>
    internal ThreadBinding(FlowWorld world)
    {
        _world = world;
        _threadId = world.ThreadId;
    }

    internal FlowWorld World => _world;

    /// <summary>The bound World, read from a thread that may not be its own.</summary>
    internal FlowWorld WorldForAnyThread => Volatile.Read(ref _world);

    /// <summary>Constructors: binds to the World whose flow runs on this thread, if any.</summary>
    internal void BindCurrent(object owner)
    {
        var world = FlowWorld.t_current;
        if (world != null) Bind(world, owner);
    }

    /// <summary>A World uses the object; <paramref name="owner"/> only names it in exceptions.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Bind(FlowWorld world, object owner)
    {
        if (!ReferenceEquals(_world, world)) BindSlow(world, owner);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void BindSlow(FlowWorld world, object owner)
    {
        var bound = WorldForAnyThread;
        if (bound == null)
        {
            lock (Gate)
            {
                bound = _world;
                if (bound == null)
                {
                    _threadId = world.ThreadId;
                    Volatile.Write(ref _world, world);
                    return;
                }
            }

            if (ReferenceEquals(bound, world)) return;
        }

        // The type only: a ToString override may print the user's value, and no user code runs while an exception is built.
        var what = owner.GetType().Name;
        var tick = what.IndexOf('`', StringComparison.Ordinal);
        if (tick > 0) what = what[..tick];
        if (bound.IsDisposed)
            throw new FlowMisuseException($"This {what} is bound to {bound}, the first World that used it, which is disposed: another World cannot use it. Create signals, properties and Once values for each session (World) and pass them, instead of keeping them in static fields.");
        if (bound.ThreadId != world.ThreadId)
            throw new FlowThreadException($"This {what} is bound to {bound} on thread {bound.ThreadId} and cannot be used by {world} on thread {world.ThreadId}.");
        // Another live World on the same thread: allowed; the object stays bound to the first.
    }

    /// <summary>A write (Emit, Set...): on the bound World's thread, once the object is bound.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Check(string what)
    {
        var bound = _threadId;
        if (bound != 0 && bound != Environment.CurrentManagedThreadId) ThrowWrongThread(what, bound);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ThrowWrongThread(string what, int bound) =>
        throw new FlowThreadException($"{what} was called from thread {Environment.CurrentManagedThreadId}, but the object belongs to thread {bound}. From other threads use EmitFromAnyThread, CloseFromAnyThread or FlowWorld.Post.");
}
