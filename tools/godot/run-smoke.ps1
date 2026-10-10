<#
.SYNOPSIS
  Runs the FlowTask Godot smoke test (tests/godot) and the Godot samples' demo (samples/godot) on the real Godot 4 .NET
  engine, headless.

.DESCRIPTION
  1. Finds or downloads the official Godot .NET (mono) build for Windows x64 into a cache outside the repository
     (default: %LOCALAPPDATA%\FlowTask\godot-cache) and verifies its SHA-512 before extracting it: against the value
     pinned below for the default version, otherwise against the release's SHA512-SUMS.txt. It stops when the archive
     cannot be verified (no pinned value and SHA512-SUMS.txt cannot be fetched or does not list it, or a mismatch), and
     it does not use an extracted engine this script has not verified (-GodotExe skips all of this).
  2. Builds tests/godot and samples/godot with `dotnet build` (Debug: the editor binary loads .godot/mono/temp/bin/Debug).
     tests/godot compiles the samples' sources too, and checks them with pretend input (tests/godot/Samples).
  3. Runs `<godot>_console.exe --headless --path tests/godot --log-file <log>` (Main.tscn), then two more scenes,
     each in a process of its own: `... --log-file <log with .handover.log> res://Handover.tscn` (it takes the autoload
     out of the tree, see tests/godot/HandoverMain.cs) and `... --log-file <log with .fixedfps.log> --fixed-fps 60
     res://FixedFps.tscn` (the clocks under a fixed frame rate, see tests/godot/FixedFpsMain.cs).
     Then it starts the samples' demo for 300 frames: `... --path samples/godot --log-file <log with .samples.log>
     --quit-after 300`.
  4. Passes when the four Godot runs exit with 0, the first log contains "SMOKE RESULT: PASS" and "EXIT UNWIND OK", the
     second "HANDOVER RESULT: PASS", the third "FIXEDFPS RESULT: PASS", and the demo's log no ERROR line.
  Exit code: 0 on success, 1 on failure (CI friendly).

  -TimeoutSec (default 300) limits each Godot run on its own, so the four runs of step 3 take up to four times that. A
  run that exceeds it is killed, and the script stops with "SMOKE: TIMEOUT" naming the scene and its log.

  The engine version must match the GodotSharp / Godot.NET.Sdk version used by src/FlowTask.Godot and the sample (4.4.1).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools/godot/run-smoke.ps1
.EXAMPLE
  pwsh tools/godot/run-smoke.ps1 -GodotExe C:\tools\Godot_v4.4.1-stable_mono_win64\Godot_v4.4.1-stable_mono_win64_console.exe
#>
[CmdletBinding()]
param(
    [string]$GodotVersion = '4.4.1-stable',
    [string]$CacheDir = (Join-Path $env:LOCALAPPDATA 'FlowTask\godot-cache'),
    [string]$GodotExe,
    [string]$LogFile,
    [int]$TimeoutSec = 300,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$project = Join-Path $repo 'tests\godot'
$samples = Join-Path $repo 'samples\godot'
if (-not $LogFile) { $LogFile = Join-Path $CacheDir 'godot-smoke.log' }

function Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }

function Get-File([string]$url, [string]$path) {
    $part = "$path.part"
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($curl) {
        & $curl.Source -fsSL --retry 3 -o $part $url
        if ($LASTEXITCODE -ne 0) { throw "Download failed ($LASTEXITCODE): $url" }
    }
    else {
        Invoke-WebRequest -Uri $url -OutFile $part -UseBasicParsing
    }
    Move-Item -Force $part $path
}

# ---------------------------------------------------------------------------------------------------- 1. engine
# SHA-512 of official archives, copied from the release's SHA512-SUMS.txt
# (https://github.com/godotengine/godot/releases/download/4.4.1-stable/SHA512-SUMS.txt, checked 2026-09-26).
# Pinned so that the default engine is verified without trusting a second file from the host that served the archive.
$KnownSha512 = @{
    'Godot_v4.4.1-stable_mono_win64.zip' = 'cc000092a21f7fd9f92ab979ee4b908f835127e2bacea66f57ed4c1617d8b7d0cba65feaabbe42a48d5bb05f9f2f6bd87db21f2e7deb2b964390e4349c0ea8e4'
}

# The expected SHA-512 of an official archive: the pinned value, else its line in the release's SHA512-SUMS.txt.
# Throws when there is neither: an engine that cannot be verified is not used.
function Get-ExpectedSha512([string]$fileName, [string]$baseUrl) {
    if ($KnownSha512.ContainsKey($fileName)) { return $KnownSha512[$fileName] }
    $sums = Join-Path $CacheDir "SHA512-SUMS-$GodotVersion.txt"
    if (-not (Test-Path $sums)) {
        try { Get-File "$baseUrl/SHA512-SUMS.txt" $sums }
        catch { throw "Cannot verify ${fileName}: SHA512-SUMS.txt could not be downloaded ($($_.Exception.Message)). Run again when $baseUrl is reachable, or pass -GodotExe." }
    }
    foreach ($line in Get-Content $sums) {
        $parts = $line.Trim() -split '\s+'
        if ($parts.Count -eq 2 -and $parts[1].TrimStart('*') -eq $fileName) { return $parts[0] }
    }
    throw "Cannot verify ${fileName}: $sums does not list it."
}

# Verifies the archive and returns its SHA-512. A mismatching archive is deleted.
function Test-GodotArchive([string]$zip, [string]$fileName, [string]$baseUrl) {
    $expected = Get-ExpectedSha512 $fileName $baseUrl
    $actual = (Get-FileHash -Algorithm SHA512 $zip).Hash
    if ($actual -ne $expected) { # -ne ignores case (SHA512-SUMS.txt is lower case, Get-FileHash upper case)
        Remove-Item $zip
        throw "Checksum mismatch for $fileName (expected $expected, got $actual). The archive was deleted; run again."
    }
    $actual
}

if (-not $GodotExe) {
    $name = "Godot_v$($GodotVersion)_mono_win64"
    $zip = Join-Path $CacheDir "$name.zip"
    $dir = Join-Path $CacheDir $name
    $GodotExe = Join-Path $dir "$($name)_console.exe"
    # Written after the archive was verified and extracted. Without it the folder is not trusted (an older version of this
    # script extracted without verifying when SHA512-SUMS.txt could not be fetched).
    $marker = Join-Path $dir '.flowtask-verified'
    if (-not ((Test-Path $GodotExe) -and (Test-Path $marker))) {
        if ((Test-Path $dir) -and -not (Test-Path $zip)) {
            throw "The engine in $dir has not been verified by this script (no .flowtask-verified) and its archive is gone. Delete $dir and run again, or pass -GodotExe."
        }
        New-Item -ItemType Directory -Force $CacheDir | Out-Null
        $base = "https://github.com/godotengine/godot/releases/download/$GodotVersion"
        if (-not (Test-Path $zip)) {
            Step "Downloading $name.zip to $CacheDir"
            Get-File "$base/$name.zip" $zip
        }

        Step 'Verifying SHA-512'
        $hash = Test-GodotArchive $zip "$name.zip" $base
        Write-Host "    OK $hash"

        Step "Extracting to $dir"
        if (Test-Path $dir) { Remove-Item -Recurse -Force $dir } # replace what an unverified run may have extracted
        Expand-Archive -Force $zip -DestinationPath $CacheDir
        Set-Content -Path $marker -Value $hash -Encoding Ascii
    }
}
if (-not (Test-Path $GodotExe)) { throw "Godot executable not found: $GodotExe" }
Write-Host "    Godot: $GodotExe"

# ---------------------------------------------------------------------------------------------------- 2. build
if (-not $SkipBuild) {
    Step "dotnet build $project (Debug)"
    & dotnet build $project -c Debug -nologo -v minimal
    if ($LASTEXITCODE -ne 0) { Write-Host 'SMOKE: BUILD FAILED' -ForegroundColor Red; exit 1 }
    Step "dotnet build $samples (Debug)"
    & dotnet build $samples -c Debug -nologo -v minimal
    if ($LASTEXITCODE -ne 0) { Write-Host 'SMOKE: BUILD FAILED (samples)' -ForegroundColor Red; exit 1 }
}

# ---------------------------------------------------------------------------------------------------- 3. run
# Runs Godot on a project (with its main scene, or the scene in $extraArgs) and returns its exit code. $scene names the
# run in the timeout message.
function Invoke-Godot([string]$scene, [string]$log, [string[]]$extraArgs, [string]$path = $project) {
    New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null
    if (Test-Path $log) { Remove-Item $log }
    $godotArgs = @('--headless', '--path', "`"$path`"", '--log-file', "`"$log`"") + $extraArgs
    Step "$GodotExe $($godotArgs -join ' ')"
    $proc = Start-Process -FilePath $GodotExe -ArgumentList $godotArgs -NoNewWindow -PassThru
    $null = $proc.Handle # Windows PowerShell 5.1: keeps ExitCode available after exit
    if (-not $proc.WaitForExit($TimeoutSec * 1000)) {
        $proc.Kill()
        Write-Host "SMOKE: TIMEOUT after $TimeoutSec s running $scene (log: $log)" -ForegroundColor Red
        exit 1
    }
    $proc.ExitCode
}

$exitCode = Invoke-Godot 'Main.tscn' $LogFile @()
# The second scene takes the autoload out of the tree (the FlowWorldNode.Default hand-over), so it gets a process of its own.
$handoverLogFile = [System.IO.Path]::ChangeExtension($LogFile, '.handover.log')
$handoverExitCode = Invoke-Godot 'Handover.tscn' $handoverLogFile @('res://Handover.tscn')
# The third scene needs a fixed frame rate (Movie Maker forces one too), which is set on the command line only.
$fixedFpsLogFile = [System.IO.Path]::ChangeExtension($LogFile, '.fixedfps.log')
$fixedFpsExitCode = Invoke-Godot 'FixedFps.tscn' $fixedFpsLogFile @('--fixed-fps', '60', 'res://FixedFps.tscn')
# The samples' demo starts and runs for a few seconds: an exception in it is logged as an ERROR line.
$samplesLogFile = [System.IO.Path]::ChangeExtension($LogFile, '.samples.log')
$samplesExitCode = Invoke-Godot 'samples/godot' $samplesLogFile @('--quit-after', '300') $samples

# ---------------------------------------------------------------------------------------------------- 4. verdict
$log = if (Test-Path $LogFile) { Get-Content $LogFile -Raw } else { '' }
$passLine = $log -match 'SMOKE RESULT: PASS'
$unwindLine = $log -match 'EXIT UNWIND OK'
$handoverLog = if (Test-Path $handoverLogFile) { Get-Content $handoverLogFile -Raw } else { '' }
$handoverLine = $handoverLog -match 'HANDOVER RESULT: PASS'
$fixedFpsLog = if (Test-Path $fixedFpsLogFile) { Get-Content $fixedFpsLogFile -Raw } else { '' }
$fixedFpsLine = $fixedFpsLog -match 'FIXEDFPS RESULT: PASS'
$samplesErrors = @(if (Test-Path $samplesLogFile) { Get-Content $samplesLogFile | Where-Object { $_ -match '^(SCRIPT )?ERROR' } })
Write-Host ''
Write-Host "Logs: $LogFile, $handoverLogFile, $fixedFpsLogFile, $samplesLogFile"
Write-Host "Godot exit code: $exitCode, 'SMOKE RESULT: PASS': $passLine, 'EXIT UNWIND OK': $unwindLine"
Write-Host "Godot exit code (Handover.tscn): $handoverExitCode, 'HANDOVER RESULT: PASS': $handoverLine"
Write-Host "Godot exit code (FixedFps.tscn): $fixedFpsExitCode, 'FIXEDFPS RESULT: PASS': $fixedFpsLine"
Write-Host "Godot exit code (samples/godot): $samplesExitCode, ERROR lines: $($samplesErrors.Count)"
$samplesErrors | Select-Object -First 5 | ForEach-Object { Write-Host "    $_" }
if ($exitCode -eq 0 -and $passLine -and $unwindLine -and $handoverExitCode -eq 0 -and $handoverLine -and $fixedFpsExitCode -eq 0 -and $fixedFpsLine -and $samplesExitCode -eq 0 -and $samplesErrors.Count -eq 0) {
    Write-Host 'SMOKE: PASS' -ForegroundColor Green
    exit 0
}
Write-Host 'SMOKE: FAIL' -ForegroundColor Red
exit 1
