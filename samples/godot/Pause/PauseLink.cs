using Godot;

namespace Katout.FlowTask.Samples;

/// <summary>
/// Sample: pauses the scene tree while a <see cref="Clock"/> is paused. <c>clocks.Game.Pause()</c> stops only the flows
/// on the clock; the tree's pause stops Godot's own systems too (the physics, AnimationPlayers, the <c>_process</c> of
/// pausable nodes). The clock's pauses are counted and the tree's pause is a single flag, so one node owns the flag and
/// follows the clock: whoever pauses Game (a menu, a cutscene) pauses the tree, and the tree resumes when the last pause
/// is released. Screens take only <c>clocks.Game.Pause()</c> and never set <c>GetTree().Paused</c> themselves.
/// <para>
/// Add one per session, on a node that stays in the tree (an autoload, the session's root). It runs while the tree is
/// paused (ProcessMode Always) and checks the clock in every <c>_Process</c>, so the tree follows within a frame.
/// Leaving the tree, it unpauses the tree if it paused it.
/// </para>
/// </summary>
public partial class PauseLink : Node
{
    bool _paused;

    public PauseLink() => ProcessMode = ProcessModeEnum.Always;

    /// <summary>The clock the tree follows: the session's <see cref="GameClocks.Game"/>.</summary>
    public Clock Clock { get; set; }

    public override void _Process(double delta)
    {
        var paused = Clock != null && Clock.IsPausedInHierarchy;
        if (paused == _paused) return;
        _paused = paused;
        GetTree().Paused = paused;
    }

    public override void _ExitTree()
    {
        if (!_paused) return;
        _paused = false;
        GetTree().Paused = false;
    }
}
