using System.Text;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Katout.FlowTask.Unity;

/// <summary>
/// The Unity integration: the default <see cref="Katout.FlowTask.FlowWorld"/> (<see cref="World"/>), driven from Unity's
/// PlayerLoop, and its settings.
/// <para>
/// On startup (<c>RuntimeInitializeLoadType.SubsystemRegistration</c>, i.e. before any Awake) a World is created and
/// systems are inserted into the PlayerLoop: one Tick per frame (default: start of Update, before
/// <c>MonoBehaviour.Update</c>) plus <c>FlowWorld.Flush()</c> at the configured flush points (default: end of Update and
/// end of PreLateUpdate). See <see cref="FlowTaskSettings"/> and https://katout.github.io/FlowTask/en/unity/setup/.
/// </para>
/// <para>
/// Leaving Play Mode (or quitting the player) disposes the World, which unwinds every flow, and removes the systems.
/// Entering Play Mode with domain reload disabled starts from a fresh World as well. Scripts recompiled during Play
/// Mode drop the World (flows that no OnDisable cancels, as RunWhileActive does, do not unwind) and FlowTask stays off
/// until Play Mode restarts or <see cref="Initialize"/> is called; the Editor warns
/// (https://katout.github.io/FlowTask/en/unity/setup/).
/// </para>
/// </summary>
public static class FlowTaskUnity
{
    /// <summary>PlayerLoop marker type of the Tick system (visible in the Profiler).</summary>
    public struct FlowTaskTick
    {
    }

    public struct FlowTaskFlushAfterEarlyUpdate
    {
    }

    public struct FlowTaskFlushAfterFixedUpdate
    {
    }

    public struct FlowTaskFlushAfterUpdate
    {
    }

    public struct FlowTaskFlushAfterLateUpdate
    {
    }

    public struct FlowTaskFlushEndOfFrame
    {
    }

    static readonly Type[] s_markerTypes =
    {
        typeof(FlowTaskTick),
        typeof(FlowTaskFlushAfterEarlyUpdate),
        typeof(FlowTaskFlushAfterFixedUpdate),
        typeof(FlowTaskFlushAfterUpdate),
        typeof(FlowTaskFlushAfterLateUpdate),
        typeof(FlowTaskFlushEndOfFrame),
    };

    static readonly PlayerLoopSystem.UpdateFunction s_tick = RunTick;
    static readonly PlayerLoopSystem.UpdateFunction s_flush = RunFlush;
    static readonly Action<FlowExceptionInfo> s_exceptionHandler = HandleUnhandledException;
    static readonly Action<FlowWarning> s_warningHandler = HandleWarning;

    static FlowWorld s_world;
    static bool s_ownsWorld;
    // Initialize, Configure or Shutdown was called by game code since startup: the settings asset no longer applies.
    static bool s_setFromCode;
    static bool s_installed;
    static FlowTaskSettings s_settings = new();
    static long s_ticks;
    static long s_flushes;

    /// <summary>
    /// The default World, driven by the PlayerLoop. When there is none (in Edit Mode, after <see cref="Shutdown"/>, after
    /// the scripts were recompiled during Play Mode), throws <see cref="InvalidOperationException"/> naming the reason;
    /// <see cref="IsInstalled"/> tells whether there is one.
    /// </summary>
    public static FlowWorld World => s_world ?? throw new InvalidOperationException(NoWorldMessage());

    /// <summary>The default World, or null (for the integration's own code, which works without one).</summary>
    internal static FlowWorld WorldOrNull => s_world;

    /// <summary>
    /// A copy of the settings in effect. Changing the returned object has no effect until passed to
    /// <see cref="Configure"/>.
    /// </summary>
    public static FlowTaskSettings Settings => s_settings.Clone();

    /// <summary>True while the default World exists and the integration runs it (then <see cref="World"/> does not throw).</summary>
    public static bool IsInstalled => s_installed;

    // Diagnostics for the integration tests (InternalsVisibleTo FlowTask.Unity.Tests).
    internal static long TickCount => s_ticks;
    internal static long FlushCount => s_flushes;

    // ------------------------------------------------------------------ startup / shutdown

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    internal static void OnSubsystemRegistration()
    {
        // Runs on every player start and every Play Mode entry, including with domain reload disabled, where static
        // state from the previous session survives: start clean.
#if UNITY_EDITOR
        SessionState.EraseBool(ScriptReloadKey);
#endif
        ShutdownCore();
        s_settings = new FlowTaskSettings();
        s_setFromCode = false;
        FlowLifetime.ResetStatics();
        FlowWorldRegistry.ResetStatics();
        RegisterShutdownHooks();
#if !FLOWTASK_DISABLE_AUTO_INSTALL
        InitializeCore(s_settings, null);
#endif
    }

    /// <summary>
    /// Applies the settings asset to the World created at startup. Skipped when FLOWTASK_DISABLE_AUTO_INSTALL is defined
    /// (no World was created) and when game code has already called Initialize, Configure or Shutdown (from an earlier
    /// RuntimeInitializeOnLoadMethod): what code set wins over the asset, and a World it installed stays.
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void OnBeforeSceneLoad()
    {
        if (s_setFromCode) return;
#if !FLOWTASK_DISABLE_AUTO_INSTALL
        var asset = Resources.Load<FlowTaskSettingsAsset>(FlowTaskSettingsAsset.ResourceName);
        if (asset != null) ApplySettingsAsset(asset.Settings);
#endif
    }

    /// <summary>The part of <see cref="OnBeforeSceneLoad"/> after the asset was loaded (internal for the tests).</summary>
    internal static void ApplySettingsAsset(FlowTaskSettings assetSettings)
    {
        if (s_setFromCode) return;
        var settings = assetSettings.Clone();
        if (!settings.AutoInstall)
        {
            ShutdownCore();
            return;
        }

        if (s_world == null) InitializeCore(settings, null);
        else ConfigureCore(settings);
    }

    /// <summary>
    /// Creates the default World (or adopts <paramref name="world"/>) and installs the PlayerLoop systems. Called
    /// automatically at startup; call it yourself only after <see cref="Shutdown"/> or with
    /// <c>FLOWTASK_DISABLE_AUTO_INSTALL</c> defined. An adopted World is not disposed by <see cref="Shutdown"/>, and
    /// keeps its own <see cref="FlowWorld.OnUnhandledException"/> if it has one (then
    /// <see cref="FlowTaskSettings.OnUnhandledException"/> and the default logging do not apply to it).
    /// </summary>
    public static FlowWorld Initialize(FlowTaskSettings settings = null, FlowWorld world = null)
    {
        s_setFromCode = true;
        return InitializeCore(settings, world);
    }

    static FlowWorld InitializeCore(FlowTaskSettings settings, FlowWorld world)
    {
        if (s_world != null) ShutdownCore();
#if UNITY_EDITOR
        // Started again after a reload during Play Mode dropped the World: a later Shutdown is not that reload.
        SessionState.EraseBool(ScriptReloadKey);
#endif
        s_settings = (settings ?? new FlowTaskSettings()).Clone();
        s_ownsWorld = world == null;
        s_world = world ?? new FlowWorld("Unity");
        s_ticks = 0;
        s_flushes = 0;
        // A World adopted with a handler of its own keeps it; Shutdown removes only the handler installed here.
        s_world.OnUnhandledException ??= s_exceptionHandler;
        s_world.OnWarning -= s_warningHandler;
        s_world.OnWarning += s_warningHandler;
        FlowWorldRegistry.Register(s_world);
        InstallSystems();
        RegisterShutdownHooks();
        return s_world;
    }

    /// <summary>
    /// Applies new settings: the OnUnhandledException handler applies immediately; a changed AutoTick / flush point set
    /// replaces the PlayerLoop systems, which Unity picks up from the next frame (the frame in progress keeps running
    /// the old loop).
    /// </summary>
    public static void Configure(FlowTaskSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        s_setFromCode = true;
        ConfigureCore(settings);
    }

    static void ConfigureCore(FlowTaskSettings settings)
    {
        s_settings = settings.Clone();
        if (s_world == null) return;
        InstallSystems();
    }

    /// <summary>
    /// Removes the PlayerLoop systems and disposes the default World (unwinding every flow: using/finally/AddCleanup run).
    /// Called automatically when leaving Play Mode and when the player quits. Safe to call repeatedly.
    /// </summary>
    public static void Shutdown()
    {
        s_setFromCode = true;
        ShutdownCore();
    }

    static void ShutdownCore()
    {
        UninstallSystems();
        var w = s_world;
        s_world = null;
        if (w == null) return;
        FlowWorldRegistry.Unregister(w);
        if (s_ownsWorld && !w.IsDisposed)
        {
            try
            {
                w.Dispose();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        w.OnWarning -= s_warningHandler;
        if (ReferenceEquals(w.OnUnhandledException, s_exceptionHandler)) w.OnUnhandledException = null;
    }

    static void RegisterShutdownHooks()
    {
        Application.quitting -= OnQuitting;
        Application.quitting += OnQuitting;
#if UNITY_EDITOR
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
#endif
    }

    static void OnQuitting() => ShutdownCore();

#if UNITY_EDITOR
    static void OnPlayModeStateChanged(PlayModeStateChange change)
    {
        // ExitingPlayMode comes before scene objects are destroyed, so flow cleanup code can still use them.
        if (change == PlayModeStateChange.ExitingPlayMode) ShutdownCore();
    }

    // ------------------------------------------------------------------ scripts recompiled during Play Mode

    // Set when a domain reload drops the World during Play Mode ("Script Changes While Playing" = "Recompile And
    // Continue Playing"). SessionState survives the reload and is erased when the Editor closes, Play Mode is entered
    // again (OnSubsystemRegistration) or the World is created again (Initialize).
    internal const string ScriptReloadKey = "com.katout.flowtask.WorldLostToScriptReload";

    // Logged before Unity calls OnDisable on every behaviour for the reload (checked in 6000.3.7f1): flows that
    // OnDisable cancels and flushes still unwind in the old World, the others are dropped.
    internal const string ScriptReloadWarning =
        "[FlowTask] Scripts are being recompiled during Play Mode. The FlowTask World is dropped with the flows still " +
        "running after OnDisable, without unwinding them (their finally / AddCleanup do not run), and FlowTask stays off " +
        "until Play Mode is restarted (or FlowTaskUnity.Initialize() is called). Set Preferences > General > Script " +
        "Changes While Playing to \"Recompile After Finished Playing\" (or \"Stop Playing and Recompile\").";

    // Subscriptions to editor events live in the domain: subscribe again after every domain reload.
    [InitializeOnLoadMethod]
    static void OnEditorDomainLoaded()
    {
        AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
        AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
    }

    /// <summary>
    /// A reload during Play Mode clears the static World without running any cleanup, and the startup hook
    /// (SubsystemRegistration) does not run again until Play Mode restarts. The integration does not recover (the
    /// scripts' Awake / Start are not called again either; OnEnable is, and RunWhileActive there throws "No FlowTask
    /// World"); it warns and records the cause for <see cref="NoWorldReason"/>. Once per lost
    /// World: after the reload there is none, until <see cref="Initialize"/> creates one again.
    /// </summary>
    internal static void OnBeforeAssemblyReload()
    {
        if (!EditorApplication.isPlaying || s_world == null) return;
        SessionState.SetBool(ScriptReloadKey, true);
        Debug.LogWarning(ScriptReloadWarning);
    }
#endif

    /// <summary>The message of the exception <see cref="World"/> throws when there is no default World.</summary>
    internal static string NoWorldMessage() => "No FlowTask World: the PlayerLoop integration is not running. " + NoWorldReason();

    /// <summary>Why there is no default World, for <see cref="NoWorldMessage"/> and the Scope Tree window.</summary>
    internal static string NoWorldReason()
    {
#if UNITY_EDITOR
        if (!EditorApplication.isPlaying)
            return "It runs only in Play Mode; in Edit Mode, use a FlowWorld of your own (RunWhileActive takes one of its clocks).";
        if (SessionState.GetBool(ScriptReloadKey, false))
            return "The scripts were recompiled during this Play Mode session, which dropped the World (it is not recreated " +
                   "on its own). Restart Play Mode, or call FlowTaskUnity.Initialize() to start a new World (the dropped flows " +
                   "do not come back), and set Preferences > General > Script Changes While Playing to " +
                   "\"Recompile After Finished Playing\".";
#endif
        return "It was shut down (FlowTaskUnity.Shutdown, or the application is quitting) or not installed " +
               "(AutoInstall = false or FLOWTASK_DISABLE_AUTO_INSTALL); call FlowTaskUnity.Initialize() to start it.";
    }

    // ------------------------------------------------------------------ report / warning routing

    static void HandleUnhandledException(FlowExceptionInfo info)
    {
        var custom = s_settings.OnUnhandledException;
        if (custom != null)
        {
            custom(info);
            return;
        }

        // An undelivered exception had no receiver left (a canceled or settled one): it happens in normal play, so it is a
        // warning. Every other kind is an error.
        if (info.Kind == FlowExceptionKind.Undelivered) Debug.LogWarning(UndeliveredMessage(info));
        else Debug.LogException(new FlowTaskUnhandledException(info));
    }

    internal static string UndeliveredMessage(FlowExceptionInfo info)
    {
        var path = string.IsNullOrEmpty(info.ScopePath) ? "<root>" : info.ScopePath;
        return $"[FlowTask] Undelivered at '{path}' (no receiver could take it; it changed no result): {info.Exception}";
    }

    static void HandleWarning(FlowWarning warning)
    {
        Debug.LogWarning("[FlowTask] " + warning);
    }

    // ------------------------------------------------------------------ PlayerLoop systems

    static void RunTick()
    {
        var w = s_world;
        if (w == null || w.IsDisposed) return;
        FlowLifetime.PollWatched();
        // DefaultClock advances by Time.deltaTime, UnscaledClock by the (clamped) unscaled delta.
        var dt = DeltaTime();
        try
        {
            var scale = DefaultClockScale(dt, Time.deltaTime, Time.timeScale);
            if (w.DefaultClock.TimeScale != scale) w.DefaultClock.TimeScale = scale;
        }
        catch (ArgumentOutOfRangeException ex)
        {
            // The setter rejects a scale whose product with a descendant clock's scale is not finite. The
            // clock keeps its previous scale and the Tick below still runs, so the World does not stop.
            Debug.LogException(ex);
        }
        try
        {
            w.Tick(dt);
            s_ticks++;
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
        }
    }

    static void RunFlush()
    {
        var w = s_world;
        if (w == null || w.IsDisposed) return;
        try
        {
            w.Flush();
            s_flushes++;
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
        }
    }

    /// <summary>Unscaled delta (clamped): what UnscaledClock advances by. DefaultClock scales it with <see cref="DefaultClockScale"/>.</summary>
    static double DeltaTime()
    {
        double dt = Math.Min(Time.unscaledDeltaTime, Time.maximumDeltaTime);
        return dt > 0 && !double.IsInfinity(dt) ? dt : 0;
    }

    // Float rounding of Time.deltaTime is relative to its value (a few 1e-8 of it): below 1e-7 s for deltas under 1 s,
    // but up to 2e-6 s at timeScale 100 on a frame clamped to maximumDeltaTime. The tolerance is 1e-6 s for deltas up to
    // 1 s and 1e-6 of the delta above that: well above the rounding at any timeScale, well below any real mismatch.
    const double DeltaTolerance = 1e-6;

    /// <summary>
    /// The DefaultClock time scale for a Tick of <paramref name="dt"/> that makes DefaultClock advance by exactly
    /// <paramref name="unityDelta"/> (Time.deltaTime). Usually Time.timeScale. Unity's deltaTime is not dt x timeScale
    /// on the provisional first frames of a session (0.02 s on frames 1 and 2 in 6000.3), on a frame where timeScale
    /// was changed after Unity fixed that frame's deltaTime and before this Tick (in FixedUpdate, EarlyUpdate, PreUpdate
    /// or a UniTask continuation at PlayerLoopTiming.Update, whose runner comes first in Update), and on every frame
    /// while Time.captureDeltaTime is set (deltaTime is then captureDeltaTime x timeScale, whatever the frame took).
    /// There the ratio is used, so DefaultClock.TimeScale reads differently from Time.timeScale on those frames: for
    /// that frame only, or for as long as the capture lasts. Returning timeScale when the values agree keeps the
    /// DefaultClock.TimeScale write (and the recomputation of every clock's scale) off the steady state.
    /// </summary>
    internal static double DefaultClockScale(double dt, double unityDelta, double timeScale) =>
        dt > 0 && Math.Abs(unityDelta - dt * timeScale) > DeltaTolerance * Math.Max(1.0, unityDelta) ? unityDelta / dt : timeScale;

    static void InstallSystems()
    {
        var loop = PlayerLoop.GetCurrentPlayerLoop();
        RemoveFlowTaskSystems(ref loop);
        var s = s_settings;
        if (s.AutoTick)
            Insert(ref loop, typeof(Update), typeof(Update.ScriptRunBehaviourUpdate), MakeSystem(typeof(FlowTaskTick), s_tick), atEnd: false);

        var f = s.FlushPoints;
        if ((f & FlowFlushPoints.AfterEarlyUpdate) != 0)
            Insert(ref loop, typeof(EarlyUpdate), null, MakeSystem(typeof(FlowTaskFlushAfterEarlyUpdate), s_flush), atEnd: true);
        if ((f & FlowFlushPoints.AfterFixedUpdate) != 0)
            Insert(ref loop, typeof(FixedUpdate), null, MakeSystem(typeof(FlowTaskFlushAfterFixedUpdate), s_flush), atEnd: true);
        if ((f & FlowFlushPoints.AfterUpdate) != 0)
            Insert(ref loop, typeof(Update), null, MakeSystem(typeof(FlowTaskFlushAfterUpdate), s_flush), atEnd: true);
        if ((f & FlowFlushPoints.AfterLateUpdate) != 0)
            Insert(ref loop, typeof(PreLateUpdate), null, MakeSystem(typeof(FlowTaskFlushAfterLateUpdate), s_flush), atEnd: true);
        if ((f & FlowFlushPoints.EndOfFrame) != 0)
            Insert(ref loop, typeof(PostLateUpdate), null, MakeSystem(typeof(FlowTaskFlushEndOfFrame), s_flush), atEnd: true);

        PlayerLoop.SetPlayerLoop(loop);
        s_installed = true;
    }

    static void UninstallSystems()
    {
        var loop = PlayerLoop.GetCurrentPlayerLoop();
        if (RemoveFlowTaskSystems(ref loop)) PlayerLoop.SetPlayerLoop(loop);
        s_installed = false;
    }

    static PlayerLoopSystem MakeSystem(Type marker, PlayerLoopSystem.UpdateFunction update) =>
        new() { type = marker, updateDelegate = update };

    /// <summary>Inserts <paramref name="system"/> into phase <paramref name="phase"/>, before <paramref name="before"/> or at an end.</summary>
    static void Insert(ref PlayerLoopSystem root, Type phase, Type before, PlayerLoopSystem system, bool atEnd)
    {
        var phases = root.subSystemList;
        if (phases == null) return;
        for (var i = 0; i < phases.Length; i++)
        {
            if (phases[i].type != phase) continue;
            var list = new List<PlayerLoopSystem>(phases[i].subSystemList ?? Array.Empty<PlayerLoopSystem>());
            var index = atEnd ? list.Count : 0;
            if (before != null)
            {
                for (var j = 0; j < list.Count; j++)
                {
                    if (list[j].type != before) continue;
                    index = j;
                    break;
                }
            }

            list.Insert(index, system);
            phases[i].subSystemList = list.ToArray();
            return;
        }

        Debug.LogWarning($"[FlowTask] PlayerLoop phase {phase.Name} not found; {system.type.Name} was not installed.");
    }

    static bool RemoveFlowTaskSystems(ref PlayerLoopSystem system)
    {
        var subs = system.subSystemList;
        if (subs == null) return false;
        var removed = false;
        List<PlayerLoopSystem> kept = null;
        for (var i = 0; i < subs.Length; i++)
        {
            var child = subs[i];
            if (IsFlowTaskMarker(child.type))
            {
                if (kept == null)
                {
                    kept = new List<PlayerLoopSystem>(subs.Length);
                    for (var j = 0; j < i; j++) kept.Add(subs[j]);
                }

                removed = true;
                continue;
            }

            if (RemoveFlowTaskSystems(ref child))
            {
                removed = true;
                subs[i] = child;
            }

            kept?.Add(child);
        }

        if (kept != null) system.subSystemList = kept.ToArray();
        return removed;
    }

    static bool IsFlowTaskMarker(Type type)
    {
        if (type == null) return false;
        for (var i = 0; i < s_markerTypes.Length; i++)
        {
            if (s_markerTypes[i] == type) return true;
        }

        return false;
    }

    /// <summary>Human-readable list of the installed FlowTask systems with their phase and neighbours (for tests).</summary>
    internal static string DescribeInstalledSystems()
    {
        var loop = PlayerLoop.GetCurrentPlayerLoop();
        var sb = new StringBuilder();
        if (loop.subSystemList == null) return "";
        foreach (var phase in loop.subSystemList)
        {
            var subs = phase.subSystemList;
            if (subs == null) continue;
            for (var i = 0; i < subs.Length; i++)
            {
                if (!IsFlowTaskMarker(subs[i].type)) continue;
                var prev = i > 0 ? subs[i - 1].type?.Name : "<start>";
                var next = i + 1 < subs.Length ? subs[i + 1].type?.Name : "<end>";
                sb.Append(phase.type?.Name).Append(": ").Append(subs[i].type.Name)
                    .Append(" (after ").Append(prev).Append(", before ").Append(next).Append(")\n");
            }
        }

        return sb.ToString();
    }
}
