param(
    [switch]$Demo
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$candidates = @(
    (Join-Path $root 'artifacts\SerialScope\SerialScope.exe'),
    (Join-Path $root 'src\SerialScope\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\SerialScope.exe'),
    (Join-Path $root 'src\SerialScope\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\SerialScope.exe')
)

$exe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $exe) {
    throw '未找到已构建的 SerialScope.exe，请先运行 build.ps1。'
}

$arguments = @()
if ($Demo) { $arguments += '--demo' }
Start-Process -FilePath $exe -ArgumentList $arguments
Write-Host "已启动 $exe"
