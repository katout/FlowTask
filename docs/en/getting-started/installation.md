# Installation

This page explains how to add FlowTask to a .NET, Unity, or Godot project. Besides the core, there are optional packages for testing, UniTask, and R3.

## Packages

| Package | Distribution | Contents |
|---|---|---|
| FlowTask | NuGet `FlowTask`, UPM `com.katout.flowtask` | The core (FlowTask, scopes, World, Clock, signals, Task bridges) and the analyzers |
| FlowTask.Unity | UPM `com.katout.flowtask.unity` | PlayerLoop integration, Unity bridges, GameObject lifetime, the Scope Tree window |
| FlowTask.Godot | NuGet `FlowTask.Godot` | `_Process` integration, Godot signal bridges, node lifetime |
| FlowTask.Testing | NuGet `FlowTask.Testing`, UPM `com.katout.flowtask.testing` | Testing without an engine (`TestWorld`, virtual time, scope tree assertions) |
| FlowTask.Testing.NUnit | NuGet `FlowTask.Testing.NUnit`, UPM `com.katout.flowtask.testing.nunit` | Turns unhandled exceptions into NUnit test failures |
| FlowTask.UniTask | NuGet `FlowTask.UniTask`, UPM `com.katout.flowtask.unitask` | Lets UniTask and FlowTask await each other |
| FlowTask.R3 | NuGet `FlowTask.R3` | Connects R3 Observables and Signals |

All dependencies point to the core, and the core depends on no other package. The namespace is `Katout.FlowTask` (the engine integration and testing packages have namespaces under it).

## .NET

```sh
dotnet add package FlowTask --version 0.1.0-preview.1
```

- The targets are netstandard2.1 and net10.0. Projects such as net8.0 use the netstandard2.1 build. It also works with NativeAOT.
- The analyzers and code fixes are included in the package and work as soon as you reference it. Messages appear in English or Japanese, following the display language of the IDE and `dotnet build` (the OS display language or `DOTNET_CLI_UI_LANGUAGE`) ([Analyzer rules](../tools/analyzers.md)).

Add the optional packages the same way.

```sh
dotnet add package FlowTask.Testing --version 0.1.0-preview.1
dotnet add package FlowTask.Testing.NUnit --version 0.1.0-preview.1
dotnet add package FlowTask.UniTask --version 0.1.0-preview.1
dotnet add package FlowTask.R3 --version 0.1.0-preview.1
```

| Package | Installed as dependencies |
|---|---|
| FlowTask.Testing.NUnit | NUnit 3.14 or later |
| FlowTask.UniTask | The UniTask NuGet package 2.5.10 or later |
| FlowTask.R3 | R3 1.3.1 or later |

Once it's installed, try running a World in [Your first flow](first-flow.md).

## Unity

FlowTask works with Unity 2023.1 and later. Add the core and the Unity integration to `Packages/manifest.json` with git URLs.

```json
{
  "dependencies": {
    "com.katout.flowtask": "https://github.com/katout/FlowTask.git?path=src/FlowTask",
    "com.katout.flowtask.unity": "https://github.com/katout/FlowTask.git?path=src/FlowTask.Unity"
  }
}
```

You can also enter them one at a time in the Package Manager's "Add package from git URL...".

> **Note**: the integration package depends on the core, but UPM doesn't fetch git URL dependencies automatically. Always list the core `com.katout.flowtask` too. When you add optional packages, also list every FlowTask package they depend on.

### Pinning a version

Add `#` and a tag (or a commit) to the end of the URL to pin that version. Use the same tag for all FlowTask packages.

```json
{
  "dependencies": {
    "com.katout.flowtask": "https://github.com/katout/FlowTask.git?path=src/FlowTask#v0.1.0-preview.1",
    "com.katout.flowtask.unity": "https://github.com/katout/FlowTask.git?path=src/FlowTask.Unity#v0.1.0-preview.1"
  }
}
```

### Testing packages

Add the testing packages to `dependencies`, and **also list them in `testables`**.

```json
{
  "dependencies": {
    "com.katout.flowtask": "https://github.com/katout/FlowTask.git?path=src/FlowTask",
    "com.katout.flowtask.testing": "https://github.com/katout/FlowTask.git?path=src/FlowTask.Testing",
    "com.katout.flowtask.testing.nunit": "https://github.com/katout/FlowTask.git?path=src/FlowTask.Testing.NUnit"
  },
  "testables": ["com.katout.flowtask.testing", "com.katout.flowtask.testing.nunit"]
}
```

- These two assemblies are compiled only in the editor and in test players, so they don't end up in regular players. Unity compiles such assemblies only for packages listed in `testables`, so without it, tests that reference them stop with `CS0246`. The two packages contain no tests themselves, so they don't add entries to the Test Runner.
- `com.katout.flowtask.testing.nunit` depends on `com.unity.test-framework` 1.1.33 or later.
- How to write the test asmdef is covered in [Testing without an engine](../tools/testing.md).

### UniTask bridge

Add `com.katout.flowtask.unitask`, and install UniTask itself separately with a git URL or OpenUPM. In Unity, the bridge is compiled when the UPM package `com.cysharp.unitask` 2.0.0 or later is present (the 2.5.10 minimum is the dependency of the NuGet package).

```json
"com.katout.flowtask.unitask": "https://github.com/katout/FlowTask.git?path=src/FlowTask.UniTask",
"com.cysharp.unitask": "https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask#2.5.10"
```

- If UniTask isn't in the project, the bridge is silently not compiled (no error either).
- If you installed UniTask as a `.unitypackage` in `Assets/Plugins/UniTask`, it isn't visible as a package, so the bridge again isn't compiled, and code that uses `FlowUniTask` fails with `CS0246`. Add `FLOWTASK_UNITASK` under Player Settings > Other Settings > Scripting Define Symbols, for each platform.
- Usage is covered in [UniTask](../integrations/unitask.md).

`FlowTask.R3` has no UPM package.

### Things to know on Unity

- The integration creates a World at startup and Ticks it from the PlayerLoop every frame. You don't Tick it yourself. Start flows with `FlowTaskUnity.World.Run(...)`.
- The package sources are C# 10. Unity's default is C# 9, but a `csc.rsp` next to each assembly raises the language version for that assembly only. Your code works with the default.
- The analyzer DLL is included in the core package. The error rules (FLOW001, FLOW002, FLOW005) also stop Unity's compilation.
- If your code is in an asmdef, add `FlowTask` and `FlowTask.Unity` to its references.

The assembly layout, how FlowTask relates to the PlayerLoop, and the settings are covered in [Unity setup](../unity/setup.md).

## Godot

FlowTask works with the .NET edition of Godot 4.4.1 and later. Add `FlowTask.Godot` to your game's `.csproj`. The core `FlowTask` and the analyzers come in as dependencies.

```xml
<ItemGroup>
  <PackageReference Include="FlowTask.Godot" Version="0.1.0-preview.1" />
</ItemGroup>
```

`dotnet add package FlowTask.Godot --version 0.1.0-preview.1` does the same.

| | Supported |
|---|---|
| Godot | .NET edition of 4.4.1 and later (the package depends on `GodotSharp` 4.4.1 or later) |
| Game project | `Godot.NET.Sdk/4.4.1`, `net8.0` |
| .NET SDK | Godot 4.4's requirement (.NET 8 or later) |

- FlowTask.Godot targets net8.0 and uses the netstandard2.1 build of the core. The features and public API are the same.
- Keep the engine version and the `Godot.NET.Sdk` version the same.
- Add the optional packages (testing, UniTask, R3) from NuGet, as in the .NET section above.

Once it's installed, write a one-line subclass of `FlowWorldNode` and register it as an autoload.

```csharp
// res://FlowAutoload.cs (the class name must match the file name)
public partial class FlowAutoload : Katout.FlowTask.Godot.FlowWorldNode { }
```

Registering the autoload and starting flows are covered in [Godot setup](../godot/setup.md).

## Supported versions

| Environment | Version |
|---|---|
| .NET | netstandard2.1 / net10.0 (including NativeAOT) |
| Unity | 2023.1 and later (Mono, IL2CPP) |
| Godot | .NET edition of 4.4.1 and later (net8.0) |
| Bridges | UniTask 2.x (2.5.10 or later on NuGet, 2.0.0 or later in Unity), R3 1.3.1 or later |

FlowTask is verified on the following environments.

- The Unity 6 (6000.3) Editor (EditMode, PlayMode)
- Unity players on Windows (Mono, IL2CPP)
- An IL2CPP build for Android (build only; not run on a device)
- Godot 4.4.1 (built with .NET SDK 10)
- .NET NativeAOT

It hasn't been verified on Android or iOS devices, on WebGL, on Unity versions before 6000.3 (including 2023.1 and 2023.2 themselves), or on Godot versions other than 4.4.1.

## What to read next

- [Your first flow](first-flow.md): run a World in a console program
- [Unity setup](../unity/setup.md), [Godot setup](../godot/setup.md)
- Samples: [Unity](../unity/samples.md) (imported from the Package Manager), [Godot](../godot/samples.md) (open `samples/godot`)
