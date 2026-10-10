using System.Threading.Tasks;

// A block namespace, not a file-scoped one: Unity finds the class of a MonoBehaviour or ScriptableObject by parsing
// its file, and does not see it in a file-scoped namespace (CONTRIBUTING.md).
namespace Katout.FlowTask.Unity.Tests
{
    /// <summary>
    /// Completes a task from MonoBehaviour.Update when armed, on the main thread under UnitySynchronizationContext (used
    /// to check where a bridged Task resumes the flow).
    /// </summary>
    public sealed class TestTaskCompleter : MonoBehaviour
    {
        public TaskCompletionSource<int> CompleteNextUpdate;
        public int CompletedFrame = -1;

        /// <summary>Ticks and flushes of the default World completed before the task was completed.</summary>
        public long CompletedAfterPoints = -1;

        void Update()
        {
            var source = CompleteNextUpdate;
            if (source == null) return;
            CompleteNextUpdate = null;
            CompletedFrame = Time.frameCount;
            CompletedAfterPoints = FlowTaskUnity.TickCount + FlowTaskUnity.FlushCount;
            source.SetResult(CompletedFrame);
        }
    }
}
