<#
.SYNOPSIS
    FlowTask Unity verification: syncs the core test suite and the package's samples into tests/unity/, runs them headless
    in the Unity Editor (EditMode, PlayMode), in a Windows standalone player (Mono / IL2CPP) and builds an Android IL2CPP
    test player.

.DESCRIPTION
    Suites (default: EditMode, PlayMode, StandaloneMono, AndroidIl2cppBuild):
      EditMode            Unity.exe -batchmode -nographics -runTests -testPlatform EditMode
      PlayMode            Unity.exe -batchmode -nographics -runTests -testPlatform PlayMode
      StandaloneMono      -testPlatform StandaloneWindows64, scripting backend Mono (the Test Framework builds and runs a player)
      StandaloneIl2cpp    -testPlatform StandaloneWindows64, scripting backend IL2CPP (needs the "Windows Build Support (IL2CPP)"
                          module and a C++ toolchain; skipped with a message when the module is not installed)
      The Standalone suites build the player into <OutDir>/StandalonePlayer/PlayerWithTests/ (-buildPlayerPath), not into
      a new folder of Temp/ each run: Windows Defender Firewall remembers its answer per executable path, so it asks
      once instead of on every run.
      AndroidIl2cppBuild  -testPlatform Android, IL2CPP/ARM64, build only (no device needed): proves AOT compilation of the
                          core, the custom async method builder and the whole test suite

    Every run uses -releaseCodeOptimization: the Editor otherwise compiles scripts in Debug mode, where the C# compiler
    emits async state machines as classes, which would make the allocation tests measure the compiler instead of FlowTask
    (the .NET test project sets <Optimize>true</Optimize> for the same reason).

    Results (NUnit 3 XML + Editor logs) go to tests/unity/TestResults/ (gitignored); a summary is printed and written to
    summary.json. The script exits with 1 when any suite failed.

    Each Unity run is stopped after -TimeoutMinutes (default: 15 for EditMode and PlayMode, 30 for the player suites,
    which include a build): the process tree is killed, the suite is reported as "timeout" together with what was
    running (StuckTest) and the script exits with 1. StuckTest is the last "[FlowTask] test started:" or "suite
    started:" line that tests/unity/Assets/Editor/TestProgressLog.cs writes to the Editor log (a suite means its
    [OneTimeSetUp] or a [SetUpFixture]). In EditMode and PlayMode that is what was running. In the player suites it is
    only the last start the Editor received: the player sends them one per frame, so a test that blocks the player's
    main thread is not named, nor are the starts queued before it (6000.3.7f1: a namespace suite was the last line).
    After 600 s without a message from the player the Editor logs "Test execution timed out. No activity received from
    the player" (shown under Errors) but keeps running until the time limit. A suite without results also gets
    StuckTest when a test had started.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/unity/run-tests.ps1
.EXAMPLE
    powershell -File tools/unity/run-tests.ps1 -Suites EditMode -TestFilter "Katout.FlowTask.Tests.AllocationTests"
#>
param(
    [string]$UnityVersion = "6000.3.7f1",
    [string]$UnityExe = "",
    # One or more of: EditMode, PlayMode, StandaloneMono, StandaloneIl2cpp, AndroidIl2cppBuild (comma-separated works with -File too).
    [string[]]$Suites = @("EditMode", "PlayMode", "StandaloneMono", "AndroidIl2cppBuild"),
    [string]$OutDir = "",
    [string]$TestFilter = "",
    # Upper bound of each Unity run (one suite), in minutes. A hanging test otherwise blocks the script forever.
    # 0: 15 for EditMode and PlayMode (usually 1-5 min, more when the Library is imported the first time), 30 for the
    # player suites (build included; StandaloneIl2cpp takes about 12 min).
    [double]$TimeoutMinutes = 0,
    [switch]$NoSync
)

$ErrorActionPreference = "Stop"
if ($TimeoutMinutes -lt 0) { throw "-TimeoutMinutes must be positive (or 0 for the per-suite default)" }
$knownSuites = @("EditMode", "PlayMode", "StandaloneMono", "StandaloneIl2cpp", "AndroidIl2cppBuild")
$Suites = @($Suites | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
foreach ($s in $Suites) { if ($knownSuites -notcontains $s) { throw "Unknown suite '$s'. Known: $($knownSuites -join ', ')" } }
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$project = Join-Path $repo "tests\unity"
if (-not $OutDir) { $OutDir = Join-Path $project "TestResults" }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
if (-not $UnityExe) { $UnityExe = "C:\Program Files\Unity\Hub\Editor\$UnityVersion\Editor\Unity.exe" }
if (-not (Test-Path $UnityExe)) { throw "Unity Editor not found: $UnityExe (pass -UnityExe or -UnityVersion)" }
$editorData = Join-Path (Split-Path $UnityExe -Parent) "Data"

# Runs a tool of tools/ (a .NET file-based app: dotnet <file>.cs).
function Invoke-Tool([string[]]$ArgList) {
    & dotnet @ArgList
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($ArgList -join ' ') failed with exit code $LASTEXITCODE" }
}

# Copies tests/FlowTask.Core.Tests/*.cs unchanged into a folder of tests/unity (gitignored), which then holds exactly those
# files: a copy whose original is gone is deleted with its .meta. Unchanged files are not rewritten, so Unity does not
# recompile for them. What differs in Unity is in the sources themselves (#if UNITY_5_3_OR_NEWER, docs/maintainers/engine-tests.md).
function Copy-CoreTests([string]$Destination) {
    $sources = @(Get-ChildItem (Join-Path $repo "tests\FlowTask.Core.Tests") -File | Where-Object { $_.Extension -eq ".cs" })
    New-Item -ItemType Directory -Force $Destination | Out-Null
    foreach ($copy in @(Get-ChildItem $Destination -File | Where-Object { $_.Extension -eq ".cs" })) {
        if ($sources.Name -notcontains $copy.Name) {
            Remove-Item $copy.FullName
            Remove-Item "$($copy.FullName).meta" -ErrorAction SilentlyContinue
        }
    }
    foreach ($source in $sources) {
        $target = Join-Path $Destination $source.Name
        if (-not (Test-Path $target) -or (Get-FileHash $source.FullName).Hash -ne (Get-FileHash $target).Hash) {
            Copy-Item $source.FullName $target -Force
        }
    }
}

# 1. Copy tests/FlowTask.Core.Tests into tests/unity (single source of truth stays in tests/), once for EditMode and once
#    for PlayMode, each its own test assembly. FlowTask.Testing* are UPM packages.
if (-not $NoSync) {
    Copy-CoreTests (Join-Path $project "Assets\Tests\EditMode\Synced")
    Copy-CoreTests (Join-Path $project "Assets\Tests\PlayMode\Synced")
}
# 2. Make sure every package file has its deterministic .meta (the samples' too).
Invoke-Tool @((Join-Path $repo "tools\unity\generate_meta.cs"))
# 3. Mirror the package's samples, .meta files included, into tests/unity/Assets/Samples (gitignored), as the Package
#    Manager's Import does in a game; Assets/Tests/Samples tests them. robocopy skips unchanged files, so Unity does not
#    reimport them, and deletes what the samples no longer have.
if (-not $NoSync) {
    & robocopy (Join-Path $repo "src\FlowTask.Unity\Samples~\Scenarios") (Join-Path $project "Assets\Samples") /MIR /NJH /NJS /NFL /NDL /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy of the samples failed with exit code $LASTEXITCODE" }
}

function Format-Arg([string]$a) { if ($a -match '\s') { '"' + $a + '"' } else { $a } }

# -TimeoutMinutes, or the suite's default: the Editor suites usually take 1-5 min, the player suites include a build.
function Get-SuiteTimeout([string]$Suite) {
    if ($TimeoutMinutes -gt 0) { return $TimeoutMinutes }
    if ($Suite -eq "EditMode" -or $Suite -eq "PlayMode") { return 15 }
    return 30
}

# Returns the exit code, or $null when the run was stopped after $Minutes.
function Invoke-Unity([string[]]$ArgList, [hashtable]$EnvVars, [double]$Minutes) {
    foreach ($k in $EnvVars.Keys) { Set-Item "env:$k" $EnvVars[$k] }
    try {
        Write-Host ("> Unity.exe " + ($ArgList -join ' ') + " (time limit: $Minutes min)")
        # Not "Start-Process -Wait": it waits for the whole process tree, and the Editor can leave children running
        # (e.g. the Android SDK's adb server once the Android module was used), which would block forever.
        $p = Start-Process -FilePath $UnityExe -ArgumentList ($ArgList | ForEach-Object { Format-Arg $_ }) -PassThru -NoNewWindow
        $null = $p.Handle # keep the handle so ExitCode is available after exit (Windows PowerShell 5.1)
        if (-not $p.WaitForExit([int][math]::Min([int]::MaxValue, $Minutes * 60000))) {
            Write-Host ("Unity did not finish within {0} min; stopping it (process tree of PID {1})." -f $Minutes, $p.Id)
            # The whole tree: a test player (Standalone*) or the shader compiler would otherwise keep running.
            # Windows PowerShell 5.1 runs on .NET Framework, whose Process.Kill() has no entire-tree overload.
            & taskkill.exe /PID $p.Id /T /F | Out-Null
            if (-not $p.WaitForExit(60000)) { Write-Host "    Unity is still running after taskkill (PID $($p.Id))." }
            return $null
        }
        return $p.ExitCode
    }
    finally {
        foreach ($k in $EnvVars.Keys) { Remove-Item "env:$k" -ErrorAction SilentlyContinue }
    }
}

# The Windows test player can outlive the Editor that ran it: it is not a child process the Editor waits for. A player
# left running keeps the CPU busy and slows the frames of the next run, so every player started from this run's
# player folder is stopped once the suite ends.
function Stop-TestPlayers {
    $playerDir = (Join-Path $OutDir "StandalonePlayer") + [IO.Path]::DirectorySeparatorChar
    $players = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($playerDir, [StringComparison]::OrdinalIgnoreCase) })
    foreach ($proc in $players) {
        try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop } catch { Write-Host "    could not stop the test player (PID $($proc.Id))" }
    }
}

function Read-Results([string]$Xml) {
    if (-not (Test-Path $Xml)) { return $null }
    [xml]$doc = Get-Content -Raw -Encoding UTF8 $Xml
    $run = $doc.'test-run'
    $failed = @($doc.SelectNodes("//test-case[@result='Failed']") | ForEach-Object { $_.fullname })
    $skipped = @($doc.SelectNodes("//test-case[@result='Skipped']") | ForEach-Object { $_.fullname })
    [pscustomobject]@{
        Total = [int]$run.total; Passed = [int]$run.passed; Failed = [int]$run.failed
        Skipped = [int]$run.skipped; Inconclusive = [int]$run.inconclusive
        Duration = [double]$run.duration; FailedTests = $failed; SkippedTests = $skipped
    }
}

# What was running when the run stopped: the last "test started" or "suite started" line (TestProgressLog.cs), unless
# the run finished after it. A suite that comes last had not started its first test ([OneTimeSetUp], [SetUpFixture]).
# In the player suites this is only the last start the Editor received; the test that blocks can be a later one (see
# the header), and the name says so.
function Get-StuckTest([string]$Log, [bool]$Player) {
    if (-not (Test-Path $Log)) { return $null }
    $lines = @(Select-String -Path $Log -Pattern '^\[FlowTask\] (test started: |suite started: |test run finished)' | ForEach-Object { $_.Line.Trim() })
    if ($lines.Count -eq 0) { return "(none: no test had started)" }
    $last = $lines[-1]
    if ($last -eq "[FlowTask] test run finished") { return "(none: every test had finished)" }
    $suitePrefix = "[FlowTask] suite started: "
    if ($Player) {
        $name = if ($last.StartsWith($suitePrefix)) { $last.Substring($suitePrefix.Length) } else { $last.Substring("[FlowTask] test started: ".Length) }
        return "$name (the last start the player reported; the test that blocks the player can be a later one)"
    }
    if ($last.StartsWith($suitePrefix)) {
        return $last.Substring($suitePrefix.Length) + " (suite, before its first test: [OneTimeSetUp] or [SetUpFixture])"
    }
    return $last.Substring("[FlowTask] test started: ".Length)
}

function Get-LogErrors([string]$Log) {
    if (-not (Test-Path $Log)) { return @() }
    # "Test execution timed out": the Editor received nothing from the test player for -playerHeartbeatTimeout (600 s).
    @(Select-String -Path $Log -Pattern 'error CS\d+|Compilation failed|Scripts have compiler errors|BuildFailedException|Build Finished, Result: Failure|Error building Player|Test execution timed out' |
        Select-Object -First 20 | ForEach-Object { $_.Line.Trim() })
}

$summary = [ordered]@{}

function Invoke-TestSuite([string]$Name, [string]$Platform, [hashtable]$EnvVars, [string[]]$Extra = @()) {
    $xml = Join-Path $OutDir "$Name.xml"
    $log = Join-Path $OutDir "$Name.log"
    Remove-Item $xml, $log -ErrorAction SilentlyContinue
    $unityArgs = @("-batchmode", "-nographics", "-projectPath", $project, "-runTests", "-testPlatform", $Platform,
        "-testResults", $xml, "-logFile", $log, "-releaseCodeOptimization") + $Extra
    if ($TestFilter) { $unityArgs += @("-testFilter", $TestFilter) }
    $minutes = Get-SuiteTimeout $Name
    $player = $Platform -ne "EditMode" -and $Platform -ne "PlayMode"
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $code = Invoke-Unity $unityArgs $EnvVars $minutes
    if ($player) { Stop-TestPlayers }
    $sw.Stop()
    $r = Read-Results $xml
    $entry = [ordered]@{ Suite = $Name; Platform = $Platform; ExitCode = $code; Minutes = [math]::Round($sw.Elapsed.TotalMinutes, 1); Log = $log; Results = $xml }
    if ($null -eq $code) {
        $entry.Status = "timeout"
        $entry.TimeoutMinutes = $minutes
        $entry.StuckTest = Get-StuckTest $log $player
        $entry.Errors = Get-LogErrors $log
        Write-Host ("    still running after {0} min: {1}" -f $minutes, $entry.StuckTest)
    }
    elseif ($r) {
        $entry.Total = $r.Total; $entry.Passed = $r.Passed; $entry.Failed = $r.Failed; $entry.Skipped = $r.Skipped
        $entry.FailedTests = $r.FailedTests; $entry.SkippedTests = $r.SkippedTests
        $entry.Status = if ($r.Failed -eq 0 -and $r.Total -gt 0) { "passed" } else { "failed" }
    }
    else {
        $entry.Status = "no results"
        $entry.Errors = Get-LogErrors $log
        # The Editor or the player crashed, or the Editor quit during the run: name the last test that started.
        $stuck = Get-StuckTest $log $player
        if ($stuck -and -not $stuck.StartsWith("(none")) {
            $entry.StuckTest = $stuck
            Write-Host "    last start before the run ended: $stuck"
        }
    }
    $summary[$Name] = $entry
    Write-Host ("[{0}] {1}: total={2} passed={3} failed={4} skipped={5} exit={6} ({7} min)" -f $Name, $entry.Status, $entry.Total, $entry.Passed, $entry.Failed, $entry.Skipped, $code, $entry.Minutes)
    foreach ($t in @($entry.FailedTests)) { if ($t) { Write-Host "    FAILED $t" } }
    foreach ($e in @($entry.Errors)) { if ($e) { Write-Host "    $e" } }
}

function Test-Il2cppModule([string]$Engine) {
    $variations = Join-Path $editorData "PlaybackEngines\$Engine\Variations"
    return (Test-Path $variations) -and (@(Get-ChildItem $variations -Directory | Where-Object { $_.Name -like "*il2cpp*" }).Count -gt 0)
}

# The Windows player of the Standalone suites always goes to the same path (see .DESCRIPTION: one firewall prompt, not
# one per run). The folder is emptied first, so a Mono build does not leave files in an IL2CPP one and the other way round.
function Get-StandaloneArgs {
    $playerDir = Join-Path $OutDir "StandalonePlayer"
    Remove-Item $playerDir -Recurse -Force -ErrorAction SilentlyContinue
    return @("-buildTarget", "Win64", "-buildPlayerPath", $playerDir)
}

foreach ($suite in $Suites) {
    switch ($suite) {
        "EditMode" { Invoke-TestSuite "EditMode" "EditMode" @{} @("-buildTarget", "Win64") }
        "PlayMode" { Invoke-TestSuite "PlayMode" "PlayMode" @{} @("-buildTarget", "Win64") }
        "StandaloneMono" {
            Invoke-TestSuite "StandaloneMono" "StandaloneWindows64" @{ FLOWTASK_TEST_BACKEND = "Mono" } (Get-StandaloneArgs)
        }
        "StandaloneIl2cpp" {
            if (-not (Test-Il2cppModule "windowsstandalonesupport")) {
                $summary["StandaloneIl2cpp"] = [ordered]@{ Suite = "StandaloneIl2cpp"; Status = "skipped"; Reason = "Windows Build Support (IL2CPP) is not installed in $editorData" }
                Write-Host "[StandaloneIl2cpp] skipped: Windows Build Support (IL2CPP) module not installed for this editor."
                continue
            }
            Invoke-TestSuite "StandaloneIl2cpp" "StandaloneWindows64" @{ FLOWTASK_TEST_BACKEND = "IL2CPP" } (Get-StandaloneArgs)
        }
        "AndroidIl2cppBuild" {
            if (-not (Test-Il2cppModule "AndroidPlayer")) {
                $summary["AndroidIl2cppBuild"] = [ordered]@{ Suite = "AndroidIl2cppBuild"; Status = "skipped"; Reason = "Android Build Support is not installed in $editorData" }
                Write-Host "[AndroidIl2cppBuild] skipped: Android module not installed for this editor."
                continue
            }
            $buildDir = Join-Path $OutDir "AndroidIl2cpp"
            Remove-Item $buildDir -Recurse -Force -ErrorAction SilentlyContinue
            $log = Join-Path $OutDir "AndroidIl2cppBuild.log"
            Remove-Item $log -ErrorAction SilentlyContinue
            $unityArgs = @("-batchmode", "-nographics", "-projectPath", $project, "-buildTarget", "Android", "-runTests", "-testPlatform", "Android",
                "-logFile", $log, "-releaseCodeOptimization")
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            $code = Invoke-Unity $unityArgs @{ FLOWTASK_TEST_BACKEND = "IL2CPP"; FLOWTASK_TEST_BUILD_ONLY = $buildDir } (Get-SuiteTimeout "AndroidIl2cppBuild")
            $sw.Stop()
            $apk = @(Get-ChildItem $buildDir -Filter *.apk -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1)
            $il2cpp = (Test-Path $log) -and (Select-String -Path $log -Pattern "il2cpp" -SimpleMatch -Quiet)
            $ok = ($apk.Count -gt 0) -and ($null -ne $code)
            $entry = [ordered]@{
                Suite = "AndroidIl2cppBuild"; ExitCode = $code; Minutes = [math]::Round($sw.Elapsed.TotalMinutes, 1); Log = $log
                Status = if ($null -eq $code) { "timeout" } elseif ($ok) { "built" } else { "failed" }; Apk = if ($ok) { $apk[0].FullName } else { $null }
                ApkMB = if ($ok) { [math]::Round($apk[0].Length / 1MB, 1) } else { $null }; Il2cppInLog = $il2cpp
                Errors = if ($ok) { @() } else { Get-LogErrors $log }
            }
            $summary["AndroidIl2cppBuild"] = $entry
            Write-Host ("[AndroidIl2cppBuild] {0}: apk={1} ({2} MB) exit={3} ({4} min)" -f $entry.Status, $entry.Apk, $entry.ApkMB, $code, $entry.Minutes)
            foreach ($e in @($entry.Errors)) { if ($e) { Write-Host "    $e" } }
        }
    }
}

$summaryPath = Join-Path $OutDir "summary.json"
$summary | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 $summaryPath
Write-Host "Summary written to $summaryPath"
$bad = @($summary.Values | Where-Object { $_.Status -eq "failed" -or $_.Status -eq "no results" -or $_.Status -eq "timeout" })
if ($bad.Count -gt 0) { exit 1 }
exit 0
