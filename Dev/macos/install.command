#!/bin/bash
#
# Installs Melvor Mod Loader into the Steam macOS build of Melvor Idle 2.
# It installs the files that sit next to it: the loader's data folders and, if
# there is one, a "mods" folder.
#
#   bash install.command               install or update
#   bash install.command --uninstall   remove the loader (mods and settings stay)
#
# Run it again after a game update: Steam may replace the app bundle, which
# removes the loader. Your mods and their settings live next to the bundle
# ("Melvor Idle 2/mods") and are not touched by that.
#
# If the game is not in the default Steam library, point MELVOR_GAME_ROOT at
# the "Melvor Idle 2" folder (the one that holds Melvor2.app).

set -eu

HERE="$(cd "$(dirname "$0")" && pwd)"
GAME_ROOT="${MELVOR_GAME_ROOT:-$HOME/Library/Application Support/Steam/steamapps/common/Melvor Idle 2}"
APP="$GAME_ROOT/Melvor2.app"
RES="$APP/Contents/Resources"
MACOS="$APP/Contents/MacOS"
LOADER_GLOB="data_MelvorModLoader_macos_"

die() { echo "$*" >&2; exit 1; }
refreshed=0

[ -d "$RES" ] && [ -d "$MACOS" ] || die "Game not found: $APP
Point MELVOR_GAME_ROOT at your 'Melvor Idle 2' folder."
pgrep -x Melvor2 >/dev/null 2>&1 && die "Close the game first."

# macOS "App Management" protection blocks writes into an app bundle that has
# been launched before. Renaming the bundle aside and cloning it back gives a
# writable copy (clonefile: instant, shares its disk space). If the copy fails,
# the original is put back. macOS treats that copy as an app it has never
# started, and may then refuse the modified bundle as "damaged"; giving the
# terminal the App Management permission avoids the copy altogether.
make_writable() {
    if touch "$RES/.mml-writable" 2>/dev/null; then rm -f "$RES/.mml-writable"; return; fi
    echo "== Refreshing the app bundle (App Management workaround) =="
    mv "$APP" "$APP.mml-old" || die "Cannot modify $APP.
Give your terminal permission in System Settings > Privacy & Security > App Management and retry."
    if cp -cR "$APP.mml-old" "$APP" 2>/dev/null || { rm -rf "$APP"; cp -R "$APP.mml-old" "$APP"; }; then
        rm -rf "$APP.mml-old"
    else
        rm -rf "$APP"; mv "$APP.mml-old" "$APP"
        die "Could not refresh the app bundle; the game was left as it was."
    fi
    touch "$RES/.mml-writable" 2>/dev/null || die "Cannot write into $APP.
Give your terminal permission in System Settings > Privacy & Security > App Management and retry."
    rm -f "$RES/.mml-writable"
    refreshed=1
}

if [ "${1:-}" = "--uninstall" ]; then
    make_writable
    rm -f "$MACOS/override.cfg"
    rm -rf "$RES/$LOADER_GLOB"*
    echo "The loader is removed; the game starts as before."
    echo "Your mods and their settings are still in: $GAME_ROOT/mods"
    exit 0
fi

found=0
for dir in "$HERE/$LOADER_GLOB"*; do
    [ -f "$dir/MelvorModLoader.dll" ] && found=1
done
[ "$found" = 1 ] || die "The loader's files (${LOADER_GLOB}*) are not next to this script.
Extract the whole zip and run the script from the extracted folder."

make_writable

echo "== Installing the loader =="
rm -rf "$RES/$LOADER_GLOB"*
for dir in "$HERE/$LOADER_GLOB"*; do
    [ -f "$dir/MelvorModLoader.dll" ] || continue
    cp -R "$dir" "$RES/"
    # A zip that came through a browser is quarantined, and macOS would refuse to load its libraries.
    xattr -dr com.apple.quarantine "$RES/$(basename "$dir")" 2>/dev/null || true
done
printf '[dotnet]\nproject/assembly_name="MelvorModLoader"\n' > "$MACOS/override.cfg"

if [ -d "$HERE/mods" ]; then
    echo "== Installing mods =="
    mkdir -p "$GAME_ROOT/mods"
    cp -R "$HERE/mods/." "$GAME_ROOT/mods/"
    xattr -dr com.apple.quarantine "$GAME_ROOT/mods" 2>/dev/null || true
fi

echo
echo "Done. Start the game from Steam and watch the top-left corner for"
echo "\"Melvor Mod Loader - N mod\"."
if [ "$refreshed" = 1 ]; then
    echo
    echo "If macOS says the game is damaged: run this script with --uninstall, start the"
    echo "game once from Steam, close it, give your terminal permission in System Settings >"
    echo "Privacy & Security > App Management, and install again."
fi
echo "  Mods go into:  $GAME_ROOT/mods"
echo "  The log is at: $GAME_ROOT/MelvorModLoader.log"
