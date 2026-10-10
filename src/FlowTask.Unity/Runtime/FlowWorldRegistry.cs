using System.Collections.ObjectModel;

namespace Katout.FlowTask.Unity;

/// <summary>
/// Worlds shown by tooling (the Scope Tree window). The PlayerLoop's default World registers itself; register
/// additional Worlds you create to inspect them too. Disposed Worlds are dropped automatically. Main thread only.
/// </summary>
public static class FlowWorldRegistry
{
    static readonly List<FlowWorld> s_worlds = new();
    static readonly ReadOnlyCollection<FlowWorld> s_view = s_worlds.AsReadOnly();

    public static void Register(FlowWorld world)
    {
        if (world == null || s_worlds.Contains(world)) return;
        s_worlds.Add(world);
    }

    public static void Unregister(FlowWorld world) => s_worlds.Remove(world);

    /// <summary>Live registered Worlds, default World first.</summary>
    public static IReadOnlyList<FlowWorld> Worlds
    {
        get
        {
            s_worlds.RemoveAll(w => w == null || w.IsDisposed);
            var d = FlowTaskUnity.WorldOrNull;
            if (d != null)
            {
                var i = s_worlds.IndexOf(d);
                if (i > 0)
                {
                    s_worlds.RemoveAt(i);
                    s_worlds.Insert(0, d);
                }
            }

            return s_view;
        }
    }

    // Entering Play Mode with domain reload disabled keeps static state: the startup hook of FlowTaskUnity
    // forgets the Worlds of the previous session.
    internal static void ResetStatics() => s_worlds.Clear();
}
