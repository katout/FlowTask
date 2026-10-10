using System;
using Katout.FlowTask.Godot;
using Godot;

namespace Katout.FlowTask.Samples;

/// <summary>
/// Sample: an enemy that patrols, flinches when hit and dies at HP 0. The brain starts in
/// <c>_Ready</c> with <c>RunWhileInTree</c> on the Game clock (<see cref="GameClocks"/>),
/// so freeing the enemy or leaving its scene stops it, and pausing the game freezes it. Set <see cref="Clocks"/> before
/// adding it to the tree.
/// </summary>
public partial class Enemy : Node2D
{
    int _nextWaypoint;

    public GameClocks Clocks { get; set; }
    public Vector2[] Waypoints { get; set; } = new[] { Vector2.Zero, new Vector2(300, 0) };
    public float Speed { get; set; } = 120f;
    public int MaxHp { get; set; } = 5;
    public double FlinchSeconds { get; set; } = 0.3;

    /// <summary>Hits, from areas, bullets or the network. Emitted outside flow code by <see cref="TakeHit"/>.</summary>
    public Signal<int> Damaged { get; } = new Signal<int>("Enemy.Damaged");

    public FlowProperty<int> Hp { get; private set; }
    public FlowHandle<bool> Brain { get; private set; }
    public int Flinches { get; private set; }
    public bool IsPatrolling { get; private set; }
    public bool IsDead { get; private set; }

    public override void _Ready()
    {
        Hp = new FlowProperty<int>(MaxHp);
        Brain = this.RunWhileInTree(Run(), Clocks.Game);
    }

    public void TakeHit(int damage)
    {
        if (Hp == null || IsDead) return;
        Hp.Set(Math.Max(0, Hp.Value - damage));
        Damaged.Emit(damage);
    }

    async FlowTask Run()
    {
        // HP 0 cuts through the patrol and a flinch at once: the losing branch is unwound.
        await FlowTask.Race(Alive(), Hp.WaitUntil(hp => hp <= 0));
        await Die();
    }

    async FlowTask Alive()
    {
        // Subscribed for the whole life, so a hit that lands while flinching waits in the buffer instead of being lost.
        using var hits = Damaged.Subscribe(BufferPolicy.Latest);
        while (true)
        {
            // The hit comes first: a hit buffered during the flinch completes at once and the patrol does not start.
            var r = await FlowTask.Race(hits.Next(), Patrol());
            if (r.Index == 0) await Flinch();
        }
    }

    async FlowTask Patrol()
    {
        IsPatrolling = true;
        try
        {
            while (true)
            {
                var target = Waypoints[_nextWaypoint];
                while (Position != target)
                {
                    await FlowTask.NextFrame();
                    // The clock of this flow (Game): its DeltaTime follows Engine.TimeScale and is 0 while paused.
                    Position = Position.MoveToward(target, Speed * (float)Flow.CurrentClock.DeltaTime);
                }

                await FlowTask.WaitForSeconds(0.5);
                _nextWaypoint = (_nextWaypoint + 1) % Waypoints.Length;
            }
        }
        finally
        {
            IsPatrolling = false;
        }
    }

    async FlowTask Flinch()
    {
        Flinches++;
        await FlowTask.WaitForSeconds(FlinchSeconds);
    }

    async FlowTask Die()
    {
        IsDead = true;
        await FlowTask.WaitForSeconds(0.3); // the death animation
        QueueFree();
    }
}
