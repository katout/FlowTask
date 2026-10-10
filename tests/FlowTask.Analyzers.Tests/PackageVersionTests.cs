using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Katout.FlowTask.Analyzers.Tests;

/// <summary>
/// The library version is written only in Directory.Build.props, which every NuGet package takes. Unity cannot read
/// MSBuild files, so each UPM package (src/*/package.json) repeats it, in its own version and in its dependencies on
/// the other FlowTask packages. The installation steps in docs/ repeat it too.
/// </summary>
public class PackageVersionTests
{
    // https://semver.org/#is-there-a-suggested-regular-expression-regex-to-check-a-semver-string (UPM needs SemVer).
    const string SemVer = @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-((?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?(?:\+([0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?$";

    [Test]
    public void NoProjectFileSetsItsOwnVersion()
    {
        var root = UnityAnalyzerFilesTests.RepositoryRoot();
        var projects = Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories);
        Assert.That(projects, Is.Not.Empty);
        foreach (var project in projects)
        {
            var text = File.ReadAllText(project);
            Assert.That(Regex.IsMatch(text, "<(Version|PackageVersion|VersionPrefix|VersionSuffix|AssemblyVersion|FileVersion|InformationalVersion)>"),
                Is.False, Path.GetRelativePath(root, project) + " sets a version property; the version lives in Directory.Build.props");
        }
    }

    [Test]
    public void EveryUpmPackageCarriesTheLibraryVersion()
    {
        var root = UnityAnalyzerFilesTests.RepositoryRoot();
        var version = LibraryVersion(root);

        var manifests = Directory.GetFiles(Path.Combine(root, "src"), "package.json", SearchOption.AllDirectories);
        Assert.That(manifests, Is.Not.Empty);
        foreach (var manifest in manifests)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(manifest));
            var name = Path.GetRelativePath(root, manifest);
            Assert.That(json.RootElement.GetProperty("version").GetString(), Is.EqualTo(version), name);
            if (!json.RootElement.TryGetProperty("dependencies", out var dependencies)) continue;
            var own = dependencies.EnumerateObject().Where(d => d.Name.StartsWith("com.katout.flowtask", System.StringComparison.Ordinal));
            foreach (var dependency in own)
                Assert.That(dependency.Value.GetString(), Is.EqualTo(version), name + ": " + dependency.Name);
        }
    }

    // The documentation tells users which version to install: dotnet add package --version, a PackageReference, the tag
    // of a git URL. The site is published at each release (.github/workflows/docs.yml) and the README in the NuGet
    // packages (src/package-readme.md) ships with it, so these name that release.
    [Test]
    public void TheDocumentationInstallsTheLibraryVersion()
    {
        var root = UnityAnalyzerFilesTests.RepositoryRoot();
        var version = LibraryVersion(root);
        var install = new Regex(@"(?:FlowTask[\w.]* --version |FlowTask\.git\?path=src/[\w.]+#v|Include=""FlowTask[\w.]*"" Version="")(?<version>[^\s""`]+)");
        var pages = Directory.GetFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(root, "README*.md"))
            .Append(Path.Combine(root, "src", "package-readme.md"));

        var found = 0;
        foreach (var page in pages)
        {
            foreach (Match match in install.Matches(File.ReadAllText(page)))
            {
                found++;
                Assert.That(match.Groups["version"].Value, Is.EqualTo(version), Path.GetRelativePath(root, page) + ": " + match.Value);
            }
        }
        Assert.That(found, Is.Not.Zero, "no FlowTask version found in the documentation: update the pattern to the installation steps");
    }

    // A release pull request bumps the version and dates its section of CHANGELOG.md together, so the newest dated
    // section is the version's. This fails in that pull request, before the tag, where a mistake still costs nothing.
    [Test]
    public void TheNewestReleaseInTheChangelogIsTheLibraryVersion()
    {
        var root = UnityAnalyzerFilesTests.RepositoryRoot();
        var version = LibraryVersion(root);
        var changelog = File.ReadAllText(Path.Combine(root, "CHANGELOG.md"));
        var newest = Regex.Match(changelog, @"^## \[(?<version>[^\]]+)\] - \d{4}-\d{2}-\d{2}", RegexOptions.Multiline);
        Assume.That(newest.Success, "no dated section in CHANGELOG.md: nothing has been released yet");
        Assert.That(newest.Groups["version"].Value, Is.EqualTo(version), "the newest dated section of CHANGELOG.md");
    }

    static string LibraryVersion(string root)
    {
        var props = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));
        var version = Regex.Match(props, "<Version>([^<]+)</Version>").Groups[1].Value;
        Assert.That(version, Does.Match(SemVer), "the <Version> of Directory.Build.props");
        return version;
    }
}
