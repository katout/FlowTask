namespace Katout.FlowTask.Unity;

/// <summary>
/// Binds flows to GameObject lifetime: <c>gameObject.RunWhileActive(Patrol())</c> starts the flow and cancels it (with
/// full unwinding) when the GameObject is deactivated or destroyed; <c>await gameObject.WhileActive(Step())</c> does the
/// same inside the awaiting scope.
/// </summary>
public static class FlowLifetime
{
    static readonly List<FlowLifetimeTrigger> s_watched = new();

    /// <summary>
    /// Starts <paramref name="task"/> as a root flow on <paramref name="clock"/> (default: the DefaultClock of
    /// <see cref="FlowTaskUnity.World"/>; another World's clock runs it in that World) and cancels it when
    /// <paramref name="gameObject"/> stops being active in the hierarchy: <c>SetActive(false)</c> on it or on a parent,
    /// <c>Destroy</c> (which deactivates it at once), or the unload of its scene. The flow unwinds right there, inside the
    /// OnDisable of a hidden component, while the GameObject is still accessible.
    /// Activating the GameObject again does not start the flow again: start a new one (from OnEnable, for an object that
    /// goes back to a pool). A GameObject that is not active in the hierarchy runs nothing: <paramref name="task"/> is
    /// discarded, and the returned handle is already <see cref="FlowStatus.Canceled"/>. The GameObject owns the flow, so
    /// it does not matter who calls this: called from flow code, the flow is not a child of the calling scope, and its
    /// exception goes to <see cref="FlowWorld.OnUnhandledException"/> (to await it in a flow, use
    /// <see cref="WhileActive(GameObject, FlowTask)"/>). To stop only when the GameObject is destroyed, race the task
    /// with <see cref="WaitForDestroy"/>.
    /// </summary>
    public static FlowHandle RunWhileActive(this GameObject gameObject, FlowTask task, Clock clock = null)
    {
        if (gameObject == null) throw new ArgumentNullException(nameof(gameObject), "The GameObject is null or destroyed.");
        var w = clock?.World ?? FlowTaskUnity.World;
        if (!gameObject.activeInHierarchy)
        {
            task.Discard();
            return default;
        }

        var trigger = FlowLifetimeTrigger.GetOrAdd(gameObject);
        var handle = w.Run(task, clock);
        Bind(gameObject, trigger, w, new HandleEntry<FlowUnit>(handle));
        return handle;
    }

    /// <inheritdoc cref="RunWhileActive(GameObject, FlowTask, Clock)"/>
    public static FlowHandle<T> RunWhileActive<T>(this GameObject gameObject, FlowTask<T> task, Clock clock = null)
    {
        if (gameObject == null) throw new ArgumentNullException(nameof(gameObject), "The GameObject is null or destroyed.");
        var w = clock?.World ?? FlowTaskUnity.World;
        if (!gameObject.activeInHierarchy)
        {
            task.Discard();
            return default;
        }

        var trigger = FlowLifetimeTrigger.GetOrAdd(gameObject);
        var handle = w.Run(task, clock);
        Bind(gameObject, trigger, w, new HandleEntry<T>(handle));
        return handle;
    }

    /// <summary>
    /// GameObject owns flow, lazy like every FlowTask: when awaited (or passed to a combinator), runs
    /// <paramref name="task"/> and cancels it when <paramref name="gameObject"/> stops being active in the hierarchy, as
    /// <see cref="RunWhileActive(GameObject, FlowTask, Clock)"/> does. Deactivated outside flow code, the task unwinds
    /// inside OnDisable, while the GameObject is still accessible; deactivated from flow code, it unwinds when its World
    /// next runs the queued resumes (later in the same Tick or Flush, or at the next flush point after a
    /// <see cref="FlowWorld.Run(FlowTask, Clock)"/> called from outside). It runs in the scope that awaits it, so it also
    /// ends with that scope, and its exception is thrown there. Returns true when the task completed, false when the
    /// deactivation canceled it. If the GameObject is not active in the hierarchy (or destroyed) when this starts,
    /// <paramref name="task"/> is not run and the result is false.
    /// </summary>
    public static FlowTask<bool> WhileActive(this GameObject gameObject, FlowTask task)
    {
        // Only a null reference is rejected: a destroyed GameObject (null for Unity) ends the task with false when it starts.
        if (ReferenceEquals(gameObject, null)) throw new ArgumentNullException(nameof(gameObject));
        return Flow.Named(ScopeName(gameObject), WhileActiveCore(gameObject, task));
    }

    /// <summary>
    /// <see cref="WhileActive(GameObject, FlowTask)"/> for a task with a result: <c>(true, result)</c> when it completed,
    /// <c>(false, default)</c> when the deactivation canceled it first.
    /// </summary>
    public static FlowTask<(bool Completed, T Value)> WhileActive<T>(this GameObject gameObject, FlowTask<T> task)
    {
        if (ReferenceEquals(gameObject, null)) throw new ArgumentNullException(nameof(gameObject));
        return Flow.Named(ScopeName(gameObject), WhileActiveCore(gameObject, task));
    }

    /// <summary>The scope's name in dumps and scope paths: "WhileActive(Enemy) &gt; Patrol".</summary>
    static string ScopeName(GameObject gameObject) =>
        "WhileActive(" + (gameObject != null ? gameObject.name : "destroyed GameObject") + ")";

    static async FlowTask<bool> WhileActiveCore(GameObject gameObject, FlowTask task)
    {
        if (gameObject == null || !gameObject.activeInHierarchy)
        {
            task.Discard();
            return false;
        }

        using var deactivated = DeactivationWait.Bind(gameObject);
        // The deactivation wait starts first: the task's first step runs inside Race, and a deactivation it causes there
        // must find the wait already in place.
        var r = await FlowTask.Race(deactivated.Wait(), task);
        return r.Index == 1;
    }

    static async FlowTask<(bool Completed, T Value)> WhileActiveCore<T>(GameObject gameObject, FlowTask<T> task)
    {
        if (gameObject == null || !gameObject.activeInHierarchy)
        {
            task.Discard();
            return (false, default);
        }

        using var deactivated = DeactivationWait.Bind(gameObject);
        var r = await FlowTask.Race(deactivated.Wait(), task); // the deactivation wait first, as above
        return r.Index == 1 ? (true, r.Value1) : (false, default);
    }

    /// <summary>
    /// Completes when <paramref name="gameObject"/> is destroyed (immediately if it already is); deactivating it does
    /// not complete it. Combine with <c>FlowTask.Race</c> to stop work when an object goes away: the race is decided,
    /// and its loser unwound, inside OnDestroy. Made in flow code, start it in the same World (await it, or pass it to a
    /// combinator there): it belongs to that World.
    /// </summary>
    public static FlowTask WaitForDestroy(this GameObject gameObject)
    {
        if (gameObject == null) return FlowTask.CompletedTask;
        return FlowLifetimeTrigger.GetOrAdd(gameObject).WaitForDestroy();
    }

    /// <summary>
    /// Completes the <see cref="WaitForDestroy"/> of GameObjects destroyed without ever having been active (Unity calls
    /// no OnDestroy for them), and cancels the flows bound to them where the hidden component never woke (Edit Mode).
    /// Called by the PlayerLoop Tick; call it yourself when ticking the World manually.
    /// </summary>
    public static void PollWatched()
    {
        for (var i = s_watched.Count - 1; i >= 0; i--)
        {
            var t = s_watched[i];
            if (t == null) // destroyed (UnityEngine.Object null semantics)
            {
                s_watched.RemoveAt(i);
                if (!ReferenceEquals(t, null)) t.OnOwnerGone();
            }
            else if (t.HasAwoken)
            {
                s_watched.RemoveAt(i);
            }
        }
    }

    internal static int WatchedCount => s_watched.Count;

    internal static void Watch(FlowLifetimeTrigger trigger)
    {
        if (!s_watched.Contains(trigger)) s_watched.Add(trigger);
    }

    internal static void Unwatch(FlowLifetimeTrigger trigger) => s_watched.Remove(trigger);

    internal static void ResetStatics() => s_watched.Clear();

    /// <summary>
    /// Binds a flow that Run has just started. Run executes the flow up to its first await before the binding exists:
    /// if that part deactivated or destroyed the GameObject, the OnDisable that cancels the bound flows has already run
    /// without this one, so it is canceled (and unwound) here, as OnDisable would have done.
    /// </summary>
    static void Bind(GameObject gameObject, FlowLifetimeTrigger trigger, FlowWorld world, IEntry flow)
    {
        trigger.Add(world, flow);
        if (gameObject != null && gameObject.activeInHierarchy) return;
        if (flow.IsDone) return;
        flow.Cancel();
        FlowLifetimeTrigger.FlushNow(world, null);
    }

    /// <summary>What the hidden component ends when the GameObject is deactivated or destroyed.</summary>
    internal interface IEntry
    {
        bool IsDone { get; }
        void Cancel();
    }

    sealed class HandleEntry<T> : IEntry
    {
        readonly FlowHandle<T> _handle;

        internal HandleEntry(FlowHandle<T> handle) => _handle = handle;

        public bool IsDone => _handle.IsCompleted;

        public void Cancel()
        {
            if (!_handle.IsCompleted) _handle.Cancel();
        }
    }

    /// <summary>
    /// The deactivation that <see cref="WhileActive(GameObject, FlowTask)"/> races its task against. The hidden component
    /// sets it in OnDisable or OnDestroy, as it cancels the flows of RunWhileActive, and then flushes the World, so the
    /// Race is decided and its loser unwound right there. Released when the WhileActive scope ends.
    /// </summary>
    sealed class DeactivationWait : IEntry, IDisposable
    {
        // Made in flow code: bound to the World that runs the WhileActive.
        readonly Once<FlowUnit> _deactivated = new();
        bool _released;

        DeactivationWait()
        {
        }

        public bool IsDone => _released || _deactivated.IsSet;

        /// <summary>Called from flow code: binds a new wait to <paramref name="gameObject"/> in the current World.</summary>
        internal static DeactivationWait Bind(GameObject gameObject)
        {
            var wait = new DeactivationWait();
            FlowLifetimeTrigger.GetOrAdd(gameObject).Add(FlowWorld.Current, wait);
            return wait;
        }

        internal FlowTask<FlowUnit> Wait() => _deactivated.Wait();

        public void Cancel()
        {
            if (!IsDone) _deactivated.Set(FlowUnit.Default);
        }

        public void Dispose() => _released = true;
    }
}
