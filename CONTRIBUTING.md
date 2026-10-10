English | [日本語](CONTRIBUTING.ja.md)

# Contributing to FlowTask

Contributions are welcome. Please report bugs and send suggestions through [Issues](https://github.com/katout/FlowTask/issues). For large changes or additions to the public API, please open an issue to discuss them before you open a pull request (see [Public API](#public-api)).

The FlowTask core is pure C# (netstandard2.1 and net10.0, C# 10), and the same tests run on .NET, Unity, Godot, and NativeAOT. Related documents:

- User documentation: [docs/en/](docs/en/index.md)
- Internals: [docs/maintainers/internals.md](docs/maintainers/internals.md) (Japanese)
- How the engine tests work: [docs/maintainers/engine-tests.md](docs/maintainers/engine-tests.md) (Japanese)
- Branches, CI and releases: [docs/maintainers/ci-and-release.md](docs/maintainers/ci-and-release.md) (Japanese)
- Reasons behind the design: [Design rationale](docs/en/advanced/design-rationale.md)

## Requirements

- .NET SDK 10.0.300 or later in the 10.0 line (`global.json`). This is all you need to build, run the tests, and run the tools in `tools/`.
- Unity: 6000.3.7f1 (use `-UnityExe` to point to it). The IL2CPP and Android suites need their modules.
- Godot: the script downloads the .NET build of Godot 4.4.1 and tests with it (use `-GodotExe` to use your own).
- Engine tests and rebuilding the analyzer DLL are done on Windows.

## Pull requests

- Work on a branch from main and open a pull request to main. The maintainer's changes go the same way.
- Pull requests are squash-merged only: one pull request becomes one commit on main.
- A pull request needs the CI check `ci-ok` to pass (.NET on Windows, Linux and macOS, NativeAOT, Godot).
- The Unity tests (`unity.yml`) use the license secrets, so they do not run for pull requests from forks. The maintainer also checks changes that touch Unity locally.

## Build and test

```sh
dotnet build FlowTask.slnx -c Release
dotnet test tests/FlowTask.Core.Tests -c Debug
dotnet test tests/FlowTask.Core.Tests -c Release
dotnet test tests/FlowTask.Analyzers.Tests
dotnet test tests/FlowTask.Bridges.Tests
powershell -ExecutionPolicy Bypass -File tools/pack.ps1   # the 6 NuGet packages (artifacts/packages)
```

The tools in `tools/` are .NET file-based apps. Run them with `dotnet tools/<name>.cs`. Tools are written in C# too. Before you add a new check, first consider whether it can live in the build or in a test.

## Engine tests

```powershell
./tools/unity/run-tests.ps1                     # EditMode, PlayMode, the Mono player, and the Android IL2CPP build
./tools/unity/run-tests.ps1 -Suites EditMode    # about 1 minute
./tools/godot/run-smoke.ps1                     # Godot 4 headless; the core tests also run inside Godot
dotnet publish tests/FlowTask.AotSmoke -c Release -r win-x64   # then run the exe it produces
```

- When a change reaches the engine integrations or the scheduler, check it on Unity, Godot, and NativeAOT. For the Windows IL2CPP player, use `-Suites StandaloneIl2cpp`.
- Always run EditMode for changes that remove or change public API, and for changes that touch `src/FlowTask.Unity` or `tests/unity/`. The Unity integration tests (`tests/unity/Assets/Tests/Unity`) are not compiled by dotnet, and the Unity job in CI (`unity.yml`) runs only when the license secrets are present.
- A stuck test is stopped after 15 minutes in EditMode and PlayMode, and after 30 minutes in a player. Its name appears under `StuckTest` in `tests/unity/TestResults/summary.json` (change the limit with `-TimeoutMinutes`).
- The Unity samples are in the UPM package, in `src/FlowTask.Unity/Samples~/`, and the Godot samples in `samples/godot/`. `run-tests.ps1` copies the Unity samples into `tests/unity/` and tests them; `run-smoke.ps1` builds the Godot samples, starts their demo, and checks them with simulated input. The engine-free files exist once for each engine, so fix both when you change one.
- For the test project `tests/unity/`, the Godot smoke test, and the license setup for Unity in CI, see [docs/maintainers/engine-tests.md](docs/maintainers/engine-tests.md) (Japanese).

## Writing tests

- Core tests use only the public API. Observe internal state through `FlowWorld.Diagnostics`. Do not put `InternalsVisibleTo` in the core assembly (FlowTask). The two exceptions are FlowTask.Unity and FlowTask.Analyzers.
- `tests/FlowTask.Core.Tests/*.cs` also runs on Unity (`run-tests.ps1` copies the files as they are) and on Godot (`tests/godot`). Write anything that differs on Unity inside the same file with `#if UNITY_5_3_OR_NEWER`.
- Write tests so that they fail instead of hanging. Give loops that drive the scheduler an upper bound and fail with an assertion, and wait for other threads with `TestThreads.JoinOrFail` / `WaitOrFail` (30 seconds). Do not put timeouts in test code (`[Timeout]` is not available on .NET, and `[CancelAfter]` does not stop a loop inside the library).
- Through `tests/test.runsettings`, `dotnet test` stops a single test after 2 minutes and a project after 10 minutes, and prints the name of the test that was running. The summary line may still say the run passed, so judge the result by the exit code. For long runs, add `-- RunConfiguration.TestSessionTimeout=0`.

## Public API

We keep the public API (types, members, settings) small. Add to it only when both of these are true:

1. There is a real need: an integration package or a sample uses it, or a user has a concrete scenario.
2. It cannot be written by combining the existing public API (if it takes one or two lines, do not add it).

"Nice to have", "another library has it", and "for symmetry" are not reasons. If something can be written two ways, reduce it to one. Add new enum values at the end. While the version is 0.x, breaking changes are allowed, but record them in CHANGELOG.md.

When you change the public API, also check the places that use it but are not compiled by `FlowTask.slnx`: `src/FlowTask.Unity` and its samples (`Samples~`), `tests/unity/`, `samples/godot/` and `tests/godot/` (built by `run-smoke.ps1`), the README, `src/package-readme.md`, and the examples in `docs/`.

## Build rules

All warnings are errors, and the analysis level is fixed at 10.0 (`Directory.Build.props`, so that updating the SDK alone does not add rules). The libraries in `src/` also get all CA rules and code style rules (`src/Directory.Build.props`). `tests/godot/Directory.Build.props` and `samples/godot/Directory.Build.props` repeat the root values, so change them together with them.

The sources of the core (FlowTask), Testing, Testing.NUnit, and UniTask also get the FLOW rules. Unity users compile these from source, so any FLOW warning would show up in every user's console. FlowTask.Unity is not compiled by dotnet, so if you touch it, check that the Unity log has no `warning FLOW` from the package sources.

You may disable a rule only when it conflicts with a design decision. When you do, write the reason right there:

- Whole repository: in `.editorconfig`, with the reason on the line before.
- One place: `[SuppressMessage(..., Justification = "…")]`, or the reason at the end of the `#pragma warning disable` line.
- Do not silence rules by adding them to `NoWarn`.

`catch (Exception)` is allowed only where the code catches an exception from user code (callbacks, conditions, cleanup) and rethrows it at an await or turns it into a report (after the World that would report it is disposed, it drops it). Wrap such places in `#pragma warning disable CA1031 // reason`.

If a dependency has a known vulnerability, restore fails, even for transitive dependencies (`NuGetAuditMode=all`). Fix it by updating the dependency, or by referencing the fixed version directly with a reason. Do not use `NuGetAuditSuppress`.

## Language version and style

The libraries in `src/` and the core tests are written in C# 10 (`LangVersion` 10.0). It is the newest version that works both on Unity 2023.1 and later and on the .NET 8 SDK that builds Godot 4.4 games.

- Use only features that are pure syntax. Features that need runtime support (static abstract members in interfaces, ref fields) do not work on Mono and IL2CPP. `CallerArgumentExpression` and interpolated string handlers are not used because netstandard2.1 lacks their types. Types that only carry values are records. The `IsExternalInit` that their init accessors need is defined as internal in the core (FlowTask) and in FlowTask.Unity.
- Analyzers and code fixes are written with `latest` and built against Roslyn 3.8 (the .NET 5 SDK, the oldest environment that loads the analyzers). The analyzer tests also use 3.8 on purpose.
- Unity compiles with C# 9 by default, so each `.asmdef` in the UPM packages has a `csc.rsp` next to it that contains only the line `-langversion:10.0`. It applies only to that assembly; user code keeps the default. If you add an asmdef, add the same `csc.rsp` and its `.meta` too (`ConstraintTests` checks this). The samples in `Samples~` have none: imported into a project, they compile as the user's code, with the default C# 9.
- Use one file-scoped namespace per file, and keep public types and `Katout.FlowTask.Internal` types in separate files. Only files that define a MonoBehaviour or a ScriptableObject (including EditorWindow) use a block namespace, and they must be added to the list in `.editorconfig`. Unity finds these classes by parsing the files itself, and it does not find classes inside a file-scoped namespace (`MonoScript.GetClass()` returns null). A Unity editor test (`EveryMonoBehaviourAndScriptableObjectIsFoundThroughItsScript`) checks this.
- Namespaces used by many files go in a `GlobalUsings.cs` per assembly. Unity does not read csproj files, so do not use `<Using>` in a csproj. On Unity and Godot, the core tests' global usings also apply to the other files in the same assembly, so they do not include `System.IO` (FileAccess) or `System.Threading` (Timer), which clash with Godot types. The Unity tests' global usings do not include `System` (Object, Random), which clashes with UnityEngine.
- `.editorconfig` requires C# 10 style in the `src/` build (except the samples in `Samples~`): file-scoped namespaces, `new()`, `??=`, `is A or B` and property patterns, switch expressions, ranges and indices, and `static` on lambdas that capture nothing. Patterns and switch expressions can produce slightly different IL from the longer forms, but hot-path measurements showed no difference beyond noise.
- The test assemblies in the Unity test project (`tests/unity/`) are compiled with `-langversion:latest` (C# 10 with Unity 6's Roslyn 4.3.1). The samples, the sample tests, the UniTask tests, and the editor settings (`Assets/Editor/`) have no `csc.rsp` and stay on C# 9.

## Performance

- Do not allocate on hot paths (await, resume, Emit, Tick). Do not use LINQ, closures, boxing, or `params`. `AllocationTests` checks this.
- Measure before you merge a performance change, and leave it out if it has no effect. Compare before and after with `dotnet tools/bench/ab.cs` (it runs both alternately and compares medians). For several changes, remove them one at a time with `--base-dir` and compare. IL2CPP has no runtime PGO, so also look at the `--no-pgo` results. BenchmarkDotNet's short job is too noisy for before-and-after comparisons, so do not use it for that.

## Versions

- Write package versions only in `Directory.Packages.props`. Do not write `Version` in a csproj (if a different version is needed, use `VersionOverride` with a reason).
- The library's own version number goes in `Version` in `Directory.Build.props`. The `version` in `src/*/package.json` (UPM), the dependencies on FlowTask packages and the versions in the installation steps (`docs/` and `src/package-readme.md`) must be the same string. `PackageVersionTests` checks this. The version is bumped only in a release pull request ([CI and releases](docs/maintainers/ci-and-release.md), Japanese). When you bump the version, rebuild the analyzer DLL too.
- Add as few dependencies to the shipped packages as possible. For anything not under MIT or Apache-2.0, discuss it in an issue first.

## Generated files that are committed

| File | How it is made (CI checks that it is up to date) |
|---|---|
| `src/FlowTask/Combinators.g.cs`, `src/FlowTask/Internal/CombinatorNodes.g.cs` | `dotnet tools/gen_combinators.cs`. Do not edit by hand |
| `.meta` files in UPM packages (folders with a `src/*/package.json`, their samples in `Samples~` included) | Run `dotnet tools/unity/generate_meta.cs` after adding a file or folder. When you rename something, move its `.meta` too |
| `src/FlowTask/Analyzers/FlowTask.Analyzers.dll` (for Unity) | The Windows Release build rewrites it only when the bytes differ. Include it in the same commit |

With a different SDK feature band (for example 10.0.3xx and 10.0.4xx), the DLL can be rewritten even if the source did not change. In that case, do not commit it. If only the CI check fails, rebuild with the SDK version shown in the log.

## Analyzers

Change the diagnostic text (titles, messages, descriptions, code fix names) in both `src/FlowTask.Analyzers/Resources.resx` (English) and `Resources.ja.resx` (Japanese) in the same commit. The Japanese title must be the same sentence as the heading in [docs/ja/tools/analyzers.md](docs/ja/tools/analyzers.md) (`LocalizationTests`). Put the `<a id="flow00n"></a>` for each rule's section on both the Japanese and the English page (the help link of a diagnostic points to the English page; `GeneralTests`). When you change an analyzer, do a Release build on Windows and commit the DLL.

## Documentation

- User documentation lives in `docs/ja/` (Japanese, the original) and `docs/en/` (the English translation), and `website/` turns it into a site (GitHub Pages, `.github/workflows/docs.yml`). The site describes the released version, so it is updated at each release: a change to the documentation on main reaches the site with the next version. Both are written in plain Markdown that reads as-is on GitHub (start with a `# Heading`, and use relative `.md` paths for links).
- When you change the public API, behavior, or commands, update the matching page in `docs/ja/` and CHANGELOG.md in the same change. Update the English page in the same change too if you can; if not, updating only the Japanese page is fine. (The site does not show that an English page is out of date, so when the two differ a lot, delete the English page so that the site shows the original.)
- When you make or change a design decision, write the reason in [Design rationale](docs/ja/advanced/design-rationale.md) (Japanese, the original).
- To preview the site locally, run the following with Node.js 22 or later:

  ```sh
  cd website
  npm ci
  npm run dev     # http://localhost:4321/FlowTask/
  ```

## Code from other libraries

Do not bring in code from UniTask or other libraries, even under MIT (this project carries no third-party license notices). Take only ideas from them, write the structure yourself, and check that the field layout, method bodies, and interfaces do not resemble the original.

## Use of AI

- Much of the code and documentation in this repository was written with AI assistance (see `Co-Authored-By` in the commits). The maintainer reads all of it and is responsible for it.
- You may use AI for your contributions. However, read the whole change yourself and be able to explain it in review. Name the tools you used in the pull request description.
- The rules in the previous section apply to AI-written code too. Do not include third-party code.
- Working rules for agents are in [AGENTS.md](AGENTS.md) (the review skill for Claude Code is in `.claude/skills/`).
