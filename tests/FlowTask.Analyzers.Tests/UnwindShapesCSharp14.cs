using System;
using Katout.FlowTask;

namespace Katout.FlowTask.Analyzers.Tests.UnwindShapesCSharp14;

// The shapes of UnwindShapes.cs that need C# 14, which Roslyn 3.8 cannot parse. SkipUnwindingParityTests compiles this
// file with the compiler of the .NET SDK and the analyzer for FLOW011, and the check reads its IL in this test assembly.
// A type named Pass_* has no block around an await that unwinding would run; one named Reject_* has one. Never run.

internal static class Counter
{
    internal static int Value;
}

/// <summary>The members of an extension block are emitted in the static class it is written in: they are its methods.</summary>
internal static class Reject_ExtensionBlock
{
    extension(string text)
    {
        internal async FlowTask Run()
        {
            try
            {
                await FlowTask.NextFrame();
            }
            finally
            {
                Counter.Value += text.Length;
            }
        }
    }
}

internal static class Reject_StaticMemberOfAnExtensionBlock
{
    extension(string)
    {
        internal static async FlowTask Run()
        {
            using var resource = new System.IO.MemoryStream();
            await FlowTask.NextFrame();
        }
    }
}

internal static class Pass_ExtensionBlockWithoutBlocks
{
    extension(string text)
    {
        internal async FlowTask Run()
        {
            await FlowTask.NextFrame();
            Counter.Value += text.Length;
        }

        internal async System.Threading.Tasks.Task RunTask()
        {
            try
            {
                await System.Threading.Tasks.Task.Yield();
            }
            finally
            {
                Counter.Value += text.Length;
            }
        }
    }
}
