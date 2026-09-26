# 在 Windows 上编译并打包成 zip（需要 .NET 8 SDK）
#   .\scripts\build.ps1                    # win-x64
#   .\scripts\build.ps1 -Arch arm64        # win-arm64
#   .\scripts\build.ps1 -Version 1.1.0     # 指定版本号
param(
    [string]$Version = "1.0.0",
    [string]$Arch = "x64"
)
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

$rid = "win-$Arch"
$out = "dist\$rid"
$zip = "dist\VPNAutoConnect-$Version-$rid.zip"

Write-Host "==> 单元测试"
dotnet test tests\VPNAutoConnect.Tests -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "==> 发布 ($rid, 单文件, 自带运行时)"
Remove-Item -Recurse -Force $out, $zip -ErrorAction SilentlyContinue
dotnet publish src\VPNAutoConnect -c Release -r $rid --self-contained true --nologo `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:Version=$Version `
    -o $out
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "==> 打包 zip"
Compress-Archive -Path "$out\VPNAutoConnect.exe" -DestinationPath $zip -Force

Write-Host "完成：$zip"
