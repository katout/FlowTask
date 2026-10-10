using Katout.FlowTask.Analyzers.CodeFixes;

namespace Katout.FlowTask.Analyzers.Tests;

/// <summary>FLOW010 (Warning): an await in a finally block, or a catch that passes its exception on, without Flow.NonCancelable.</summary>
public class Flow010Tests
{
    [Test]
    public Task FLOW010_AwaitsInAFinallyOrARethrowingCatchAreReported() => AnalyzerHarness.VerifyMessagesAsync("""
        using System;
        using System.IO;
        using Katout.FlowTask;

        class Dialog
        {
            static async FlowTask Close() { await FlowTask.NextFrame(); }
            static async FlowTask<int> Save() { await FlowTask.NextFrame(); return 1; }

            async FlowTask<int> Show()
            {
                try
                {
                    await FlowTask.NextFrame();
                    return 1;
                }
                catch (IOException)
                {
                    {|FLOW010:await Close()|};
                    throw;
                }
                catch (Exception e) when (e is not FlowCanceledException)
                {
                    var saved = {|FLOW010:await Save()|};
                    throw new InvalidOperationException("failed", e);
                }
                finally
                {
                    {|FLOW010:await Close()|};
                    try
                    {
                        {|FLOW010:await (FlowTask.DelayFrames(2))|};
                    }
                    catch (FlowCanceledException)
                    {
                        throw;
                    }
                }
            }
        }
        """,
        "A cancel that comes while this await in a finally block (or a catch that passes its exception on) waits skips the rest of the block",
        "", "", "");

    [Test]
    public Task FLOW010_MarkedAwaitsAndCatchesEnteredOnlyByTheCancellationOrThatHandleTheirExceptionAreNotReported() => AnalyzerHarness.VerifyAsync("""
        using System;
        using System.IO;
        using System.Threading.Tasks;
        using Katout.FlowTask;

        class Dialog
        {
            static async FlowTask Close() { await FlowTask.NextFrame(); }

            async FlowTask Show()
            {
                try
                {
                    await FlowTask.NextFrame();
                }
                catch (FlowCanceledException)
                {
                    await Close();
                    throw;
                }
                catch (OperationCanceledException)
                {
                    await Close();
                    throw;
                }
                catch (IOException)
                {
                    await Close();
                }
                finally
                {
                    await Flow.NonCancelable(Close());
                    await (Flow.NonCancelable(FlowTask.NextFrame()));
                    var close = Flow.NonCancelable(Close());
                    await close;
                    Func<FlowTask> later = async () => await FlowTask.NextFrame();
                }
            }

            async Task NotAFlowTaskMethod()
            {
                try { await Task.Yield(); }
                finally { await Task.Delay(1); }
            }
        }
        """);

    [Test]
    public Task FixMarksTheAwait() => CodeFixHarness.VerifyAsync(
        """
        using Katout.FlowTask;

        class Dialog
        {
            static async FlowTask Close() { await FlowTask.NextFrame(); }

            async FlowTask Show()
            {
                try { await FlowTask.NextFrame(); }
                finally
                {
                    await Close();
                }
            }
        }
        """,
        """
        using Katout.FlowTask;

        class Dialog
        {
            static async FlowTask Close() { await FlowTask.NextFrame(); }

            async FlowTask Show()
            {
                try { await FlowTask.NextFrame(); }
                finally
                {
                    await Flow.NonCancelable(Close());
                }
            }
        }
        """,
        new NonCancelableCodeFixProvider(),
        "FLOW010",
        absentAfter: new[] { "FLOW010" });

    [Test]
    public Task FixSpellsFlowInFullWhereTheNameDoesNotBind() => CodeFixHarness.VerifyAsync(
        """
        class Dialog
        {
            class Flow { }

            static async Katout.FlowTask.FlowTask Show()
            {
                try { await Katout.FlowTask.FlowTask.NextFrame(); }
                finally
                {
                    await Katout.FlowTask.FlowTask.NextFrame();
                }
            }
        }
        """,
        """
        class Dialog
        {
            class Flow { }

            static async Katout.FlowTask.FlowTask Show()
            {
                try { await Katout.FlowTask.FlowTask.NextFrame(); }
                finally
                {
                    await global::Katout.FlowTask.Flow.NonCancelable(Katout.FlowTask.FlowTask.NextFrame());
                }
            }
        }
        """,
        new NonCancelableCodeFixProvider(),
        "FLOW010",
        absentAfter: new[] { "FLOW010" });
}
