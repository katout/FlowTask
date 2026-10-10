// A block namespace, not a file-scoped one: Unity finds the class of a MonoBehaviour or ScriptableObject by parsing
// its file, and does not see it in a file-scoped namespace (CONTRIBUTING.md).
namespace Katout.FlowTask.Unity.Tests
{
    /// <summary>
    /// The pool recipe of docs/en/unity/lifetime.md: one line in OnEnable runs the flight while the bullet is out of the pool.
    /// </summary>
    public sealed class PooledBullet : MonoBehaviour
    {
        public FlowHandle Flight { get; private set; }
        public int Flights { get; private set; }
        public int Cleanups { get; private set; }
        public bool CleanedWhileInactive { get; private set; }

        void OnEnable() => Flight = gameObject.RunWhileActive(Fly());

        async FlowTask Fly()
        {
            Flights++;
            try
            {
                while (true)
                {
                    transform.position += Vector3.forward * 0.1f;
                    await FlowTask.NextFrame();
                }
            }
            finally
            {
                // Runs inside OnDisable: the bullet is inactive but still accessible.
                Cleanups++;
                CleanedWhileInactive = !gameObject.activeInHierarchy;
            }
        }
    }
}
