param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$Run,
    [switch]$Demo
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\SerialScope\SerialScope.csproj'

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw '未找到 .NET SDK。请安装 .NET 8 SDK，或从 Visual Studio 2022 的开发者 PowerShell 中运行此脚本。'
}

Write-Host "Restoring $project ..." -ForegroundColor Cyan
& $dotnet.Source restore $project
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore 失败。' }

Write-Host "Building $Configuration | x64 ..." -ForegroundColor Cyan
& $dotnet.Source build $project -c $Configuration -p:Platform=x64 --no-restore
if ($LASTEXITCODE -ne 0) { throw 'dotnet build 失败。' }

$target = Join-Path $root "src\SerialScope\bin\x64\$Configuration\net8.0-windows10.0.19041.0\win-x64\SerialScope.exe"
Write-Host "构建完成: $target" -ForegroundColor Green

if ($Run -or $Demo) {
    $arguments = @()
    if ($Demo) { $arguments += '--demo' }
    Start-Process -FilePath $target -ArgumentList $arguments
}
