# Blueipooth Full Build & Test Script (Windows .NET 10 + Bumble Pytest + Android APK)
$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$PyExe = Join-Path $RepoRoot ".tools\python\python.exe"
$env:JAVA_HOME = "C:\Program Files\Microsoft\jdk-17.0.21.21-hotspot"
$env:ANDROID_HOME = Join-Path $RepoRoot ".tools\android-sdk"
$env:ANDROID_SDK_ROOT = $env:ANDROID_HOME
$env:PATH = "$env:JAVA_HOME\bin;$env:ANDROID_HOME\platform-tools;$env:PATH"

Write-Host "=== [1/3] Running Bumble Engine Pytest Suite ===" -ForegroundColor Cyan
& $PyExe -m pytest -c (Join-Path $RepoRoot "pc\bumble-engine\pytest.ini") (Join-Path $RepoRoot "pc\bumble-engine\tests") -v
if ($LASTEXITCODE -ne 0) { throw "Bumble pytest failed with exit code $LASTEXITCODE" }

Write-Host "=== [2/3] Building & Testing .NET 10 Solution ===" -ForegroundColor Cyan
dotnet build (Join-Path $RepoRoot "pc\Blueipooth.sln") --configuration Release
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

dotnet test (Join-Path $RepoRoot "pc\Blueipooth.sln") --configuration Release --no-build
if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE" }

Write-Host "=== [3/3] Building & Testing Android Companion APK ===" -ForegroundColor Cyan
Push-Location (Join-Path $RepoRoot "android")
try {
    .\gradlew.bat testDebugUnitTest assembleDebug --no-daemon
    if ($LASTEXITCODE -ne 0) { throw "Gradle build failed with exit code $LASTEXITCODE" }
} finally {
    Pop-Location
}

Write-Host "=== All Builds & Tests Passed! ===" -ForegroundColor Green
Write-Host "PC App:      $RepoRoot\pc\Blueipooth.App\bin\Release\net10.0-windows\Blueipooth.App.exe"
Write-Host "Android APK: $RepoRoot\android\app\build\outputs\apk\debug\app-debug.apk"
