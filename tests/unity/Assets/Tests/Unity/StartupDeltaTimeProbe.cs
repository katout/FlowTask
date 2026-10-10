// A block namespace, not a file-scoped one: Unity finds the class of a MonoBehaviour or ScriptableObject by parsing
// its file, and does not see it in a file-scoped namespace (CONTRIBUTING.md).
namespace Katout.FlowTask.Unity.Tests
{
    /// <summary>
    /// Records the first frames of the Play Mode session or player (created before the first scene loads), when Unity
    /// reports a provisional Time.deltaTime (0.02 s on frames 1 and 2 in 6000.3.7f1), then destroys itself.
    /// </summary>
    public sealed class StartupDeltaTimeProbe : MonoBehaviour
    {
        public const int Frames = 5;
        public static readonly List<DeltaTimeSample> Samples = new List<DeltaTimeSample>(Frames);
        public static bool Done;
        int _frames;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Install()
        {
            // Static state survives with domain reload disabled: start clean on every Play Mode entry.
            Samples.Clear();
            Done = false;
            var go = new GameObject(nameof(StartupDeltaTimeProbe));
            DontDestroyOnLoad(go);
            go.AddComponent<StartupDeltaTimeProbe>();
        }

        void Update()
        {
            if (FlowTaskUnity.IsInstalled)
            {
                var w = FlowTaskUnity.World;
                Samples.Add(new DeltaTimeSample { Frame = Time.frameCount, UnityDelta = Time.deltaTime, ClockDelta = w.DefaultClock.DeltaTime, TimeScale = Time.timeScale });
            }
            if (++_frames < Frames) return;
            Done = true;
            Destroy(gameObject);
        }
    }
}
