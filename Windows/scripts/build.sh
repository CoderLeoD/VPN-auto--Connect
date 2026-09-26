#!/bin/bash
# 在 macOS / Linux 上交叉编译 Windows 版并打包成 zip（需要 .NET 8 SDK）
#   ./scripts/build.sh                 # win-x64
#   ARCH=arm64 ./scripts/build.sh      # win-arm64
#   VERSION=1.1.0 ./scripts/build.sh   # 指定版本号
set -euo pipefail

cd "$(dirname "$0")/.."
VERSION="${VERSION:-1.0.0}"
RID="win-${ARCH:-x64}"
DIST="dist"
OUT="$DIST/$RID"
ZIP="$DIST/VPNAutoConnect-$VERSION-$RID.zip"

echo "==> 单元测试"
dotnet test tests/VPNAutoConnect.Tests -c Release --nologo -v quiet

echo "==> 发布 ($RID, 单文件, 自带运行时)"
rm -rf "$OUT" "$ZIP"
dotnet publish src/VPNAutoConnect -c Release -r "$RID" --self-contained true --nologo \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -p:DebugType=none \
    -p:Version="$VERSION" \
    -o "$OUT"

echo "==> 打包 zip"
(cd "$OUT" && zip -q -9 "../$(basename "$ZIP")" VPNAutoConnect.exe)

echo "完成：$ZIP"
