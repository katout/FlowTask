using Godot;

/// <summary>A node with script-defined signals, used by the typed signal bridge checks.</summary>
public partial class SmokeEmitter : Node
{
    [Signal]
    public delegate void HitEventHandler(int damage);

    [Signal]
    public delegate void MovedEventHandler(int x, string label);
}
