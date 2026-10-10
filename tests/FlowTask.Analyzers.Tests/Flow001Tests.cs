using System.Threading;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Katout.FlowTask.Analyzers.Tests;

/// <summary>
/// FLOW001: a catch clause of a FlowTask method that can take FlowCanceledException lets it
/// pass. A clause of FlowCanceledException or OperationCanceledException ends every path with 'throw'; a catch-all
/// excludes it with a filter (even a log-and-rethrow: its body runs for every cancellation). Filters are judged from
/// their IOperation tree.
/// </summary>
public class Flow001Tests
{
    [Test]
    public Task CancellationClausesThatDoNotEndWithThrowAreReported() => AnalyzerHarness.VerifyMessagesAsync("""
        using System;
        using Katout.FlowTask;

        class C
        {
            static void Log() { }

            async FlowTask M(bool c)
            {
                try { await FlowTask.WaitForSeconds(1); }
                {|FLOW001:catch (FlowCanceledException)|} { return; }

                try { await FlowTask.WaitForSeconds(1); }
                {|FLOW001:catch (OperationCanceledException)|} { Log(); }

                try { await FlowTask.WaitForSeconds(1); }
                {|FLOW001:catch (OperationCanceledException e) when (e.CancellationToken.IsCancellationRequested)|} { return; }

                try { await FlowTask.WaitForSeconds(1); }
                {|FLOW001:catch (FlowCanceledException)|} { if (c) throw; }

                while (c)
                {
                    try { await FlowTask.WaitForSeconds(1); }
                    {|FLOW001:catch (FlowCanceledException)|} { continue; }
                }
            }

            async FlowTask<int> WithResult()
            {
                try { return await FlowTask.FromResult(1); }
                {|FLOW001:catch (OperationCanceledException)|} { return -1; }
            }
        }
        """,
        "takes FlowCanceledException and does not end with 'throw'",
        "which derives from OperationCanceledException",
        "take only external cancellations with 'when (e is not FlowCanceledException)'",
        "takes FlowCanceledException and does not end with 'throw'",
        "takes FlowCanceledException and does not end with 'throw'",
        "which derives from OperationCanceledException");

    [Test]
    public Task CatchAllClausesAreReportedEvenWhenTheyRethrow() => AnalyzerHarness.VerifyMessagesAsync("""
        using System;
        using Katout.FlowTask;

        class C
        {
            static bool Observe(Exception e) => false;
            static void Log(Exception e) { }

            async FlowTask M()
            {
                try { await FlowTask.WaitForSeconds(1); }
                {|FLOW001:catch|} { }

                try { await FlowTask.WaitForSeconds(1); }
                {|FLOW001:catch (Exception)|} { }

                try { await FlowTask.WaitForSeconds(1); }
                {|FLOW001:catch (System.Exception e)|} { Log(e); throw; }

                try { await FlowTask.WaitForSeconds(1); }
                {|FLOW001:catch (Exception e) when (Observe(e))|} { }

                try { await FlowTask.WaitForSeconds(1); }
                {|FLOW001:catch (Exception e) when (e is not FlowCanceledException || Observe(e))|} { }

                try { await FlowTask.WaitForSeconds(1); }
                {|FLOW001:catch (SystemException)|} { }

                // In a finally block too: its awaits throw FlowCanceledException while FlowWorld.Dispose ends the flow.
                try { await FlowTask.WaitForSeconds(1); }
                finally
                {
                    try { {|FLOW010:await FlowTask.NextFrame()|}; }
                    {|FLOW001:catch (Exception)|} { }
                }
            }

            async FlowTask<int> WithResult()
            {
                try { await FlowTask.WaitForSeconds(1); }
                catch (FormatException) { }
                {|FLOW001:catch (Exception)|} { return -1; }

                return 1;
            }

            async FlowTask Generic<TEx>() where TEx : Exception
            {
                try { await FlowTask.WaitForSeconds(1); }
                {|FLOW001:catch (TEx)|} { }
            }

            void Host()
            {
                async FlowTask Local()
                {
                    try { await FlowTask.WaitForSeconds(1); }
                    {|FLOW001:catch|} { }
                }

                Func<FlowTask> lambda = async () =>
                {
                    try { await FlowTask.WaitForSeconds(1); }
                    {|FLOW001:catch (Exception)|} { }
                };
            }
        }
        """,
        "also takes FlowCanceledException", "also takes FlowCanceledException", "also takes FlowCanceledException",
        "also takes FlowCanceledException", "also takes FlowCanceledException", "also takes FlowCanceledException",
        "Flow.NonCancelable",
        "also takes FlowCanceledException", "also takes FlowCanceledException", "also takes FlowCanceledException",
        "also takes FlowCanceledException", "also takes FlowCanceledException");

    [Test]
    public Task ClausesThatLetTheCancellationPassAreNotReported() => AnalyzerHarness.VerifyAsync("""
        using System;
        using System.Threading.Tasks;
        using Katout.FlowTask;

        class C
        {
            static void Cleanup() { }
            static bool Log(Exception e) => true;

            async FlowTask M(bool c)
            {
                try { await FlowTask.WaitForSeconds(1); }
                catch (FlowCanceledException) { Cleanup(); throw; }

                try { await FlowTask.WaitForSeconds(1); }
                catch (OperationCanceledException) { Cleanup(); throw; }

                try { await FlowTask.WaitForSeconds(1); }
                catch (OperationCanceledException e) { throw new OperationCanceledException("wrapped", e); }

                try { await FlowTask.WaitForSeconds(1); }
                catch (FlowCanceledException) { if (c) throw; else throw new InvalidOperationException("x"); }

                try { await FlowTask.WaitForSeconds(1); }
                catch (OperationCanceledException e) when (e is not FlowCanceledException) { return; }

                try { await FlowTask.WaitForSeconds(1); }
                catch (TaskCanceledException) { return; }

                try { await FlowTask.WaitForSeconds(1); }
                catch (Exception e) when (e is not FlowCanceledException) { }

                try { await FlowTask.WaitForSeconds(1); }
                catch (Exception e) when (e is not OperationCanceledException) { }

                try { await FlowTask.WaitForSeconds(1); }
                catch (Exception e) when (!(e is FlowCanceledException)) { }

                try { await FlowTask.WaitForSeconds(1); }
                catch (Exception e) when (e is not FlowCanceledException && Log(e)) { }

                try { await FlowTask.WaitForSeconds(1); }
                catch (Exception e) when (e is FormatException { Message: "x" }) { }

                try { await FlowTask.WaitForSeconds(1); }
                catch (Exception) when (false) { }

                try { await FlowTask.WaitForSeconds(1); }
                catch (FlowCanceledException) { throw; }
                catch (Exception) { }

                // No await in the try block: FlowCanceledException cannot come out of it.
                try { Cleanup(); }
                catch (Exception) { }

                // Cleanup that awaits in a finally block, with the filter that lets the cancellation pass.
                try { await FlowTask.WaitForSeconds(1); }
                finally
                {
                    try { await Flow.NonCancelable(FlowTask.WaitForSeconds(1)); }
                    catch (Exception e) when (e is not FlowCanceledException) { Log(e); }
                }
            }

            async FlowTask<int> SyncOnlyTry(string s)
            {
                await FlowTask.NextFrame();
                try { return int.Parse(s, System.Globalization.CultureInfo.InvariantCulture); }
                catch (Exception) { return 0; }
            }
        }
        """);

    /// <summary>The 'when' filters the classifier proves false for FlowCanceledException, and the ones it cannot.</summary>
    [Test]
    public async Task CatchClassifierEvaluatesFilters()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Katout.FlowTask;

            class C
            {
                static async FlowTask A() { await FlowTask.NextFrame(); }
                static bool Log(Exception e) => true;

                async FlowTask M()
                {
                    try { await A(); } catch (Exception e) when (e is not FlowCanceledException) { }            // excluded
                    try { await A(); } catch (Exception e) when (!(e is FlowCanceledException)) { }             // excluded
                    try { await A(); } catch (Exception e) when (e is System.IO.IOException) { }                // excluded
                    try { await A(); } catch (Exception e) when (e is not (FlowCanceledException or TimeoutException)) { } // excluded
                    try { await A(); } catch (Exception e) when (e is not FlowCanceledException && Log(e)) { }   // excluded
                    try { await A(); } catch (Exception e) when (e is FormatException { Message: "x" }) { }     // excluded
                    try { await A(); } catch (Exception e) when (Log(e)) { }                                    // received
                    try { await A(); } catch (Exception e) when (e is not FlowCanceledException || Log(e)) { }  // received
                    try { await A(); } catch (Exception e) when (e is FlowCanceledException) { }                // received
                    try { await A(); } catch (Exception) { }                                                    // received
                    try { await A(); } catch { }                                                                // received
                    try { await A(); } catch (FormatException) { }                                              // excluded
                    try { A(); } catch (Exception) { }                                                          // excluded (no await)
                    try { await A(); } catch (FlowCanceledException) { throw; } catch (Exception) { }           // received, excluded
                }

                async FlowTask Generic<TEx, TIo>() where TEx : Exception where TIo : System.IO.IOException
                {
                    try { await A(); } catch (TEx) { }                                                          // received
                    try { await A(); } catch (TIo) { }                                                          // excluded
                }
            }
            """;
        var compilation = AnalyzerHarness.CreateCompilation(source);
        var tree = compilation.SyntaxTrees.Single();
        var model = compilation.GetSemanticModel(tree);
        var types = FlowTaskTypes.TryCreate(compilation);
        var received = (await tree.GetRootAsync()).DescendantNodes().OfType<CatchClauseSyntax>()
            .Select(c => CatchClassifier.ReceivesCancellation(c, model, types, CancellationToken.None))
            .ToArray();
        Assert.That(received, Is.EqualTo(new[]
        {
            false, false, false, false, false, false, true, true, true, true, true, false, false, true, false, true, false,
        }));
    }

    [Test]
    public Task SpecificExceptionsAndOtherFunctionsAreNotReported() => AnalyzerHarness.VerifyAsync("""
        using System;
        using System.Threading.Tasks;
        using Katout.FlowTask;

        class C
        {
            async FlowTask M()
            {
                try { await FlowTask.WaitForSeconds(1); }
                catch (FormatException) { }
                catch (InvalidOperationException e) when (e.Message != null) { }

                // Nested synchronous and non-FlowTask functions are not the FlowTask method's body.
                Action sync = () =>
                {
                    try { } catch (Exception) { }
                };

                void Local()
                {
                    try { } catch { }
                }

                Func<Task> external = async () =>
                {
                    try { await Task.Delay(1); } catch (Exception) { }
                };
            }

            async Task NotAFlowTaskMethod()
            {
                try { await Task.Delay(1); } catch (Exception) { }
            }

            FlowTask NotAsync()
            {
                try { return FlowTask.WaitForSeconds(1); } catch (Exception) { return FlowTask.CompletedTask; }
            }
        }
        """);

    // ------------------------------------------------------------------ code fixes

    static readonly string[] NoFlow001 = { "FLOW001" };

    [Test]
    public Task RethrowFixReplacesTheReturns() => CodeFixHarness.VerifyAsync(
        """
        using System;
        using Katout.FlowTask;

        class C
        {
            static void Log() { }

            async FlowTask<int> Load(bool quiet)
            {
                try
                {
                    return await FlowTask.FromResult(1);
                }
                catch (OperationCanceledException)
                {
                    if (quiet) return -1; // quiet
                    Log();
                    return -2;
                }
            }
        }
        """,
        """
        using System;
        using Katout.FlowTask;

        class C
        {
            static void Log() { }

            async FlowTask<int> Load(bool quiet)
            {
                try
                {
                    return await FlowTask.FromResult(1);
                }
                catch (OperationCanceledException)
                {
                    if (quiet) throw; // quiet
                    Log();
                    throw;
                }
            }
        }
        """,
        new CatchCancellationCodeFixProvider(), DiagnosticIds.CatchCanObserveCancellation,
        equivalenceKey: CatchCancellationCodeFixProvider.RethrowEquivalenceKey, absentAfter: NoFlow001);

    [Test]
    public Task RethrowFixAddsAThrowAtTheEnd() => CodeFixHarness.VerifyAsync(
        """
        using Katout.FlowTask;

        class C
        {
            static void Log() { }

            async FlowTask Screen()
            {
                try
                {
                    await FlowTask.WaitForSeconds(1);
                }
                catch (FlowCanceledException)
                {
                    Log();
                }

                try { await FlowTask.WaitForSeconds(1); }
                catch (FlowCanceledException) { }
            }
        }
        """,
        """
        using Katout.FlowTask;

        class C
        {
            static void Log() { }

            async FlowTask Screen()
            {
                try
                {
                    await FlowTask.WaitForSeconds(1);
                }
                catch (FlowCanceledException)
                {
                    Log();
                    throw;
                }

                try { await FlowTask.WaitForSeconds(1); }
                catch (FlowCanceledException) { }
            }
        }
        """,
        new CatchCancellationCodeFixProvider(), DiagnosticIds.CatchCanObserveCancellation,
        equivalenceKey: CatchCancellationCodeFixProvider.RethrowEquivalenceKey);

    [Test]
    public Task RethrowFixFillsAnEmptyOneLineBlock() => CodeFixHarness.VerifyAsync(
        """
        using Katout.FlowTask;

        class C
        {
            async FlowTask Screen()
            {
                try { await FlowTask.WaitForSeconds(1); }
                catch (FlowCanceledException) { }
            }
        }
        """,
        """
        using Katout.FlowTask;

        class C
        {
            async FlowTask Screen()
            {
                try { await FlowTask.WaitForSeconds(1); }
                catch (FlowCanceledException) { throw; }
            }
        }
        """,
        new CatchCancellationCodeFixProvider(), DiagnosticIds.CatchCanObserveCancellation,
        equivalenceKey: CatchCancellationCodeFixProvider.RethrowEquivalenceKey, absentAfter: NoFlow001);

    [Test]
    public Task RemoveFixRemovesAClauseThatOnlyReturns() => CodeFixHarness.VerifyAsync(
        """
        using System;
        using Katout.FlowTask;

        class C
        {
            async FlowTask Screen()
            {
                try
                {
                    await FlowTask.WaitForSeconds(1);
                }
                catch (FormatException)
                {
                }
                catch (FlowCanceledException)
                {
                    return;
                }
            }
        }
        """,
        """
        using System;
        using Katout.FlowTask;

        class C
        {
            async FlowTask Screen()
            {
                try
                {
                    await FlowTask.WaitForSeconds(1);
                }
                catch (FormatException)
                {
                }
            }
        }
        """,
        new CatchCancellationCodeFixProvider(), DiagnosticIds.CatchCanObserveCancellation,
        equivalenceKey: CatchCancellationCodeFixProvider.RemoveEquivalenceKey, absentAfter: NoFlow001);

    [Test]
    public Task RemoveFixUnwrapsATryStatementWithNothingElse() => CodeFixHarness.VerifyAsync(
        """
        using System;
        using Katout.FlowTask;

        class C
        {
            async FlowTask<int> Load()
            {
                // load
                try
                {
                    var v = await FlowTask.FromResult(1);
                    return v + 1;
                }
                catch (OperationCanceledException)
                {
                    return -1;
                }
            }
        }
        """,
        """
        using System;
        using Katout.FlowTask;

        class C
        {
            async FlowTask<int> Load()
            {
                // load
                var v = await FlowTask.FromResult(1);
                return v + 1;
            }
        }
        """,
        new CatchCancellationCodeFixProvider(), DiagnosticIds.CatchCanObserveCancellation,
        equivalenceKey: CatchCancellationCodeFixProvider.RemoveEquivalenceKey, absentAfter: NoFlow001);

    [Test]
    public Task RemoveFixKeepsTheBlockWhenANameOfTheTryBlockIsUsedAroundIt() => CodeFixHarness.VerifyAsync(
        """
        using Katout.FlowTask;

        class C
        {
            async FlowTask Load()
            {
                try
                {
                    var v = await FlowTask.FromResult(1);
                }
                catch (FlowCanceledException)
                {
                    return;
                }

                try
                {
                    var v = await FlowTask.FromResult(2);
                }
                catch (FlowCanceledException)
                {
                    return;
                }
            }
        }
        """,
        """
        using Katout.FlowTask;

        class C
        {
            async FlowTask Load()
            {
                {
                    var v = await FlowTask.FromResult(1);
                }

                try
                {
                    var v = await FlowTask.FromResult(2);
                }
                catch (FlowCanceledException)
                {
                    return;
                }
            }
        }
        """,
        new CatchCancellationCodeFixProvider(), DiagnosticIds.CatchCanObserveCancellation,
        equivalenceKey: CatchCancellationCodeFixProvider.RemoveEquivalenceKey);

    [Test]
    public Task RemoveFixKeepsTheBlockOfAUsingDeclaration() => CodeFixHarness.VerifyAsync(
        """
        using System;
        using Katout.FlowTask;

        class C
        {
            static IDisposable Open() => null;

            async FlowTask Screen()
            {
                try
                {
                    using var r = Open();
                    await FlowTask.WaitForSeconds(1);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                await FlowTask.WaitForSeconds(100);
            }
        }
        """,
        """
        using System;
        using Katout.FlowTask;

        class C
        {
            static IDisposable Open() => null;

            async FlowTask Screen()
            {
                {
                    using var r = Open();
                    await FlowTask.WaitForSeconds(1);
                }

                await FlowTask.WaitForSeconds(100);
            }
        }
        """,
        new CatchCancellationCodeFixProvider(), DiagnosticIds.CatchCanObserveCancellation,
        equivalenceKey: CatchCancellationCodeFixProvider.RemoveEquivalenceKey, absentAfter: NoFlow001);

    [Test]
    public Task ExcludeFixNamesTheVariableOfAnOperationCanceledClause() => CodeFixHarness.VerifyAsync(
        """
        using System;
        using Katout.FlowTask;

        class C
        {
            static void Hide() { }

            async FlowTask Panel()
            {
                try
                {
                    await FlowTask.WaitForSeconds(1);
                }
                catch (OperationCanceledException)
                {
                    Hide();
                    return;
                }
            }
        }
        """,
        """
        using System;
        using Katout.FlowTask;

        class C
        {
            static void Hide() { }

            async FlowTask Panel()
            {
                try
                {
                    await FlowTask.WaitForSeconds(1);
                }
                catch (OperationCanceledException e) when (e is not FlowCanceledException)
                {
                    Hide();
                    return;
                }
            }
        }
        """,
        new CatchCancellationCodeFixProvider(), DiagnosticIds.CatchCanObserveCancellation,
        equivalenceKey: CatchCancellationCodeFixProvider.ExcludeEquivalenceKey, absentAfter: NoFlow001);

    [Test]
    public Task ExcludeFixDeclaresTheExceptionOfAnUntypedCatch() => CodeFixHarness.VerifyAsync(
        """
        using Katout.FlowTask;

        class C
        {
            static void Log() { }

            async FlowTask Screen(int e)
            {
                try { await FlowTask.WaitForSeconds(e); }
                catch { Log(); }
            }
        }
        """,
        """
        using Katout.FlowTask;

        class C
        {
            static void Log() { }

            async FlowTask Screen(int e)
            {
                try { await FlowTask.WaitForSeconds(e); }
                catch (global::System.Exception e1) when (e1 is not FlowCanceledException) { Log(); }
            }
        }
        """,
        new CatchCancellationCodeFixProvider(), DiagnosticIds.CatchCanObserveCancellation,
        equivalenceKey: CatchCancellationCodeFixProvider.ExcludeEquivalenceKey, absentAfter: NoFlow001);

    [Test]
    public Task ExcludeFixGoesInFrontOfTheFilterAndAddsTheUsing() => CodeFixHarness.VerifyAsync(
        """
        using System;
        using System.IO;

        class C
        {
            static bool Observe(Exception e) => true;

            async Katout.FlowTask.FlowTask Screen()
            {
                try
                {
                    await Katout.FlowTask.FlowTask.WaitForSeconds(1);
                }
                catch (Exception ex) when (Observe(ex) || ex is IOException)
                {
                    throw;
                }
            }
        }
        """,
        """
        using System;
        using System.IO;
        using Katout.FlowTask;

        class C
        {
            static bool Observe(Exception e) => true;

            async Katout.FlowTask.FlowTask Screen()
            {
                try
                {
                    await Katout.FlowTask.FlowTask.WaitForSeconds(1);
                }
                catch (Exception ex) when (ex is not FlowCanceledException && (Observe(ex) || ex is IOException))
                {
                    throw;
                }
            }
        }
        """,
        new CatchCancellationCodeFixProvider(), DiagnosticIds.CatchCanObserveCancellation,
        equivalenceKey: CatchCancellationCodeFixProvider.ExcludeEquivalenceKey, absentAfter: NoFlow001);

    [Test]
    public async Task TheFixesOfferedDependOnTheClause()
    {
        var provider = new CatchCancellationCodeFixProvider();

        // catch (FlowCanceledException) that does more than return: 'throw;' only (a filter would take nothing).
        var flowCanceled = await CodeFixHarness.GetActionsAsync("""
            using Katout.FlowTask;
            class C
            {
                static void Log() { }
                async FlowTask M()
                {
                    try { await FlowTask.NextFrame(); }
                    catch (FlowCanceledException) { Log(); }
                }
            }
            """, provider, DiagnosticIds.CatchCanObserveCancellation);
        Assert.That(flowCanceled.Select(a => a.EquivalenceKey), Is.EqualTo(new[] { CatchCancellationCodeFixProvider.RethrowEquivalenceKey }));

        // catch (OperationCanceledException) { return; }: all three.
        var operationCanceled = await CodeFixHarness.GetActionsAsync("""
            using System;
            using Katout.FlowTask;
            class C
            {
                async FlowTask M()
                {
                    try { await FlowTask.NextFrame(); }
                    catch (OperationCanceledException) { return; }
                }
            }
            """, provider, DiagnosticIds.CatchCanObserveCancellation);
        Assert.That(operationCanceled.Select(a => a.EquivalenceKey), Is.EqualTo(new[]
        {
            CatchCancellationCodeFixProvider.RethrowEquivalenceKey,
            CatchCancellationCodeFixProvider.RemoveEquivalenceKey,
            CatchCancellationCodeFixProvider.ExcludeEquivalenceKey,
        }));

        // A catch-all: the filter only.
        var catchAll = await CodeFixHarness.GetActionsAsync("""
            using System;
            using Katout.FlowTask;
            class C
            {
                async FlowTask M()
                {
                    try { await FlowTask.NextFrame(); }
                    catch (Exception) { return; }
                }
            }
            """, provider, DiagnosticIds.CatchCanObserveCancellation);
        Assert.That(catchAll.Select(a => a.EquivalenceKey), Is.EqualTo(new[] { CatchCancellationCodeFixProvider.ExcludeEquivalenceKey }));

        // A return inside a nested catch or finally: 'throw;' there would rethrow another exception or not compile.
        var nested = await CodeFixHarness.GetActionsAsync("""
            using System;
            using Katout.FlowTask;
            class C
            {
                static void Log() { }
                async FlowTask M()
                {
                    try { await FlowTask.NextFrame(); }
                    catch (FlowCanceledException)
                    {
                        try { Log(); }
                        catch (FormatException) { return; }
                        throw;
                    }
                }
            }
            """, provider, DiagnosticIds.CatchCanObserveCancellation);
        Assert.That(nested, Is.Empty);
    }
}
