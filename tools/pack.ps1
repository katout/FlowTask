# Builds every NuGet package into artifacts/packages (Release), each with its symbol package (.snupkg, the PDBs). The
# UPM packages are the folders src/FlowTask, src/FlowTask.Unity and the others with a package.json (installed with a git
# URL and ?path=...).
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$projects = @(
  "src/FlowTask/FlowTask.csproj",
  "src/FlowTask.Testing/FlowTask.Testing.csproj",
  "src/FlowTask.Testing.NUnit/FlowTask.Testing.NUnit.csproj",
  "src/FlowTask.Godot/FlowTask.Godot.csproj",
  "src/FlowTask.UniTask/FlowTask.UniTask.csproj",
  "src/FlowTask.R3/FlowTask.R3.csproj"
)
# ContinuousIntegrationBuild: the DLLs record /_/ instead of this machine's folders. --no-incremental: an earlier build
# without the flag would otherwise be reused.
& dotnet build (Join-Path $root "FlowTask.slnx") -c Release --nologo --no-incremental -p:ContinuousIntegrationBuild=true
if ($LASTEXITCODE -ne 0) { throw "build failed" }

# The build rewrites the analyzer DLL committed for Unity when its bytes differ; the FlowTask package and the UPM package
# carry the same analyzer only while git sees no change to it. --stat: see ci.yml.
& git -C $root diff --exit-code --stat -- src/FlowTask/Analyzers/FlowTask.Analyzers.dll
if ($LASTEXITCODE -ne 0) { throw "the build changed src/FlowTask/Analyzers/FlowTask.Analyzers.dll: commit it" }

$packages = Join-Path $root "artifacts/packages"
if (Test-Path $packages) { Remove-Item -Recurse -Force $packages }
foreach ($p in $projects) {
  & dotnet pack (Join-Path $root $p) -c Release --nologo --no-build -p:ContinuousIntegrationBuild=true
  if ($LASTEXITCODE -ne 0) { throw "pack failed: $p" }
}

# The analyzers enter the core's package through a target of FlowTask.csproj, not through NuGet's defaults.
Add-Type -AssemblyName System.IO.Compression.FileSystem
# -Filter takes only * and ? on Windows PowerShell 5.1; "FlowTask.[0-9]*" matched nothing there.
$core = Get-ChildItem $packages -Filter *.nupkg | Where-Object Name -match '^FlowTask\.\d' | Select-Object -First 1
if (-not $core) { throw "no FlowTask package in $packages" }
$zip = [System.IO.Compression.ZipFile]::OpenRead($core.FullName)
try { $names = @($zip.Entries | ForEach-Object { $_.FullName }) } finally { $zip.Dispose() }
foreach ($entry in @("analyzers/dotnet/cs/FlowTask.Analyzers.dll", "analyzers/dotnet/cs/ja/FlowTask.Analyzers.resources.dll")) {
  if ($names -notcontains $entry) { throw "$($core.Name) has no $entry" }
}
Get-ChildItem $packages | Where-Object { $_.Extension -in '.nupkg', '.snupkg' } | Select-Object Name, Length
