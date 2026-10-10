using System.IO;
using System.Reflection;
using System.Reflection.PortableExecutable;

namespace Katout.FlowTask.Analyzers.Tests;

/// <summary>
/// FlowTask.Analyzers.dll is committed for Unity (src/FlowTask/Analyzers), and CI rebuilds it and fails
/// when git sees it change, so its bytes must not depend on the machine or the commit: no PDB path and no commit hash.
/// The code fixes ship next to it in the NuGet package and follow the same settings.
/// </summary>
public class ReproducibleBuildTests
{
    static readonly Assembly[] Shipped = { typeof(AwaitAnalyzer).Assembly, typeof(StartFlowTaskCodeFixProvider).Assembly };

    [Test]
    public void AnalyzerAssembliesCarryNoCommitHash()
    {
        foreach (var assembly in Shipped)
        {
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion;
            Assert.That(version, Does.Not.Contain("+"), assembly.GetName().Name);
        }
    }

    [Test]
    public void ReleaseAnalyzerAssembliesCarryNoPdbPath()
    {
#if DEBUG
        Assert.Ignore("Debug builds keep their PDB for debugging; CI runs this test in Release.");
#else
        foreach (var assembly in Shipped)
        {
            using var pe = new PEReader(File.OpenRead(assembly.Location));
            var entries = pe.ReadDebugDirectory().Select(e => e.Type).ToArray();
            Assert.That(entries, Has.None.EqualTo(DebugDirectoryEntryType.CodeView), assembly.GetName().Name);
            Assert.That(entries, Has.None.EqualTo(DebugDirectoryEntryType.EmbeddedPortablePdb), assembly.GetName().Name);
        }
#endif
    }
}
