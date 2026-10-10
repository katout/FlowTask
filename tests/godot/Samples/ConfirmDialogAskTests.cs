using Katout.FlowTask.Testing;
using Katout.FlowTask.Testing.NUnit;
using NUnit.Framework;

namespace Katout.FlowTask.Samples.Tests;

/// <summary>
/// Godot: the decision of <see cref="ConfirmDialog"/> tested without nodes, on plain signals, in
/// virtual time. Compiled into the smoke project, so CoreSuiteRunner runs it inside the Godot process with the core
/// suite, on a thread of its own: such a test must not create Godot nodes.
/// </summary>
public class ConfirmDialogAskTests
{
    [Test, FailOnUnhandledFlowException]
    public void ConfirmDialogAsk_TheFirstPressWins()
    {
        using var tw = FlowNUnit.CreateWorld();
        var ok = new Signal<FlowUnit>();
        var cancel = new Signal<FlowUnit>();
        var router = new BackKeyRouter();

        var h = tw.World.Run(ConfirmDialog.Ask(ok, cancel, router, tw.World.UnscaledClock, 10));
        ok.Emit(FlowUnit.Default);
        cancel.Emit(FlowUnit.Default); // the same frame: reaches nobody once OK has won
        tw.World.TickUntil(() => h.IsCompleted);

        Assert.That(h.Result, Is.True);
        Assert.That(router.Count, Is.Zero, "the back-key entry left with its losing branch");
    }

    [Test, FailOnUnhandledFlowException]
    public void ConfirmDialogAsk_TheBackKeyIsNo()
    {
        using var tw = FlowNUnit.CreateWorld();
        var router = new BackKeyRouter();
        var h = tw.World.Run(ConfirmDialog.Ask(new Signal<FlowUnit>(), new Signal<FlowUnit>(), router, tw.World.UnscaledClock, 10));
        Assert.That(router.Press(), Is.True);
        tw.World.TickUntil(() => h.IsCompleted);
        Assert.That(h.Result, Is.False);
    }

    [Test, FailOnUnhandledFlowException]
    public void ConfirmDialogAsk_NoAnswerTimesOutInVirtualTime()
    {
        using var tw = FlowNUnit.CreateWorld();
        var answer = tw.World.RunUntilComplete(
            ConfirmDialog.Ask(new Signal<FlowUnit>(), new Signal<FlowUnit>(), new BackKeyRouter(), tw.World.UnscaledClock, 10));
        Assert.That(answer, Is.False);
        Assert.That(tw.World.UnscaledClock.Time, Is.GreaterThanOrEqualTo(10.0), "10 s passed at once, in virtual time");
    }
}
