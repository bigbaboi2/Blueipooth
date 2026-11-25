# Blueipooth One-Click Android APK Installer & USB Tunnel Setup
# Supports Xiaomi MIUI (pushes APK to Download folder first + triggers install + sets up TCP 49550 USB tunnel)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$AdbExe = Join-Path $RepoRoot ".tools\android-sdk\platform-tools\adb.exe"
$ApkPath = Join-Path $RepoRoot "android\app\build\outputs\apk\debug\app-debug.apk"

if (-not (Test-Path $ApkPath)) {
    throw "APK not found at $ApkPath. Run scripts\build-all.ps1 first."
}

Write-Host "=== [1/4] Checking Connected Android Device ===" -ForegroundColor Cyan
& $AdbExe start-server
$devices = & $AdbExe devices -l
Write-Host $devices

Write-Host "=== [2/4] Copying Blueipooth.apk to Phone ===" -ForegroundColor Cyan
& $AdbExe push $ApkPath /sdcard/Download/Blueipooth.apk
& $AdbExe push $ApkPath /data/local/tmp/Blueipooth.apk

Write-Host "=== [3/4] Installing Blueipooth on Android (Tap 'Install' on phone screen if prompted by Xiaomi MIUI!) ===" -ForegroundColor Yellow
& $AdbExe shell pm install -r /data/local/tmp/Blueipooth.apk
if ($LASTEXITCODE -ne 0) {
    Write-Host "Note: Xiaomi MIUI blocked silent USB install ('Cài đặt qua USB' may be off in Developer Options)." -ForegroundColor Yellow
    Write-Host "Blueipooth.apk has already been copied to your phone's 'Download' folder! Open File Manager -> Download -> Blueipooth.apk to tap Install." -ForegroundColor Green
} else {
    Write-Host "APK Installed Successfully! Launching Blueipooth..." -ForegroundColor Green
    & $AdbExe shell am start -n com.blueipooth/.ui.MainActivity
}

Write-Host "=== [4/4] Activating ~1ms USB Cable Tunnel (TCP 49550) ===" -ForegroundColor Cyan
& $AdbExe reverse tcp:49550 tcp:49550
Write-Host "Done! Open Blueipooth on PC and scan the QR code from your phone." -ForegroundColor Green
