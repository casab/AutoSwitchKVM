#!/bin/bash
# Builds a release of the macOS app: a universal (Apple silicon + Intel) Release .app with the
# given version, signed, inside a DMG with an Applications shortcut.
#
#   tools/release.sh <version>            e.g. tools/release.sh 0.2.0   (or v0.2.0, 0.2.0-beta.1)
#
# Environment:
#   SIGN_IDENTITY  codesign identity (name or SHA-1 in the keychain). Default "-" (ad-hoc).
#                  Without an Apple Developer ID the app can't be notarized either way, but a
#                  *fixed* self-made certificate keeps the same code identity across releases, so
#                  macOS keeps the Bluetooth permission and the login item after an update. Ad-hoc
#                  builds get a new identity every time (the permission is asked again).
#   BUILD_NUMBER   CFBundleVersion. Default: the git commit count.
#   OUT_DIR        where the DMG goes. Default: build/release.
#
# Runs locally and in .github/workflows/release.yml (macos job). Needs Xcode 16+ and XcodeGen.
set -euo pipefail

cd "$(dirname "$0")/.."

TAG="${1:?usage: tools/release.sh <version>}"
VERSION="${TAG#v}"
# CFBundleShortVersionString must be numbers and dots; a pre-release suffix (-beta.1) stays in
# the file name and the release, not in the bundle.
SHORT_VERSION="${VERSION%%-*}"
if ! [[ "$SHORT_VERSION" =~ ^[0-9]+(\.[0-9]+){0,2}$ ]]; then
    echo "error: '$TAG' is not a version like 1.2.3 or v1.2.3-beta.1" >&2
    exit 1
fi
BUILD_NUMBER="${BUILD_NUMBER:-$(git rev-list --count HEAD)}"
SIGN_IDENTITY="${SIGN_IDENTITY:--}"
OUT_DIR="${OUT_DIR:-build/release}"
SYMROOT="$PWD/build/xcode"
APP_NAME="AutoSwitchKVM"
DMG="$OUT_DIR/AutoSwitchKVM-$VERSION-macos.dmg"

echo "==> AutoSwitch KVM $VERSION (bundle $SHORT_VERSION, build $BUILD_NUMBER), signing: $SIGN_IDENTITY"

echo "==> Generating the Xcode project"
xcodegen generate --quiet

echo "==> Building (Release, arm64 + x86_64)"
rm -rf "$SYMROOT"
xcodebuild \
    -project "$APP_NAME.xcodeproj" \
    -target "$APP_NAME" \
    -configuration Release \
    SYMROOT="$SYMROOT" \
    ARCHS="arm64 x86_64" \
    ONLY_ACTIVE_ARCH=NO \
    CODE_SIGN_IDENTITY="-" \
    -quiet \
    build
APP="$SYMROOT/Release/$APP_NAME.app"
[ -d "$APP" ] || { echo "error: $APP was not built" >&2; exit 1; }

echo "==> Stamping the version"
PLIST="$APP/Contents/Info.plist"
plutil -replace CFBundleShortVersionString -string "$SHORT_VERSION" "$PLIST"
plutil -replace CFBundleVersion -string "$BUILD_NUMBER" "$PLIST"

echo "==> Signing"
# Re-sign after stamping (the plist is part of the signature). --deep also re-signs the embedded
# Sparkle.framework and its helpers (Autoupdate, Updater.app), so the whole bundle carries one
# identity. No hardened runtime: it only matters for notarization, which needs a Developer ID.
codesign --force --deep --sign "$SIGN_IDENTITY" --timestamp=none "$APP"
codesign --verify --strict --verbose=1 "$APP"

ARCHS_BUILT="$(lipo -archs "$APP/Contents/MacOS/$APP_NAME")"
echo "    architectures: $ARCHS_BUILT"
for arch in arm64 x86_64; do
    [[ " $ARCHS_BUILT " == *" $arch "* ]] || { echo "error: $arch missing from the binary" >&2; exit 1; }
done

echo "==> Packaging $DMG"
mkdir -p "$OUT_DIR"
STAGING="$(mktemp -d)"
trap 'rm -rf "$STAGING"' EXIT
cp -R "$APP" "$STAGING/"
ln -s /Applications "$STAGING/Applications"
rm -f "$DMG"
hdiutil create -quiet -volname "AutoSwitch KVM $VERSION" -srcfolder "$STAGING" -fs HFS+ -format UDZO "$DMG"
codesign --force --sign "$SIGN_IDENTITY" --timestamp=none "$DMG"
(cd "$OUT_DIR" && shasum -a 256 "$(basename "$DMG")" > "$(basename "$DMG").sha256")

echo "==> Done: $DMG"
