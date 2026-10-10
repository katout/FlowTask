using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Katout.FlowTask.Verification
{
    /// <summary>
    /// Writes one line to the Editor log when each test case or suite (assembly, namespace, SetUpFixture, fixture,
    /// parameterized method) starts and one when the run finishes, so that tools/unity/run-tests.ps1 can name what was
    /// running when it stops a suite after its timeout (-TimeoutMinutes). A suite line that comes last means the run
    /// stopped before the suite's first test started, e.g. in its [OneTimeSetUp] or in a [SetUpFixture].
    /// <para>
    /// In EditMode and PlayMode the Test Framework calls these callbacks in the Editor as each test starts, so the last
    /// line names what was running. Player runs (StandaloneMono / StandaloneIl2cpp) send their progress to the Editor
    /// from a coroutine, one message per frame: a test that blocks the player's main thread never reports its start, and
    /// the last line is then an earlier test or suite (the last start the Editor received).
    /// </para>
    /// <para>
    /// The line format is read by run-tests.ps1 (Get-StuckTest): keep both in step.
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    static class TestProgressLog
    {
        internal const string StartedPrefix = "[FlowTask] test started: ";
        internal const string SuiteStartedPrefix = "[FlowTask] suite started: ";
        internal const string RunFinishedLine = "[FlowTask] test run finished";

        // Callbacks are kept in static state, which a domain reload clears: register again after every reload.
        static TestProgressLog() => ScriptableObject.CreateInstance<TestRunnerApi>().RegisterCallbacks(new Callbacks());

        sealed class Callbacks : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun)
            {
            }

            public void RunFinished(ITestResultAdaptor result) => Write(RunFinishedLine);

            public void TestStarted(ITestAdaptor test) => Write((test.IsSuite ? SuiteStartedPrefix : StartedPrefix) + test.FullName);

            public void TestFinished(ITestResultAdaptor result)
            {
            }

            // No stack trace: one line per test instead of a block.
            static void Write(string line) => Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "{0}", line);
        }
    }
}
