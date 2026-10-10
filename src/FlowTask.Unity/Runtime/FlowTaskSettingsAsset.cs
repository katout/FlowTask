// A block namespace, not a file-scoped one: Unity finds the class of a MonoBehaviour or ScriptableObject by parsing
// its file, and does not see it in a file-scoped namespace.
namespace Katout.FlowTask.Unity
{
    /// <summary>
    /// Optional project-wide settings. Create one via Assets &gt; Create &gt; FlowTask &gt; Settings, name it
    /// <c>FlowTaskSettings</c> and put it in a <c>Resources</c> folder; it is applied before the first scene loads.
    /// </summary>
    [CreateAssetMenu(fileName = ResourceName, menuName = "FlowTask/Settings", order = 1000)]
    public sealed class FlowTaskSettingsAsset : ScriptableObject
    {
        /// <summary>Resources path the PlayerLoop integration loads at startup.</summary>
        public const string ResourceName = "FlowTaskSettings";

        [SerializeField] FlowTaskSettings _settings = new();

        public FlowTaskSettings Settings => _settings;
    }
}
