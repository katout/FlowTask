// A block namespace, not a file-scoped one: Unity finds the class of a MonoBehaviour or ScriptableObject by parsing
// its file, and does not see it in a file-scoped namespace (CONTRIBUTING.md).
namespace Katout.FlowTask.Unity.Tests
{
    /// <summary>Emits a signal from MonoBehaviour.Update when armed (used to check same-frame flush points).</summary>
    public sealed class TestEmitter : MonoBehaviour
    {
        public readonly Signal<int> Signal = new Signal<int>("TestEmitter");
        public bool EmitNextUpdate;
        public int EmittedFrame = -1;

        void Update()
        {
            if (!EmitNextUpdate) return;
            EmitNextUpdate = false;
            EmittedFrame = Time.frameCount;
            Signal.Emit(EmittedFrame);
        }
    }
}
