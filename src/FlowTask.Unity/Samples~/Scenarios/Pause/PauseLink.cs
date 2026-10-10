using UnityEngine;

namespace Katout.FlowTask.Samples
{
    /// <summary>
    /// Sample: stops Unity's own systems while a <see cref="Clock"/> is paused. <c>clocks.Game.Pause()</c> stops only the
    /// flows on Game; Animators, physics, <c>Time.deltaTime</c> and coroutines that wait with WaitForSeconds go on. This
    /// sets <c>Time.timeScale</c> to 0 while Game is paused and gives the previous value back after, so they stop with
    /// it (Update and a coroutine that yields null still run). The clock's pauses are counted
    /// and the time scale is a single value, so this component is the only one that writes it for pausing: screens take
    /// only <c>clocks.Game.Pause()</c> (<see cref="PauseMenu"/>), and the game resumes when the last pause is released.
    /// <para>
    /// At a time scale of 0, DefaultClock stops too, with every clock under it: what runs during the pause (the menus)
    /// runs on the UI clock, under UnscaledClock. Put it on an object that stays active for the whole session (the one with
    /// <see cref="BackKeyInput"/>, say). Disabled or destroyed, it gives the time scale back.
    /// </para>
    /// </summary>
    public sealed class PauseLink : MonoBehaviour
    {
        float _timeScaleBeforePause;
        bool _paused;

        /// <summary>The clock Unity follows: the session's <see cref="GameClocks.Game"/>.</summary>
        public Clock Clock { get; set; }

        void Update()
        {
            var paused = Clock != null && Clock.IsPausedInHierarchy;
            if (paused == _paused) return;
            _paused = paused;
            if (paused)
            {
                _timeScaleBeforePause = Time.timeScale;
                Time.timeScale = 0;
            }
            else
            {
                Time.timeScale = _timeScaleBeforePause;
            }
        }

        // Called before OnDestroy as well, when an enabled link is destroyed.
        void OnDisable()
        {
            if (!_paused) return;
            _paused = false;
            Time.timeScale = _timeScaleBeforePause;
        }
    }
}
