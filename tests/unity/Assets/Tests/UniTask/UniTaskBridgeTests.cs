using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Katout.FlowTask.Unity;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;
using UnityEngine.TestTools;

namespace Katout.FlowTask.UniTaskTests
{
    /// <summary>
    /// The UniTask bridge in Unity: the com.katout.flowtask.unitask package against the real com.cysharp.unitask (pinned
    /// to 2.5.10 in tests/unity/Packages/manifest.json), with UniTask's PlayerLoop runners next to the FlowTask systems. The
    /// .NET suite (tests/FlowTask.Bridges.Tests) covers the bridge logic; these tests cover what only Unity has: the
    /// PlayerLoop that both libraries insert systems into, UnitySynchronizationContext and UniTask's frame-based waits.
    /// </summary>
    public class UniTaskBridgeTests
    {
        static FlowWorld W => FlowTaskUnity.World;

        [TearDown]
        public void RestoreDefaults()
        {
            if (!FlowTaskUnity.IsInstalled) FlowTaskUnity.Initialize();
            else FlowTaskUnity.Configure(new FlowTaskSettings());
        }

        static IEnumerator WaitUntil(Func<bool> done, float maxSeconds = 5f)
        {
            var start = Time.realtimeSinceStartup;
            while (!done() && Time.realtimeSinceStartup - start < maxSeconds) yield return null;
        }

        /// <summary>
        /// Where code runs: the managed thread, and whether it runs inside the World (FlowWorld.Current is set only while the
        /// World executes flow code: Run, Tick, Flush).
        /// </summary>
        static (int Thread, bool InWorld) Here(FlowWorld w) => (Environment.CurrentManagedThreadId, FlowWorld.Current == w);

        // Resuming inside the World: UniTask completes its waits in its own PlayerLoop runners, outside the World; the
        // bridged flow must still resume inside the World (a Tick or flush), not synchronously in UniTask's runner.

        [UnityTest]
        public IEnumerator FromUniTaskResumesTheFlowInsideTheWorldOnItsThread()
        {
            var w = W;
            var main = Environment.CurrentManagedThreadId;
            var completedAt = new List<(int, bool)>();
            var resumedAt = new List<(int, bool)>();

            async UniTask Wait(CancellationToken ct)
            {
                await UniTask.Delay(30, cancellationToken: ct);
                completedAt.Add(Here(w));
            }

            async UniTask<int> AnswerAfterFrames(int frames, CancellationToken ct)
            {
                await UniTask.DelayFrame(frames, cancellationToken: ct);
                completedAt.Add(Here(w));
                return 42;
            }

            async FlowTask<int> Flow()
            {
                await FlowUniTask.FromUniTask(ct => Wait(ct));
                resumedAt.Add(Here(w));
                var answer = await FlowUniTask.FromUniTask(ct => AnswerAfterFrames(2, ct));
                resumedAt.Add(Here(w));
                return answer;
            }

            var h = w.Run(Flow());
            yield return WaitUntil(() => h.IsCompleted);
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
            Assert.That(h.Result, Is.EqualTo(42));
            Assert.That(completedAt, Is.EqualTo(new[] { (main, false), (main, false) }),
                "control: UniTask completed both waits on the main thread, in its runners outside the World");
            Assert.That(resumedAt, Is.EqualTo(new[] { (main, true), (main, true) }),
                "the flow resumes on the World thread, inside the World's Tick or flush");
        }

        [UnityTest]
        public IEnumerator FromUniTaskCompletedOnAThreadPoolThreadResumesTheFlowInsideTheWorld()
        {
            var w = W;
            var main = Environment.CurrentManagedThreadId;
            (int Thread, bool InWorld) completedAt = default, resumedAt = default;

            async UniTask<int> OnThreadPool(CancellationToken ct)
            {
                await UniTask.SwitchToThreadPool();
                completedAt = Here(w);
                return 5;
            }

            async FlowTask<int> Flow()
            {
                var value = await FlowUniTask.FromUniTask(ct => OnThreadPool(ct));
                resumedAt = Here(w);
                return value;
            }

            var h = w.Run(Flow());
            yield return WaitUntil(() => h.IsCompleted);
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
            Assert.That(h.Result, Is.EqualTo(5));
            Assert.That(completedAt.Thread, Is.Not.EqualTo(main), "control: the UniTask completed on a thread-pool thread");
            Assert.That(completedAt.InWorld, Is.False);
            Assert.That(resumedAt, Is.EqualTo((main, true)), "the flow resumes on the World thread, inside the World");
        }

        // The same frame as the completion (the AfterUpdate or AfterLateUpdate flush point), whatever SynchronizationContext
        // is current, as for a Task (BridgeTests.TaskCompletedInMonoBehaviourUpdateResumesAtTheNextFlushPoint). A bridge
        // whose continuation went to the thread pool under UnitySynchronizationContext would let the resume slip to a later
        // frame.

        [UnityTest]
        public IEnumerator UniTaskCompletedInAPlayerLoopRunnerResumesTheFlowInTheSameFrame()
        {
            Assert.That(SynchronizationContext.Current?.GetType().Name, Is.EqualTo("UnitySynchronizationContext"),
                "precondition: the test runs on the main thread under Unity's SynchronizationContext");
            var frames = new List<string>();
            var late = 0;
            for (var trial = 0; trial < 20; trial++)
            {
                int completedFrame = -1, resumedFrame = -1;

                async UniTask NextFrame(CancellationToken ct)
                {
                    await UniTask.DelayFrame(1, cancellationToken: ct);
                    completedFrame = Time.frameCount;
                }

                async FlowTask Flow()
                {
                    await FlowUniTask.FromUniTask(ct => NextFrame(ct));
                    resumedFrame = Time.frameCount;
                }

                var h = W.Run(Flow());
                yield return WaitUntil(() => h.IsCompleted);
                Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
                frames.Add(completedFrame + "->" + resumedFrame);
                if (resumedFrame != completedFrame) late++;
            }

            var all = string.Join(",", frames);
            Debug.Log("[FlowTask] UniTask completed frame -> flow resumed frame, per trial: " + all);
            Assert.That(late, Is.Zero, "the flow resumes in the frame UniTask completed the wait: " + all);
        }

        [UnityTest]
        public IEnumerator CancelingTheScopeCancelsTheUniTaskToken()
        {
            var token = default(CancellationToken);
            var h = W.Run(FlowUniTask.FromUniTask(ct =>
            {
                token = ct;
                return UniTask.Delay(60000, cancellationToken: ct);
            }));
            yield return null;
            Assert.That(token.CanBeCanceled && !token.IsCancellationRequested, Is.True, "the factory ran when the flow started, with the scope's token");
            h.Cancel();
            yield return WaitUntil(() => h.IsCompleted);
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
            Assert.That(token.IsCancellationRequested, Is.True, "the unwinding scope cancels the UniTask's token");
        }

        [UnityTest]
        public IEnumerator ToUniTaskCompletesWhenTheFlowEnds()
        {
            async FlowTask<int> Seven()
            {
                await FlowTask.DelayFrames(2);
                return 7;
            }

            async FlowTask Exploding()
            {
                await FlowTask.NextFrame();
                throw new InvalidOperationException("unitask-boom");
            }

            var succeeded = W.Run(Seven()).ToUniTask();
            var neverHandle = W.Run(FlowTask.Never());
            var canceled = neverHandle.ToUniTask();
            LogAssert.Expect(LogType.Exception, new Regex("unitask-boom")); // the default World logs the unhandled exception
            var faulted = W.Run(Exploding()).ToUniTask();
            yield return null;
            neverHandle.Cancel();
            yield return WaitUntil(() => succeeded.Status != UniTaskStatus.Pending && canceled.Status != UniTaskStatus.Pending &&
                                         faulted.Status != UniTaskStatus.Pending);

            Assert.That(succeeded.Status, Is.EqualTo(UniTaskStatus.Succeeded));
            Assert.That(succeeded.GetAwaiter().GetResult(), Is.EqualTo(7));
            Assert.That(canceled.Status, Is.EqualTo(UniTaskStatus.Canceled));
            Assert.Catch<OperationCanceledException>(() => canceled.GetAwaiter().GetResult());
            Assert.That(faulted.Status, Is.EqualTo(UniTaskStatus.Faulted));
            var ex = Assert.Throws<InvalidOperationException>(() => faulted.GetAwaiter().GetResult(), "the original exception");
            Assert.That(ex.Message, Is.EqualTo("unitask-boom"));
        }

        // Both libraries insert systems into the PlayerLoop at startup: FlowTask at SubsystemRegistration, UniTask later on
        // its own. Neither may drop the other's systems, including when FlowTask reinstalls them (Configure).

        [Test]
        public void FlowTaskAndUniTaskSystemsShareThePlayerLoop()
        {
            var before = Describe();
            Debug.Log("[FlowTask] PlayerLoop with UniTask:\n" + before.Text);
            AssertBothInstalled(before);

            FlowTaskUnity.Configure(new FlowTaskSettings { FlushPoints = FlowFlushPoints.All });
            var after = Describe();
            AssertBothInstalled(after);
            Assert.That(after.UniTaskSystems, Is.EqualTo(before.UniTaskSystems), "Configure keeps UniTask's systems");
            Assert.That(after.Text, Does.Contain("FixedUpdate: FlowTaskFlushAfterFixedUpdate"));
            Assert.That(after.Text, Does.Contain("PostLateUpdate: FlowTaskFlushEndOfFrame"));
        }

        static void AssertBothInstalled(LoopDescription d)
        {
            Assert.That(d.UniTaskSystems, Is.GreaterThan(0), "UniTask's runners are in the PlayerLoop\n" + d.Text);
            Assert.That(d.Text, Does.Contain("Update: FlowTaskTick"), d.Text);
            Assert.That(d.Text, Does.Contain("Update: FlowTaskFlushAfterUpdate"), d.Text);
            Assert.That(d.Text, Does.Contain("PreLateUpdate: FlowTaskFlushAfterLateUpdate"), d.Text);
            Assert.That(d.TickBeforeScriptUpdate, Is.True, "the Tick still runs before MonoBehaviour.Update\n" + d.Text);
        }

        sealed class LoopDescription
        {
            public string Text;
            public int UniTaskSystems;
            public bool TickBeforeScriptUpdate;
        }

        /// <summary>"Phase: System" per line for every system of the current PlayerLoop.</summary>
        static LoopDescription Describe()
        {
            var d = new LoopDescription();
            var sb = new StringBuilder();
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            foreach (var phase in loop.subSystemList ?? Array.Empty<PlayerLoopSystem>())
            {
                var tick = -1;
                var scriptUpdate = -1;
                var subs = phase.subSystemList ?? Array.Empty<PlayerLoopSystem>();
                for (var i = 0; i < subs.Length; i++)
                {
                    var type = subs[i].type;
                    sb.Append(phase.type?.Name).Append(": ").Append(type?.FullName ?? "<null>").Append('\n');
                    if (type == null) continue;
                    if (type.Namespace != null && type.Namespace.StartsWith("Cysharp.Threading.Tasks", StringComparison.Ordinal)) d.UniTaskSystems++;
                    if (phase.type == typeof(Update) && type == typeof(FlowTaskUnity.FlowTaskTick)) tick = i;
                    if (phase.type == typeof(Update) && type == typeof(Update.ScriptRunBehaviourUpdate)) scriptUpdate = i;
                }

                if (phase.type == typeof(Update)) d.TickBeforeScriptUpdate = tick >= 0 && tick < scriptUpdate;
            }

            // Short names for the FlowTask markers, as in FlowTaskUnity's own description.
            d.Text = sb.ToString().Replace(typeof(FlowTaskUnity).FullName + "+", "");
            return d;
        }
    }
}
