// Guards the csc.rsp next to this assembly's .asmdef (-langversion:latest): this file only compiles with C# 10 syntax
// enabled, so the synced copies of tests/FlowTask.Core.Tests (LangVersion 10.0 on .NET) can use it unchanged.
using NUnit.Framework;

namespace Katout.FlowTask.Tests.UnityOnly; // file-scoped namespace: C# 10

public class LangVersionTests
{
    const string Product = "FlowTask";
    const string Banner = $"{Product} tests"; // constant interpolated string: C# 10

    [Test]
    public void TestAssembliesAreCompiledWithCSharp10()
    {
        object pair = new System.Collections.Generic.KeyValuePair<string, int>("ab", 1);
        Assert.That(pair is System.Collections.Generic.KeyValuePair<string, int> { Key.Length: 2 }, Is.True); // extended property pattern: C# 10
        Assert.That(Banner, Is.EqualTo("FlowTask tests"));
    }
}
