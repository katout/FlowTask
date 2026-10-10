using BenchmarkDotNet.Running;

namespace Katout.FlowTask.Benchmarks;

/// <summary>
/// Every FlowTask benchmark drives a <see cref="FlowWorld"/> the way an engine does (Emit, then Tick). UniTask baselines use
/// manually completed sources (<c>AutoResetUniTaskCompletionSource</c>) so no Unity PlayerLoop is needed.
/// </summary>
public static class Program
{
    public static void Main(string[] args)
    {
        // "hotpath [scenario]": the quick Stopwatch measurement used by tools/bench/ab.cs.
        if (args.Length >= 1 && args[0] == "hotpath")
        {
            HotPath.Run(args.Length > 1 ? args[1] : null);
            return;
        }

        // Everything else goes to BenchmarkDotNet, for example:
        //   dotnet run -c Release --project tests/FlowTask.Benchmarks -- --filter '*'
        //   dotnet run -c Release --project tests/FlowTask.Benchmarks -- --filter '*Cancellation*' --job short
        // The short job is for a quick look: its results vary too much between runs to compare two versions
        // (tools/bench/ab.cs does that).
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}

internal static class Bench
{
    internal const double Dt = 1.0 / 60;
}
