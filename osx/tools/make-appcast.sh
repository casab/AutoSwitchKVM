#!/bin/bash
# Writes the Sparkle appcast for one release: an appcast.xml with a single item pointing at the
# DMG on the GitHub release, signed with the EdDSA key (Sparkle's sign_update).
#
#   tools/make-appcast.sh <version> <dmg> <build-number> <out-appcast.xml>
#
# Environment:
#   SPARKLE_BIN               folder with Sparkle's sign_update (from the Sparkle release tarball)
#   SPARKLE_ED_PRIVATE_KEY    the private EdDSA key (base64, as `generate_keys -x` exports it)
#   GITHUB_REPOSITORY         owner/repo (set by GitHub Actions). Default casab/AutoSwitchKVM.
#
# A one-item feed is enough: Sparkle offers the newest item whose version is higher than the
# installed one. The feed URL in the app is .../releases/latest/download/appcast.xml, and GitHub's
# "latest" release is the newest *stable* one, so pre-releases never reach it.
set -euo pipefail

TAG="${1:?usage: make-appcast.sh <version> <dmg> <build> <out>}"
DMG="${2:?dmg}"
BUILD="${3:?build number}"
OUT="${4:?output path}"
VERSION="${TAG#v}"
SHORT_VERSION="${VERSION%%-*}"
REPO="${GITHUB_REPOSITORY:-casab/AutoSwitchKVM}"
: "${SPARKLE_BIN:?SPARKLE_BIN must point at the bin folder of the Sparkle release}"
: "${SPARKLE_ED_PRIVATE_KEY:?SPARKLE_ED_PRIVATE_KEY is not set}"

DMG_NAME="$(basename "$DMG")"
URL="https://github.com/$REPO/releases/download/v$VERSION/$DMG_NAME"
NOTES="https://github.com/$REPO/releases/tag/v$VERSION"

# Prints: sparkle:edSignature="…" length="…"
SIGNATURE_ATTRS="$(printf '%s' "$SPARKLE_ED_PRIVATE_KEY" | "$SPARKLE_BIN/sign_update" --ed-key-file - "$DMG")"
if [[ "$SIGNATURE_ATTRS" != *sparkle:edSignature=*length=* ]]; then
    echo "error: unexpected sign_update output: $SIGNATURE_ATTRS" >&2
    exit 1
fi

PUB_DATE="$(LC_ALL=C date -u '+%a, %d %b %Y %H:%M:%S +0000')"
cat > "$OUT" <<XML
<?xml version="1.0" encoding="utf-8"?>
<rss version="2.0" xmlns:sparkle="http://www.andymatuschak.org/xml-namespaces/sparkle">
  <channel>
    <title>AutoSwitch KVM</title>
    <link>https://github.com/$REPO</link>
    <item>
      <title>AutoSwitch KVM $VERSION</title>
      <pubDate>$PUB_DATE</pubDate>
      <sparkle:version>$BUILD</sparkle:version>
      <sparkle:shortVersionString>$SHORT_VERSION</sparkle:shortVersionString>
      <sparkle:minimumSystemVersion>13.0</sparkle:minimumSystemVersion>
      <sparkle:fullReleaseNotesLink>$NOTES</sparkle:fullReleaseNotesLink>
      <enclosure url="$URL" type="application/octet-stream" $SIGNATURE_ATTRS />
    </item>
  </channel>
</rss>
XML
echo "==> Wrote $OUT for $VERSION (build $BUILD)"
