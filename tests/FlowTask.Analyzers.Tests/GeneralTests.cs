using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Katout.FlowTask.Analyzers.Tests;

public class GeneralTests
{
    /// <summary>The default severity of each rule (docs/ja/tools/analyzers.md, "重大度の変更").</summary>
    static readonly Dictionary<string, DiagnosticSeverity> DocumentedSeverities = new()
    {
        ["FLOW001"] = DiagnosticSeverity.Error,
        ["FLOW002"] = DiagnosticSeverity.Error,
        ["FLOW003"] = DiagnosticSeverity.Warning,
        ["FLOW004"] = DiagnosticSeverity.Warning,
        ["FLOW005"] = DiagnosticSeverity.Error,
        ["FLOW006"] = DiagnosticSeverity.Info,
        ["FLOW007"] = DiagnosticSeverity.Warning,
        ["FLOW008"] = DiagnosticSeverity.Warning,
        ["FLOW009"] = DiagnosticSeverity.Warning,
        ["FLOW010"] = DiagnosticSeverity.Warning,
    };

    [Test]
    public void EveryRuleHasItsDocumentedSeverityCategoryAndAbsoluteHelpLink()
    {
        var descriptors = AnalyzerHarness.Analyzers.SelectMany(a => a.SupportedDiagnostics).ToList();
        var ids = descriptors.Select(d => d.Id).Distinct().OrderBy(id => id, StringComparer.Ordinal).ToArray();
        Assert.That(ids, Is.EqualTo(DocumentedSeverities.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray()));
        foreach (var d in descriptors)
        {
            // Every descriptor of one id has the same severity: AnalyzerReleases.*.md lists one row per id.
            Assert.That(d.DefaultSeverity, Is.EqualTo(DocumentedSeverities[d.Id]), d.Id);
            Assert.That(d.IsEnabledByDefault, Is.True, d.Id);
            Assert.That(d.Category, Is.EqualTo("FlowTask"), d.Id);
            Assert.That(Uri.IsWellFormedUriString(d.HelpLinkUri, UriKind.Absolute), Is.True, d.Id + ": " + d.HelpLinkUri);
            Assert.That(d.HelpLinkUri, Is.EqualTo(DiagnosticIds.HelpLinkBase + d.Id.ToLowerInvariant()), d.Id);
            Assert.That(d.Title.ToString(CultureInfo.InvariantCulture), Is.Not.Empty, d.Id);
        }
    }

    [Test]
    public void EveryRuleHasAnAnchorInAnalyzersMd()
    {
        // The helpLinkUri of every rule points at '<a id="flow00n"></a>' on the analyzer page (docs/en/tools/analyzers.md);
        // the Japanese page has the same anchors, so the language switch of the site keeps the section.
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "FlowTask.slnx"))) directory = directory.Parent;
        Assert.That(directory, Is.Not.Null, "repository root (FlowTask.slnx) not found above the test directory");

        foreach (var locale in new[] { "en", "ja" })
        {
            var text = File.ReadAllText(Path.Combine(directory.FullName, "docs", locale, "tools", "analyzers.md"));
            foreach (var id in AnalyzerHarness.Analyzers.SelectMany(a => a.SupportedDiagnostics).Select(d => d.Id).Distinct())
            {
                Assert.That(text, Does.Contain("<a id=\"" + id.ToLowerInvariant() + "\"></a>"), locale + ": " + id);
            }
        }
    }

    [Test]
    public async Task NothingIsReportedWithoutAFlowTaskReference()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            using System;
            using System.Threading.Tasks;

            class C
            {
                async Task M()
                {
                    try { await Task.Delay(1); } catch (Exception) { } finally { await Task.Yield(); }
                }
            }
            """, AnalyzerHarness.ParseOptions);
        var references = AnalyzerHarness.References.Where(r => Path.GetFileName(r.Display) != "FlowTask.dll").ToImmutableArray();
        var compilation = CSharpCompilation.Create("NoFlowTask", new[] { tree }, references, AnalyzerHarness.CompilationOptions);
        AnalyzerHarness.AssertCompiles(compilation);
        Assert.That(await AnalyzerHarness.GetFlowDiagnosticsAsync(compilation), Is.Empty);
    }

    [Test]
    public Task PragmaSuppressionIsHonoured() => AnalyzerHarness.VerifyAsync("""
        using System.Threading.Tasks;
        using Katout.FlowTask;

        class C
        {
            async FlowTask M(TaskCompletionSource<int> tcs)
            {
        #pragma warning disable FLOW002
                var v = await tcs.Task;
        #pragma warning restore FLOW002
                v += {|FLOW002:await tcs.Task|};
            }
        }
        """);

    /// <summary>Error rules are suppressed like warnings: '#pragma warning disable' and [SuppressMessage].</summary>
    [Test]
    public Task ErrorRulesCanBeSuppressedWithPragmaAndSuppressMessage() => AnalyzerHarness.VerifyAsync("""
        using System.Diagnostics.CodeAnalysis;
        using System.Threading.Tasks;
        using Katout.FlowTask;

        class C
        {
            async FlowTask Pragma()
            {
                try { await FlowTask.NextFrame(); }
        #pragma warning disable FLOW001 // the swallowed cancellation is what this code shows
                catch (FlowCanceledException) { }
        #pragma warning restore FLOW001

                try { await FlowTask.NextFrame(); }
                {|FLOW001:catch (FlowCanceledException)|} { }
            }

            [SuppressMessage("FlowTask", "FLOW002", Justification = "Not bridged on purpose.")]
            async FlowTask Attribute(Task task)
            {
                await task;
            }

            async FlowTask NotSuppressed(Task task)
            {
                {|FLOW002:await task|};
            }
        }
        """);

    /// <summary>
    /// Ordinary UniTask and Task code in a project that references FlowTask. No FLOW rule applies to a function that
    /// is not a FlowTask method (catch-all, finally awaits, Forget, await using).
    /// </summary>
    [Test]
    public Task OrdinaryTaskAndUniTaskCodeHasNoFlowDiagnostics() => AnalyzerHarness.VerifyAsync("""
        using System;
        using System.Collections.Generic;
        using System.IO;
        using System.Threading;
        using System.Threading.Tasks;
        using Cysharp.Threading.Tasks;

        public static class PUniTask
        {
            static async UniTask<int> Load(CancellationToken ct) { await UniTask.Yield(); return 1; }

            static async UniTask Screen(CancellationToken ct)
            {
                try { await Load(ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { }
                finally { await UniTask.Yield(); }
                await Task.Delay(1, ct);
                Load(ct).Forget();
                var tasks = new List<UniTask<int>> { Load(ct), Load(ct) };
                await UniTask.WhenAll(tasks);
                await using var s = new MemoryStream();
                Func<UniTask> f = async () => { try { await UniTask.Yield(); } catch (Exception) { } };
                await f();
            }

            static async Task<int> TaskScreen()
            {
                try { return await Task.FromResult(1); }
                catch (Exception) { return 0; }
            }

            static async void Handler() { await UniTask.Yield(); }
        }
        """ + Stubs.UniTask);

    /// <summary>
    /// The FLOW002, FLOW004 and FLOW005 probes (class P004). Differences from the probes' expectations, by design:
    /// 'await started.AsFlow()' is the regular bridge of a running task (FLOW002's fix and docs steer to FromTask
    /// instead), and a void lambda body is not checked for FLOW004 (the Assert.Throws idiom).
    /// </summary>
    [Test]
    public Task ProbesOfFlow002To005() => AnalyzerHarness.VerifyAsync("""
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Cysharp.Threading.Tasks;
        using Katout.FlowTask;

        public static class P004
        {
            static async FlowTask A() { await FlowTask.NextFrame(); }
            static event Action Clicked;

            static async FlowTask InFlow(bool c, int k, Clock clock, Clock other, Task<int> started)
            {
                Func<FlowTask> f = async () => { {|FLOW002:await Task.Delay(1)|}; };
                {|FLOW002:await (k switch { 0 => Task.Delay(1), _ => Task.CompletedTask })|};
                {|FLOW002:await UniTask.Yield()|};
                await started.AsFlow();

                {|FLOW004:clock.Pause()|};
                Action a1 = () => clock.Pause();
                void P() { {|FLOW004:clock.Pause()|}; }
                void P2() => {|FLOW004:clock.Pause()|};
                var {|FLOW004:h|} = clock.Pause();
                _ = clock.Pause();
                _ = k switch { 0 => clock.Pause(), _ => other.Pause() };

                Clicked += async () => {|FLOW005:await A()|};
                async Task LocalTask() { {|FLOW005:await A()|}; }
                await f();
                P();
                P2();
            }

            static void Pause(Clock clock) => {|FLOW004:clock.Pause()|};

            static async UniTask UniTaskAwaitsFlow() { {|FLOW005:await A()|}; }

            static async Task TaskAwaitsHandle(FlowWorld w) { {|FLOW005:await w.Run(A()).Join()|}; }

            static async Task TaskAwaitsAsTask(FlowWorld w) { await w.Run(A()).AsTask(); }
        }
        """ + Stubs.UniTask);

    /// <summary>
    /// Edge cases (FlowEdge), every rule. 'X().Discard()' is FLOW003, an await in a finally block is not reported, and
    /// FLOW001 follows its one rule: a catch-all filtered with 'when (e is not FlowCanceledException)' is not
    /// reported, a catch-all that only rethrows is.
    /// </summary>
    [Test]
    public Task EdgeCasesOfEveryRule() => AnalyzerHarness.VerifyOnlyAsync("""
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Cysharp.Threading.Tasks;
        using Katout.FlowTask;

        namespace Coexist.FlowEdge
        {
            public sealed class Door
            {
                public bool Open { get; set; }
            }

            public static class DoorAwaitExtensions
            {
                public static FlowTask.Awaiter GetAwaiter(this Door door) => FlowTask.WaitUntil(() => door.Open).GetAwaiter();
            }

            public sealed class EdgeCases
            {
                readonly Signal<int> sig = new Signal<int>();
                readonly Clock clock;
                readonly FlowWorld world;

                public EdgeCases(FlowWorld world)
                {
                    this.world = world;
                    clock = world.DefaultClock;
                }

                // ---- FLOW001
                public async FlowTask<int> SyncOnlyTry(string s)
                {
                    await FlowTask.NextFrame();
                    try { return int.Parse(s, System.Globalization.CultureInfo.InvariantCulture); }
                    catch (Exception) { return 0; }
                }

                public async FlowTask FilteredCatch()
                {
                    try { await FlowTask.DelayFrames(1); }
                    catch (Exception e) when (e is not FlowCanceledException) { Console.WriteLine(e); }
                }

                public async FlowTask PureRethrow()
                {
                    try { await FlowTask.DelayFrames(1); }
                    {|FLOW001:catch (Exception)|} { throw; }
                }

                public async FlowTask SpecificCatch()
                {
                    try { await sig.Next(); }
                    catch (SignalClosedException) { }
                }

                public async FlowTask CatchCanceled()
                {
                    try { await FlowTask.DelayFrames(1); }
                    {|FLOW001:catch (FlowCanceledException)|} { }
                }

                public async FlowTask CatchInNestedSyncLambda()
                {
                    Func<int> parse = () =>
                    {
                        try { return int.Parse("1", System.Globalization.CultureInfo.InvariantCulture); }
                        catch (Exception) { return 0; }
                    };
                    await FlowTask.WaitForSeconds(parse());
                }

                // ---- FLOW002
                public async FlowTask AwaitExtensionAwaiter(Door door)
                {
                    await door;
                }

                public async FlowTask AwaitTaskDirectly()
                {
                    {|FLOW002:await Task.Delay(1)|};
                }

                public async FlowTask AwaitUniTaskBridges()
                {
                    await FlowUniTask.FromUniTask(ct => Work(ct));
                    await Work(default).AsFlow();
                }

                public async FlowTask AwaitOnce(Once<int> once)
                {
                    var a = await once;
                    var b = await once.Wait();
                    Console.WriteLine(a + b);
                }

                public async FlowTask AwaitHandleJoin()
                {
                    var h = Flow.Spawn(FlowTask.WaitForSeconds(1));
                    await h.Join();
                }

                static UniTask Work(CancellationToken ct) => UniTask.CompletedTask;

                // ---- FLOW003
                public async FlowTask StoredThenRead()
                {
                    var t = FlowTask.WaitForSeconds(1);
                    var list = new List<FlowTask> { t, FlowTask.NextFrame() };
                    await FlowTask.WhenAll(list);
                }

                public async FlowTask ConditionalAssign(bool c)
                {
                    FlowTask t;
                    if (c) t = FlowTask.WaitForSeconds(1);
                    else t = FlowTask.NextFrame();
                    await t;
                }

                public FlowTask ReturnsLazy() => FlowTask.WaitForSeconds(1);

                public Func<FlowTask> Factory() => () => FlowTask.WaitForSeconds(1);

                public async FlowTask DropInFlow()
                {
                    {|FLOW003:FlowTask.WaitForSeconds(1)|};
                    await FlowTask.NextFrame();
                }

                public async FlowTask ExplicitDiscardMethod()
                {
                    {|FLOW003:FlowTask.WaitForSeconds(1).Discard()|};
                    await FlowTask.NextFrame();
                }

                public void StartFromOutside()
                {
                    world.Run(FlowTask.WaitForSeconds(1));
                    _ = world.Run(FlowTask.WaitForSeconds(1));
                }

                // ---- FLOW004
                public async FlowTask UsingStatement()
                {
                    using (clock.Pause())
                    {
                        await FlowTask.WaitForSeconds(1);
                    }

                    var sub = sig.Subscribe(BufferPolicy.Latest);
                    try { await sub.Next(); }
                    finally { sub.Dispose(); }
                }

                public async FlowTask DroppedHold()
                {
                    {|FLOW004:clock.Pause()|};
                    await FlowTask.WaitForSeconds(1);
                }

                // ---- FLOW005
                public async Task LocalFlowInsideTaskMethod()
                {
                    async FlowTask Local()
                    {
                        await FlowTask.WaitForSeconds(1);
                    }

                    await world.Run(Local()).AsTask();
                }

                public async UniTask UniTaskBridgeBack()
                {
                    await world.Run(FlowTask.WaitForSeconds(1)).ToUniTask();
                }

                public async Task AwaitFlowInTask()
                {
                    {|FLOW005:await FlowTask.WaitForSeconds(1)|};
                }

                // ---- Cleanup in a finally block
                public async FlowTask AwaitInFinally()
                {
                    try { await FlowTask.WaitForSeconds(1); }
                    finally
                    {
                        await FlowTask.WaitForSeconds(0.2);
                        _ = Flow.Spawn(FlowTask.WaitForSeconds(0.2));
                    }
                }

                public async FlowTask FinallyWithNestedTaskLambda()
                {
                    try { await FlowTask.WaitForSeconds(1); }
                    finally
                    {
                        Func<Task> f = async () => await Task.Delay(1);
                        _ = f;
                    }
                }

                public async FlowTask ForeignAwaitInFinally()
                {
                    try { await FlowTask.WaitForSeconds(1); }
                    finally { {|FLOW002:await Task.Delay(1)|}; }
                }
            }
        }
        """ + Stubs.UniTask,
        new[] { "FLOW001", "FLOW002", "FLOW003", "FLOW004", "FLOW005", "FLOW006" });

    /// <summary>Idiomatic flow code from the typical game scenarios must produce no diagnostics.</summary>
    [Test]
    public Task IdiomaticScenarioCodeHasNoDiagnostics() => AnalyzerHarness.VerifyAsync("""
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;
        using Katout.FlowTask;

        public sealed class Button
        {
            public event Action Clicked;
            public EventSignal<FlowUnit> ClickedSignal() => FlowBridge.FromCallback<FlowUnit>(emit => { System.Action h = () => emit(FlowUnit.Default); Clicked += h; return () => Clicked -= h; });
        }

        public sealed class Game
        {
            readonly Signal<FlowUnit> _start = new Signal<FlowUnit>();
            readonly Signal<FlowUnit> _expired = new Signal<FlowUnit>();
            readonly Signal<int> _damaged = new Signal<int>();
            readonly Signal<FlowUnit> _back = new Signal<FlowUnit>();
            readonly List<string> _log = new List<string>();
            Clock _game;

            public async FlowTask Loop()
            {
                while (true)
                {
                    await Title();
                    if (await InGame()) await Notice();
                }
            }

            async FlowTask Title()
            {
                _log.Add("title");
                await _start.Next();
            }

            async FlowTask<bool> InGame()
            {
                var r = await FlowTask.Race(Stages(), _expired.Next());
                return r.Index == 1;
            }

            async FlowTask Stages()
            {
                for (var stage = 1; ; stage++)
                {
                    using var pause = _game.Pause();
                    await FlowTask.WaitForSeconds(1.0);
                }
            }

            async FlowTask Notice()
            {
                try
                {
                    await FlowTask.WaitForSeconds(0.5);
                }
                finally
                {
                    await Flow.NonCancelable(FlowTask.WaitForSeconds(0.1));
                }
            }

            async FlowTask Enemy()
            {
                using var hits = _damaged.Subscribe(BufferPolicy.Latest);
                while (true)
                {
                    var r = await FlowTask.Race(FlowTask.NextFrame(), hits.Next().WithoutResult());
                    if (r.Index == 1) await FlowTask.WaitForSeconds(0.8);
                }
            }

            async FlowTask<bool> Confirm(Button ok, Button cancel)
            {
                using var okSignal = ok.ClickedSignal();
                using var cancelSignal = cancel.ClickedSignal();
                var r = await FlowTask.Race(okSignal.Next(), cancelSignal.Next(), _back.Next());
                return r.Index == 0;
            }

            async FlowTask<string> Fetch()
            {
                try
                {
                    return await FlowBridge.FromTask(ct => Task.FromResult("stage"));
                }
                catch (InvalidOperationException e)
                {
                    _log.Add(e.Message);
                    return null;
                }
            }

            async FlowTask<int> Arcade()
            {
                try { return await FlowTask.FromResult(1); }
                catch (Exception e) when (e is not FlowCanceledException)
                {
                    _log.Add(e.Message);
                    return 0;
                }
            }

            async FlowTask Host()
            {
                var enemy = Flow.Spawn(Enemy());
                Flow.AddCleanup(() => _log.Add("bye"));
                await FlowTask.WaitForSeconds(1);
                enemy.Cancel();
            }

            public void Start(FlowWorld world)
            {
                _game = world.CreateClock("Game");
                world.Run(Loop());
                var h = world.Run(Arcade());
            }

            public Task<int> Bridge(FlowWorld world) => world.Run(Arcade()).AsTask();
        }
        """);
}
