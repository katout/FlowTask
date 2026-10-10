// Deterministic Unity .meta generator for the FlowTask UPM packages.
//
// Unity needs a .meta file next to every file and folder of a package. For packages installed from git (immutable)
// Unity cannot write them itself, so they must be committed. This tool creates the missing ones with stable GUIDs:
//
//     guid = md5("<namespace>/<package-relative path>")
//
// where <namespace> is the "name" field of the root's package.json (e.g. "com.katout.flowtask"), or the root path relative
// to the repository when there is no package.json (a sample: src/FlowTask.Unity/Samples~/Scenarios). The namespace keeps
// GUIDs unique across packages that contain files with the same relative path (package.json, Runtime/, ...). Re-running
// the tool never changes an existing .meta.
//
// A GUID is the identity of an asset, so it is kept when the asset is renamed or moved (move the .meta with it). The
// .meta files that existed before the rename from FlowKit to FlowTask were moved with their assets and still
// carry the GUIDs computed from the old package names and paths (com.flowkit.core, com.flowkit.unity); only files added
// after the rename follow the formula above with the current names.
//
// Importers:
//   folders              -> folderAsset: yes + DefaultImporter
//   *.cs                 -> MonoImporter
//   *.asmdef             -> AssemblyDefinitionImporter
//   *.asmref             -> AssemblyDefinitionReferenceImporter
//   package.json         -> PackageManifestImporter
//   *.json/*.md/*.txt/.. -> TextScriptImporter
//   Analyzers/*.dll      -> PluginImporter, label RoslynAnalyzer, every platform disabled (Roslyn analyzer)
//   *.dll                -> PluginImporter (any platform)
//   everything else      -> DefaultImporter (e.g. the .NET FlowTask.csproj, harmless for Unity)
//
// Usage:
//   dotnet tools/unity/generate_meta.cs                 # default roots: every UPM package under src/ (src/*/package.json)
//                                                       # and each of its samples (src/*/Samples~/<sample>)
//   dotnet tools/unity/generate_meta.cs <root> [...]    # explicit roots
//   dotnet tools/unity/generate_meta.cs --check         # exit 1 if any .meta is missing (CI)
//   dotnet tools/unity/generate_meta.cs --prune         # also delete orphan .meta files

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class GenerateMeta
{
    static readonly string RepoRoot = Path.GetFullPath(Path.Combine(ScriptDirectory(), "..", ".."));

    static readonly HashSet<string> TextExtensions = new HashSet<string>(StringComparer.Ordinal)
    {
        ".json", ".md", ".txt", ".xml", ".yaml", ".yml", ".csv", ".bytes", ".html", ".htm",
    };

    const string Footer = "  userData: \n  assetBundleName: \n  assetBundleVariant: \n";

    static int Main(string[] args)
    {
        var check = false;
        var prune = false;
        var roots = new List<string>();
        foreach (var a in args)
        {
            if (a == "--check")
                check = true;
            else if (a == "--prune")
                prune = true;
            else if (a.StartsWith('-'))
                return Usage($"unknown option: {a}");
            else
                roots.Add(a);
        }
        var status = 0;
        foreach (var root in roots.Count > 0 ? roots : UpmPackageRoots())
            status = Math.Max(status, ProcessRoot(root, check, prune));
        return status;
    }

    static int Usage(string error)
    {
        Console.Error.WriteLine(error);
        Console.Error.WriteLine("usage: dotnet tools/unity/generate_meta.cs [--check] [--prune] [root ...]");
        Console.Error.WriteLine("       default roots: " + string.Join(" ", UpmPackageRoots()));
        return 2;
    }

    /// <summary>One root per UPM package: every folder src/&lt;name&gt;/ with a package.json (folder = UPM package = NuGet
    /// package). Found, not listed, so a package added under src/ is covered by --check without editing this tool (the
    /// same rule as the version check of src/Directory.Build.targets). Each sample of a package (a folder of its Samples~,
    /// which Unity skips in the package) is a root too: the Package Manager copies it into Assets/ with its .meta files,
    /// which keep the references of its scene.</summary>
    static List<string> UpmPackageRoots()
    {
        var src = Path.Combine(RepoRoot, "src");
        var roots = new List<string>();
        foreach (var name in Directory.GetDirectories(src).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal))
        {
            if (!File.Exists(Path.Combine(src, name, "package.json")))
                continue;
            roots.Add("src/" + name);
            var samples = Path.Combine(src, name, "Samples~");
            if (Directory.Exists(samples))
            {
                roots.AddRange(Directory.GetDirectories(samples).Select(Path.GetFileName).OrderBy(s => s, StringComparer.Ordinal)
                    .Select(s => $"src/{name}/Samples~/{s}"));
            }
        }
        return roots;
    }

    /// <summary>Path relative to the repository when possible (roots may live on another drive).</summary>
    static string Display(string path)
    {
        var rel = Path.GetRelativePath(RepoRoot, path);
        return Path.IsPathRooted(rel) ? Path.GetFullPath(path) : rel;
    }

    static string GuidFor(string ns, string relPath)
    {
#pragma warning disable CA5351 // Not security: Unity GUIDs are the md5 of a name, the formula every committed .meta follows.
        return Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(ns + "/" + relPath)));
#pragma warning restore CA5351
    }

    /// <summary>Unity ignores hidden entries, entries ending with '~', 'cvs' and '*.tmp'.</summary>
    static bool IsIgnored(string name)
    {
        var lower = name.ToLowerInvariant();
        return name.StartsWith('.') || name.EndsWith('~') || lower == "cvs" || lower.EndsWith(".tmp", StringComparison.Ordinal)
            || lower.EndsWith(".meta", StringComparison.Ordinal);
    }

    static string FolderMeta(string guid) =>
        "fileFormatVersion: 2\n"
        + $"guid: {guid}\n"
        + "folderAsset: yes\n"
        + "DefaultImporter:\n"
        + "  externalObjects: {}\n" + Footer;

    static string MonoMeta(string guid) =>
        "fileFormatVersion: 2\n"
        + $"guid: {guid}\n"
        + "MonoImporter:\n"
        + "  externalObjects: {}\n"
        + "  serializedVersion: 2\n"
        + "  defaultReferences: []\n"
        + "  executionOrder: 0\n"
        + "  icon: {instanceID: 0}\n" + Footer;

    static string SimpleMeta(string guid, string importer) =>
        "fileFormatVersion: 2\n"
        + $"guid: {guid}\n"
        + $"{importer}:\n"
        + "  externalObjects: {}\n" + Footer;

    // Unity manual "Roslyn analyzers and source generators": label RoslynAnalyzer, no platform selected.
    static string RoslynAnalyzerMeta(string guid) =>
        "fileFormatVersion: 2\n"
        + $"guid: {guid}\n"
        + "labels:\n"
        + "- RoslynAnalyzer\n"
        + "PluginImporter:\n"
        + "  externalObjects: {}\n"
        + "  serializedVersion: 2\n"
        + "  iconMap: {}\n"
        + "  executionOrder: {}\n"
        + "  defineConstraints: []\n"
        + "  isPreloaded: 0\n"
        + "  isOverridable: 0\n"
        + "  isExplicitlyReferenced: 0\n"
        + "  validateReferences: 1\n"
        + "  platformData:\n"
        + "  - first:\n"
        + "      : Any\n"
        + "    second:\n"
        + "      enabled: 0\n"
        + "      settings:\n"
        + "        Exclude Editor: 1\n"
        + "        Exclude Linux64: 1\n"
        + "        Exclude OSXUniversal: 1\n"
        + "        Exclude Win: 1\n"
        + "        Exclude Win64: 1\n"
        + "  - first:\n"
        + "      Any: \n"
        + "    second:\n"
        + "      enabled: 0\n"
        + "      settings: {}\n"
        + "  - first:\n"
        + "      Editor: Editor\n"
        + "    second:\n"
        + "      enabled: 0\n"
        + "      settings:\n"
        + "        CPU: AnyCPU\n"
        + "        DefaultValueInitialized: true\n"
        + "        OS: AnyOS\n"
        + "  - first:\n"
        + "      Standalone: Linux64\n"
        + "    second:\n"
        + "      enabled: 0\n"
        + "      settings:\n"
        + "        CPU: None\n"
        + "  - first:\n"
        + "      Standalone: OSXUniversal\n"
        + "    second:\n"
        + "      enabled: 0\n"
        + "      settings:\n"
        + "        CPU: None\n"
        + "  - first:\n"
        + "      Standalone: Win\n"
        + "    second:\n"
        + "      enabled: 0\n"
        + "      settings:\n"
        + "        CPU: None\n"
        + "  - first:\n"
        + "      Standalone: Win64\n"
        + "    second:\n"
        + "      enabled: 0\n"
        + "      settings:\n"
        + "        CPU: None\n" + Footer;

    static string PluginMeta(string guid) =>
        "fileFormatVersion: 2\n"
        + $"guid: {guid}\n"
        + "PluginImporter:\n"
        + "  externalObjects: {}\n"
        + "  serializedVersion: 2\n"
        + "  iconMap: {}\n"
        + "  executionOrder: {}\n"
        + "  defineConstraints: []\n"
        + "  isPreloaded: 0\n"
        + "  isOverridable: 0\n"
        + "  isExplicitlyReferenced: 0\n"
        + "  validateReferences: 1\n"
        + "  platformData:\n"
        + "  - first:\n"
        + "      Any: \n"
        + "    second:\n"
        + "      enabled: 1\n"
        + "      settings: {}\n" + Footer;

    static string FileMeta(string guid, string relPath)
    {
        var parts = relPath.Split('/');
        var name = parts[^1];
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext == ".cs")
            return MonoMeta(guid);
        if (ext == ".asmdef")
            return SimpleMeta(guid, "AssemblyDefinitionImporter");
        if (ext == ".asmref")
            return SimpleMeta(guid, "AssemblyDefinitionReferenceImporter");
        if (name == "package.json" && parts.Length == 1)
            return SimpleMeta(guid, "PackageManifestImporter");
        if (ext == ".dll")
            return parts[..^1].Contains("Analyzers") ? RoslynAnalyzerMeta(guid) : PluginMeta(guid);
        if (TextExtensions.Contains(ext))
            return SimpleMeta(guid, "TextScriptImporter");
        return SimpleMeta(guid, "DefaultImporter");
    }

    static string NamespaceFor(string root)
    {
        var manifest = Path.Combine(root, "package.json");
        if (File.Exists(manifest))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(manifest));
            if (json.RootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(name.GetString()))
            {
                return name.GetString();
            }
        }
        return Display(root).Replace(Path.DirectorySeparatorChar, '/');
    }

    // LF line endings regardless of platform (see .gitattributes), UTF-8 without a BOM.
    static void WriteMeta(string path, string content) => File.WriteAllText(path, content, new UTF8Encoding(false));

    static string RelativeTo(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    static int ProcessRoot(string rootArgument, bool check, bool prune)
    {
        var root = Path.GetFullPath(Path.IsPathRooted(rootArgument) ? rootArgument : Path.Combine(RepoRoot, rootArgument));
        root = Path.TrimEndingDirectorySeparator(root);
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"error: {root} is not a directory");
            return 1;
        }
        var ns = NamespaceFor(root);
        var expected = new Dictionary<string, string>(StringComparer.Ordinal); // meta path -> content
        CollectExpected(root, root, ns, expected);

        var missing = expected.Keys.Where(p => !File.Exists(p) && !Directory.Exists(p)).OrderBy(p => p, StringComparer.Ordinal).ToList();
        var created = 0;
        foreach (var p in missing)
        {
            if (check)
            {
                Console.WriteLine($"missing: {Display(p)}");
            }
            else
            {
                WriteMeta(p, expected[p]);
                created++;
                Console.WriteLine($"created: {Display(p)}");
            }
        }

        var orphans = 0;
        foreach (var p in MetaFiles(root))
        {
            if (expected.ContainsKey(p))
                continue;
            orphans++;
            if (prune && !check)
            {
                File.Delete(p);
                Console.WriteLine($"removed orphan: {Display(p)}");
            }
            else
            {
                Console.WriteLine($"orphan: {Display(p)}");
            }
        }

        Console.WriteLine($"{ns}: {expected.Count} expected, {missing.Count} missing, {created} created, {orphans} orphan");
        return check && missing.Count > 0 ? 1 : 0;
    }

    /// <summary>The .meta every folder and file below <paramref name="directory"/> needs, except what Unity ignores.</summary>
    static void CollectExpected(string root, string directory, string ns, Dictionary<string, string> expected)
    {
        var folders = Directory.GetDirectories(directory).Where(d => !IsIgnored(Path.GetFileName(d)))
            .OrderBy(d => d, StringComparer.Ordinal).ToList();
        foreach (var d in folders)
            expected[d + ".meta"] = FolderMeta(GuidFor(ns, RelativeTo(root, d)));
        foreach (var f in Directory.GetFiles(directory).Where(f => !IsIgnored(Path.GetFileName(f))).OrderBy(f => f, StringComparer.Ordinal))
        {
            var rel = RelativeTo(root, f);
            expected[f + ".meta"] = FileMeta(GuidFor(ns, rel), rel);
        }
        foreach (var d in folders)
            CollectExpected(root, d, ns, expected);
    }

    /// <summary>Every .meta below <paramref name="directory"/>, outside the hidden and '~' folders Unity skips.</summary>
    static IEnumerable<string> MetaFiles(string directory)
    {
        foreach (var f in Directory.GetFiles(directory))
        {
            if (f.EndsWith(".meta", StringComparison.Ordinal))
                yield return f;
        }
        foreach (var d in Directory.GetDirectories(directory))
        {
            var name = Path.GetFileName(d);
            if (name.StartsWith('.') || name.EndsWith('~'))
                continue;
            foreach (var f in MetaFiles(d))
                yield return f;
        }
    }

    static string ScriptDirectory() => AppContext.GetData("EntryPointFileDirectoryPath") as string
        ?? throw new InvalidOperationException("Run this file as a .NET file-based app: dotnet tools/unity/generate_meta.cs");
}
