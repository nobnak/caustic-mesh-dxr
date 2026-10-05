# Unity CLI Benchmark & Screenshot Capture Script
# Usage: powershell -ExecutionPolicy Bypass -File .\run_benchmark.ps1 [-UnityPath "path\to\Unity.exe"]

param(
    [string]$UnityPath
)

$ErrorActionPreference = "Stop"

$projectRoot = $PSScriptRoot
$unityVersion = "6000.3.20f1"

# Candidate paths for Unity Editor executable
$candidatePaths = @(
    $UnityPath,
    $env:UNITY_EDITOR_PATH,
    $env:UNITY_PATH,
    "C:\Program Files\Unity\Hub\Editor\$unityVersion\Editor\Unity.exe",
    "C:\Program Files\Unity\Editor\Unity.exe",
    "D:\Program Files\Unity\Hub\Editor\$unityVersion\Editor\Unity.exe",
    "D:\Unity\Hub\Editor\$unityVersion\Editor\Unity.exe",
    "E:\Unity\Hub\Editor\$unityVersion\Editor\Unity.exe"
)

$unityExe = $null

foreach ($candidate in $candidatePaths) {
    if ($candidate -and (Test-Path $candidate)) {
        $unityExe = $candidate
        break
    }
}

# If not found in standard paths, search PATH but EXCLUDE the AppData Unity CLI wrapper tool
if (-not $unityExe) {
    $commands = Get-Command "Unity.exe" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
    foreach ($cmd in $commands) {
        # AppData\Local\Unity\bin\unity.exe is Unity CLI (Hub CLI tool), NOT the Unity Editor engine!
        if ($cmd -notmatch "AppData\\Local\\Unity\\bin") {
            $unityExe = $cmd
            break
        }
    }
}

if (-not $unityExe) {
    Write-Host "`n[ERROR] Unity Editor executable (Unity.exe for $unityVersion) not found." -ForegroundColor Red
    Write-Host "Note: 'AppData\Local\Unity\bin\unity.exe' is a CLI wrapper tool and cannot run -batchmode directly." -ForegroundColor Yellow
    Write-Host "`nPlease provide the path to Unity.exe via one of the following:" -ForegroundColor Cyan
    Write-Host "  1. powershell -File .\run_benchmark.ps1 -UnityPath `"C:\Path\To\Unity.exe`""
    Write-Host "  2. `$env:UNITY_EDITOR_PATH = `"C:\Path\To\Unity.exe`""
    Write-Host "  3. Or run directly inside Unity Editor via menu: [Caustics] > [Run Benchmark & Captures]"
    exit 1
}

$logPath = Join-Path $projectRoot "Logs\benchmark.log"
$logDir = Split-Path $logPath -Parent
if (-not (Test-Path $logDir)) {
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " Running Caustic Mesh DXR Benchmark via Unity Editor" -ForegroundColor Cyan
Write-Host " Unity Editor: $unityExe" -ForegroundColor Green
Write-Host " Project     : $projectRoot" -ForegroundColor Gray
Write-Host " Log         : $logPath" -ForegroundColor Gray
Write-Host "==========================================================" -ForegroundColor Cyan

# NOTE: Do NOT use -nographics because DXR (DirectX Raytracing) requires a D3D12 GPU hardware device!
$arguments = @(
    "-batchmode",
    "-projectPath", "`"$projectRoot`"",
    "-executeMethod", "CausticMeshDxr.Editor.CausticBenchmarkRunner.RunFromCommandLine",
    "-logFile", "`"$logPath`""
)

$process = Start-Process -FilePath $unityExe -ArgumentList $arguments -Wait -PassThru -NoNewWindow

if ($process.ExitCode -eq 0) {
    Write-Host "`n[SUCCESS] Benchmark and captures completed successfully!" -ForegroundColor Green
    Write-Host "Captures directory : $projectRoot\Captures" -ForegroundColor Yellow
    Write-Host "Report file        : $projectRoot\docs\benchmark_results.md" -ForegroundColor Yellow
} else {
    Write-Host "`n[ERROR] Unity Editor execution failed with exit code: $($process.ExitCode)" -ForegroundColor Red
    Write-Host "Check log file for details: $logPath" -ForegroundColor Red
    exit $process.ExitCode
}
