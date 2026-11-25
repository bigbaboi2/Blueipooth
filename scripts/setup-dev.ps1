# Blueipooth Development Environment Setup Script
# Installs/verifies usbip-win2 (WHQL signed), isolated Python + Bumble, and local Android SDK CLI.

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$ToolsDir = Join-Path $RepoRoot ".tools"
New-Item -ItemType Directory -Force -Path $ToolsDir | Out-Null

Write-Host "=== [1/3] Checking usbip-win2 WHQL Driver ===" -ForegroundColor Cyan
$usbipExe = "C:\Program Files\USBip\usbip.exe"
if (-not (Test-Path $usbipExe)) {
    $cmd = Get-Command usbip -ErrorAction SilentlyContinue
    if ($cmd) { $usbipExe = $cmd.Source }
}

if (Test-Path $usbipExe) {
    Write-Host "usbip-win2 already installed at: $usbipExe" -ForegroundColor Green
} else {
    Write-Host "Installing vadimgrn.usbip-win2 via winget (accept UAC prompt if shown)..." -ForegroundColor Yellow
    winget install --id vadimgrn.usbip-win2 --accept-package-agreements --accept-source-agreements
}

Write-Host "=== [2/3] Checking Isolated Python 3.11 + Google Bumble ===" -ForegroundColor Cyan
$pyExe = Join-Path $ToolsDir "python\python.exe"
if (-not (Test-Path $pyExe)) {
    $pyDir = Join-Path $ToolsDir "python"
    New-Item -ItemType Directory -Force -Path $pyDir | Out-Null
    Invoke-WebRequest -Uri "https://www.python.org/ftp/python/3.11.9/python-3.11.9-embed-amd64.zip" -OutFile "$ToolsDir\python-embed.zip"
    Expand-Archive -Path "$ToolsDir\python-embed.zip" -DestinationPath $pyDir -Force
    Remove-Item "$ToolsDir\python-embed.zip" -Force

    $pthFile = Join-Path $pyDir "python311._pth"
    $pthContent = (Get-Content $pthFile) -replace "#import site", "import site"
    $pthContent += "`n.."
    Set-Content -Path $pthFile -Value $pthContent -Encoding ASCII

    Invoke-WebRequest -Uri "https://bootstrap.pypa.io/get-pip.py" -OutFile "$pyDir\get-pip.py"
    & $pyExe "$pyDir\get-pip.py" --no-warn-script-location
}
& $pyExe -m pip install --no-warn-script-location bumble pytest pytest-asyncio

Write-Host "=== [3/3] Checking Local Android SDK Command-Line Tools ===" -ForegroundColor Cyan
$sdkRoot = Join-Path $ToolsDir "android-sdk"
$sdkMgr = Join-Path $sdkRoot "cmdline-tools\latest\bin\sdkmanager.bat"
if (Test-Path $sdkMgr) {
    Write-Host "Android SDK ready at: $sdkRoot" -ForegroundColor Green
} else {
    Write-Host "Run Android SDK bootstrap if needed." -ForegroundColor Yellow
}

Write-Host "=== Setup Completed Successfully! ===" -ForegroundColor Green
