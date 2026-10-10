using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Katout.FlowTask.Unity.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Katout.FlowTask.Unity.Editor.Tests;

/// <summary>Editor-side checks: the Scope Tree window and the UPM packaging.</summary>
public class EditorIntegrationTests
{
    [Test]
    public void ScopeTreeWindowIsRegisteredInTheMenuAndListsRegisteredWorlds()
    {
        var open = typeof(FlowScopeTreeWindow).GetMethod(nameof(FlowScopeTreeWindow.Open));
        var menu = (MenuItem)Attribute.GetCustomAttribute(open, typeof(MenuItem));
        Assert.That(menu.menuItem, Is.EqualTo("Window/FlowTask/Scope Tree"));

        var world = new FlowWorld("EditorProbe");
        try
        {
            FlowWorldRegistry.Register(world);
            world.Run(FlowTask.Never());
            Assert.That(FlowWorldRegistry.Worlds, Has.Member(world));
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                // Needs a graphics device (not available with -nographics).
                Assert.That(EditorApplication.ExecuteMenuItem("Window/FlowTask/Scope Tree"), Is.True);
                var windows = Resources.FindObjectsOfTypeAll<FlowScopeTreeWindow>();
                Assert.That(windows, Is.Not.Empty);
                foreach (var w in windows) w.Close();
            }
            else
            {
                var window = ScriptableObject.CreateInstance<FlowScopeTreeWindow>(); // OnEnable/OnDisable without a view
                UnityEngine.Object.DestroyImmediate(window);
            }

            Assert.That(world.Dump(), Does.Contain("EditorProbe"));
        }
        finally
        {
            world.Dispose();
            FlowWorldRegistry.Unregister(world);
        }

        Assert.That(FlowWorldRegistry.Worlds, Has.No.Member(world));
    }

    // The window's source lookup is internal to FlowTask.Unity.Editor, which grants no InternalsVisibleTo.
    static MonoScript FindScript(Type type) =>
        (MonoScript)typeof(FlowScopeTreeWindow).GetMethod("FindScript", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { type });

    static int FindMethodLine(string text, string method) =>
        (int)typeof(FlowScopeTreeWindow).GetMethod("FindMethodLine", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { text, method });

    static string Describe(Katout.FlowTask.Diagnostics.FlowScopeInfo info) =>
        (string)typeof(FlowScopeTreeWindow).GetMethod("Describe", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { info, false });

    [Test]
    public void ScopeTreeWindowShowsWhatARootLevelWaitWaitsFor()
    {
        // A wait run directly with FlowWorld.Run shows what it waits for; only a scope is timed, as in the dump.
        var world = new FlowWorld("WaitProbe");
        try
        {
            var login = new Signal<int>(world, "LoginResult");
            world.Run(login.Next());
            world.Run(FlowTask.WhenAll(WaitInTheEditorTests(), FlowTask.Never()));
            for (var i = 0; i < 180; i++) world.Tick(1.0 / 60);
            var rows = world.Diagnostics.Root.Children.Select(Describe).ToList();
            Assert.That(rows[0], Does.Contain("waiting: LoginResult.Next").And.Not.Contain(" for "));
            Assert.That(rows[1], Does.Contain("WhenAll").And.Not.Contain(" for "));
        }
        finally
        {
            world.Dispose();
        }
    }

    static MonoScript FindWaitScript(string file) =>
        (MonoScript)typeof(FlowScopeTreeWindow).GetMethod("FindWaitScript", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { file });

    static int Line([System.Runtime.CompilerServices.CallerLineNumber] int line = 0) => line;

    [Test]
    public void ScopeTreeWindowShowsAndOpensWhereAWaitWasCreated()
    {
        // The row shows the file name and line of the wait (FlowScopeInfo.WaitingFile, WaitingLine), and the path the
        // compiler gave finds the script that a double-click opens at that line.
        var world = new FlowWorld("SiteProbe");
        try
        {
            var line = Line(); world.Run(FlowTask.WaitForSeconds(10));
            var wait = world.Diagnostics.Root.Children.Single();
            Assert.That(wait.WaitingLine, Is.EqualTo(line));
            Assert.That(Describe(wait), Does.Contain("at EditorIntegrationTests.cs:" + line));
            var script = FindWaitScript(wait.WaitingFile);
            Assert.That(script, Is.Not.Null, wait.WaitingFile);
            Assert.That(AssetDatabase.GetAssetPath(script), Does.EndWith("Tests/Editor/EditorIntegrationTests.cs"));
            Assert.That(FindWaitScript("Z:/nowhere/Missing.cs"), Is.Null);
        }
        finally
        {
            world.Dispose();
        }
    }

    static async FlowTask WaitInTheEditorTests()
    {
        await FlowTask.Never();
    }

    sealed class GenericHost<T>
    {
        internal static async FlowTask<T> WaitInAGenericType()
        {
            await FlowTask.Never();
            return default;
        }
    }

    [Test]
    public void ScopeTreeWindowOpensTheScriptAndLineOfAScopesMethod()
    {
        // FlowScopeInfo.DeclaringType finds the script, MethodName the line. A method of a generic type nested in the
        // test class, and a lambda (its containing method: this test), are found in this file too.
        var world = new FlowWorld("SourceProbe");
        try
        {
            Func<FlowTask> lambda = async () => await FlowTask.Never();
            world.Run(WaitInTheEditorTests());
            world.Run(GenericHost<int>.WaitInAGenericType());
            world.Run(lambda());
            var expected = new[]
            {
                "static async FlowTask WaitInTheEditorTests()",
                "internal static async FlowTask<T> WaitInAGenericType()",
                "public void ScopeTreeWindowOpensTheScriptAndLineOfAScopesMethod()",
            };
            var scopes = world.Diagnostics.Root.Children;
            Assert.That(scopes, Has.Count.EqualTo(expected.Length));
            for (var i = 0; i < scopes.Count; i++)
            {
                var script = FindScript(scopes[i].DeclaringType);
                Assert.That(script, Is.Not.Null, scopes[i].Name);
                Assert.That(AssetDatabase.GetAssetPath(script), Does.EndWith("Tests/Editor/EditorIntegrationTests.cs"));
                var line = FindMethodLine(script.text, scopes[i].MethodName);
                Assert.That(line, Is.GreaterThan(0), scopes[i].MethodName);
                Assert.That(script.text.Split('\n')[line - 1], Does.Contain(expected[i]));
            }
        }
        finally
        {
            world.Dispose();
        }

        Assert.That(FindScript(typeof(string)), Is.Null, "a type from a precompiled assembly has no script");
        // Calls, expression bodies and comments are not declarations; an async declaration comes first.
        const string text = "// Save() writes the file\n" +
                            "void Start() => world.Run(Save());\n" +
                            "var saved = await Save();\n" +
                            "Func<FlowTask> f = () => Save();\n" +
                            "static async FlowTask<int> Save(string name)\n";
        Assert.That(FindMethodLine(text, "Save"), Is.EqualTo(5));
        Assert.That(FindMethodLine(text, "Start"), Is.EqualTo(2));
        Assert.That(FindMethodLine(text, "Load"), Is.EqualTo(0));
    }

    [TestCase("com.katout.flowtask")]
    [TestCase("com.katout.flowtask.unity")]
    [TestCase("com.katout.flowtask.testing")]
    [TestCase("com.katout.flowtask.testing.nunit")]
    [TestCase("com.katout.flowtask.unitask")]
    public void EveryPackageFileAndFolderHasAMetaFile(string package)
    {
        var info = PackageInfo.FindForAssetPath("Packages/" + package + "/package.json");
        Assert.That(info, Is.Not.Null, package + " is not installed");
        var root = info.resolvedPath;
        // tests/unity installs the packages with file: paths into src/, so the single version source is two levels up.
        var props = File.ReadAllText(Path.Combine(root, "..", "..", "Directory.Build.props"));
        var version = Regex.Match(props, "<Version>([^<]+)</Version>").Groups[1].Value;
        Assert.That(info.version, Is.EqualTo(version), "package.json and Directory.Build.props differ (the build of the repository reports it: src/Directory.Build.targets)");
        var missing = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Where(p => !p.EndsWith(".meta", StringComparison.Ordinal))
            .Where(p => !Path.GetRelativePath(root, p).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part.StartsWith(".") || part.EndsWith("~")))
            .Where(p => !File.Exists(p + ".meta"))
            .Select(p => Path.GetRelativePath(root, p))
            .ToArray();
        Assert.That(missing, Is.Empty, "run dotnet tools/unity/generate_meta.cs");
    }

    // Unity finds the class of a MonoBehaviour or ScriptableObject through its script file (MonoScript), which it parses
    // by itself, not with the compiler: a class in a file-scoped namespace (C# 10) compiles but is not found, so the
    // component cannot be added in the Inspector and assets that use the class lose it. Those files keep a block
    // namespace (CONTRIBUTING.md).
    [TestCase("Packages/com.katout.flowtask.unity")]
    [TestCase("Assets/Tests/Unity")]
    [TestCase("Assets/Samples")]
    public void EveryMonoBehaviourAndScriptableObjectIsFoundThroughItsScript(string folder)
    {
        var types = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name.StartsWith("FlowTask", StringComparison.Ordinal))
            .SelectMany(a => a.GetTypes())
            .Where(t => t.IsSubclassOf(typeof(UnityEngine.Object)) && !t.IsAbstract)
            .ToLookup(t => t.Name);
        var scripts = AssetDatabase.FindAssets("t:MonoScript", new[] { folder })
            .Select(guid => AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(guid)))
            .Where(s => s != null && types[s.name].Any())
            .ToArray();
        Assert.That(scripts, Is.Not.Empty, "no MonoBehaviour or ScriptableObject script in " + folder);
        var notFound = scripts.Where(s => s.GetClass() == null || s.GetClass().Name != s.name)
            .Select(AssetDatabase.GetAssetPath)
            .ToArray();
        Assert.That(notFound, Is.Empty, "Unity does not find the class in these scripts: declare their namespace with a block");
    }

    // Unity finds a MonoBehaviour or ScriptableObject only in a script file named after it. One in a file of another name
    // compiles and AddComponent works, but it cannot be added in the Inspector, nor kept on a prefab or in a scene.
    [TestCase("Packages/com.katout.flowtask.unity")]
    [TestCase("Assets/Tests/Unity")]
    [TestCase("Assets/Samples")]
    public void EveryMonoBehaviourAndScriptableObjectHasAScriptNamedAfterIt(string folder)
    {
        var paths = AssetDatabase.FindAssets("t:MonoScript", new[] { folder }).Select(AssetDatabase.GUIDToAssetPath).ToArray();
        var assemblies = new HashSet<string>(paths
            .Select(CompilationPipeline.GetAssemblyNameFromScriptPath)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n.EndsWith(".dll", StringComparison.Ordinal) ? n.Substring(0, n.Length - 4) : n));
        Assert.That(assemblies, Is.Not.Empty, "no script in " + folder);
        var found = new HashSet<Type>(paths
            .Select(p => AssetDatabase.LoadAssetAtPath<MonoScript>(p))
            .Where(s => s != null && s.GetClass() != null)
            .Select(s => s.GetClass()));
        var missing = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => assemblies.Contains(a.GetName().Name))
            .SelectMany(a => a.GetTypes())
            .Where(t => (t.IsSubclassOf(typeof(MonoBehaviour)) || t.IsSubclassOf(typeof(ScriptableObject))) && !t.IsAbstract && !t.IsGenericTypeDefinition)
            .Where(t => !found.Contains(t))
            .Select(t => t.FullName)
            .ToArray();
        Assert.That(missing, Is.Empty, "put each of these classes in a script file named after it");
    }

    [Test]
    public void CoreAssemblyDefinitionHasNoEngineReferences()
    {
        var info = PackageInfo.FindForAssetPath("Packages/com.katout.flowtask/package.json");
        var asmdef = File.ReadAllText(Path.Combine(info.resolvedPath, "FlowTask.asmdef"));
        Assert.That(asmdef, Does.Contain("\"noEngineReferences\": true"));
        Assert.That(asmdef, Does.Contain("\"autoReferenced\": true"));
    }

    [Test]
    public void AnalyzerDllIsImportedAsARoslynAnalyzer()
    {
        const string dll = "Packages/com.katout.flowtask/Analyzers/FlowTask.Analyzers.dll";
        var info = PackageInfo.FindForAssetPath("Packages/com.katout.flowtask/package.json");
        var path = Path.Combine(info.resolvedPath, "Analyzers", "FlowTask.Analyzers.dll");
        if (!File.Exists(path)) Assert.Ignore("FlowTask.Analyzers.dll is missing from the package. It is committed in src/FlowTask/Analyzers/: restore it with git, or build src/FlowTask.Analyzers in Release on Windows, which writes it there.");
        var labels = AssetDatabase.GetLabels(AssetDatabase.LoadMainAssetAtPath(dll));
        Assert.That(labels, Does.Contain("RoslynAnalyzer"));
        var importer = (PluginImporter)AssetImporter.GetAtPath(dll);
        Assert.That(importer.GetCompatibleWithAnyPlatform(), Is.False);
        Assert.That(importer.GetCompatibleWithEditor(), Is.False);
    }

    [Test]
    public void NoWorldMessageInEditModeSaysToUseAWorldOfYourOwn()
    {
        Assert.That(FlowTaskUnity.IsInstalled, Is.False, "the default World exists only in Play Mode");
        var noWorld = Assert.Throws<InvalidOperationException>(() => _ = FlowTaskUnity.World);
        Assert.That(noWorld.Message, Does.StartWith("No FlowTask World"));
        Assert.That(noWorld.Message, Does.Contain("only in Play Mode"));
        var go = new GameObject("NoWorld");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => go.RunWhileActive(FlowTask.CompletedTask));
            Assert.That(ex.Message, Does.StartWith("No FlowTask World"));
            Assert.That(ex.Message, Does.Contain("only in Play Mode"));
            Assert.That(ex.Message, Does.Contain("a FlowWorld of your own"));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
        }
    }
}
