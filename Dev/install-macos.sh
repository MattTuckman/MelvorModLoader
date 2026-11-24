#!/usr/bin/env bash
#
# Builds Melvor Mod Loader for macOS (and any mods named on the command line),
# then installs it into the Steam game or packs it as a zip for players.
#
#   ./Dev/install-macos.sh                       build and install the loader
#   ./Dev/install-macos.sh CheatMenu BulkSell    ... and these mods
#   ./Dev/install-macos.sh --package             build dist/MelvorModLoader-macos.zip instead
#   ./Dev/install-macos.sh --package CheatMenu   ... with these mods in the zip
#
# The game's DLLs are needed as compile-time references, so the game must be
# installed here either way. If it is not in the default Steam library, point
# MELVOR_GAME_ROOT at the "Melvor Idle 2" folder.
#
# The install itself is Dev/macos/install.command, the same script the zip
# carries; see it for what goes where.

set -eu

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GAME_ROOT="${MELVOR_GAME_ROOT:-$HOME/Library/Application Support/Steam/steamapps/common/Melvor Idle 2}"
RES="$GAME_ROOT/Melvor2.app/Contents/Resources"
NAME="MelvorModLoader-macos"
STAGE="$ROOT/out/$NAME"

package=0
if [ "${1:-}" = "--package" ]; then package=1; shift; fi

command -v dotnet >/dev/null 2>&1 || {
    echo "dotnet not found. Install the .NET SDK 10 first: https://dotnet.microsoft.com/download" >&2
    exit 1
}

# Reference reading is architecture-neutral, so either data folder will do.
REFS="$RES/data_GamesByMalcs.Melvor2_macos_arm64"
[ -d "$REFS" ] || REFS="$RES/data_GamesByMalcs.Melvor2_macos_x86_64"
[ -d "$REFS" ] || {
    echo "Game not found: $GAME_ROOT/Melvor2.app" >&2
    echo "Point MELVOR_GAME_ROOT at your 'Melvor Idle 2' folder." >&2
    exit 1
}
for mod in "$@"; do
    [ -f "$ROOT/Mods/$mod/$mod.csproj" ] || { echo "No such mod: Mods/$mod" >&2; exit 1; }
done

rm -rf "$STAGE"
mkdir -p "$STAGE"

echo "== Building the loader (arm64 and x86_64) =="
# NoWarn=8012: the game's DLLs are ready-to-run images for one architecture;
# as compile-time references that is fine.
dotnet publish "$ROOT/Loader/MelvorModLoader.csproj" -c Release -r osx-arm64 --nologo -v q \
    -p:GameData="$REFS" -p:NoWarn=8012 -o "$STAGE/data_MelvorModLoader_macos_arm64"
dotnet publish "$ROOT/Loader/MelvorModLoader.csproj" -c Release -r osx-x64 --nologo -v q \
    -p:GameData="$REFS" -p:NoWarn=8012 -o "$STAGE/data_MelvorModLoader_macos_x86_64"

for mod in "$@"; do
    echo "== Building mod: $mod =="
    dotnet build "$ROOT/Mods/$mod/$mod.csproj" -c Release --nologo -v q \
        -p:GameData="$REFS" -p:LoaderDir="$STAGE/data_MelvorModLoader_macos_arm64" -p:NoWarn=8012
    mkdir -p "$STAGE/mods/$mod"
    cp "$ROOT/Mods/$mod/bin/Release/net10.0/$mod.dll" "$STAGE/mods/$mod/"
    [ -f "$ROOT/Mods/$mod/README.txt" ] && cp "$ROOT/Mods/$mod/README.txt" "$STAGE/mods/$mod/"
    # files a mod reads from its own folder at run time (One Style's art and data)
    for extra in art data; do
        [ -d "$ROOT/Mods/$mod/$extra" ] && cp -R "$ROOT/Mods/$mod/$extra" "$STAGE/mods/$mod/"
    done
done

cp "$ROOT/Dev/macos/install.command" "$STAGE/"
chmod +x "$STAGE/install.command"

if [ "$package" = 1 ]; then
    mkdir -p "$ROOT/dist"
    rm -f "$ROOT/dist/$NAME.zip"
    ditto -c -k --norsrc --noextattr --keepParent "$STAGE" "$ROOT/dist/$NAME.zip"
    echo
    echo "Packed: $ROOT/dist/$NAME.zip"
    echo "Players extract it and run:  bash install.command"
else
    MELVOR_GAME_ROOT="$GAME_ROOT" bash "$STAGE/install.command"
fi
