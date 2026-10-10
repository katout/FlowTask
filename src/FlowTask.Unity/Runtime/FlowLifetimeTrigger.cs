// A block namespace, not a file-scoped one: Unity finds the class of a MonoBehaviour or ScriptableObject by parsing
// its file, and does not see it in a file-scoped namespace.
namespace Katout.FlowTask.Unity
{
    /// <summary>
    /// Hidden component added by <see cref="FlowLifetime.RunWhileActive(GameObject, FlowTask, Clock)"/>,
    /// <see cref="FlowLifetime.WhileActive(GameObject, FlowTask)"/> and <see cref="FlowLifetime.WaitForDestroy"/>. On
    /// disable (the GameObject or a parent is deactivated, or it is being destroyed) it cancels the bound flows and
    /// completes the deactivation waits of WhileActive; on destroy it also completes the waits for the destruction. Either
    /// way it then flushes the Worlds concerned right away (when not called from inside flow code), so the using/finally/
    /// AddCleanup blocks, and the loser of a Race with WaitForDestroy or WhileActive, run inside OnDisable or OnDestroy
    /// while the GameObject is still accessible.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    internal sealed class FlowLifetimeTrigger : MonoBehaviour
    {
        // Two lists that trade places when the flows are canceled: a flow that unwinds may bind another one meanwhile.
        List<Entry> _entries = new();
        List<Entry> _canceling = new();
        readonly List<DestroyedEntry> _destroyed = new();
        bool _awoken;
        bool _gone;

        internal bool HasAwoken => _awoken;

        internal bool IsGone => _gone;

        internal int LiveCount
        {
            get
            {
                var n = 0;
                foreach (var e in _entries)
                    if (!e.Flow.IsDone)
                        n++;
                return n;
            }
        }

        /// <summary>
        /// Completes when the GameObject is destroyed (at once if it already is). Each World waits on a Once of its own: a
        /// Once belongs to the first World that uses it, and the GameObject may outlive that World
        /// (<see cref="FlowTaskUnity.Shutdown"/> then <see cref="FlowTaskUnity.Initialize"/>, a World per session,
        /// DontDestroyOnLoad), after which the next World could not wait on it. Made in flow code, the wait takes the Once of
        /// the World running that code, and is to be started there: started in another World, it no longer completes once
        /// the World that made it is disposed. Made outside flow code, it takes the Once of the World that starts it.
        /// </summary>
        internal FlowTask WaitForDestroy()
        {
            if (_gone) return FlowTask.CompletedTask;
            // Called from flow code (the usual case, passed to a Race), the current World runs the wait: a leaf, so losing
            // the Race or unwinding costs no state machine and no FlowCanceledException. From outside flow code the World
            // is known only once a World starts the task.
            var world = FlowWorld.Current;
            return world != null ? DestroyedFor(world).Wait().WithoutResult() : WaitForDestroyOnTheStartingWorld();
        }

        async FlowTask WaitForDestroyOnTheStartingWorld()
        {
            if (_gone) return;
            await DestroyedFor(FlowWorld.Current).Wait();
        }

        /// <summary>
        /// The Once that <paramref name="world"/> waits on. The ones of disposed Worlds are dropped: the waits of a World end
        /// with it (only a wait made in one World and started in another could still be on one, see WaitForDestroy).
        /// </summary>
        Once<FlowUnit> DestroyedFor(FlowWorld world)
        {
            Once<FlowUnit> found = null;
            for (var i = _destroyed.Count - 1; i >= 0; i--)
            {
                var e = _destroyed[i];
                if (e.World.IsDisposed) _destroyed.RemoveAt(i);
                else if (ReferenceEquals(e.World, world)) found = e.Once;
            }

            if (found != null) return found;
            found = new Once<FlowUnit>();
            _destroyed.Add(new DestroyedEntry(world, found));
            return found;
        }

        internal static FlowLifetimeTrigger GetOrAdd(GameObject gameObject)
        {
            if (gameObject.TryGetComponent(out FlowLifetimeTrigger trigger)) return trigger;
            trigger = gameObject.AddComponent<FlowLifetimeTrigger>();
            // Never saved: added in Edit Mode (in a World of your own) it would otherwise end up in the scene or prefab,
            // invisible, without the flows it was bound to. The flag has no effect in Play Mode or in a player.
            trigger.hideFlags = HideFlags.HideInInspector | HideFlags.DontSaveInEditor;
            // AddComponent on an active GameObject runs Awake synchronously. On an inactive one it does not (nor in Edit
            // Mode), and Unity will not call OnDestroy either unless the object gets activated first: watch it from the
            // PlayerLoop.
            if (!trigger._awoken) FlowLifetime.Watch(trigger);
            return trigger;
        }

        void Awake()
        {
            _awoken = true;
            FlowLifetime.Unwatch(this);
        }

        // Unity calls OnDisable when the GameObject or a parent is deactivated, inside Object.Destroy on an active GameObject
        // (OnDestroy follows at the end of the frame), and when its scene is unloaded. A reactivation calls OnEnable, which
        // restarts nothing. Code that disables every MonoBehaviour of the object (to freeze it) disables this one too: its
        // flows stop as well, and Add enables it again for the next ones.
        void OnDisable()
        {
            if (_gone) return;
            FlowWorld first = null;
            List<FlowWorld> more = null;
            CancelFlows(ref first, ref more);
            FlushNow(first, more);
        }

        void OnDestroy() => OnOwnerGone();

        internal void Add(FlowWorld world, FlowLifetime.IEntry flow)
        {
            if (_gone)
            {
                flow.Cancel();
                return;
            }

            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].Flow.IsDone) _entries.RemoveAt(i);
            }

            _entries.Add(new Entry(world, flow));
            // Disabled, it would get no OnDisable when the GameObject is deactivated (only the OnDestroy).
            if (!enabled) enabled = true;
        }

        internal void OnOwnerGone()
        {
            if (_gone) return;
            _gone = true;
            FlowLifetime.Unwatch(this);
            FlowWorld first = null;
            List<FlowWorld> more = null;
            CancelFlows(ref first, ref more);

            var destroyed = _destroyed.ToArray();
            _destroyed.Clear();
            foreach (var d in destroyed)
            {
                if (d.World.IsDisposed || d.Once.IsSet) continue;
                try
                {
                    d.Once.Set(FlowUnit.Default);
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }

                // The waiters resume in a flush: flush now, so that a Race with WaitForDestroy is decided (and its loser
                // unwound) inside OnDestroy, while the GameObject is still accessible.
                AddWorld(d.World, ref first, ref more);
            }

            FlushNow(first, more);
        }

        /// <summary>Cancels the bound flows and collects their Worlds (no allocation when they share one World).</summary>
        void CancelFlows(ref FlowWorld first, ref List<FlowWorld> more)
        {
            if (_entries.Count == 0) return;
            var entries = _entries;
            _entries = _canceling;
            _canceling = entries;
            try
            {
                for (var i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    if (e.Flow.IsDone) continue;
                    try
                    {
                        e.Flow.Cancel();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex);
                    }

                    AddWorld(e.World, ref first, ref more);
                }
            }
            finally
            {
                entries.Clear();
            }
        }

        static void AddWorld(FlowWorld world, ref FlowWorld first, ref List<FlowWorld> more)
        {
            if (first == null)
            {
                first = world;
                return;
            }

            if (ReferenceEquals(first, world)) return;
            more ??= new List<FlowWorld>(1);
            if (!more.Contains(world)) more.Add(world);
        }

        /// <summary>
        /// Flushes the Worlds right away, so that the cancellations unwind now. Not from inside flow code
        /// (<see cref="FlowWorld.Current"/> is set): there a cancellation unwinds on the spot in the World that runs it,
        /// and the flush is left to the running Tick or Flush.
        /// </summary>
        internal static void FlushNow(FlowWorld first, List<FlowWorld> more)
        {
            if (first == null || FlowWorld.Current != null) return;
            Flush(first);
            if (more == null) return;
            foreach (var w in more) Flush(w);
        }

        static void Flush(FlowWorld world)
        {
            if (world.IsDisposed || world.IsExecuting) return;
            try
            {
                world.Flush();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        readonly record struct DestroyedEntry(FlowWorld World, Once<FlowUnit> Once);

        readonly record struct Entry(FlowWorld World, FlowLifetime.IEntry Flow);
    }
}
