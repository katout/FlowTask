using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Godot.NativeInterop;

namespace Katout.FlowTask.Godot;

/// <summary>
/// Godot signal bridges. <c>ToFlowSignal*</c> turn a Godot signal into an <see cref="EventSignal{T}"/>: the
/// connection is made now, owned by the current scope and disconnected when that scope ends (or on Dispose).
/// <see cref="AsFlow(SignalAwaiter)"/> bridges Godot's own <c>ToSignal</c> awaiter; to wait for one emission
/// and disconnect even on cancellation, write <c>using var s = obj.ToFlowSignal(name); await s.Next();</c>. Signals with
/// two or more arguments use <see cref="ToFlowSignalArgs"/> (<c>Variant[]</c>). Emissions never resume a flow synchronously inside Godot's
/// emit: the resume is reserved and runs in the World's next flush.
/// </summary>
public static class GodotSignalExtensions
{
    /// <summary>Bridges a signal of any arity; each emission delivers its arguments as <c>Variant[]</c>.</summary>
    public static unsafe EventSignal<Variant[]> ToFlowSignalArgs(this GodotObject source, StringName signal)
    {
        CheckSignal(source, signal);
        return FlowBridge.FromCallback<Variant[]>(
            emit => Connect(source, signal, Callable.CreateWithUnsafeTrampoline(emit, &ArgsTrampoline)),
            Describe(source, signal));
    }

    /// <summary>Bridges a signal of any arity, discarding its arguments (e.g. <c>pressed</c>, <c>timeout</c>).</summary>
    public static unsafe EventSignal<FlowUnit> ToFlowSignal(this GodotObject source, StringName signal)
    {
        CheckSignal(source, signal);
        return FlowBridge.FromCallback<FlowUnit>(
            emit => Connect(source, signal, Callable.CreateWithUnsafeTrampoline(emit, &UnitTrampoline)),
            Describe(source, signal));
    }

    /// <summary>Bridges a signal with exactly one argument, converted to <typeparamref name="T"/>.</summary>
    public static EventSignal<T> ToFlowSignal<[MustBeVariant] T>(this GodotObject source, StringName signal)
    {
        CheckSignal(source, signal);
        return FlowBridge.FromCallback<T>(emit => Connect(source, signal, Callable.From(emit)), Describe(source, signal));
    }

    /// <summary><c>BaseButton.pressed</c> as a signal owned by the current scope.</summary>
    public static EventSignal<FlowUnit> PressedSignal(this BaseButton button) => button.ToFlowSignal(BaseButton.SignalName.Pressed);

    /// <summary>
    /// Bridges an existing Godot <see cref="SignalAwaiter"/> (<c>ToSignal(obj, name)</c>) to a FlowTask. It
    /// completes in the World's flush after the signal fired. On cancellation the result is ignored; Godot keeps the
    /// awaiter's one-shot connection until the signal fires or the source is freed (prefer <c>ToFlowSignal</c>, which
    /// disconnects with its scope).
    /// </summary>
    public static FlowTask<Variant[]> AsFlow(this SignalAwaiter awaiter)
    {
        if (awaiter == null) throw new ArgumentNullException(nameof(awaiter));
        return FlowBridge.FromTask(ct => AwaitSignal(awaiter, ct)).ToFlowTask();
    }

    static Task<Variant[]> AwaitSignal(SignalAwaiter awaiter, CancellationToken ct)
    {
        if (awaiter.IsCompleted) return Task.FromResult(awaiter.GetResult());
        var tcs = new TaskCompletionSource<Variant[]>();
        var registration = ct.Register(static s => ((TaskCompletionSource<Variant[]>)s).TrySetCanceled(), tcs);
        awaiter.OnCompleted(() =>
        {
            registration.Dispose();
            tcs.TrySetResult(awaiter.GetResult());
        });
        return tcs.Task;
    }

    // ------------------------------------------------------------------ plumbing

    internal static Action Connect(GodotObject source, StringName signal, Callable callable)
    {
        var err = source.Connect(signal, callable);
        if (err != Error.Ok) throw new InvalidOperationException($"Connecting to '{Describe(source, signal)}' failed: {err}.");
        return () => Disconnect(source, signal, callable);
    }

    static void Disconnect(GodotObject source, StringName signal, Callable callable)
    {
        // The source may already be freed (Godot then dropped the connection itself).
        if (GodotObject.IsInstanceValid(source) && source.IsConnected(signal, callable)) source.Disconnect(signal, callable);
    }

    internal static void CheckSignal(GodotObject source, StringName signal)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (signal == null || string.IsNullOrEmpty(signal.ToString())) throw new ArgumentException("Signal name is empty.", nameof(signal));
        if (!GodotObject.IsInstanceValid(source)) throw new ObjectDisposedException(source.GetType().Name, "The signal source was freed.");
        if (!source.HasSignal(signal)) throw new ArgumentException($"{Describe(source, null)} has no signal '{signal}'.", nameof(signal));
    }

    internal static string Describe(GodotObject source, StringName signal)
    {
        var owner = source is Node n ? n.Name.ToString() : source.GetClass();
        return signal == null ? owner : owner + "." + signal;
    }

    static void ArgsTrampoline(object delegateObj, NativeVariantPtrArgs args, out godot_variant ret)
    {
        var count = args.Count;
        var values = count == 0 ? Array.Empty<Variant>() : new Variant[count];
        for (var i = 0; i < count; i++) values[i] = Variant.CreateCopyingBorrowed(args[i]);
        ((Action<Variant[]>)delegateObj)(values);
        ret = default;
    }

    static void UnitTrampoline(object delegateObj, NativeVariantPtrArgs args, out godot_variant ret)
    {
        ((Action<FlowUnit>)delegateObj)(FlowUnit.Default);
        ret = default;
    }
}
