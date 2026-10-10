namespace Katout.FlowTask.Samples
{
    /// <summary>
    /// Sample: the clocks of a game session. Gameplay (enemies, timers) runs on <see cref="Game"/>, menus and dialogs on
    /// <see cref="Ui"/>, so pausing the game (<c>using var pause = clocks.Game.Pause();</c>) leaves the UI running. Make one
    /// per World (session) and pass it to the flows: a clock made with World.CreateClock stays in its World until the World
    /// is disposed, so do not make them per screen (or per test).
    /// </summary>
    public sealed class GameClocks
    {
        public GameClocks(FlowWorld world)
        {
            // A child of DefaultClock: follows Time.timeScale, slow motion included, and stops at 0 with it.
            Game = world.CreateClock("Game");
            // A child of UnscaledClock: advances with unscaled time, so the UI keeps moving at a time scale of 0 and while
            // Game is paused.
            Ui = world.CreateClock("UI", world.UnscaledClock);
        }

        GameClocks(Clock game, Clock ui)
        {
            Game = game;
            Ui = ui;
        }

        /// <summary>
        /// Clocks that the current scope owns (<see cref="Flow.CreateClock(string, Clock)"/>): they leave the World when
        /// that scope ends. For a session that is itself a flow and may start again in the same World, such as the demo,
        /// whose scene can be loaded again. Call it from flow code.
        /// </summary>
        public static GameClocks ForCurrentScope()
        {
            var world = FlowWorld.Current ?? throw new System.InvalidOperationException("GameClocks.ForCurrentScope is called from flow code.");
            return new GameClocks(Flow.CreateClock("Game", world.DefaultClock), Flow.CreateClock("UI", world.UnscaledClock));
        }

        public Clock Game { get; }
        public Clock Ui { get; }
    }
}
