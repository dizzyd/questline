#!/usr/bin/env bash
# Takes the ModDB screenshots: runs the Gallery tests on the test box at a desktop size and
# copies the PNGs back into screenshots/moddb/. The Expanded Foods shot needs the addon mods, so it
# runs a second time with ~/questline-addon-mods.
#
#   bash scripts/screenshots.sh [user@host] [--force]
#
# Shot.Take copies the newest new file from a screenshots folder every slot on the box
# shares, so a second client taking pictures at the same time can swap them. This refuses
# to run while another client is up, unless --force.
set -euo pipefail

host="dizzyd@vsclient.home"
force=0
for arg in "$@"; do
    case "$arg" in
        --force) force=1 ;;
        *) host="$arg" ;;
    esac
done

repo="$(cd "$(dirname "$0")/.." && pwd)"
vstestkit="${VSTESTKIT:-$repo/../vstestkit}"
out="$repo/screenshots/moddb"
width=1600 height=900

clients="$(ssh "$host" 'cd vstestkit-questline 2>/dev/null || cd vstestkit; bash scripts/slots' | sed -n 's/^clients \([0-9]*\)\/.*/\1/p')"
if [ "${clients:-0}" != "0" ] && [ "$force" = "0" ]; then
    echo "$clients other client(s) running on $host; their screenshots could swap with these." >&2
    echo "Wait for them, or pass --force." >&2
    exit 1
fi

(cd "$vstestkit" && bash scripts/sync-linux.sh "$host" --mod "$repo/questline")

# The testkit's client window is 960x600; this slot's copy is resized for the pictures (the
# next sync puts it back).
run='cd vstestkit-questline
    sed -i -e "s/\"screenWidth\": [0-9]*/\"screenWidth\": '$width'/" -e "s/\"screenHeight\": [0-9]*/\"screenHeight\": '$height'/" templates/clientsettings.json
    export VSTK_SCREEN_WIDTH='$width' VSTK_SCREEN_HEIGHT='$height

# Keep whatever shots were taken even if one fails, then report the failure
status=0
ssh "$host" "rm -f ~/.cairn/games/*/questline-gallery-*.png
    $run
    bash scripts/run.sh mods/questline/tests --mod mods/questline/questline --client --filter Gallery
    bash scripts/run.sh mods/questline/tests --mod mods/questline/questline --mods \"\$HOME/questline-addon-mods\" --client --filter ExpandedFoodsKitchen" || status=$?

mkdir -p "$out"
rm -f "$out"/questline-gallery-*.png
scp -q "$host:.cairn/games/*/questline-gallery-*.png" "$out/" || true
ssh "$host" 'rm -f ~/.cairn/games/*/questline-gallery-*.png'

ls -1 "$out"
exit "$status"
