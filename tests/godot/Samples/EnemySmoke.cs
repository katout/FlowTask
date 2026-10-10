using Katout.FlowTask.Godot;
using Godot;

namespace Katout.FlowTask.Samples.Tests;

/// <summary>The smoke test's checks of <see cref="Enemy"/>.</summary>
public static class EnemySmoke
{
    /// <summary>
    /// Adds the enemy outside flow code (deferred), as a scene does: its _Ready then starts the brain as a root flow,
    /// not as a child of the check.
    /// </summary>
    static async FlowTask<Enemy> Spawn(Node host, GameClocks clocks, string name)
    {
        var enemy = new Enemy { Name = name, Clocks = clocks };
        host.CallDeferred(Node.MethodName.AddChild, enemy);
        await FlowTask.WaitUntil(enemy, e => e.IsInsideTree());
        return enemy;
    }

    public static async FlowTask<Verdict> Run(Node host)
    {
        var clocks = new GameClocks(FlowWorldNode.Default);
        var enemy = await Spawn(host, clocks, "Enemy");
        await FlowTask.WaitUntil(enemy, e => e.Position.X > 1);
        var patrolled = enemy.IsPatrolling;

        // A hit, then a second one during the flinch: kept by the subscription, not lost.
        enemy.TakeHit(1);
        await FlowTask.WaitUntil(enemy, e => e.Flinches == 1);
        var patrolUnwound = !enemy.IsPatrolling;
        enemy.TakeHit(1);
        await FlowTask.WaitUntil(enemy, e => e.Flinches == 2);

        // Paused: frozen, and a hit during the pause waits for the resume.
        await FlowTask.WaitUntil(enemy, e => e.IsPatrolling);
        var pause = clocks.Game.Pause();
        await FlowTask.NextFrame();
        var x = enemy.Position.X;
        enemy.TakeHit(1);
        await FlowTask.DelayFrames(5);
        var frozen = enemy.Position.X == x && enemy.Flinches == 2;
        pause.Dispose();
        await FlowTask.WaitUntil(enemy, e => e.Flinches == 3);

        // HP 5 - 3 - 2 = 0: dies and frees itself.
        enemy.TakeHit(2);
        await FlowTask.WaitUntil(enemy, e => e.IsDead);
        var diedWhilePatrolling = enemy.IsPatrolling;
        var brain = enemy.Brain;
        await FlowTask.WaitUntil(() => !GodotObject.IsInstanceValid(enemy));

        // Freed during the patrol: the brain is unwound in the frame the enemy leaves the tree.
        var doomed = await Spawn(host, clocks, "DoomedEnemy");
        await FlowTask.WaitUntil(doomed, e => e.IsPatrolling);
        var doomedBrain = doomed.Brain;
        var unwoundWhenFreed = false;
        // tree_exited comes after tree_exiting, where the brain is unwound: in the same frame, during the free.
        doomed.TreeExited += () => unwoundWhenFreed = doomedBrain.IsCompleted && !doomed.IsPatrolling;
        doomed.QueueFree();
        await FlowTask.WaitUntil(() => doomedBrain.IsCompleted);

        return Verdict.Of($"patrolled {patrolled}, flinches {enemy.Flinches}, frozen while paused {frozen}, died: {brain.Status} ({brain.Result}); freed during the patrol: {doomedBrain.Status} ({doomedBrain.Result}), unwound by tree_exited {unwoundWhenFreed}",
            (patrolled, "patrolled"), (patrolUnwound, "the hit won the Race and unwound the patrol"),
            (frozen, "frozen while Game was paused, the hit kept"),
            (!diedWhilePatrolling, "HP 0 stopped the patrol"),
            (brain.Status == FlowStatus.Succeeded && brain.Result, "the brain ended with the enemy's death"),
            (doomedBrain.Status == FlowStatus.Succeeded && !doomedBrain.Result, "freeing the enemy ended its brain (false: canceled by the exit)"),
            (unwoundWhenFreed, "the patrol was unwound while the enemy left the tree"));
    }
}
