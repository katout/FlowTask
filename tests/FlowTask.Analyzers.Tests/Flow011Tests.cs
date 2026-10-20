using System;
using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Katout.FlowTask.Analyzers.Tests;

/// <summary>
/// FLOW011 (Error): a type passed to FlowWorld.SkipUnwinding as typeof whose source has a block around an await that
/// unwinding would run. FLOW012 (Warning): a type passed that the analyzer cannot read. The shapes that FLOW011 and
/// FlowAssert.CanSkipUnwinding are both held to are in <see cref="SkipUnwindingParityTests"/>.
/// </summary>
public class Flow011Tests
{
    static readonly string[] Ids = { "FLOW011", "FLOW012" };

    // ------------------------------------------------------------------ passed (written first: an error stops the build)

    [Test]
    public Task FLOW011_TypesWithoutABlockAroundAnAwaitAreNotReported() => AnalyzerHarness.VerifyOnlyAsync("""
        using System;
        using System.Collections.Generic;
        using System.IO;
        using Katout.FlowTask;

        static class Flows
        {
            static int s_count;

            static async FlowTask Arrays(int[] frames, string text)
            {
                foreach (var f in frames) await FlowTask.DelayFrames(f);
                foreach (var c in text) await FlowTask.DelayFrames(c);
            }

            static async FlowTask BlocksWithoutAwait(List<int> values)
            {
                try { s_count++; } finally { s_count--; }
                foreach (var v in values) s_count += v;
                using (new MemoryStream()) s_count++;
                await FlowTask.NextFrame();
                using var after = new MemoryStream();
            }

            static async FlowTask AwaitsInCleanup()
            {
                try { s_count++; } finally { await FlowTask.NextFrame(); }
                try { s_count++; } catch (Exception) { await FlowTask.NextFrame(); }
            }

            static async FlowTask Filtered()
            {
                try { await FlowTask.NextFrame(); }
                catch (Exception e) when (e is not FlowCanceledException) { s_count++; }
                try { await FlowTask.NextFrame(); }
                catch (Exception e) when (!(e is OperationCanceledException) && e.Message.Length > 0) { s_count++; }
                try { await FlowTask.NextFrame(); }
                catch (InvalidOperationException) { s_count++; }
                catch (IOException e) when (e.Message.Length > 0) { s_count++; }
            }

            static void Sync()
            {
                try { s_count++; } finally { s_count--; }
            }

            internal static class Nested
            {
                static async FlowTask Run()
                {
                    try { await FlowTask.NextFrame(); } finally { s_count++; }
                }
            }
        }

        static class Generic<T>
        {
            internal static async FlowTask<T> Run()
            {
                await FlowTask.NextFrame();
                return default;
            }
        }

        static class Setup
        {
            static void Register(FlowWorld world)
            {
                world.SkipUnwinding(typeof(Flows));
                world.SkipUnwinding(typeof(Generic<>));
                world.SkipUnwinding(typeof(Generic<int>));
                world.SkipUnwinding(null);
                world.SkipUnwinding(typeof(int[]));
            }
        }
        """, Ids);

    [Test]
    public Task FLOW011_OtherMethodsNamedSkipUnwindingAreNotReported() => AnalyzerHarness.VerifyOnlyAsync("""
        using System;
        using Katout.FlowTask;

        static class Flows
        {
            static async FlowTask Run()
            {
                try { await FlowTask.NextFrame(); } finally { }
            }
        }

        sealed class Other
        {
            public void SkipUnwinding(Type type) { }
        }

        static class Setup
        {
            static void Register(Other other) => other.SkipUnwinding(typeof(Flows));
        }
        """, Ids);

    // ------------------------------------------------------------------ reported

    [Test]
    public Task FLOW011_TypesWithABlockAroundAnAwaitAreReported() => AnalyzerHarness.VerifyOnlyAsync("""
        using System;
        using System.Collections.Generic;
        using System.IO;
        using Katout.FlowTask;

        static class WithFinally
        {
            static async FlowTask Run()
            {
                try { await FlowTask.NextFrame(); } finally { }
            }
        }

        static class WithUsing
        {
            static async FlowTask Run()
            {
                using var stream = new MemoryStream();
                await FlowTask.NextFrame();
            }
        }

        static class WithForeach
        {
            static async FlowTask Run(IEnumerable<int> frames)
            {
                foreach (var f in frames) await FlowTask.DelayFrames(f);
            }
        }

        static class WithCatch
        {
            static async FlowTask Run()
            {
                try { await FlowTask.NextFrame(); }
                catch (Exception e) when (e.Message.Length > 0 && e is not FlowCanceledException) { throw; }
            }
        }

        static class WithLambda
        {
            static readonly Func<FlowTask> s_make = async () =>
            {
                try { await FlowTask.NextFrame(); } catch (OperationCanceledException) { throw; }
            };
        }

        static class Generic<T>
        {
            static async FlowTask Run()
            {
                try { await FlowTask.NextFrame(); } finally { }
            }
        }

        static class Outer
        {
            internal static class Inner
            {
                static async FlowTask Run()
                {
                    try { await FlowTask.NextFrame(); } finally { }
                }
            }
        }

        static class Setup
        {
            static void Register(FlowWorld world)
            {
                world.SkipUnwinding({|FLOW011:typeof(WithFinally)|});
                world.SkipUnwinding({|FLOW011:typeof(WithUsing)|});
                world.SkipUnwinding({|FLOW011:typeof(WithForeach)|});
                world.SkipUnwinding({|FLOW011:typeof(WithCatch)|});
                world.SkipUnwinding({|FLOW011:typeof(WithLambda)|});
                world.SkipUnwinding({|FLOW011:typeof(Generic<int>)|});
                world.SkipUnwinding({|FLOW011:typeof(Generic<>)|});
                world.SkipUnwinding({|FLOW011:typeof(Outer.Inner)|});
            }
        }
        """, Ids);

    [Test]
    public async Task FLOW011_TheMessageNamesTheTypeAndTheCodeAndPointsAtEachBlock()
    {
        var compilation = AnalyzerHarness.CreateCompilation("""
            using System;
            using Katout.FlowTask;

            static class Enemy
            {
                static async FlowTask Patrol()
                {
                    try { await FlowTask.NextFrame(); } finally { }
                }

                static Func<FlowTask> Make() => async () =>
                {
                    using (new System.IO.MemoryStream()) await FlowTask.NextFrame();
                };

                static async FlowTask Idle() => await FlowTask.NextFrame();
            }

            static class Setup
            {
                static void Register(FlowWorld world) => world.SkipUnwinding(typeof(Enemy));
            }
            """);
        AnalyzerHarness.AssertCompiles(compilation);
        var diagnostic = (await AnalyzerHarness.GetFlowDiagnosticsAsync(compilation)).Single(d => d.Id == "FLOW011");
        Assert.That(diagnostic.Severity, Is.EqualTo(DiagnosticSeverity.Error));
        Assert.That(diagnostic.GetMessage(CultureInfo.InvariantCulture), Does.StartWith("'Enemy' is passed to FlowWorld.SkipUnwinding, but it has a block around an await").And.Contain(" in 'Make', 'Patrol', which"));
        Assert.That(diagnostic.GetMessage(CultureInfo.GetCultureInfo("ja")), Does.StartWith("'Enemy' を FlowWorld.SkipUnwinding に渡していますが、その中の 'Make', 'Patrol' に"));
        var source = compilation.SyntaxTrees.First().ToString();
        Assert.That(diagnostic.AdditionalLocations.Select(l => source.Substring(l.SourceSpan.Start, l.SourceSpan.Length)), Is.EqualTo(new[] { "finally", "using" }));
    }

    [Test]
    public Task FLOW011_EveryPartOfAPartialTypeIsRead() => AnalyzerHarness.VerifyOnlyAsync("""
        using Katout.FlowTask;

        static partial class Enemy
        {
            static async FlowTask Idle() => await FlowTask.NextFrame();
        }

        static class Setup
        {
            static void Register(FlowWorld world) => world.SkipUnwinding({|FLOW011:typeof(Enemy)|});
        }
        """, Ids, """
        using Katout.FlowTask;

        static partial class Enemy
        {
            static async FlowTask Patrol()
            {
                try { await FlowTask.NextFrame(); } finally { }
            }
        }
        """);

    [Test]
    public Task FLOW011_GeneratedCodeIsAnalyzed() => AnalyzerHarness.VerifyOnlyAsync("""
        // <auto-generated/>
        using Katout.FlowTask;

        static class Enemy
        {
            static async FlowTask Patrol()
            {
                try { await FlowTask.NextFrame(); } finally { }
            }
        }

        static class GeneratedSetup
        {
            static void Register(FlowWorld world)
            {
                world.SkipUnwinding({|FLOW011:typeof(Enemy)|});
                world.SkipUnwinding({|FLOW012:typeof(string)|});
            }
        }
        """, Ids);

    // ------------------------------------------------------------------ FLOW012

    [Test]
    public Task FLOW012_ATypeTheAnalyzerCannotReadIsReported() => AnalyzerHarness.VerifyOnlyAsync("""
        using System;
        using Katout.FlowTask;

        static class Setup
        {
            static Type Pick() => typeof(Setup);

            static void Register<T>(FlowWorld world, Type type)
            {
                world.SkipUnwinding({|FLOW012:type|});
                world.SkipUnwinding({|FLOW012:Pick()|});
                world.SkipUnwinding({|FLOW012:typeof(T)|});
                world.SkipUnwinding({|FLOW012:typeof(FlowWorld)|});
                world.SkipUnwinding({|FLOW012:typeof(System.Collections.Generic.List<>)|});
            }
        }
        """, Ids);

    [Test]
    public async Task FLOW012_TheMessagesPointToFlowAssert()
    {
        var compilation = AnalyzerHarness.CreateCompilation("""
            using System;
            using Katout.FlowTask;

            static class Setup
            {
                static void Register(FlowWorld world, Type type)
                {
                    world.SkipUnwinding(type);
                    world.SkipUnwinding(typeof(FlowWorld));
                }
            }
            """);
        AnalyzerHarness.AssertCompiles(compilation);
        var diagnostics = await AnalyzerHarness.GetFlowDiagnosticsAsync(compilation);
        Assert.That(diagnostics.Select(d => d.Id), Is.EqualTo(new[] { "FLOW012", "FLOW012" }));
        Assert.That(diagnostics.Select(d => d.Severity), Is.All.EqualTo(DiagnosticSeverity.Warning));
        Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture), Does.Contain("not written as typeof").And.Contain("FlowAssert.CanSkipUnwinding"));
        Assert.That(diagnostics[1].GetMessage(CultureInfo.InvariantCulture), Does.StartWith("'Katout.FlowTask.FlowWorld' is declared in another assembly").And.Contain("FlowAssert.CanSkipUnwinding"));
    }
}
