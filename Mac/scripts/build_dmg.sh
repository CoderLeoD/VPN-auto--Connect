#!/bin/bash
# 构建 VPN AutoConnect.app 并打包成 dmg
#   ./scripts/build_dmg.sh              # 当前架构
#   UNIVERSAL=1 ./scripts/build_dmg.sh  # arm64 + x86_64 通用版
set -euo pipefail

cd "$(dirname "$0")/.."
VERSION="${VERSION:-1.0.0}"
APP_NAME="VPN AutoConnect"
EXEC_NAME="VPNAutoConnect"
DIST="dist"
APP="$DIST/$APP_NAME.app"
DMG="$DIST/VPNAutoConnect-$VERSION.dmg"

echo "==> 编译 (release)"
if [[ "${UNIVERSAL:-0}" == "1" ]]; then
    swift build -c release --arch arm64 --arch x86_64
    # 通用版的输出目录随 SwiftPM 版本变化（.build/apple/... 或 .build/out/...），直接向 SwiftPM 查询
    BIN_DIR="$(swift build -c release --arch arm64 --arch x86_64 --show-bin-path)"
else
    swift build -c release
    BIN_DIR="$(swift build -c release --show-bin-path)"
fi

echo "==> 组装 .app"
rm -rf "$DIST"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN_DIR/$EXEC_NAME" "$APP/Contents/MacOS/$EXEC_NAME"
sed "s/__VERSION__/$VERSION/g" Resources/Info.plist > "$APP/Contents/Info.plist"

echo "==> 生成图标"
ICONSET="$(mktemp -d)/AppIcon.iconset"
swift scripts/make_icon.swift "$ICONSET"
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"

echo "==> 签名 (ad-hoc)"
codesign --force --deep --sign - "$APP"

echo "==> 打包 dmg"
STAGE="$(mktemp -d)"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
hdiutil create -volname "$APP_NAME" -srcfolder "$STAGE" -ov -format UDZO "$DMG" >/dev/null
rm -rf "$STAGE"

echo "完成：$DMG"
