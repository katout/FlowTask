using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Katout.FlowTask.Samples.Tests.SampleTestUtil;

namespace Katout.FlowTask.Samples.Tests
{
    /// <summary>Tests of the <see cref="Enemy"/> sample.</summary>
    public class EnemyTests
    {
        [UnityTearDown]
        public IEnumerator CleanUp() => SampleHost.CleanUp();

        static Enemy Spawn(SampleHost host, GameClocks clocks)
        {
            var enemy = host.GameObject.AddComponent<Enemy>();
            enemy.Clocks = clocks;
            return enemy;
        }

        [UnityTest]
        public IEnumerator EnemySample_PatrolsAndAHitDuringTheFlinchIsNotLost()
        {
            var enemy = Spawn(SampleHost.Create("Enemy", Ending.Destroy), Clocks);
            yield return WaitUntil(() => enemy.transform.position.x > 0.1f, what: "the patrol to move");
            Assert.That(enemy.IsPatrolling, Is.True);

            enemy.TakeHit(1);
            yield return WaitUntil(() => enemy.Flinches == 1, what: "the first flinch");
            Assert.That(enemy.IsPatrolling, Is.False, "the hit won the Race and unwound the patrol");
            enemy.TakeHit(1); // during the flinch: kept by the subscription
            yield return WaitUntil(() => enemy.Flinches == 2, what: "the second flinch, from the buffered hit");
            yield return WaitUntil(() => enemy.IsPatrolling, what: "the patrol to start again");
            Assert.That(enemy.Hp.Value, Is.EqualTo(3));
        }

        [UnityTest]
        public IEnumerator EnemySample_HpZeroStopsThePatrolAndTheEnemyDies()
        {
            var enemy = Spawn(SampleHost.Create("Enemy", Ending.Destroy), Clocks);
            yield return WaitUntil(() => enemy.IsPatrolling, what: "the patrol");

            enemy.TakeHit(5);
            yield return WaitUntil(() => enemy.IsDead, what: "death");
            Assert.That(enemy.IsPatrolling, Is.False);
            var brain = enemy.Brain;
            yield return WaitUntil(() => enemy == null, what: "the enemy to destroy itself after the death animation");
            // Destroy deactivates the enemy inside the call, which cancels the brain that called it (RunWhileActive): the
            // brain ends canceled, right after it destroyed its enemy.
            Assert.That(brain.Status, Is.EqualTo(FlowStatus.Canceled));
            Assert.That(brain.CancelCause, Is.EqualTo(CancelCause.Explicit));
        }

        [UnityTest]
        public IEnumerator EnemySample_PausingTheGameClockFreezesItAndKeepsTheHit()
        {
            var enemy = Spawn(SampleHost.Create("Enemy", Ending.Destroy), Clocks);
            yield return WaitUntil(() => enemy.transform.position.x > 0.1f, what: "the patrol to move");

            var pause = Clocks.Game.Pause(); // outside flow code: no owner, disposed below
            try
            {
                yield return null;
                var x = enemy.transform.position.x;
                enemy.TakeHit(1);
                yield return Frames(5);
                Assert.That(enemy.transform.position.x, Is.EqualTo(x), "no patrol step while paused");
                Assert.That(enemy.Flinches, Is.Zero, "the hit waits for the pause to end");
            }
            finally
            {
                pause.Dispose(); // the clocks are shared: never leave them paused
            }

            yield return WaitUntil(() => enemy.Flinches == 1, what: "the hit kept during the pause");
        }

        [UnityTest]
        public IEnumerator EnemySample_StopsWithTheEnemy([Values] Ending ending)
        {
            var host = SampleHost.Create("Enemy", ending);
            var enemy = Spawn(host, Clocks);
            yield return WaitUntil(() => enemy.IsPatrolling, what: "the patrol");
            var brain = enemy.Brain;

            yield return host.End();
            Assert.That(brain.Status, Is.EqualTo(FlowStatus.Canceled));
            Assert.That(enemy == null, Is.EqualTo(ending != Ending.Deactivate));
            Assert.That(enemy.IsPatrolling, Is.False, "the patrol's finally ran");
            enemy.Damaged.Emit(1); // a late hit reaches nobody
        }

        [UnityTest]
        public IEnumerator EnemySample_ReactivatingTheEnemyDoesNotStartItsBrainAgain()
        {
            var host = SampleHost.Create("Enemy", Ending.Deactivate);
            var enemy = Spawn(host, Clocks);
            yield return WaitUntil(() => enemy.IsPatrolling, what: "the patrol");
            var brain = enemy.Brain;

            host.GameObject.SetActive(false);
            Assert.That(brain.Status, Is.EqualTo(FlowStatus.Canceled));
            var x = enemy.transform.position.x;
            host.GameObject.SetActive(true);
            yield return Frames(5);
            Assert.That(enemy.Brain.Status, Is.EqualTo(FlowStatus.Canceled), "Start runs once: no new brain");
            Assert.That(enemy.IsPatrolling, Is.False);
            Assert.That(enemy.transform.position.x, Is.EqualTo(x));
        }

        [UnityTest]
        public IEnumerator EnemySample_AnEnemyNeverActivatedNeverStartsItsBrain()
        {
            var host = SampleHost.Create("PooledEnemy", Ending.Destroy);
            host.GameObject.SetActive(false); // spawned inactive, as by a pool, and never activated
            var enemy = Spawn(host, Clocks);
            yield return Frames(2);
            Assert.That(enemy.Hp, Is.Null, "Unity calls no Start (nor Awake or OnEnable) on an inactive object, so the brain never started");
            Assert.That(enemy.IsPatrolling, Is.False);

            yield return host.End();
            yield return null;
            Assert.That(enemy == null, Is.True);
            Assert.That(enemy.Hp, Is.Null, "destroyed before it ever ran: nothing to unwind");
        }
    }
}
