using System;
using Katout.FlowTask.Unity;
using UnityEngine;

namespace Katout.FlowTask.Samples
{
    /// <summary>
    /// Sample: an enemy that patrols, flinches when hit and dies at HP 0. The brain is a flow bound to the
    /// GameObject (<c>RunWhileActive</c>), so destroying the enemy, deactivating it or unloading its scene stops it, and it
    /// runs on the Game clock, so pausing the game freezes it. The spawner sets <see cref="Clocks"/> before the first frame.
    /// </summary>
    public sealed class Enemy : MonoBehaviour
    {
        public GameClocks Clocks;
        public Vector3[] Waypoints = { Vector3.zero, new Vector3(10, 0, 0) };
        public float Speed = 3f;
        public int MaxHp = 5;
        public double FlinchSeconds = 0.3;

        int _nextWaypoint;

        /// <summary>Hits, from colliders, bullets or the network. Emitted outside flow code by <see cref="TakeHit"/>.</summary>
        public Signal<int> Damaged { get; } = new Signal<int>("Enemy.Damaged");

        public FlowProperty<int> Hp { get; private set; }
        public FlowHandle Brain { get; private set; }
        public int Flinches { get; private set; }
        public bool IsPatrolling { get; private set; }
        public bool IsDead { get; private set; }

        // Start, not Awake or OnEnable: on an active GameObject those two run inside Instantiate or AddComponent, before the
        // spawner has set Clocks; Start runs later, before the first frame. Start also runs once, so this enemy lives once:
        // deactivated and activated again, it stays idle. An enemy reused from a pool starts its brain in OnEnable instead,
        // with Clocks set before it is activated (https://katout.github.io/FlowTask/en/unity/lifetime/).
        void Start()
        {
            Hp = new FlowProperty<int>(MaxHp);
            Brain = gameObject.RunWhileActive(Run(), Clocks.Game);
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
                // The hit comes first: a hit buffered during the flinch completes at once, and the Race then does not start
                // the patrol at all (with the patrol first, it would take a step before the buffered hit wins).
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
                    while (transform.position != target)
                    {
                        await FlowTask.NextFrame();
                        // The clock of this flow (Game): its DeltaTime follows Time.timeScale and is 0 while paused.
                        var step = Speed * (float)Flow.CurrentClock.DeltaTime;
                        transform.position = Vector3.MoveTowards(transform.position, target, step);
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
            Destroy(gameObject);
        }
    }
}
