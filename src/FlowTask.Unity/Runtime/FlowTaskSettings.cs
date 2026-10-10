namespace Katout.FlowTask.Unity;

/// <summary>
/// Extra <c>FlowWorld.Flush()</c> points. A flush runs the resumes that game code reserved since the last
/// Tick/Flush (signal emits from MonoBehaviours, UnityEvents, FlowProperty.Set, completed bridges) without advancing time.
/// An empty flush costs a few field reads.
/// </summary>
[Flags]
public enum FlowFlushPoints
{
    None = 0,

    /// <summary>End of EarlyUpdate (after input and platform events were processed).</summary>
    AfterEarlyUpdate = 1 << 0,

    /// <summary>End of every FixedUpdate step (after <c>MonoBehaviour.FixedUpdate</c> and physics callbacks).</summary>
    AfterFixedUpdate = 1 << 1,

    /// <summary>
    /// End of the Update phase: after <c>MonoBehaviour.Update</c> (including uGUI's EventSystem, i.e. button
    /// clicks), coroutines and <c>SynchronizationContext</c>/Awaitable continuations.
    /// </summary>
    AfterUpdate = 1 << 2,

    /// <summary>End of PreLateUpdate: after <c>MonoBehaviour.LateUpdate</c>, animation events and Director updates.</summary>
    AfterLateUpdate = 1 << 3,

    /// <summary>End of PostLateUpdate (after rendering). Rarely useful: the next Tick comes right after it.</summary>
    EndOfFrame = 1 << 4,

    /// <summary>
    /// The default: <see cref="AfterUpdate"/> | <see cref="AfterLateUpdate"/>. Together with the Tick at
    /// the start of Update, anything emitted from Update or LateUpdate code is handled before the frame renders.
    /// </summary>
    Default = AfterUpdate | AfterLateUpdate,

    All = AfterEarlyUpdate | AfterFixedUpdate | AfterUpdate | AfterLateUpdate | EndOfFrame,
}

/// <summary>
/// PlayerLoop integration settings for the default World. Apply with <see cref="FlowTaskUnity.Configure"/>, or
/// create a <see cref="FlowTaskSettingsAsset"/> named "FlowTaskSettings" in a Resources folder.
/// <para>
/// Time is not configurable: the World is ticked with <c>Time.unscaledDeltaTime</c> (clamped to
/// <c>Time.maximumDeltaTime</c>), so UnscaledClock keeps unscaled time, and <c>FlowWorld.DefaultClock.TimeScale</c>
/// is set every frame so that DefaultClock advances by exactly <c>Time.deltaTime</c>. It equals <c>Time.timeScale</c>
/// except on frames where Unity's deltaTime is not the unscaled delta times timeScale (the first frames of a session, a
/// timeScale changed before the Tick, e.g. in FixedUpdate, and every frame while <c>Time.captureDeltaTime</c> is set to
/// record at a constant frame rate); see https://katout.github.io/FlowTask/en/unity/setup/. While capturing,
/// DefaultClock follows the recorded time, but UnscaledClock and its child clocks run on real time, like
/// <c>Time.unscaledTime</c>. Flows that must run at the physics rate get a World of their own, ticked from a FixedUpdate you own (one World per drive source).
/// </para>
/// </summary>
[Serializable]
public sealed class FlowTaskSettings
{
    [SerializeField, Tooltip("Tick the default World at the start of Update every frame. Off: call FlowTaskUnity.World.Tick(dt) yourself.")]
    bool _autoTick = true;

    [SerializeField, Tooltip("Additional FlowWorld.Flush() points in the frame.")]
    FlowFlushPoints _flushPoints = FlowFlushPoints.Default;

    [SerializeField, Tooltip("Create the default World and install the PlayerLoop systems automatically on startup.")]
    bool _autoInstall = true;

    /// <summary>Tick at the start of Update (before <c>MonoBehaviour.Update</c>); false means you call Tick yourself.</summary>
    public bool AutoTick
    {
        get => _autoTick;
        set => _autoTick = value;
    }

    public FlowFlushPoints FlushPoints
    {
        get => _flushPoints;
        set => _flushPoints = value;
    }

    /// <summary>Only read from a <see cref="FlowTaskSettingsAsset"/> at startup.</summary>
    public bool AutoInstall
    {
        get => _autoInstall;
        set => _autoInstall = value;
    }

    /// <summary>
    /// Overrides the World's OnUnhandledException handler (code only). When null, the reports are logged with
    /// <c>Debug.LogException</c> as a <see cref="FlowTaskUnhandledException"/> that carries the scope path and wraps
    /// the original exception, except <see cref="FlowExceptionKind.Undelivered"/> (an exception no receiver could
    /// take), which is logged with <c>Debug.LogWarning</c>. A handler replaces both; switch on
    /// <see cref="FlowExceptionInfo.Kind"/> to treat the kinds differently.
    /// </summary>
    public Action<FlowExceptionInfo> OnUnhandledException { get; set; }

    internal FlowTaskSettings Clone() => (FlowTaskSettings)MemberwiseClone();

    public override string ToString() => $"FlowTaskSettings(autoTick={_autoTick}, flush={_flushPoints})";
}
