using System.Globalization;
using System.Text;

// A block namespace, not a file-scoped one: Unity finds the class of a MonoBehaviour or ScriptableObject by parsing
// its file, and does not see it in a file-scoped namespace (CONTRIBUTING.md).
namespace Katout.FlowTask.Unity.Tests
{
    /// <summary>One frame as seen from MonoBehaviour.Update, after the FlowTask Tick at the start of the Update phase.</summary>
    public struct DeltaTimeSample
    {
        public int Frame;
        public float UnityDelta;
        public double ClockDelta;
        public float TimeScale;
        public bool TimeScaleChangedInFixedUpdate;

        public bool Matches => System.Math.Abs(UnityDelta - ClockDelta) <= 1e-6;

        public override string ToString() => string.Format(CultureInfo.InvariantCulture,
            "frame {0}: Time.deltaTime={1:R} DefaultClock.DeltaTime={2:R} timeScale={3}{4}", Frame, UnityDelta, ClockDelta, TimeScale,
            TimeScaleChangedInFixedUpdate ? " (timeScale changed in FixedUpdate)" : "");

        public static string Describe(IEnumerable<DeltaTimeSample> samples)
        {
            var sb = new StringBuilder();
            foreach (var s in samples) sb.Append(s.Matches ? "  " : "! ").Append(s).Append('\n');
            return sb.ToString();
        }
    }

    /// <summary>
    /// Compares Time.deltaTime with the default World's DefaultClock.DeltaTime every frame. Setting
    /// <see cref="TimeScaleForNextFixedUpdate"/> changes Time.timeScale in the next FixedUpdate, i.e. after Unity fixed the
    /// frame's deltaTime and before the Tick.
    /// </summary>
    public sealed class DeltaTimeProbe : MonoBehaviour
    {
        public readonly List<DeltaTimeSample> Samples = new List<DeltaTimeSample>();
        public float? TimeScaleForNextFixedUpdate;
        public int FixedUpdateChanges;
        int _changedFrame = -1;

        void FixedUpdate()
        {
            if (!(TimeScaleForNextFixedUpdate is float scale)) return;
            TimeScaleForNextFixedUpdate = null;
            Time.timeScale = scale;
            _changedFrame = Time.frameCount;
            FixedUpdateChanges++;
        }

        void Update()
        {
            if (!FlowTaskUnity.IsInstalled) return;
            var w = FlowTaskUnity.World;
            Samples.Add(new DeltaTimeSample
            {
                Frame = Time.frameCount, UnityDelta = Time.deltaTime, ClockDelta = w.DefaultClock.DeltaTime, TimeScale = Time.timeScale,
                TimeScaleChangedInFixedUpdate = _changedFrame == Time.frameCount,
            });
        }
    }
}
