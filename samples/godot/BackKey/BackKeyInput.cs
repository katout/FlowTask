using System;
using Godot;

namespace Katout.FlowTask.Samples;

/// <summary>
/// Sample: hands the back key to a <see cref="BackKeyRouter"/>. Reads the <c>ui_cancel</c> action
/// (Escape by default) in <c>_UnhandledInput</c>, so a focused control that uses the key first keeps it, and Android's
/// back button (NOTIFICATION_WM_GO_BACK_REQUEST; set application/config/quit_on_go_back to false). The flow that owns
/// the key resumes at the next flush point, in the same frame. Add it once per session and set <see cref="Router"/>.
/// </summary>
public partial class BackKeyInput : Node
{
    // Input reaches a node only while it runs: the back key also closes the pause menu, while the tree is paused.
    public BackKeyInput() => ProcessMode = ProcessModeEnum.Always;

    /// <summary>The router of the current session.</summary>
    public BackKeyRouter Router { get; set; }

    /// <summary>A press that reached no entry: nothing on screen owns the key (e.g. ask whether to quit).</summary>
    public event Action Unhandled;

    public override void _UnhandledInput(InputEvent e)
    {
        if (Router == null || !e.IsActionPressed("ui_cancel")) return;
        GetViewport().SetInputAsHandled();
        Press();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMGoBackRequest && Router != null) Press();
    }

    void Press()
    {
        if (!Router.Press()) Unhandled?.Invoke();
    }
}
