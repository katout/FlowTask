# FlowTask Roslyn analyzers for Unity

This folder holds `FlowTask.Analyzers.dll`, the FLOW rules (see https://katout.github.io/FlowTask/en/tools/analyzers/) built from `src/FlowTask.Analyzers`
(netstandard2.0), so that the UPM package `com.katout.flowtask` carries them.

`FlowTask.Analyzers.dll.meta` gives the DLL the `RoslynAnalyzer` asset label and disables every platform. Unity then
passes the DLL to the C# compiler for every assembly that references the `FlowTask` assembly instead of loading it as a plugin.
The .meta file must keep its GUID; restore it with git if it is lost.

Only the analyzer goes here. The code fixes need Roslyn's Workspaces, which Unity does not load, and Unity compiles with
`/preferreduilang:en-US`, so the Japanese messages (a satellite assembly) would never be used.

The DLL is committed. A Release build on Windows (`dotnet build FlowTask.slnx -c Release`) rewrites it whenever the
built bytes differ, and says so: commit it. The build is reproducible on Windows (the same bytes for every location and
SDK patch version); whether other systems produce the same bytes is not verified, so only a Windows build writes it.
The DLL carries the library version, so a version change in `Directory.Build.props` changes it too. CI fails when the
committed DLL differs from a build of the sources, and the analyzer tests (`UnityAnalyzerFilesTests`) fail when the
.meta file loses the label or enables a platform, or when this folder holds another DLL.
