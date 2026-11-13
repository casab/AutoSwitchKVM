#!/bin/bash
# Builds a ZMK firmware with the AutoSwitch module, as a compile check (CI) or to try the module
# locally. Runs inside the zmkfirmware/zmk-build-arm:stable image, e.g. from the repo root:
#
#   docker run --rm -v zmk-ws:/ws -v "$PWD:/repo" -e WS=/ws zmkfirmware/zmk-build-arm:stable \
#     bash /repo/firmware/zmk-autoswitch/ci/build.sh
#
# Environment: WS (west workspace, kept between runs; default .zmk-ws in the repo), CONFIG (a
# zmk-config "config" directory; default ci/config), BOARD (default nice_nano//zmk), SHIELDS
# (default "reviung41 raw_hid_adapter"), OUT (output directory; default out/ in the repo).
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../../.." && pwd)"
ws="${WS:-$repo/.zmk-ws}"
out="${OUT:-$repo/firmware/out}"
mkdir -p "$ws" "$out"
cd "$ws"
rm -rf config && cp -r "${CONFIG:-$here/config}" config
[ -d .west ] || west init -l config
west update --fetch-opt=--filter=tree:0 > west-update.log 2>&1 || { tail -30 west-update.log; exit 1; }
west zephyr-export > /dev/null
west build -s zmk/app -d build -b "${BOARD:-nice_nano//zmk}" -p -- \
  -DZMK_CONFIG="$ws/config" \
  "-DSHIELD=${SHIELDS:-reviung41 raw_hid_adapter}" \
  -DZMK_EXTRA_MODULES="$repo" > "$out/build.log" 2>&1 || { tail -60 "$out/build.log"; exit 1; }
grep -q 'zmk-autoswitch.*autoswitch.c.obj' "$out/build.log" || { echo "the AutoSwitch module wasn't compiled (CONFIG_ZMK_AUTOSWITCH off?)"; exit 1; }
grep -E 'autoswitch.*(warning|error)' "$out/build.log" && { echo "warnings in the AutoSwitch module"; exit 1; }
tail -6 "$out/build.log"
cp build/zephyr/zmk.uf2 "$out/zmk-autoswitch-test.uf2"
echo "UF2: $out/zmk-autoswitch-test.uf2"
