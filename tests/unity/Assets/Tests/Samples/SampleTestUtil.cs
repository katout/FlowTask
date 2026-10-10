using System;
using System.Collections;
using NUnit.Framework;
using Katout.FlowTask.Unity;
using UnityEngine;

namespace Katout.FlowTask.Samples.Tests
{
    static class SampleTestUtil
    {
        /// <summary>The PlayerLoop's World: the samples run where a game runs them.</summary>
        internal static FlowWorld W => FlowTaskUnity.World;

        static GameClocks s_clocks;

        /// <summary>
        /// The session's clocks on <see cref="W"/>, shared by every test as a game shares one pair per session: a clock of
        /// World.CreateClock stays in its World, so a pair per test would pile up for the rest of the run. A test that
        /// pauses <c>Clocks.Game</c> itself releases the pause in a finally block, so the next test starts unpaused.
        /// </summary>
        internal static GameClocks Clocks
        {
            get
            {
                if (s_clocks == null || s_clocks.Game.World != W) s_clocks = new GameClocks(W);
                return s_clocks;
            }
        }

        /// <summary>Yields frames until the handle completes; fails after <paramref name="maxSeconds"/> of real time.</summary>
        internal static IEnumerator WaitFor<T>(FlowHandle<T> handle, float maxSeconds = 5f) =>
            WaitUntil(() => handle.IsCompleted, maxSeconds, "the flow did not end: " + handle);

        internal static IEnumerator WaitFor(FlowHandle handle, float maxSeconds = 5f) => WaitFor((FlowHandle<FlowUnit>)handle, maxSeconds);

        /// <summary>Yields frames until <paramref name="done"/> holds; fails after <paramref name="maxSeconds"/> of real time.</summary>
        internal static IEnumerator WaitUntil(Func<bool> done, float maxSeconds = 5f, string what = "condition")
        {
            var start = Time.realtimeSinceStartup;
            while (!done())
            {
                if (Time.realtimeSinceStartup - start > maxSeconds) Assert.Fail("timed out waiting for: " + what);
                yield return null;
            }
        }

        internal static IEnumerator Frames(int count)
        {
            for (var i = 0; i < count; i++) yield return null;
        }
    }

    /// <summary>Presses the back key from MonoBehaviour.Update when armed, as <see cref="BackKeyInput"/> does for a real key.</summary>
    public sealed class BackKeyPresser : MonoBehaviour
    {
        public BackKeyRouter Router;
        public bool PressNextUpdate;
        public int PressedFrame = -1;
        public bool Delivered;

        void Update()
        {
            if (!PressNextUpdate) return;
            PressNextUpdate = false;
            PressedFrame = Time.frameCount;
            Delivered = Router.Press();
        }
    }
}
