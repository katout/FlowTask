/// <summary>
/// The autoload that drives the default World (project.godot: FlowAutoload="*res://FlowAutoload.cs"). Godot autoloads
/// need a script inside the project, so this subclass exposes FlowWorldNode
/// (https://katout.github.io/FlowTask/en/godot/setup/).
/// </summary>
public partial class FlowAutoload : Katout.FlowTask.Godot.FlowWorldNode
{
}
