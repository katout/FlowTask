using System.Threading;
using System.Threading.Tasks;

namespace Katout.FlowTask.Samples;

/// <summary>Where <see cref="GameSettings"/> are written (a file, a cloud save), as Task-based code sees it.</summary>
public interface ISettingsStore
{
    /// <summary>
    /// Writes the values and completes once they are written. A write that fails (a full disk) is the store's to
    /// handle: keep the previous file and try again until <paramref name="ct"/> is canceled, which happens when the
    /// save is given up (its time limit, the World disposed). A fault is a bug.
    /// </summary>
    Task SaveAsync(float volume, CancellationToken ct);
}

/// <summary>
/// Sample: the player's settings. The settings screen changes them and saves them when it closes, however it closes
/// (<see cref="PauseMenu.Settings"/>), in its finally block. Settings whose save was given up stay
/// <see cref="IsDirty"/> and are saved at the next close. It uses no engine API.
/// </summary>
public sealed class GameSettings
{
    readonly ISettingsStore _store;
    float _volume = 1;

    public GameSettings(ISettingsStore store) => _store = store;

    /// <summary>The master volume, set by the settings screen.</summary>
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = value;
            IsDirty = true;
        }
    }

    /// <summary>True while a change has not been written.</summary>
    public bool IsDirty { get; private set; }

    /// <summary>
    /// Writes the current values through the store and completes once they are written. The store's token is canceled
    /// when this flow is (bridged with FlowBridge.FromTask).
    /// </summary>
    public FlowTask Save()
    {
        var volume = _volume;
        return FlowBridge.FromTask(ct => _store.SaveAsync(volume, ct));
    }

    /// <summary>Records that the values are written: call it only when <see cref="Save"/> completed.</summary>
    public void MarkClean() => IsDirty = false;
}
