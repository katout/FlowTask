// A/B comparison of FlowTask's hot path between two versions.
//
// Builds the benchmark project twice, against the working tree and against a base version, then runs the quick
// Stopwatch measurement (`FlowTask.Benchmarks hotpath <scenario>`, tests/FlowTask.Benchmarks/HotPath.cs) alternately and
// prints the median of each metric. Alternating runs cancel out most of the machine's drift, which is larger than the
// differences a hot-path change makes (BenchmarkDotNet's short job moved the UniTask baseline by 30% between two runs).
// Every scenario runs in its own process: in a shared process, dynamic PGO specializes shared code for the scenarios that
// ran first. --no-pgo turns dynamic PGO off (closer to IL2CPP and NativeAOT, which have no guarded devirtualization).
//
// The base gets the *current* benchmark sources, so only FlowTask differs between A and B.
//
// Usage:
//   dotnet tools/bench/ab.cs                     # base = HEAD (compare uncommitted changes)
//   dotnet tools/bench/ab.cs <git-ref>           # base = any ref at or after the namespace rename to Katout.FlowTask
//   dotnet tools/bench/ab.cs --base-dir <dir>    # base = a directory with the repository layout (src/FlowTask, ...)
//   dotnet tools/bench/ab.cs <ref> --new <ref2>  # compare two refs (bisecting); default new = the working tree
//   options: --runs N (default 5), --no-pgo, --only <substring> (scenarios whose name contains it)
//
// The benchmark sources use the current names (namespace Katout.FlowTask, src/FlowTask, FlowWorld, WaitForSeconds), so
// an older ref does not build. To compare with an older version, check it out, rename it to the current names and pass it
// with --base-dir.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;

internal static class AB
{
    static readonly string Root = Path.GetFullPath(Path.Combine(ScriptDirectory(), "..", ".."));
    static readonly string Bench = Path.Combine("tests", "FlowTask.Benchmarks");
    static readonly string[] RootFiles = { "Directory.Build.props", "Directory.Packages.props", ".editorconfig" };
    static readonly Regex Metric = new Regex(@"^(.+?):\s+([\d.]+) ");

    sealed class Options
    {
        public string Ref = "HEAD";
        public string BaseDir;
        public int Runs = 5;
        public bool NoPgo;
        public string Only = "";
        public string NewRef;
    }

    static int Main(string[] args)
    {
        var options = Parse(args);
        if (options == null)
        {
            Console.Error.WriteLine("usage: dotnet tools/bench/ab.cs [ref] [--base-dir <dir>] [--new <ref>] [--runs N] [--no-pgo] [--only <substring>]");
            return 2;
        }
        try
        {
            Compare(options);
            return 0;
        }
        catch (InvalidOperationException e)
        {
            // A failed step throws rather than calling Environment.Exit, so that Compare's finally deletes the temporary
            // folder.
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    static void Compare(Options options)
    {
        var tmp = Directory.CreateTempSubdirectory("flowtask-ab-").FullName;
        try
        {
            var baseTree = Path.Combine(tmp, "base");
            string label;
            if (options.BaseDir != null)
            {
                // Copied like the working tree below, so both sides build under the same conditions and the given
                // directory is not modified.
                CopyTree(Path.Combine(Path.GetFullPath(options.BaseDir), "src"), Path.Combine(baseTree, "src"), "bin", "obj");
                label = options.BaseDir;
            }
            else
            {
                Directory.CreateDirectory(baseTree);
                ExportRef(options.Ref, baseTree);
                label = options.Ref;
            }
            RequireCore(baseTree, label);
            PrepareBase(baseTree);
            // Both sides are built from copies in the same temporary layout: building one of them in place measurably
            // shifted a scenario (code placement), which a same-condition comparison did not reproduce.
            var newTree = Path.Combine(tmp, "new");
            if (options.NewRef != null)
            {
                Directory.CreateDirectory(newTree);
                ExportRef(options.NewRef, newTree);
                RequireCore(newTree, options.NewRef);
            }
            else
            {
                CopyTree(Path.Combine(Root, "src"), Path.Combine(newTree, "src"), "bin", "obj");
            }
            PrepareBase(newTree);
            Console.WriteLine($"building base ({label}) and {options.NewRef ?? "working tree"}...");
            var dlls = new Dictionary<string, string> { ["base"] = Build(baseTree), ["new"] = Build(newTree) };

            var environment = new Dictionary<string, string>();
            if (options.NoPgo)
                environment["DOTNET_TieredPGO"] = "0";
            var names = Scenarios(dlls["new"]).Where(n => n.Contains(options.Only, StringComparison.Ordinal)).ToList();
            var samples = dlls.Keys.ToDictionary(k => k, _ => names.ToDictionary(n => n, _ => new List<double>()));
            for (var i = 0; i < options.Runs; i++)
            {
                // ABBA: alternate which side runs first, so a drift within a pair does not favour one side.
                var order = i % 2 == 0 ? new[] { "base", "new" } : new[] { "new", "base" };
                foreach (var n in names)
                {
                    foreach (var k in order)
                        samples[k][n].Add(Measure(dlls[k], n, environment));
                }
                Console.WriteLine($"run {i + 1}/{options.Runs} done");
            }

            Console.WriteLine();
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-20} {1,10} {2,10} {3,8}{4}",
                $"median of {options.Runs}", "base", "new", "diff", options.NoPgo ? "  (no PGO)" : ""));
            foreach (var n in names)
            {
                var b = Median(samples["base"][n]);
                var v = Median(samples["new"][n]);
                var diff = (100 * (v - b) / b).ToString("+0.0;-0.0;+0.0", CultureInfo.InvariantCulture);
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-20} {1,10:F1} {2,10:F1} {3,7}%", n, b, v, diff));
            }
        }
        finally
        {
            try
            {
                Directory.Delete(tmp, true);
            }
            catch (IOException)
            {
                // Left for the system's temporary-file cleanup, as a file a build server still holds may be.
            }
            catch (UnauthorizedAccessException)
            {
                // The same.
            }
        }
    }

    static Options Parse(string[] args)
    {
        var options = new Options();
        var positional = 0;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            var hasValue = i + 1 < args.Length;
            if (a == "--no-pgo")
                options.NoPgo = true;
            else if (a == "--base-dir" && hasValue)
                options.BaseDir = args[++i];
            else if (a == "--new" && hasValue)
                options.NewRef = args[++i];
            else if (a == "--only" && hasValue)
                options.Only = args[++i];
            else if (a == "--runs" && hasValue && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var runs) && runs > 0)
            {
                options.Runs = runs;
                i++;
            }
            else if (!a.StartsWith('-') && positional++ == 0)
                options.Ref = a;
            else
                return null;
        }
        return options;
    }

    /// <summary>Runs a command and returns its standard output; on failure prints its output and throws.</summary>
    static string Run(string cwd, IReadOnlyDictionary<string, string> environment, params string[] command)
    {
        var info = new ProcessStartInfo(command[0])
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in command.Skip(1))
            info.ArgumentList.Add(a);
        if (environment != null)
        {
            foreach (var (name, value) in environment)
                info.Environment[name] = value;
        }
        using var process = Process.Start(info);
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            Console.Error.Write(output + error.Result);
            throw new InvalidOperationException("failed: " + string.Join(" ", command));
        }
        return output;
    }

    static void ExportRef(string reference, string destination)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "archive", "--format=zip", reference, "src" }.Concat(RootFiles))
            info.ArgumentList.Add(a);
        using var process = Process.Start(info);
        var error = process.StandardError.ReadToEndAsync();
        using var archive = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(archive);
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git archive {reference} failed: {error.Result.Trim()}");
        archive.Position = 0;
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read);
        zip.ExtractToDirectory(destination);
    }

    static void RequireCore(string tree, string label)
    {
        // The benchmark project references src/FlowTask/FlowTask.csproj. A tree from before the core folder was renamed
        // has src/FlowTask.Core/FlowTask.Core.csproj, which is moved into place; one from before the public API rename
        // has src/FlowKit.Core instead.
        var legacy = Path.Combine(tree, "src", "FlowTask.Core");
        if (Directory.Exists(legacy) && !Directory.Exists(Path.Combine(tree, "src", "FlowTask")))
        {
            Directory.Move(legacy, Path.Combine(tree, "src", "FlowTask"));
            File.Move(Path.Combine(tree, "src", "FlowTask", "FlowTask.Core.csproj"), Path.Combine(tree, "src", "FlowTask", "FlowTask.csproj"));
        }
        if (!File.Exists(Path.Combine(tree, "src", "FlowTask", "FlowTask.csproj")))
        {
            throw new InvalidOperationException($"{label} has no src/FlowTask/FlowTask.csproj: refs from before the "
                + "public API rename (only in the history before the first public commit) do not build with the current "
                + "benchmark sources; pass a renamed tree with --base-dir");
        }
    }

    static void PrepareBase(string tree)
    {
        // Current benchmark sources and package versions, so only the library differs.
        var bench = Path.Combine(tree, Bench);
        if (Directory.Exists(bench))
            Directory.Delete(bench, true);
        CopyTree(Path.Combine(Root, Bench), bench, "bin", "obj", "BenchmarkDotNet.Artifacts");
        foreach (var f in RootFiles)
            File.Copy(Path.Combine(Root, f), Path.Combine(tree, f), true);
    }

    /// <summary>Copies a folder, leaving out every file and folder with one of the given names, at any depth.</summary>
    static void CopyTree(string source, string destination, params string[] ignored)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            if (!ignored.Contains(Path.GetFileName(file)))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
        foreach (var directory in Directory.GetDirectories(source))
        {
            if (!ignored.Contains(Path.GetFileName(directory)))
                CopyTree(directory, Path.Combine(destination, Path.GetFileName(directory)), ignored);
        }
    }

    static string Build(string tree)
    {
        Run(tree, null, "dotnet", "build", Path.Combine(tree, Bench), "-c", "Release", "-nologo", "-v", "q");
        return Path.Combine(tree, "artifacts", "bin", "FlowTask.Benchmarks", "Release", "net10.0", "FlowTask.Benchmarks.dll");
    }

    static List<string> Scenarios(string dll) =>
        Run(Path.GetDirectoryName(dll), null, "dotnet", dll, "hotpath", "--list")
            .Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList();

    static double Measure(string dll, string scenario, IReadOnlyDictionary<string, string> environment)
    {
        var output = Run(Path.GetDirectoryName(dll), environment, "dotnet", dll, "hotpath", scenario).Trim();
        var m = Metric.Match(output);
        if (!m.Success)
            throw new InvalidOperationException($"unexpected output for {scenario}: {output}");
        return double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
    }

    static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    static string ScriptDirectory() => AppContext.GetData("EntryPointFileDirectoryPath") as string
        ?? throw new InvalidOperationException("Run this file as a .NET file-based app: dotnet tools/bench/ab.cs");
}
