namespace Katout.FlowTask;

/// <summary>The type with a single value, where a value is needed but carries nothing.</summary>
public readonly record struct FlowUnit
{
    /// <summary>The value.</summary>
    public static readonly FlowUnit Default;

    /// <summary><c>()</c>.</summary>
    public override string ToString() => "()";
}

/// <summary>The winner of a Race of tasks of one type: its index and value.</summary>
/// <param name="Index">The position of the winner among the tasks.</param>
/// <param name="Value">The winner's value.</param>
public readonly record struct RaceResult<T>(int Index, T Value)
{
    /// <summary><c>Race[Index] = Value</c>.</summary>
    public override string ToString() => $"Race[{Index}] = {Value}";
}

/// <summary>Marks a lifetime handle that the current scope owns: discarding it without a variable is reported by FLOW004.</summary>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class, Inherited = false)]
public sealed class LifetimeHandleAttribute : Attribute
{
}
