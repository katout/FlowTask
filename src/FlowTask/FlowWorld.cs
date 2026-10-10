namespace Katout.FlowTask;

/// <summary>
/// The execution environment: the scope tree, the scheduler and the clocks. A World belongs to the thread that created
/// it; several may exist, also on one thread (tick them one after another).
/// </summary>
public sealed class FlowWorld : IDisposable
{
    [ThreadStatic] internal static FlowWorld t_current;

    readonly int _threadId;
    readonly List<IDeferredCompletion> _completions = new();
    bool _inTick;
    bool _completing;
    bool _disposing;
    bool _disposed;

    internal FlowNode CurrentScope;
    internal int ExecutionDepth;
    /// <summary>Counts the top-level passes (Tick, Flush, Run, Dispose): the synchronous-completion limit counts per pass.</summary>
    internal long TickId = 1;
    internal readonly RootNode Root;

    // The parts of the scheduler: the four steps of a Tick in their order, then what flow code and cancellation use.
    internal readonly Inbox Inbox;
    internal readonly ClockTree ClockTree;
    internal readonly TickList TickList = new();
    internal readonly ResumeQueue ResumeQueue;
    internal readonly RunningStack RunningStack = new();
    internal readonly Unwinder Unwinder;
    internal readonly Reporter Reporter;
    internal readonly CleanupWatch CleanupWatch;

    /// <summary>Creates a World on this thread; <paramref name="name"/> names it in dumps.</summary>
    public FlowWorld(string name = null)
    {
        Name = name ?? "FlowWorld";
        _threadId = Environment.CurrentManagedThreadId;
        Reporter = new Reporter(this);
        Unwinder = new Unwinder(this, RunningStack);
        ResumeQueue = new ResumeQueue(Unwinder, Reporter);
        ClockTree = new ClockTree(this, ResumeQueue);
        Inbox = new Inbox(Unwinder, Reporter);
        CleanupWatch = new CleanupWatch(this);
        UnscaledClock = ClockTree.Add(new Clock(this, "Unscaled", null, unscaled: true));
        DefaultClock = ClockTree.Add(new Clock(this, "Default", null, unscaled: false));
        Root = new RootNode(this);
    }

    /// <summary>The name in dumps and scope paths: the root scope's name.</summary>
    public string Name { get; }

    /// <summary>
    /// Advances by the dt passed to <see cref="Tick"/>: it cannot be paused or scaled. A clock made as its child keeps
    /// running while DefaultClock is paused (a UI clock).
    /// </summary>
    public Clock UnscaledClock { get; }

    /// <summary>The clock of the root scope; scopes inherit their parent's clock.</summary>
    public Clock DefaultClock { get; }

    /// <summary>The live clocks, parents before children.</summary>
    public IReadOnlyList<Clock> Clocks => ClockTree.Clocks;

    /// <summary>
    /// Receives the exceptions no flow caught (a root flow's, <see cref="FlowExceptionKind.Unhandled"/>) and the
    /// reports that change no result (<see cref="FlowExceptionKind"/>), as they are found. It runs inside the
    /// scheduler: it cannot Tick, Flush or Dispose the World. Without a handler, the outermost Tick, Flush, Run or
    /// Dispose throws them as <see cref="FlowUnhandledException"/> once its work is done; so does a handler that
    /// throws, with the report it was given.
    /// </summary>
    public Action<FlowExceptionInfo> OnUnhandledException { get; set; }

    /// <summary>Warnings (<see cref="FlowWarningKind"/>), each kind once per World. Handlers run inside the scheduler: keep them to logging.</summary>
    public event Action<FlowWarning> OnWarning;

    internal Action<FlowWarning> WarningHandlers => OnWarning;

    /// <summary>Tooling view of the scope tree. Not for game logic.</summary>
    public FlowDiagnostics Diagnostics => new(this);

    /// <summary>True once <see cref="Dispose"/> has ended the flows.</summary>
    public bool IsDisposed => _disposed;

    /// <summary>True while this World runs flow code (in Tick, Flush, Run, Dispose or an unwind): Tick and Flush cannot be called then.</summary>
    public bool IsExecuting => ExecutionDepth > 0 || _inTick || Inbox.IsDraining;

    /// <summary>The World whose flow code runs on this thread, if any.</summary>
    public static FlowWorld Current => t_current;

    /// <summary>True on the thread that created the World, the only one that may Tick it and run its flows.</summary>
    public bool IsBoundToCurrentThread => Environment.CurrentManagedThreadId == _threadId;

    internal bool IsWorldThread => Environment.CurrentManagedThreadId == _threadId;

    internal int ThreadId => _threadId;

    /// <summary>True from the start of Dispose: nothing starts, and a canceled scope's awaits throw again.</summary>
    internal bool IsDisposing => _disposing;

    /// <summary>
    /// Creates a clock that lives as long as the World and follows the pause and time scale of <paramref name="parent"/>.
    /// For a clock that belongs to one scope, use <see cref="Flow.CreateClock(string, Clock)"/>.
    /// </summary>
    public Clock CreateClock(string name, Clock parent)
    {
        if (parent == null) throw new ArgumentNullException(nameof(parent));
        CheckThread();
        return ClockTree.CreateWorldClock(name, parent);
    }

    /// <summary>Creates a World clock under <see cref="DefaultClock"/>.</summary>
    public Clock CreateClock(string name) => CreateClock(name, DefaultClock);

    /// <summary>
    /// Starts <paramref name="task"/> as a child of the root scope: it runs until it ends, however the caller ends, and
    /// its exception goes to <see cref="OnUnhandledException"/>. While Dispose ends the flows, it does not start the
    /// task and returns a handle that has ended Canceled.
    /// </summary>
    public FlowHandle Run(FlowTask task, Clock clock = null)
    {
        var node = RunCore<FlowUnit>(task, clock);
        return node == null ? default : new FlowHandle(node, node.Token);
    }

    /// <inheritdoc cref="Run(FlowTask, Clock)"/>
    public FlowHandle<T> Run<T>(FlowTask<T> task, Clock clock = null)
    {
        var node = RunCore(task, clock);
        return node == null ? default : new FlowHandle<T>(node, node.Token);
    }

    FlowNode<T> RunCore<T>(FlowTask<T> task, Clock clock)
    {
        if (task.IsDefault) throw Errors.DefaultFlowTask(typeof(T));
        CheckThread();
        CheckNotDisposed();
        if (clock != null && clock.World != this) throw new ArgumentException("The clock belongs to another World.", nameof(clock));
        if (clock is { IsRemoved: true }) throw Errors.RemovedClock(clock);
        var node = Flow.Materialize(task);
        if (_disposing)
        {
            node.ReleaseUnstarted();
            return null;
        }

        node.Flags |= NodeFlags.NoPool | NodeFlags.Spawned;
        if (clock != null) node.ClockOverride = clock;
        // A Run from outside the World's code is a pass of its own for the synchronous-completion limit, as Tick and Flush are.
        if (!IsExecuting) TickId++;
        var prev = t_current;
        t_current = this;
        ExecutionDepth++;
        try
        {
            node.Start(Root);
            if (RunningStack.IsEmpty) Unwinder.DrainDeferred();
        }
        finally
        {
            ExecutionDepth--;
            t_current = prev;
        }

        CompleteDeferred();
        ThrowUnhandledIfTopLevel();
        return node;
    }

    /// <summary>
    /// Drives the World once: the intake takes the sends of other threads, the clocks advance by
    /// <paramref name="deltaTime"/> (unscaled seconds), the time and frame waits that are due are resumed, and the flush
    /// runs the queued resumes. At its end, the AsTask Tasks of the flows that ended complete, and without
    /// <see cref="OnUnhandledException"/> the unhandled exceptions are thrown (<see cref="FlowUnhandledException"/>).
    /// </summary>
    public void Tick(double deltaTime)
    {
        CheckThread();
        CheckNotDisposed();
        if (deltaTime < 0 || double.IsNaN(deltaTime) || double.IsInfinity(deltaTime)) throw new ArgumentOutOfRangeException(nameof(deltaTime));
        if (IsExecuting) throw Errors.Reentrant("Tick");
        var prev = t_current;
        t_current = this;
        _inTick = true;
        TickId++;
        try
        {
            Inbox.Drain();
            ClockTree.Advance(deltaTime);
            TickList.Evaluate();
            FlushQueue();
            CleanupWatch.Check();
        }
        finally
        {
            _inTick = false;
            t_current = prev;
        }

        CompleteDeferred();
        ThrowUnhandledIfTopLevel();
    }

    /// <summary>
    /// The intake and the flush of a Tick, without advancing time or checking the time and frame waits: an engine can call
    /// it at several points of its loop. Its end is a Tick's: AsTask Tasks complete, and unhandled exceptions are
    /// thrown.
    /// </summary>
    public void Flush()
    {
        CheckThread();
        CheckNotDisposed();
        if (IsExecuting) throw Errors.Reentrant("Flush");
        var prev = t_current;
        t_current = this;
        TickId++;
        try
        {
            Inbox.Drain();
            FlushQueue();
        }
        finally
        {
            t_current = prev;
        }

        CompleteDeferred();
        ThrowUnhandledIfTopLevel();
    }

    /// <summary>The flush. The resumes run flow code, so the World executes meanwhile.</summary>
    void FlushQueue()
    {
        ExecutionDepth++;
        try
        {
            ResumeQueue.Flush();
        }
        finally
        {
            ExecutionDepth--;
        }
    }

    /// <summary>
    /// Cancels every flow (<see cref="CancelCause.WorldDisposed"/>) and unwinds it once: using and finally blocks run, and
    /// so do AddCleanup and Own. Dispose runs no Tick, so an await in a canceled scope's catch or finally block throws
    /// FlowCanceledException again: each block runs up to its first await. Nothing starts meanwhile (Run returns an ended
    /// handle). Then the sends still queued from other threads are dropped, the AsTask Tasks complete, and the reports no
    /// OnUnhandledException took are thrown.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1065", Justification = "Disposing a World from its own flow code is a programming error that must surface; unhandled exceptions of the unwind surface like those of Tick.")]
    public void Dispose()
    {
        if (_disposed) return;
        CheckThread();
        if (IsExecuting) throw new FlowMisuseException("A World cannot be disposed from its own flow code, a cleanup or a handler; dispose it after Tick returns.");
        var prev = t_current;
        t_current = this;
        ExecutionDepth++;
        _disposing = true;
        TickId++;
        try
        {
            for (var c = Root.FirstChild; c != null; c = c.NextSibling) Unwinder.MarkSubtree(c, CancelCause.WorldDisposed);
            Unwinder.UnwindChildren(Root);
            Unwinder.DrainDeferred();
            Inbox.Close();
        }
        finally
        {
            ExecutionDepth--;
            t_current = prev;
        }

        ResumeQueue.Clear();
        _disposed = true;
        CompleteDeferred();
        ThrowUnhandledIfTopLevel();
    }

    /// <summary>
    /// Runs <paramref name="action"/> on this World's thread in the intake at the start of the next Tick or Flush, in the
    /// order posted, from any thread. It runs outside every scope; its exception is unhandled. Once the World is disposed,
    /// it never runs.
    /// </summary>
    public void Post(Action action)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));
        Inbox.PostAction(action);
    }

    /// <summary>The scope tree as text: each node, what it waits for, its clock and for how long.</summary>
    public string Dump() => ScopeTreeDumper.Dump(this);

    /// <summary>Queues a completion for <see cref="CompleteDeferred"/>; called when a flow with an AsTask Task ends.</summary>
    internal void QueueCompletion(IDeferredCompletion completion) => _completions.Add(completion);

    /// <summary>
    /// At the top level of a Tick, Flush, Run or Dispose: completes the AsTask Tasks of the flows that ended. Their
    /// continuations run here, on the World thread; a Run, Tick or Flush they call is part of this call.
    /// </summary>
    void CompleteDeferred()
    {
        if (_completions.Count == 0 || _completing || IsExecuting) return;
        _completing = true;
        try
        {
            for (var i = 0; i < _completions.Count; i++)
            {
                var c = _completions[i];
                _completions[i] = null;
                try
                {
                    c.Complete();
                }
#pragma warning disable CA1031 // AsTask continuations are user code: their failure is an unhandled exception
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    Reporter.Report(new FlowExceptionInfo(ex, "<AsTask continuation>", FlowExceptionKind.Unhandled));
                }
            }
        }
        finally
        {
            _completions.Clear();
            _completing = false;
        }
    }

    /// <summary>Throws the reports no OnUnhandledException took, unless flow code or an AsTask continuation of this World runs (the outer call throws them).</summary>
    void ThrowUnhandledIfTopLevel()
    {
        if (!Reporter.HasUnhandled || _completing || IsExecuting) return;
        Reporter.ThrowUnhandled();
    }

    internal void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new FlowThreadException($"{this} belongs to thread {_threadId} but was used from thread {Environment.CurrentManagedThreadId}. From other threads, use EmitFromAnyThread, FlowWorld.Post or FlowHandle.Cancel.");
    }

    void CheckNotDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FlowWorld));
    }

    /// <summary><c>FlowWorld(Name)</c>.</summary>
    public override string ToString() => $"FlowWorld({Name})";
}
