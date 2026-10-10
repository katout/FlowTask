using UnityEngine.UI;

namespace Katout.FlowTask.Unity;

/// <summary>
/// uGUI helper for the most common case, waiting for a button click:
/// <c>using var ok = button.ClickedSignal(); await ok.Next();</c> (compiled only when com.unity.ugui is installed).
/// Other controls bridge their UnityEvents directly: <c>toggle.onValueChanged.ToSignal()</c>. Like every event bridge
/// it must be called from flow code: the listener belongs to the current scope.
/// </summary>
public static class FlowTaskUIExtensions
{
    /// <summary><c>Button.onClick</c> as a signal owned by the current scope.</summary>
    public static EventSignal<FlowUnit> ClickedSignal(this Button button, string name = null)
    {
        if (button == null) throw new System.ArgumentNullException(nameof(button), "The Button is null or destroyed.");
        return button.onClick.ToSignal(name ?? button.name + ".onClick");
    }
}
