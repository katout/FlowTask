namespace Katout.FlowTask.Tests;

/// <summary>
/// Randomized stress test of the scope machinery: random trees of flows mixing Race, WhenAll, Spawn, exceptions caught
/// by try/catch at several levels, pauses and cancellations from inside and outside flows, and in a second run cleanups
/// (a canceled scope's finally block that awaits a leaf or a whole subtree, which runs to its end). Besides the lifetime invariants (every
/// entered try runs its finally exactly once, nothing but the fuzzer's own exceptions reaches OnUnhandledException,
/// nothing is left alive after Dispose), the fuzzer checks the ordering rules of the execution model against its own
/// bookkeeping: the resume order, that Emit only reserves, the timing of waits, that a child's subtree (cleanups
/// included) ends before its awaiter continues, that descendants unwind first, where a cancellation unwinds, and that
/// every exception it throws is received exactly once (by a catch, or by OnUnhandledException, the Undelivered reports
/// included).
/// </summary>
public class FuzzTests
{
    /// <summary>An exception that only a catch of its type receives: the catch-alls take it too, the other catches pass it on.</summary>
    sealed class Stop : Exception
    {
        public Stop() : base("stop")
        {
        }
    }

    /// <summary>
    /// A place where flows are started (an await, a Spawn or a World.Run). Open counts live nodes below it, and the
    /// cleanups running below it: everything above waits for them.
    /// </summary>
    sealed class Site
    {
        public int Open;
        public int Cleanups; // cleanups running below it: a later cancellation does not reach them, so it stays open meanwhile
        public int DoneMask;
    }

    sealed class Waiter
    {
        public int Signal;
        public long RegisteredAt;
        public Clock Clock;
        public bool Resolved;
    }

    sealed class NodeInfo
    {
        public int Id;
        public NodeInfo Parent;
        public Site StartSite;
        public bool IsOpen;
        public bool MayUnwindAtOwnAwait; // was on the running path when it was canceled
        public readonly List<NodeInfo> Children = new();
        public bool ExitedCanceled;
        public long ExitSweep = -1;
        public int CleanupStage; // 0: none ran, 2: the second (later registered) ran, 1: both ran
        public Site Self;
        public Site[] Chain; // the node's own start site and every site above it
    }

    sealed class Fuzzer
    {
        const double Dt = 1.0 / 60;
        readonly Random _rng;
        readonly FlowWorld _world;
        readonly Signal<long>[] _signals = { new(), new(), new() };
        readonly List<long>[] _emitted = { new(), new(), new() };
        readonly Clock _game;
        readonly List<FlowHandle> _handles = new();
        readonly List<Site> _handleSites = new();
        readonly List<Waiter> _waiters = new();
        readonly List<NodeInfo> _cancelers = new(); // the nodes inside whose Cancel (case 7) the running code is
        /// <summary>The exceptions the fuzzer threw, and each receipt of one of them (by a catch, or by OnUnhandledException).</summary>
        public readonly HashSet<Exception> Thrown = new();
        public readonly List<Exception> Received = new();
        public int Entered;
        public int Exited;
        /// <summary>A canceled scope's finally block sometimes awaits a leaf or a subtree (a cleanup).</summary>
        public bool Cleanups;
        public readonly List<string> Violations = new();
        int _nextId;
        long _resumes;
        long _emits;
        long _registrations;
        long _sweep; // changes whenever flow code resumes or a cancellation is requested: exits with the same value belong to one unwind
        long _defaultSignalEmit = -1, _defaultSignalReg = -1, _gameSignalEmit = -1, _gameSignalReg = -1;
        long _tickWaitFrame = -1, _tickWaitReg = -1;

        public Fuzzer(int seed, FlowWorld world)
        {
            _rng = new Random(seed);
            _world = world;
            _game = world.CreateClock("Game");
        }

        int Roll(int n) => _rng.Next(n);

        void Violation(string message)
        {
            if (Violations.Count < 20) Violations.Add(message + " (frame " + _world.DefaultClock.FrameCount + ")");
        }

        void Resumed()
        {
            _resumes++;
            _sweep++;
        }

        /// <summary>OnUnhandledException received <paramref name="exception"/>.</summary>
        public void OnReported(Exception exception)
        {
            if (Thrown.Contains(exception)) Received.Add(exception);
        }

        /// <summary>A catch of the fuzzer received <paramref name="exception"/>.</summary>
        void OnCaught(Exception exception)
        {
            if (Thrown.Contains(exception)) Received.Add(exception);
            else Violation("a catch received an exception the fuzzer did not throw: " + exception);
        }

        NodeInfo Enter(NodeInfo parent, Site site)
        {
            var chain = new List<Site>();
            if (site != null) chain.Add(site);
            if (parent != null)
            {
                chain.Add(parent.Self);
                chain.AddRange(parent.Chain);
            }

            var me = new NodeInfo { Id = ++_nextId, Parent = parent, StartSite = site, IsOpen = true, Self = new Site(), Chain = chain.ToArray() };
            foreach (var s in me.Chain) s.Open++;
            parent?.Children.Add(me);
            Entered++;
            return me;
        }

        void Exit(NodeInfo me, bool canceled)
        {
            if (!me.IsOpen)
            {
                Violation("finally ran twice for " + me.Id);
                return;
            }

            me.IsOpen = false;
            Exited++;
            // An unwinding scope runs its own finally only after all of its descendants have ended, their cleanups included.
            if (canceled && !me.MayUnwindAtOwnAwait && me.Self.Open > 0) Violation($"node {me.Id} unwound while {me.Self.Open} descendant(s) were alive");
            // Among siblings unwound together, the later-started one unwinds first.
            if (canceled && !me.MayUnwindAtOwnAwait && me.Parent != null)
            {
                foreach (var x in me.Parent.Children)
                {
                    if (x.Id < me.Id && x.ExitedCanceled && !x.MayUnwindAtOwnAwait && x.ExitSweep == _sweep)
                        Violation($"node {me.Id} unwound after its earlier-started sibling {x.Id}");
                }
            }

            me.ExitedCanceled = canceled;
            me.ExitSweep = _sweep;
            foreach (var s in me.Chain) s.Open--;
            // Not canceled, the node's own code ended it (it returned, or an exception left it, perhaps one thrown again at
            // an await, where no code after the await runs to mark a resume): what that end cancels is a new unwind, apart
            // from one processed before it in the same flush (a Cancel of an earlier-started sibling).
            if (!canceled) _sweep++;
        }

        // AddCleanup runs after the scope's finally and after its children ended, LIFO.
        void Cleanup(NodeInfo me, int which)
        {
            if (me.IsOpen) Violation($"a cleanup of node {me.Id} ran before its finally");
            if (me.Self.Open > 0) Violation($"a cleanup of node {me.Id} ran while {me.Self.Open} descendant(s) were alive");
            if (which == 2)
            {
                if (me.CleanupStage != 0) Violation($"cleanups of node {me.Id} ran out of order");
                me.CleanupStage = 2;
            }
            else
            {
                if (me.CleanupStage != 2) Violation($"cleanups of node {me.Id} did not run LIFO");
                me.CleanupStage = 1;
            }
        }

        // Every waiter present at an emit is resumed by the end of the Tick that processed it (DefaultClock
        // is never paused here), unless it was canceled.
        void CheckWaiters()
        {
            var w = 0;
            for (var i = 0; i < _waiters.Count; i++)
            {
                var x = _waiters[i];
                if (x.Resolved) continue;
                var emitted = _emitted[x.Signal];
                if (x.Clock == _world.DefaultClock && emitted.Count > 0 && emitted[^1] > x.RegisteredAt)
                {
                    Violation($"a waiter on signal {x.Signal} registered before emit #{emitted[^1]} was not resumed");
                    continue;
                }

                _waiters[w++] = x;
            }

            _waiters.RemoveRange(w, _waiters.Count - w);
        }

        void Closed(Site site, string awaited)
        {
            if (site.Open > 0) Violation(awaited + ": the awaiter continued while " + site.Open + " node(s) below it were alive");
        }

        // Whether `me` runs inside the flow started at `site`, following the scope tree.
        static bool IsInside(NodeInfo me, Site site)
        {
            for (var n = me; n != null; n = n.Parent)
            {
                if (n.StartSite == site) return true;
            }

            return false;
        }

        // A cleanup runs below every site of `me`'s chain: they are alive, and wait for it.
        static void CleanupStarts(NodeInfo me)
        {
            foreach (var x in me.Chain)
            {
                x.Open++;
                x.Cleanups++;
            }
        }

        static void CleanupEnds(NodeInfo me)
        {
            foreach (var x in me.Chain)
            {
                x.Open--;
                x.Cleanups--;
            }
        }

        // Whether a node on the running path runs inside the flow started at `site`: `me` or, when `me` runs inside the
        // Cancel of another node (a cleanup that the unwinding started runs its code), that node.
        bool RunsInside(NodeInfo me, Site site)
        {
            if (IsInside(me, site)) return true;
            foreach (var c in _cancelers)
            {
                if (IsInside(c, site)) return true;
            }

            return false;
        }

        // A cancellation requested by running flow code: the scopes on the running path (a subset of the requester's
        // ancestors, and of the ancestors of the nodes whose Cancel it runs inside) unwind at their own next await, so
        // the unwind order is checked only for the others. The same holds for the ancestors of a flow that throws: the
        // exception goes up the await chain, and the scopes it cancels on its way (a nursery's owner) may be on the
        // running path. Over-excludes, never misreports.
        void MarkRunningPath(NodeInfo me)
        {
            for (var n = me; n != null; n = n.Parent) n.MayUnwindAtOwnAwait = true;
            foreach (var c in _cancelers)
            {
                for (var n = c; n != null; n = n.Parent) n.MayUnwindAtOwnAwait = true;
            }
        }

        void Emit(int k)
        {
            var seq = ++_emits;
            _emitted[k].Add(seq);
            var resumes = _resumes;
            var exited = Exited;
            _signals[k].Emit(seq);
            if (_resumes != resumes || Exited != exited) Violation("Emit ran flow code synchronously");
        }

        // A frame wait on the scope's clock resumes exactly n frames later (DefaultClock is never paused
        // here; Game can be paused, so only the lower bound holds there). DelayFrames(0) completes synchronously.
        void CheckFrames(Clock clock, long f0, int n)
        {
            var d = clock.FrameCount - f0;
            var ok = n == 0 ? d == 0 : clock == _world.DefaultClock ? d == n : d >= n;
            if (!ok) Violation($"DelayFrames({n}) on {clock.Name} resumed after {d} frame(s)");
        }

        void CheckWaitForSeconds(Clock clock, double t0, long f0, double seconds)
        {
            var target = t0 + seconds;
            if (clock.Time < target) Violation($"WaitForSeconds({seconds}) on {clock.Name} resumed early");
            if (clock.FrameCount == f0) Violation($"WaitForSeconds({seconds}) on {clock.Name} resumed in the tick it was registered");
            if (clock == _world.DefaultClock && clock.Time - clock.DeltaTime >= target + 1e-9)
                Violation($"WaitForSeconds({seconds}) resumed a tick late");
        }

        // Time waits satisfied in the same Tick resume in tick-list (registration) order.
        void CheckTickWaitOrder(Clock clock, long reg)
        {
            if (clock != _world.DefaultClock) return;
            var frame = clock.FrameCount;
            if (frame == _tickWaitFrame && reg < _tickWaitReg) Violation("time waits resumed out of registration order");
            _tickWaitFrame = frame;
            _tickWaitReg = reg;
        }

        // The waiter receives the first emit after it registered. Signal resumes on one clock run in reservation order
        // (emit order, then registration order), also across a Pause and its release, cleanups included.
        void CheckSignal(Clock clock, int k, long registeredAt, long reg, long value)
        {
            var list = _emitted[k];
            var i = list.BinarySearch(registeredAt + 1);
            if (i < 0) i = ~i;
            var expected = i < list.Count ? list[i] : -1;
            if (value != expected) Violation($"waiter on signal {k} registered after emit #{registeredAt} received #{value}, expected #{expected}");
            if (clock == _world.DefaultClock) CheckReservation(ref _defaultSignalEmit, ref _defaultSignalReg, value, reg, clock);
            else CheckReservation(ref _gameSignalEmit, ref _gameSignalReg, value, reg, clock);
        }

        void CheckReservation(ref long lastEmit, ref long lastReg, long emit, long reg, Clock clock)
        {
            if (emit < lastEmit || (emit == lastEmit && reg < lastReg))
                Violation($"signal resume (emit #{emit}, reg {reg}) on {clock.Name} ran after (emit #{lastEmit}, reg {lastReg})");
            lastEmit = emit;
            lastReg = reg;
        }

        FlowTask Child(int depth, NodeInfo parent, Site site, int index) =>
            depth > 4 ? Leaf(site, index) : Node(depth, parent, site, index);

        async FlowTask Leaf(Site site, int index)
        {
            var clock = Flow.CurrentClock;
            switch (Roll(4))
            {
                case 0:
                {
                    var seconds = Roll(5) * 0.02;
                    var t0 = clock.Time;
                    var f0 = clock.FrameCount;
                    var reg = ++_registrations;
                    await FlowTask.WaitForSeconds(seconds);
                    Resumed();
                    CheckWaitForSeconds(clock, t0, f0, seconds);
                    CheckTickWaitOrder(clock, reg);
                    break;
                }
                case 1:
                {
                    var k = Roll(3);
                    var registeredAt = _emits;
                    var reg = ++_registrations;
                    var w = new Waiter { Signal = k, RegisteredAt = registeredAt, Clock = clock };
                    _waiters.Add(w);
                    long value;
                    try
                    {
                        value = await _signals[k].Next();
                    }
                    catch (FlowCanceledException)
                    {
                        w.Resolved = true;
                        throw;
                    }

                    w.Resolved = true;
                    Resumed();
                    CheckSignal(clock, k, registeredAt, reg, value);
                    break;
                }
                case 2:
                {
                    var f0 = clock.FrameCount;
                    var reg = ++_registrations;
                    await FlowTask.NextFrame();
                    Resumed();
                    CheckFrames(clock, f0, 1);
                    CheckTickWaitOrder(clock, reg);
                    break;
                }
                default:
                {
                    var n = Roll(3);
                    var f0 = clock.FrameCount;
                    var reg = ++_registrations;
                    await FlowTask.DelayFrames(n);
                    Resumed();
                    CheckFrames(clock, f0, n);
                    if (n > 0) CheckTickWaitOrder(clock, reg);
                    break;
                }
            }

            if (site != null) site.DoneMask |= 1 << index;
        }

        async FlowTask Node(int depth, NodeInfo parent, Site site, int index)
        {
            var me = Enter(parent, site);
            if (Roll(4) == 0)
            {
                Flow.AddCleanup(() => Cleanup(me, 1));
                Flow.AddCleanup(() => Cleanup(me, 2));
            }

            var steps = 1 + Roll(4);
            var canceled = false;
            try
            {
                for (var i = 0; i < steps; i++)
                {
                    switch (Roll(11))
                    {
                        case 0:
                        {
                            var s = new Site();
                            var r = await FlowTask.Race(Child(depth + 1, me, s, 0), Child(depth + 1, me, s, 1));
                            Resumed();
                            Closed(s, "Race");
                            if ((s.DoneMask & (1 << r.Index)) == 0) Violation("the Race winner did not run to its end");
                            break;
                        }
                        case 1:
                        {
                            var s = new Site();
                            await FlowTask.WhenAll(Child(depth + 1, me, s, 0), Child(depth + 1, me, s, 1));
                            Resumed();
                            Closed(s, "WhenAll");
                            break;
                        }
                        case 2:
                        {
                            var s = new Site();
                            _handles.Add(Flow.Spawn(Child(depth + 1, me, s, 0)));
                            _handleSites.Add(s);
                            break;
                        }
                        case 3:
                        {
                            // A catch of one type: every other exception goes on up.
                            var s = new Site();
                            try
                            {
                                await Child(depth + 1, me, s, 0);
                            }
                            catch (Stop stop)
                            {
                                OnCaught(stop);
                            }

                            Resumed();
                            Closed(s, "catch (Stop)");
                            break;
                        }
                        case 4:
                        {
                            // A catch-all that lets the cancellation pass: the way to contain a failure.
                            var s = new Site();
                            try
                            {
                                await Child(depth + 1, me, s, 0);
                            }
                            catch (Exception e) when (e is not FlowCanceledException)
                            {
                                OnCaught(e);
                            }

                            Resumed();
                            Closed(s, "catch");
                            break;
                        }
                        case 5:
                            if (Roll(4) == 0)
                            {
                                MarkRunningPath(me);
                                _sweep++;
                                var stop = new Stop();
                                Thrown.Add(stop);
                                throw stop;
                            }

                            break;
                        case 6:
                            if (Roll(5) == 0)
                            {
                                MarkRunningPath(me);
                                _sweep++;
                                var e = new InvalidOperationException("fuzz");
                                Thrown.Add(e);
                                throw e;
                            }

                            break;
                        case 7:
                            if (_handles.Count > 0)
                            {
                                var k = Roll(_handles.Count);
                                var target = _handleSites[k];
                                var onRunningPath = RunsInside(me, target);
                                if (onRunningPath) MarkRunningPath(me);
                                // A flow whose cancellation is already confirmed ignores a second Cancel (its unwind may
                                // still be deferred behind the running path).
                                var confirmed = _handles[k].CancelCause != CancelCause.None;
                                _sweep++;
                                _cancelers.Add(me);
                                try
                                {
                                    _handles[k].Cancel();
                                }
                                finally
                                {
                                    _cancelers.RemoveAt(_cancelers.Count - 1);
                                }

                                _sweep++;
                                // Requested during execution, the subtree unwinds on the spot, except the running path
                                // (this node and its ancestors, and the nodes whose Cancel this runs inside), which unwinds
                                // at its next await, and the cleanups that run below it.
                                if (!confirmed && !onRunningPath && target.Open > 0 && target.Cleanups == 0)
                                    Violation("a Cancel inside a flush left " + target.Open + " node(s) alive");
                            }

                            break;
                        case 8:
                            using (_game.Pause())
                            {
                                await Leaf(null, 0);
                                Resumed();
                            }

                            break;
                        case 9:
                            Emit(Roll(3));
                            await Leaf(null, 0);
                            Resumed();
                            break;
                        default:
                        {
                            var s = new Site();
                            await Flow.WithClock(Roll(2) == 0 ? _game : _world.DefaultClock, Child(depth + 1, me, s, 0));
                            Resumed();
                            Closed(s, "WithClock");
                            break;
                        }
                    }
                }
            }
            catch (FlowCanceledException)
            {
                canceled = true;
                throw;
            }
            finally
            {
                Exit(me, canceled);
                if (canceled && Cleanups && Roll(4) == 0)
                {
                    // A cleanup: once the cancellation has reached this code, the await runs to its end as a live scope's
                    // does, and a later cancellation does not reach what it starts. The flows above wait for it. While
                    // World.Dispose ends the flows, the await throws again at once.
                    var s = new Site();
                    CleanupStarts(me);
                    try
                    {
                        await Child(depth + 1, me, s, 0);
                        // The node's code runs again: its end (a Race won, a WhenAll done) may unwind other nodes, apart
                        // from the unwinds before it.
                        Resumed();
                        Closed(s, "cleanup");
                    }
                    finally
                    {
                        CleanupEnds(me);
                    }
                }
            }

            if (site != null) site.DoneMask |= 1 << index;
        }

        void RunRoot(int depth, Clock clock)
        {
            _sweep++;
            var s = new Site();
            _handles.Add(_world.Run(Child(depth, null, s, 0), clock));
            _handleSites.Add(s);
        }

        public void Dispose()
        {
            // Keeps the unwind of Dispose apart from the unwinds of the last Tick (the unwind order compares siblings of
            // one unwind).
            _sweep++;
            _world.Dispose();
        }

        public void Drive(int ticks)
        {
            for (var r = 0; r < 6; r++) RunRoot(0, Roll(2) == 0 ? _game : null);
            for (var t = 0; t < ticks; t++)
            {
                Site canceled = null;
                switch (Roll(8))
                {
                    case 0:
                        Emit(Roll(3));
                        break;
                    case 1:
                        if (_handles.Count > 0)
                        {
                            var k = Roll(_handles.Count);
                            var resumes = _resumes;
                            var exited = Exited;
                            _handles[k].Cancel();
                            _sweep++;
                            // Outside a flush the cancellation is confirmed now and unwound at the next flush head.
                            if (_resumes != resumes || Exited != exited) Violation("a Cancel outside a flush ran flow code on the spot");
                            canceled = _handleSites[k];
                        }

                        break;
                    case 2:
                        RunRoot(1, null);
                        break;
                }

                _sweep++;
                _world.Tick(Dt);
                CheckWaiters();
                // A cleanup running below keeps it (and what waits on the cleanup) alive until the cleanup ends.
                if (canceled != null && canceled.Open > 0 && canceled.Cleanups == 0)
                    Violation("a Cancel outside a flush left " + canceled.Open + " node(s) alive after the next Tick");
                // A flow that has ended has no live descendants.
                for (var h = 0; h < _handles.Count; h++)
                {
                    if (_handles[h].IsCompleted && _handleSites[h].Open > 0)
                        Violation("an ended flow still has " + _handleSites[h].Open + " live node(s) below it");
                }
            }
        }
    }

    [Test]
    public void RandomFlowTreesKeepTheirInvariants([Range(1, 300)] int seed) => RunFuzz(seed, cleanups: false);

    [Test]
    public void RandomFlowTreesWithCleanupsKeepTheirInvariants([Range(1, 300)] int seed) => RunFuzz(seed, cleanups: true);

    static void RunFuzz(int seed, bool cleanups)
    {
        var world = new FlowWorld();
        var reports = new List<FlowExceptionInfo>();
        var f = new Fuzzer(seed, world) { Cleanups = cleanups };
        world.OnUnhandledException = p =>
        {
            reports.Add(p);
            f.OnReported(p.Exception);
        };
        f.Drive(240);
        f.Dispose();
        Assert.That(f.Violations, Is.Empty);
        Assert.That(f.Exited, Is.EqualTo(f.Entered), "every entered try ran its finally exactly once");
        Assert.That(world.Diagnostics.Root.Children, Is.Empty, "nothing is left alive after Dispose");
        var unexpected = reports.Where(p => !f.Thrown.Contains(p.Exception)).ToArray();
        Assert.That(unexpected, Is.Empty, string.Join("\n", unexpected.Select(p => p.ToString())));
        // Every exception the fuzzer threw reached exactly one receiver: a catch, or OnUnhandledException (a root flow's, or Undelivered /
        // Cleanup when no receiver could take it any more).
        Assert.That(f.Received, Is.Unique, "a thrown exception was received twice");
        Assert.That(f.Received.Count, Is.EqualTo(f.Thrown.Count), "every thrown exception is received exactly once");
    }
}
