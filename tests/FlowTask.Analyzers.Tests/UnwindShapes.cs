using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Katout.FlowTask;

namespace Katout.FlowTask.Analyzers.Tests.UnwindShapes;

// The one list of shapes that FLOW011 and FlowAssert.CanSkipUnwinding (FlowTask.Testing) judge: SkipUnwindingParityTests
// compiles this file into the test assembly for the check, which reads the IL, and reads it as source for the analyzer.
// A type named Pass_* has no block around an await that unwinding would run; one named Reject_* has one. Both judges
// must give that answer for every type. Written in C# 9 apart from the file-scoped namespace (the analyzer tests parse
// it with Roslyn 3.8), and never run: only compiled. The shapes that need a newer C# are in UnwindShapesCSharp14.cs.

internal static class Shared
{
    internal static int Counter;

    internal static readonly bool Flag = Environment.ProcessorCount > 64;

    internal static bool Seen(Exception e) => e != null;

    internal static FlowTask<IDisposable> Acquire() => FlowTask.FromResult<IDisposable>(new Resource());

    internal static FlowTask<List<int>> Load() => FlowTask.FromResult(new List<int>());
}

internal sealed class Resource : IDisposable
{
    public void Dispose() => Shared.Counter++;
}

internal sealed class AsyncResource : IAsyncDisposable
{
    public System.Threading.Tasks.ValueTask DisposeAsync() => default;
}

/// <summary>A collection whose enumerator is a struct without Dispose: foreach needs no finally block.</summary>
internal readonly struct Plain
{
    public Enumerator GetEnumerator() => default;

    public struct Enumerator
    {
        public int Current => 0;

        public bool MoveNext() => false;
    }
}

/// <summary>An async enumerable whose enumerator is a struct without DisposeAsync: await foreach needs no finally block.</summary>
internal readonly struct PlainAsync
{
    public Enumerator GetAsyncEnumerator() => default;

    public struct Enumerator
    {
        public int Current => 0;

        public System.Threading.Tasks.ValueTask<bool> MoveNextAsync() => new(false);
    }
}

/// <summary>An async enumerable whose enumerator is a sealed class without DisposeAsync.</summary>
internal readonly struct SealedAsync
{
    public SealedAsyncEnumerator GetAsyncEnumerator() => new();
}

internal sealed class SealedAsyncEnumerator
{
    public int Current => 0;

    public System.Threading.Tasks.ValueTask<bool> MoveNextAsync() => new(false);
}

/// <summary>An async enumerable whose enumerator is a struct with a DisposeAsync method (the pattern, no interface).</summary>
internal readonly struct DisposingAsync
{
    public Enumerator GetAsyncEnumerator() => default;

    public struct Enumerator
    {
        public int Current => 0;

        public System.Threading.Tasks.ValueTask<bool> MoveNextAsync() => new(false);

        public System.Threading.Tasks.ValueTask DisposeAsync() => default;
    }
}

/// <summary>An async enumerable whose enumerator is a class that is not sealed, without DisposeAsync.</summary>
internal readonly struct OpenAsync
{
    public OpenAsyncEnumerator GetAsyncEnumerator() => new();
}

internal class OpenAsyncEnumerator
{
    public int Current => 0;

    public System.Threading.Tasks.ValueTask<bool> MoveNextAsync() => new(false);
}

// ------------------------------------------------------------------ passed: nothing that unwinding would run

internal static class Pass_OnlyAwaits
{
    internal static async FlowTask Run()
    {
        await FlowTask.NextFrame();
        await FlowTask.DelayFrames(2);
    }
}

internal static class Pass_ArrayForeach
{
    internal static async FlowTask Run(int[] frames)
    {
        foreach (var f in frames) await FlowTask.DelayFrames(f);
    }
}

internal static class Pass_StringForeach
{
    internal static async FlowTask Run(string text)
    {
        foreach (var c in text) await FlowTask.DelayFrames(c);
    }
}

internal static class Pass_ForeachWithoutDispose
{
    internal static async FlowTask Run(Plain values)
    {
        foreach (var v in values) await FlowTask.DelayFrames(v);
    }
}

internal static class Pass_BlocksWithoutAwait
{
    internal static async FlowTask Run(List<int> values)
    {
        try
        {
            Shared.Counter++;
        }
        finally
        {
            Shared.Counter--;
        }

        foreach (var v in values) Shared.Counter += v;
        using (new Resource()) Shared.Counter++;
        try
        {
            Shared.Counter++;
        }
        catch (Exception)
        {
            Shared.Counter--;
            throw;
        }

        await FlowTask.NextFrame();
    }
}

internal static class Pass_AwaitBeforeTheBlock
{
    internal static async FlowTask Run()
    {
        using (await Shared.Acquire()) Shared.Counter++;
        foreach (var v in await Shared.Load()) Shared.Counter += v;
        await FlowTask.NextFrame();
        using var resource = new Resource();
        Shared.Counter++;
    }
}

internal static class Pass_AwaitInFinallyOrCatch
{
    // The compiler moves an await of a catch or finally block out of its try statement.
    internal static async FlowTask Run()
    {
        try
        {
            Shared.Counter++;
        }
        finally
        {
            await FlowTask.NextFrame();
        }

        try
        {
            Shared.Counter++;
        }
        catch (Exception)
        {
            await FlowTask.NextFrame();
        }
    }
}

internal static class Pass_CatchOfAnotherException
{
    internal static async FlowTask Run()
    {
        try
        {
            await FlowTask.NextFrame();
        }
        catch (InvalidOperationException)
        {
            Shared.Counter++;
        }
        catch (IOException e) when (Shared.Seen(e))
        {
            Shared.Counter++;
        }
    }
}

internal static class Pass_FilterExcludingCancellation
{
    internal static async FlowTask Run()
    {
        try
        {
            await FlowTask.NextFrame();
        }
        catch (Exception e) when (e is not FlowCanceledException)
        {
            Shared.Counter += e.Message.Length;
        }
    }
}

internal static class Pass_FilterExcludingOperationCanceled
{
    internal static async FlowTask Run()
    {
        try
        {
            await FlowTask.NextFrame();
        }
        catch (Exception e) when (!(e is OperationCanceledException))
        {
            Shared.Counter++;
        }
    }
}

internal static class Pass_FilterExcludingCancellationFirst
{
    internal static async FlowTask Run()
    {
        try
        {
            await FlowTask.NextFrame();
        }
        catch (Exception e) when (e is not FlowCanceledException && Shared.Seen(e) && Shared.Flag)
        {
            Shared.Counter++;
        }
    }
}

internal static class Pass_FilterOfOtherTypes
{
    internal static async FlowTask Run()
    {
        try
        {
            await FlowTask.NextFrame();
        }
        catch (Exception e) when (e is IOException || e is ArgumentException)
        {
            Shared.Counter++;
        }

        try
        {
            await FlowTask.NextFrame();
        }
        catch (Exception e) when (e is not (FlowCanceledException or ObjectDisposedException))
        {
            Shared.Counter++;
        }
    }
}

internal static class Pass_LambdaWithoutBlocks
{
    internal static readonly Func<FlowTask> Field = async () => await FlowTask.NextFrame();

    internal static Func<FlowTask> Make() => async () =>
    {
        await FlowTask.NextFrame();
        Action sync = () =>
        {
            try
            {
                Shared.Counter++;
            }
            finally
            {
                Shared.Counter--;
            }
        };
        sync();
    };
}

internal static class Pass_OuterOfANestedType
{
    internal static async FlowTask Run() => await FlowTask.NextFrame();

    // A named type nested in the type is another type: SkipUnwinding of the outer type does not cover it.
    internal static class Reject_Nested
    {
        internal static async FlowTask Run()
        {
            try
            {
                await FlowTask.NextFrame();
            }
            finally
            {
                Shared.Counter++;
            }
        }
    }
}

internal static class Pass_Generic<T>
{
    internal static async FlowTask<T> Run()
    {
        await FlowTask.NextFrame();
        return default;
    }
}

internal static class Pass_AwaitUsingWithoutAwaitInside
{
    internal static async System.Threading.Tasks.Task Run()
    {
        await using (new AsyncResource()) Shared.Counter++;
    }
}

internal static class Pass_AwaitUsingWithoutAwaitInsideAFlowTask
{
    internal static async FlowTask Run()
    {
        await using (new AsyncResource()) Shared.Counter++;
    }
}

internal static class Pass_AwaitInTheFirstDeclarator
{
    internal static async FlowTask Run()
    {
        using (IDisposable first = await Shared.Acquire(), second = new Resource()) Shared.Counter++;
        using IDisposable third = await Shared.Acquire(), fourth = new Resource();
        Shared.Counter++;
    }
}

internal static class Pass_AwaitForeachWithoutDisposeAsync
{
    internal static async FlowTask Run()
    {
        await foreach (var v in new PlainAsync()) Shared.Counter += v;
    }
}

internal static class Pass_AwaitForeachOfASealedEnumeratorWithoutDisposeAsync
{
    internal static async FlowTask Run()
    {
        await foreach (var v in new SealedAsync()) Shared.Counter += v;
    }
}

internal static class Pass_AwaitForeachOfAnOpenEnumeratorWithoutDisposeAsync
{
    internal static async FlowTask Run()
    {
        await foreach (var v in new OpenAsync()) Shared.Counter += v;
    }
}

internal static class Pass_FilterComparingWithFalse
{
    internal static async FlowTask Run()
    {
        try
        {
            await FlowTask.NextFrame();
        }
        catch (Exception e) when (e is FlowCanceledException == false)
        {
            Shared.Counter++;
        }
    }
}

internal static class Pass_FilterNotEqualToTrue
{
    internal static async FlowTask Run()
    {
        try
        {
            await FlowTask.NextFrame();
        }
        catch (Exception e) when ((e is FlowCanceledException) != true)
        {
            Shared.Counter++;
        }
    }
}

internal static class Pass_FilterConditional
{
    internal static async FlowTask Run()
    {
        try
        {
            await FlowTask.NextFrame();
        }
        catch (Exception e) when (e is FlowCanceledException ? false : true)
        {
            Shared.Counter++;
        }
    }
}

internal static class Pass_FilterIsFalse
{
    internal static async FlowTask Run()
    {
        try
        {
            await FlowTask.NextFrame();
        }
        catch (Exception e) when (e is FlowCanceledException is false)
        {
            Shared.Counter++;
        }
    }
}

internal static class Pass_FilterNotNullFirst
{
    internal static async FlowTask Run()
    {
        try
        {
            await FlowTask.NextFrame();
        }
        catch (Exception e) when (e != null && e is not FlowCanceledException)
        {
            Shared.Counter++;
        }
    }
}

// The runtime skips the unwinding of FlowTask methods only: an async method that returns Task, ValueTask or void
// unwinds in any type, so its blocks are not counted (a FlowTask lambda in it is: Reject_FlowTaskLambdaInATaskMethod).

internal static class Pass_TaskFinally
{
    internal static async System.Threading.Tasks.Task Run()
    {
        try
        {
            await System.Threading.Tasks.Task.Yield();
        }
        finally
        {
            Shared.Counter++;
        }
    }
}

internal static class Pass_ValueTaskCatchAll
{
    internal static async System.Threading.Tasks.ValueTask<int> Run()
    {
        try
        {
            await System.Threading.Tasks.Task.Yield();
        }
        catch
        {
            Shared.Counter++;
            throw;
        }

        return Shared.Counter;
    }
}

internal static class Pass_AsyncVoidUsing
{
    internal static async void Run()
    {
        using var resource = new Resource();
        await System.Threading.Tasks.Task.Yield();
    }
}

internal static class Pass_TaskLambdaAndLocalFunction
{
    static readonly Func<System.Threading.Tasks.Task> s_make = async () =>
    {
        try
        {
            await System.Threading.Tasks.Task.Yield();
        }
        finally
        {
            Shared.Counter++;
        }
    };

    internal static async FlowTask Run()
    {
        await FlowTask.NextFrame();
        Shared.Counter += s_make == null ? 0 : 1;
        _ = Inner();

        static async System.Threading.Tasks.Task Inner()
        {
            using var resource = new Resource();
            await System.Threading.Tasks.Task.Yield();
        }
    }
}

internal static class Pass_TaskAwaitForeach
{
    internal static async System.Threading.Tasks.Task Run(IAsyncEnumerable<int> values)
    {
        await foreach (var v in values) Shared.Counter += v;
    }
}

internal static class Pass_TaskAwaitUsing
{
    internal static async System.Threading.Tasks.Task Run()
    {
        await using (new AsyncResource()) await System.Threading.Tasks.Task.Yield();
    }
}

internal static class Pass_TaskAwaitUsingDeclaration
{
    internal static async System.Threading.Tasks.Task Run()
    {
        await using var resource = new AsyncResource();
        await System.Threading.Tasks.Task.Yield();
    }
}

internal struct Pass_TaskInAStruct
{
    internal async System.Threading.Tasks.Task Run()
    {
        try
        {
            await System.Threading.Tasks.Task.Yield();
        }
        finally
        {
            Shared.Counter++;
        }
    }
}

// ------------------------------------------------------------------ rejected: a block around an await

internal static class Reject_Finally
{
    internal static async FlowTask Run()
    {
        try
        {
            await FlowTask.NextFrame();
        }
        finally
        {
            Shared.Counter++;
        }
    }
}

internal static class Reject_Using
{
    internal static async FlowTask Run()
    {
        using (new Resource()) await FlowTask.NextFrame();
    }
}

internal static class Reject_UsingDeclaration
{
    internal static async FlowTask Run()
    {
        using var resource = new Resource();
        await FlowTask.NextFrame();
    }
}

internal static class Reject_ListForeach
{
    internal static async FlowTask Run(List<int> frames)
    {
        foreach (var f in frames) await FlowTask.DelayFrames(f);
    }
}

internal static class Reject_InterfaceForeach
{
    internal static async FlowTask Run(IEnumerable<int> frames)
    {
        foreach (var f in frames) await FlowTask.DelayFrames(f);
    }
}

internal static class Reject_NonGenericForeach
{
    internal static async FlowTask Run(IEnumerable frames)
    {
        foreach (int f in frames) await FlowTask.DelayFrames(f);
    }
}

internal static class Reject_CatchAll
{
    internal static async FlowTask Run()
    {
        try
        {
            await FlowTask.NextFrame();
        }
        catch
        {
            Shared.Counter++;
            throw;
        }
    }
}

internal static class Reject_CatchException
{
    internal static async FlowTask Run()
    {
        try
        {
            await FlowTask.NextFrame();
        }
        catch (InvalidOperationException)
        {
            Shared.Counter++;
        }
        catch (Exception)
        {
            Shared.Counter++;
            throw;
        }
    }
}

internal static class Reject_CatchCancellation
{
    internal static async FlowTask Run()
    {
        try
        {
            await FlowTask.NextFrame();
        }
        catch (OperationCanceledException)
        {
            Shared.Counter++;
            throw;
        }
    }

    internal static async FlowTask Again()
    {
        try
        {
            await FlowTask.NextFrame();
        }
        catch (FlowCanceledException)
        {
            Shared.Counter++;
            throw;
        }
    }
}

internal static class Reject_CatchTypeParameter
{
    // catch (TEx) takes FlowCanceledException when TEx is Exception: neither judge reads the constraint.
    internal static async FlowTask Run<TEx>()
        where TEx : IOException
    {
        try
        {
            await FlowTask.NextFrame();
        }
        catch (TEx)
        {
            Shared.Counter++;
        }
    }
}

internal static class Reject_FilterOnAnotherCondition
{
    internal static async FlowTask Run()
    {
        try
        {
            await FlowTask.NextFrame();
        }
        catch (Exception) when (Shared.Flag)
        {
            Shared.Counter++;
            throw;
        }
    }
}

internal static class Reject_FilterCallingFirst
{
    // The check follows the filter's instructions in order, so a call before the type test is not known.
    internal static async FlowTask Run()
    {
        try
        {
            await FlowTask.NextFrame();
        }
        catch (Exception e) when (Shared.Seen(e) && e is not FlowCanceledException)
        {
            Shared.Counter++;
            throw;
        }
    }
}

internal static class Reject_FilterWithBitwiseAnd
{
    internal static async FlowTask Run()
    {
        try
        {
            await FlowTask.NextFrame();
        }
        catch (Exception e) when ((e is not FlowCanceledException) & Shared.Flag)
        {
            Shared.Counter++;
            throw;
        }
    }
}

internal static class Reject_FilterTakingCancellation
{
    internal static async FlowTask Run()
    {
        try
        {
            await FlowTask.NextFrame();
        }
        catch (Exception e) when (e is FlowCanceledException)
        {
            Shared.Counter++;
            throw;
        }
    }
}

internal static class Reject_AwaitInACatchWithFinally
{
    // The await moves out of the catch clause, but stays in the try block of the finally.
    internal static async FlowTask Run()
    {
        try
        {
            Shared.Counter++;
        }
        catch (InvalidOperationException)
        {
            await FlowTask.NextFrame();
        }
        finally
        {
            Shared.Counter--;
        }
    }
}

internal static class Reject_AwaitInANestedFinally
{
    internal static async FlowTask Run()
    {
        try
        {
            try
            {
                Shared.Counter++;
            }
            finally
            {
                await FlowTask.NextFrame();
            }
        }
        finally
        {
            Shared.Counter--;
        }
    }
}

internal static class Reject_Lambda
{
    internal static Func<FlowTask> Make() => async () =>
    {
        try
        {
            await FlowTask.NextFrame();
        }
        finally
        {
            Shared.Counter++;
        }
    };
}

internal static class Reject_FieldInitializerLambda
{
    internal static readonly Func<FlowTask> Make = async () =>
    {
        using (new Resource()) await FlowTask.NextFrame();
    };
}

internal static class Reject_LocalFunction
{
    internal static FlowTask Outer()
    {
        return Inner();

        static async FlowTask Inner()
        {
            try
            {
                await FlowTask.NextFrame();
            }
            finally
            {
                Shared.Counter++;
            }
        }
    }
}

internal static class Reject_LocalFunctionInALambda
{
    internal static Func<FlowTask> Make() => () =>
    {
        return Inner();

        async FlowTask Inner()
        {
            using var resource = new Resource();
            await FlowTask.NextFrame();
        }
    };
}

internal static partial class Reject_Partial
{
    internal static async FlowTask Run() => await FlowTask.NextFrame();
}

internal static partial class Reject_Partial
{
    internal static async FlowTask Other()
    {
        try
        {
            await FlowTask.NextFrame();
        }
        finally
        {
            Shared.Counter++;
        }
    }
}

internal static class Reject_Generic<T>
{
    internal static async FlowTask<T> Run(List<T> values)
    {
        foreach (var v in values) await FlowTask.NextFrame();
        return default;
    }
}

internal sealed class Reject_InstanceMethod
{
    int _count;

    internal async FlowTask Run()
    {
        try
        {
            await FlowTask.NextFrame();
        }
        catch (Exception e) when (e is not FlowCanceledException)
        {
            _count++;
        }
        finally
        {
            _count--;
        }
    }
}

internal static class Reject_AwaitForeach
{
    internal static async FlowTask Run(IAsyncEnumerable<int> values)
    {
        await foreach (var v in values) Shared.Counter += v;
    }
}

internal static class Reject_AwaitForeachWithDisposeAsync
{
    internal static async FlowTask Run()
    {
        await foreach (var v in new DisposingAsync()) Shared.Counter += v;
    }
}

internal static class Reject_AwaitUsing
{
    internal static async FlowTask Run()
    {
        await using (new AsyncResource()) await FlowTask.NextFrame();
    }
}

internal static class Reject_AwaitUsingDeclaration
{
    internal static async FlowTask Run()
    {
        await using var resource = new AsyncResource();
        await FlowTask.NextFrame();
    }
}

internal static class Reject_UsingDeclarationOfTwo
{
    // The second declarator is in the try block of the first: its await is inside the first's disposal.
    internal static async FlowTask Run()
    {
        using IDisposable first = new Resource(), second = await Shared.Acquire();
    }
}

internal static class Reject_UsingStatementOfTwo
{
    internal static async FlowTask Run()
    {
        using (IDisposable first = new Resource(), second = await Shared.Acquire()) Shared.Counter++;
    }
}

internal static class Reject_AwaitUsingDeclarationOfTwo
{
    // The second resource's DisposeAsync is awaited inside the try block of the first.
    internal static async FlowTask Run()
    {
        await using AsyncResource first = new AsyncResource(), second = new AsyncResource();
    }
}

internal static class Reject_AwaitUsingStatementOfTwo
{
    internal static async FlowTask Run()
    {
        await using (AsyncResource first = new AsyncResource(), second = new AsyncResource()) Shared.Counter++;
    }
}

internal static class Reject_LabeledUsingDeclaration
{
#pragma warning disable CS0164 // The label is the shape: a 'using var' that is a labeled statement.
    internal static async FlowTask Run()
    {
        Start:
        using var resource = new Resource();
        await FlowTask.NextFrame();
    }
#pragma warning restore CS0164
}

internal static class Reject_FilterConditionalTakingCancellation
{
    internal static async FlowTask Run()
    {
        try
        {
            await FlowTask.NextFrame();
        }
        catch (Exception e) when (e is FlowCanceledException ? Shared.Flag : false)
        {
            Shared.Counter++;
            throw;
        }
    }
}

internal static class Reject_FilterComparingWithAField
{
    internal static async FlowTask Run()
    {
        try
        {
            await FlowTask.NextFrame();
        }
        catch (Exception e) when ((e is not FlowCanceledException) == Shared.Flag)
        {
            Shared.Counter++;
            throw;
        }
    }
}

internal static class Reject_FlowTaskLambdaInATaskMethod
{
    internal static async System.Threading.Tasks.Task Run()
    {
        Func<FlowTask> make = async () =>
        {
            try
            {
                await FlowTask.NextFrame();
            }
            finally
            {
                Shared.Counter++;
            }
        };
        await System.Threading.Tasks.Task.Yield();
        Shared.Counter += make == null ? 0 : 1;
    }
}

internal struct Reject_Struct
{
    internal async FlowTask Run()
    {
        try
        {
            await FlowTask.NextFrame();
        }
        finally
        {
            Shared.Counter++;
        }
    }
}
