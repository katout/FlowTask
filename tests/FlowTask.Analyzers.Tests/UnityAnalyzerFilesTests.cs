using System;
using System.IO;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Katout.FlowTask.Analyzers.Tests;

/// <summary>
/// The UPM package com.katout.flowtask carries FlowTask.Analyzers.dll in src/FlowTask/Analyzers, which
/// a Release build on Windows keeps up to date (FlowTask.Analyzers.csproj). Unity passes the DLL to the compiler as
/// an analyzer only while its .meta carries the RoslynAnalyzer label and enables no platform, and it would pass any
/// other DLL there to the compiler too. Unity's EditMode checks the import itself
/// (AnalyzerDllIsImportedAsARoslynAnalyzer); these checks run without Unity.
/// </summary>
public class UnityAnalyzerFilesTests
{
    const string AnalyzerFile = "FlowTask.Analyzers.dll";

    [Test]
    public void TheUnityAnalyzerMetaMarksTheDllAsARoslynAnalyzerForNoPlatform()
    {
        var meta = Path.Combine(UnityAnalyzerFolder(), AnalyzerFile + ".meta");
        Assert.That(File.Exists(meta), Is.True, meta + " is missing: restore it with git (its GUID is committed)");
        var lines = File.ReadAllLines(meta);
        var labels = lines.SkipWhile(l => l != "labels:").Skip(1).TakeWhile(l => l.StartsWith("- ")).Select(l => l.Substring(2).Trim());
        Assert.That(labels, Does.Contain("RoslynAnalyzer"), "without the label Unity does not use the DLL as an analyzer");
        Assert.That(lines.Select(l => l.Trim()), Has.None.EqualTo("enabled: 1"), "an enabled platform makes Unity load the analyzer as a plugin");
    }

    [Test]
    public void TheUnityAnalyzerFolderHoldsOnlyTheAnalyzer()
    {
        var folder = UnityAnalyzerFolder();
        var dlls = Directory.GetFiles(folder, "*.dll", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(folder, f).Replace(Path.DirectorySeparatorChar, '/'))
            .ToArray();
        // The code fixes need Roslyn's Workspaces, which Unity does not load, and a satellite assembly would be passed
        // to the compiler as another analyzer.
        Assert.That(dlls, Is.EquivalentTo(new[] { AnalyzerFile }));
    }

    [TestCase("EditMode")]
    [TestCase("PlayMode")]
    public void TheUnityCoreTestAssembliesTurnOffEveryFlowRule(string mode)
    {
        // The core test suite breaks the FLOW rules on purpose, and Unity runs the package's analyzer on the copies of
        // it, so the csc.rsp next to each core test assembly (tests/unity/Assets/Tests/<mode>) must turn off every rule,
        // a new one included. Unity 6000.3 rejects a ruleset named after an assembly in the Assets folder.
        var rules = typeof(AwaitAnalyzer).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(DiagnosticAnalyzer).IsAssignableFrom(t)
                && t.GetCustomAttributes(typeof(DiagnosticAnalyzerAttribute), false).Length > 0)
            .SelectMany(t => ((DiagnosticAnalyzer)Activator.CreateInstance(t)).SupportedDiagnostics)
            .Select(d => d.Id)
            .Distinct()
            .ToArray();
        Assert.That(rules, Is.Not.Empty);
        var off = File.ReadAllLines(Path.Combine(RepositoryRoot(), "tests", "unity", "Assets", "Tests", mode, "csc.rsp"))
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("-nowarn:", StringComparison.Ordinal))
            .SelectMany(l => l.Substring("-nowarn:".Length).Split(';', ','));
        Assert.That(off, Is.SupersetOf(rules));
    }

    static string UnityAnalyzerFolder() => Path.Combine(RepositoryRoot(), "src", "FlowTask", "Analyzers");

    internal static string RepositoryRoot()
    {
        var testDirectory = TestContext.CurrentContext.TestDirectory;
        Assume.That(testDirectory, Is.Not.Null.And.Not.Empty, "no test directory in this host (static check of the repository)");
        var dir = new DirectoryInfo(testDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FlowTask.slnx"))) dir = dir.Parent;
        Assume.That(dir, Is.Not.Null, "repository root not found");
        return dir!.FullName;
    }
}
