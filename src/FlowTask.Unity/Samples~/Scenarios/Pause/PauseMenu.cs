using Katout.FlowTask.Unity;

namespace Katout.FlowTask.Samples
{
    /// <summary>
    /// Sample: pausing the game. A pause is <c>using var pause = clocks.Game.Pause();</c> held while the menu is
    /// open. Pauses are counted, so a screen opened on top (the settings) pauses too, and the game resumes when the last one
    /// is released. While Game is paused, its flows do not resume: a world event that arrives meanwhile (a hit on an
    /// enemy) waits in its subscription and is handled after the pause, while player input does not reach the game because
    /// the menu owns the back key and covers the screen. <see cref="PauseLink"/> stops Unity's own systems with it.
    /// The settings screen saves the <see cref="GameSettings"/> when it closes, in its finally block.
    /// </summary>
    public static class PauseMenu
    {
        /// <summary>The longest a save of the settings may take when the settings screen closes, in seconds of the UI clock.</summary>
        public const double SaveTimeoutSeconds = 2;

        /// <summary>The HUD: opens the menu on each press of the pause button. Presses while the menu is open are dropped.</summary>
        public static async FlowTask Hud(GameClocks clocks, PauseMenuView view, BackKeyRouter router, GameSettings settings)
        {
            using var pausePressed = view.PauseButton.ClickedSignal();
            while (true)
            {
                await pausePressed.Next();
                await Open(clocks, view, router, settings);
            }
        }

        /// <summary>
        /// Opens the menu until Resume or the back key. The menu runs on the UI clock: opened from a flow on the Game clock,
        /// it would otherwise stop under its own pause (warned: PausedOwnClock).
        /// </summary>
        public static FlowTask Open(GameClocks clocks, PauseMenuView view, BackKeyRouter router, GameSettings settings) =>
            Flow.WithClock(clocks.Ui, OpenCore(clocks, view, router, settings));

        static async FlowTask OpenCore(GameClocks clocks, PauseMenuView view, BackKeyRouter router, GameSettings settings)
        {
            using var pause = clocks.Game.Pause();
            using var resume = view.ResumeButton.ClickedSignal();
            using var settingsPressed = view.SettingsButton.ClickedSignal();
            view.IsMenuOpen = true;
            try
            {
                while (true)
                {
                    var r = await FlowTask.Race(resume.Next(), router.Next(BackKeyRouter.Dialog), settingsPressed.Next());
                    if (r.Index != 2) return;
                    await Settings(clocks, view, router, settings);
                }
            }
            finally
            {
                view.IsMenuOpen = false;
            }
        }

        /// <summary>
        /// The settings screen. It pauses too, since it is also opened where the game is not paused (title, HUD). It saves
        /// <paramref name="settings"/> when it closes, if they changed.
        /// </summary>
        public static FlowTask Settings(GameClocks clocks, PauseMenuView view, BackKeyRouter router, GameSettings settings) =>
            Flow.WithClock(clocks.Ui, SettingsCore(clocks, view, router, settings));

        static async FlowTask SettingsCore(GameClocks clocks, PauseMenuView view, BackKeyRouter router, GameSettings settings)
        {
            try
            {
                // The pause ends with the screen, before the save below: a slow save does not keep the game paused.
                using var pause = clocks.Game.Pause();
                using var close = view.SettingsCloseButton.ClickedSignal();
                view.IsSettingsOpen = true;
                // The screen's controls set settings.Volume and the like (the demo wires the volume slider).
                await FlowTask.Race(close.Next(), router.Next(BackKeyRouter.Dialog));
            }
            finally
            {
                view.IsSettingsOpen = false;
                // Saved on every exit, the HUD destroyed with its scene included, and this flow waits for the save.
                // Flow.NonCancelable lets a save the close button started finish too when the HUD is destroyed meanwhile.
                // A save given up (not ended within SaveTimeoutSeconds of the UI clock, which the game's pause does not
                // stop) leaves the settings dirty for the next close.
                if (settings.IsDirty)
                {
                    var saved = await Flow.NonCancelable(FlowTask.Race(settings.Save(), FlowTask.WaitForSeconds(SaveTimeoutSeconds)));
                    if (saved.Index == 0) settings.MarkClean();
                }
            }
        }
    }
}
