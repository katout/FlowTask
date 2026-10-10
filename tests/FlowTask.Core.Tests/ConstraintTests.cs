// Not compiled in Unity: it inspects the IL of FlowTask.dll with System.Reflection.Metadata (PEReader), which is
// not part of Unity's .NET profile, and IL2CPP players have no assembly file either. The IL2CPP build itself covers
// these constraints there (docs/maintainers/engine-tests.md).
#if !UNITY_5_3_OR_NEWER
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Katout.FlowTask.Tests;

/// <summary>Platform constraints checked on the compiled Core assembly.</summary>
public class ConstraintTests
{
    static readonly Assembly Core = typeof(FlowTask).Assembly;

    [Test]
    public void NoGenericVirtualMethods()
    {
        var offenders = new List<string>();
        foreach (var t in Core.GetTypes())
        {
            foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (m.IsGenericMethodDefinition && (m.IsVirtual || m.IsAbstract || t.IsInterface))
                    offenders.Add(t.FullName + "." + m.Name);
            }
        }

        Assert.That(offenders, Is.Empty, "generic virtual methods break IL2CPP/NativeAOT");
    }

    [Test]
    public void NoRuntimeCodeGenerationOrReflectionInvocation()
    {
        var forbiddenTypes = new[]
        {
            "System.Reflection.Emit", "System.Linq.Expressions", "System.Activator", "System.Reflection.MethodBase",
            "System.Reflection.MethodInfo", "System.Reflection.ConstructorInfo", "System.Reflection.FieldInfo",
            "System.Reflection.PropertyInfo", "System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject",
        };
        var forbiddenMembers = new[] { "MakeGenericType", "MakeGenericMethod", "GetMethod", "GetField", "GetProperty", "InvokeMember", "CreateInstance" };
        // On a reflection type or System.Delegate only: Invoke on a delegate type is an ordinary call.
        var forbiddenReflectionMembers = new[] { "Invoke", "CreateDelegate", "DynamicInvoke" };
        var found = new List<string>();
        // Static check of the built DLL; engines that load assemblies from memory (Godot) have no file to inspect.
        Assume.That(Core.Location, Is.Not.Null.And.Not.Empty, "Core assembly has no file location in this host");
        using var pe = new PEReader(File.OpenRead(Core.Location));
        var md = pe.GetMetadataReader();
        foreach (var h in md.TypeReferences)
        {
            var tr = md.GetTypeReference(h);
            var full = md.GetString(tr.Namespace) + "." + md.GetString(tr.Name);
            if (forbiddenTypes.Any(f => full.StartsWith(f, StringComparison.Ordinal))) found.Add("type " + full);
        }

        foreach (var h in md.MemberReferences)
        {
            var mr = md.GetMemberReference(h);
            var name = md.GetString(mr.Name);
            if (forbiddenMembers.Contains(name)) found.Add("member " + name);
            if (forbiddenReflectionMembers.Contains(name) && mr.Parent.Kind == HandleKind.TypeReference)
            {
                var parent = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
                var ns = md.GetString(parent.Namespace);
                var type = ns + "." + md.GetString(parent.Name);
                if (ns == "System.Reflection" || type == "System.Delegate") found.Add("member " + type + "." + name);
            }
        }

        Assert.That(found, Is.Empty, "reflection invocation / code generation is not allowed in Core");
    }

    [Test]
    public void NoFloatingPointFunctionWhoseResultDependsOnTheCpu()
    {
        // Clock time is double additions, multiplications and comparisons. These functions can give other last bits on
        // another CPU or runtime (the C runtime picks a code path by CPU features; an estimate or a fused multiply-add
        // rounds differently), and then the Tick a time wait resumes on would depend on the machine
        // (docs/en/advanced/execution-model.md, "Scope of determinism").
        var cpuDependentMath = new[]
        {
            "Sin", "Cos", "Tan", "SinCos", "Asin", "Acos", "Atan", "Atan2", "Sinh", "Cosh", "Tanh", "Asinh", "Acosh",
            "Atanh", "Exp", "Log", "Log2", "Log10", "Pow", "Cbrt", "FusedMultiplyAdd", "ReciprocalEstimate",
            "ReciprocalSqrtEstimate",
        };
        var cpuDependentNamespaces = new[] { "System.Runtime.Intrinsics", "System.Numerics" };
        var found = new List<string>();
        Assume.That(Core.Location, Is.Not.Null.And.Not.Empty, "Core assembly has no file location in this host");
        using var pe = new PEReader(File.OpenRead(Core.Location));
        var md = pe.GetMetadataReader();
        foreach (var h in md.TypeReferences)
        {
            var tr = md.GetTypeReference(h);
            var ns = md.GetString(tr.Namespace);
            if (cpuDependentNamespaces.Any(n => ns == n || ns.StartsWith(n + ".", StringComparison.Ordinal)))
                found.Add("type " + ns + "." + md.GetString(tr.Name));
        }

        foreach (var h in md.MemberReferences)
        {
            var mr = md.GetMemberReference(h);
            if (mr.Parent.Kind != HandleKind.TypeReference) continue;
            var parent = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
            var type = md.GetString(parent.Namespace) + "." + md.GetString(parent.Name);
            var name = md.GetString(mr.Name);
            if ((type == "System.Math" || type == "System.MathF") && cpuDependentMath.Contains(name)) found.Add("member " + type + "." + name);
        }

        Assert.That(found, Is.Empty, "Core must not use floating-point functions whose result depends on the CPU");
    }

    [Test]
    public void CoreDependsOnlyOnTheBaseLibrary()
    {
        var refs = Core.GetReferencedAssemblies().Select(a => a.Name).ToArray();
        var external = refs.Where(n => !(n == "netstandard" || n == "mscorlib" || n.StartsWith("System", StringComparison.Ordinal) || n.StartsWith("Microsoft.Win32", StringComparison.Ordinal))).ToArray();
        Assert.That(external, Is.Empty, "Core must not depend on external libraries: " + string.Join(", ", refs));
    }

    [Test]
    public void CoreIsCSharp10OnNetStandard21AndNet10()
    {
        var testDirectory = TestContext.CurrentContext.TestDirectory;
        Assume.That(testDirectory, Is.Not.Null.And.Not.Empty, "no test directory in this host (static check of the repository)");
        var dir = new DirectoryInfo(testDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FlowTask.slnx"))) dir = dir.Parent;
        Assume.That(dir, Is.Not.Null, "repository root not found");
        var src = Path.Combine(dir!.FullName, "src");
        var csproj = File.ReadAllText(Path.Combine(src, "FlowTask", "FlowTask.csproj"));
        Assert.That(csproj, Does.Contain("<TargetFrameworks>netstandard2.1;net10.0</TargetFrameworks>"));
        // C# 10: the newest version that every Unity 2023.1+ compiles (its Roslyn is 4.x); Godot 4.4 games build with the
        // .NET 8 SDK or newer, which compiles C# 12 (CONTRIBUTING.md). Unity compiles its own assemblies with C# 9, so the
        // csc.rsp next to each .asmdef of the UPM packages raises them to the version the .NET build uses.
        Assert.That(csproj, Does.Contain("<LangVersion>10.0</LangVersion>"));
        Assert.That(csproj, Does.Not.Contain("<PackageReference"), "Core has no package dependencies");
        // Not the samples (Samples~): imported into a game, they compile as its own code, with Unity's default C# 9.
        var asmdefs = Directory.GetFiles(src, "*.asmdef", SearchOption.AllDirectories)
            .Where(a => !a.Contains("~" + Path.DirectorySeparatorChar, StringComparison.Ordinal)).ToArray();
        Assert.That(asmdefs, Is.Not.Empty);
        var wrong = asmdefs.Select(a => Path.Combine(Path.GetDirectoryName(a)!, "csc.rsp"))
            .Where(rsp => !File.Exists(rsp) || File.ReadAllText(rsp).Trim() != "-langversion:10.0")
            .Select(rsp => Path.GetRelativePath(src, rsp)).ToArray();
        Assert.That(wrong, Is.Empty, "every .asmdef under src/ needs a csc.rsp next to it with exactly -langversion:10.0");
    }
}
#endif
