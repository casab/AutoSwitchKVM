#!/bin/bash
# Prints the CHANGELOG.md section for a release tag (without its heading), for the release body.
#
#   .github/release/changelog-section.sh <tag> [changelog]
#
# Stable tag vX.Y.Z: the section "## vX.Y.Z ..." must exist and not be empty, else exit 1 (a
# release without notes fails early, before anything is built).
# Pre-release vX.Y.Z-rc.1: its own section, else vX.Y.Z, else "## Unreleased".
set -euo pipefail

TAG="${1:?usage: changelog-section.sh <tag> [changelog]}"
FILE="${2:-CHANGELOG.md}"
BASE="${TAG%%-*}"

section() {
    # Lines after a heading "## <name>" or "## <name> (date)" up to the next "## " heading.
    awk -v name="$1" '
        /^## / {
            if (found) exit
            h = substr($0, 4)
            if (h == name || index(h, name " ") == 1) { found = 1; next }
        }
        found { print }
    ' "$FILE" | sed -e '/./,$!d' | sed -e ':a' -e '/^\n*$/{$d;N;ba' -e '}'
}

has_content() { grep -q '[^[:space:]]' <<< "$1"; }

candidates=("$TAG")
if [[ "$TAG" == *-* ]]; then candidates+=("$BASE" "Unreleased"); fi

for name in "${candidates[@]}"; do
    body="$(section "$name")"
    if has_content "$body"; then
        printf '%s\n' "$body"
        exit 0
    fi
done

echo "error: CHANGELOG.md has no (non-empty) section for $TAG. Rename '## Unreleased' to '## $TAG (date)' before tagging." >&2
exit 1
