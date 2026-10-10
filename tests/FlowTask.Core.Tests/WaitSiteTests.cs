using System.Runtime.CompilerServices;

namespace Katout.FlowTask.Tests;

/// <summary>
/// Where each wait was created: FlowScopeInfo.WaitingFile and WaitingLine give the caller's file and line of every wait and
/// combinator, a scope gives those of what it awaits directly, and the dump shows the file name and line.
/// </summary>
public class WaitSiteTests : FlowTestBase
{
    static string ThisFile([CallerFilePath] string file = "") => file;

    static int Line([CallerLineNumber] int line = 0) => line;

    /// <summary>A helper of the game's own that passes its caller's place on, as a thin wrapper of an engine does.</summary>
    static FlowTask Frames(int count, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        FlowTask.DelayFrames(count, null, file, line);

    static void AssertPlace(FlowScopeInfo info, int line)
    {
        Assert.That((info.WaitingFile, info.WaitingLine), Is.EqualTo((ThisFile(), line)), info.Name + ": " + info.Waiting);
    }

    static void AssertNoPlace(FlowScopeInfo info)
    {
        Assert.That((info.WaitingFile, info.WaitingLine), Is.EqualTo(((string)null, 0)), info.Name + ": " + info.Waiting);
    }

    FlowScopeInfo Scope(string name) => World.Diagnostics.Walk().Single(s => s.Name == name);

    [Test]
    public void EveryWaitAndCombinatorRecordsWhereItWasCreated()
    {
        // Each one is run at the root, so it is a node of its own; the line is taken on the line that creates it.
        var signal = new Signal<int>(World, "Hits");
        var subscription = signal.Subscribe(BufferPolicy.Latest); // not disposed: its waits end with the World
        var property = new FlowProperty<int>(1);
        var once = new Once<int>();
        var events = FlowBridge.FromCallback<int>(static _ => static () => { }, "Clicks"); // neither is this one
        var never = World.Run(FlowTask.Never());
        var neverOfInt = World.Run(NeverOfInt());
        var clock = World.CreateClock("Game");
        var lines = new List<int>();
        lines.Add(Line()); World.Run(FlowTask.WaitForSeconds(5, clock));
        lines.Add(Line()); World.Run(FlowTask.DelayFrames(3));
        lines.Add(Line()); World.Run(FlowTask.NextFrame());
        lines.Add(Line()); World.Run(FlowTask.WaitUntil(static () => false));
        lines.Add(Line()); World.Run(FlowTask.WaitUntil(0, static s => s > 0));
        lines.Add(Line()); World.Run(FlowTask.Never());
        lines.Add(Line()); World.Run(FlowTask.Race(new[] { FlowTask.Never() }));
        lines.Add(Line()); World.Run(FlowTask.Race(new[] { NeverOfInt() }));
        lines.Add(Line()); World.Run(FlowTask.Race(FlowTask.Never(), NeverOfInt()));
        lines.Add(Line()); World.Run(FlowTask.Race(FlowTask.Never(), FlowTask.Never(), FlowTask.Never()));
        lines.Add(Line()); World.Run(FlowTask.WhenAll(new[] { FlowTask.Never() }));
        lines.Add(Line()); World.Run(FlowTask.WhenAll(new[] { NeverOfInt() }));
        lines.Add(Line()); World.Run(FlowTask.WhenAll(FlowTask.Never(), FlowTask.Never()));
        lines.Add(Line()); World.Run(FlowTask.WhenAll(NeverOfInt(), FlowTask.Never(), NeverOfInt()));
        lines.Add(Line()); World.Run(signal.Next());
        lines.Add(Line()); World.Run(signal.NextOrClosed());
        lines.Add(Line()); World.Run(subscription.Next());
        lines.Add(Line()); World.Run(subscription.NextOrClosed());
        lines.Add(Line()); World.Run(events.Next());
        lines.Add(Line()); World.Run(events.NextOrClosed());
        lines.Add(Line()); World.Run(property.WaitUntil(static v => v > 1));
        lines.Add(Line()); World.Run(property.WaitUntil(1, static (v, s) => v > s));
        lines.Add(Line()); World.Run(once.Wait());
        lines.Add(Line()); World.Run(never.Join());
        lines.Add(Line()); World.Run(neverOfInt.Join());
        lines.Add(Line()); World.Run(signal.Next().WithoutResult());

        // The two flows that are joined come first.
        var roots = World.Diagnostics.Root.Children.Skip(2).ToList();
        Assert.That(roots, Has.Count.EqualTo(lines.Count), World.Dump());
        for (var i = 0; i < roots.Count; i++)
        {
            Assert.That(roots[i].Kind, Is.Not.EqualTo(FlowScopeKind.Scope), roots[i].Name);
            AssertPlace(roots[i], lines[i]);
        }
    }

    static async FlowTask<int> NeverOfInt()
    {
        await FlowTask.Never();
        return 0;
    }

    [Test]
    public void AScopeGivesThePlaceOfWhatItAwaitsDirectly()
    {
        // A frame or time wait awaited directly has no node: the scope gives its place. A wait with a node, a
        // combinator, and WithoutResult around a wait give theirs. Each await on the line where its line is taken.
        var signal = new Signal<int>(World, "Hits");
        var lines = new int[6];

        async FlowTask Walker()
        {
            lines[0] = Line(); await FlowTask.NextFrame();
            lines[1] = Line(); await FlowTask.DelayFrames(2);
            lines[2] = Line(); await FlowTask.WaitForSeconds(1);
            lines[3] = Line(); await signal.Next();
            lines[4] = Line(); await signal.Next().WithoutResult();
            lines[5] = Line(); await FlowTask.Race(FlowTask.Never(), FlowTask.Never());
        }

        World.Run(Walker());
        AssertPlace(Scope("Walker"), lines[0]);
        Tick();
        AssertPlace(Scope("Walker"), lines[1]);
        Tick(2);
        AssertPlace(Scope("Walker"), lines[2]);
        TickFor(1.1);
        AssertPlace(Scope("Walker"), lines[3]);
        signal.Emit(1);
        Tick();
        AssertPlace(Scope("Walker"), lines[4]);
        signal.Emit(2);
        Tick();
        AssertPlace(Scope("Walker"), lines[5]);
        Assert.That(World.Diagnostics.Walk().Where(s => s.Kind == FlowScopeKind.Wait).Select(s => s.WaitingLine),
            Is.All.EqualTo(lines[5]), "the branches were created on the line of the Race too");
    }

    [Test]
    public void APlacePassedExplicitlyIsTheOneRecorded()
    {
        var lines = new int[2];

        async FlowTask Helper()
        {
            lines[0] = Line(); await Frames(3);
            await FlowTask.NextFrame(null, "Game/Enemy.cs", 42);
            lines[1] = Line(); await Frames(1).WithoutResultOfAFlowTask();
        }

        World.Run(Helper());
        AssertPlace(Scope("Helper"), lines[0]);
        Tick(3);
        var helper = Scope("Helper");
        Assert.That((helper.WaitingFile, helper.WaitingLine), Is.EqualTo(("Game/Enemy.cs", 42)));
        Assert.That(World.Dump(), Does.Contain("Helper (scope) [Default] waiting: NextFrame on Default, 1 frame(s) left at Enemy.cs:42 for "), World.Dump());
        Tick();
        AssertPlace(Scope("Helper"), lines[1]);

        var unknown = World.Run(FlowTask.Never("", 7)); // an empty path is no place, whatever the line
        AssertNoPlace(World.Diagnostics.Root.Children.Last());
        Assert.That(World.Dump(), Does.Contain("Never (wait) [Default] waiting: Never\n"), "no place, no ' at '");
        unknown.Cancel();
    }

    [Test]
    public void AScopeAwaitingTheCallOfAFlowTaskMethodHasNoPlace()
    {
        // The call of an async FlowTask method records no place; the scope of that call gives the place of its own wait.
        var once = new Once<int>();
        var line = 0;

        async FlowTask Child()
        {
            line = Line(); await FlowTask.WaitForSeconds(10);
        }

        async FlowTask Parent()
        {
            await Child();
            await once;
        }

        World.Run(Parent());
        AssertNoPlace(Scope("Parent"));
        AssertPlace(Scope("Child"), line);
        Assert.That(World.Dump(), Does.Contain("waiting: Child for ").And.Contain($"at WaitSiteTests.cs:{line} for "), World.Dump());
        TickFor(10.1);
        // Awaiting the Once itself (its GetAwaiter) records no place either; its Wait() does.
        Assert.That(Scope("Parent").Waiting, Is.EqualTo("Once<Int32>.Wait"));
        AssertNoPlace(Scope("Parent"));
        once.Set(1);
        Tick();
        Assert.That(World.Diagnostics.Root.Children, Is.Empty, World.Dump());
    }

    [Test]
    public void ThePlaceIsGoneWithItsNode()
    {
        // A node goes back to its pool when it ends: the view of it returns nothing, and the node reused for another wait
        // gives the new place.
        var lines = new int[2];

        async FlowTask First()
        {
            lines[0] = Line(); await FlowTask.Never();
        }

        async FlowTask Second()
        {
            lines[1] = Line(); await FlowTask.Never();
        }

        var first = World.Run(First());
        var wait = World.Diagnostics.Walk().Single(s => s.Kind == FlowScopeKind.Wait);
        AssertPlace(wait, lines[0]);
        first.Cancel();
        Tick();
        Assert.That(wait.IsValid, Is.False);
        AssertNoPlace(wait);
        World.Run(Second());
        AssertPlace(World.Diagnostics.Walk().Single(s => s.Kind == FlowScopeKind.Wait), lines[1]);
    }
}

/// <summary>A WithoutResult of a task without a result, for a test: the wait it wraps through a FlowTask{FlowUnit}.</summary>
internal static class WaitSiteTestExtensions
{
    internal static FlowTask WithoutResultOfAFlowTask(this FlowTask task) => ((FlowTask<FlowUnit>)task).WithoutResult();
}
