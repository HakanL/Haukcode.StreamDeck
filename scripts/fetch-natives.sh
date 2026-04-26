#!/usr/bin/env bash
#
# Downloads prebuilt hidapi shared libraries for every RID we support and
# places them under native/runtimes/<rid>/native/ in the repo.
#
# Sources:
#   win-x64, win-x86          libusb/hidapi GitHub release zip (MSVC build)
#   linux-x64, linux-arm64    Debian bookworm libhidapi-hidraw0 .deb
#   osx-x64, osx-arm64        Homebrew bottle (ghcr.io)
#
# Required tools: curl, ar, tar, unzip, xz-utils, jq
#   apt:  sudo apt-get install -y curl binutils tar unzip xz-utils jq
#   brew: brew install jq xz binutils
#
# Re-runnable; overwrites existing files. Run once per release of hidapi
# (or when bumping HIDAPI_VERSION below).
#
# Usage: scripts/fetch-natives.sh

set -euo pipefail

HIDAPI_VERSION="0.14.0"  # libusb/hidapi GitHub release tag (Windows binaries)

# Debian package version string for libhidapi-hidraw0 (Linux binaries).
# Debian bookworm currently ships 0.13.1-1; trixie/sid will move to 0.14.x.
# Browse https://packages.debian.org/search?keywords=libhidapi-hidraw0 to update.
DEBIAN_PKG="libhidapi-hidraw0_0.13.1-1_"

# Anonymous bearer token used by Homebrew clients for ghcr.io bottle pulls.
# This is publicly known — see https://github.com/Homebrew/brew/blob/master/Library/Homebrew/utils/curl.rb
HOMEBREW_TOKEN="QQ=="

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
NATIVES="$ROOT/native/runtimes"
WORK="$(mktemp -d)"
trap "rm -rf $WORK" EXIT

mkdir -p \
  "$NATIVES/win-x64/native" \
  "$NATIVES/win-x86/native" \
  "$NATIVES/linux-x64/native" \
  "$NATIVES/linux-arm64/native" \
  "$NATIVES/osx-x64/native" \
  "$NATIVES/osx-arm64/native"

echo "==> Windows (libusb/hidapi $HIDAPI_VERSION)"
curl -fsSL -o "$WORK/hidapi-win.zip" \
  "https://github.com/libusb/hidapi/releases/download/hidapi-${HIDAPI_VERSION}/hidapi-win.zip"
unzip -oq "$WORK/hidapi-win.zip" -d "$WORK/win"
cp "$WORK/win/x64/hidapi.dll" "$NATIVES/win-x64/native/hidapi.dll"
cp "$WORK/win/x86/hidapi.dll" "$NATIVES/win-x86/native/hidapi.dll"

extract_deb() {
  # $1 = url, $2 = dest dir
  local url="$1" dest="$2"
  local deb="$WORK/$(basename "$url")"
  curl -fsSL -o "$deb" "$url"
  mkdir -p "$dest"
  # Debian .deb is an ar archive containing data.tar.{xz,zst,gz}.
  # Try xz first (bookworm), fall back to gz.
  if ar t "$deb" | grep -q '^data\.tar\.xz$'; then
    ar p "$deb" data.tar.xz | tar -xJ -C "$dest"
  elif ar t "$deb" | grep -q '^data\.tar\.gz$'; then
    ar p "$deb" data.tar.gz | tar -xz -C "$dest"
  else
    echo "Unsupported data.tar compression in $deb"; exit 1
  fi
}

echo "==> Linux x64 (Debian bookworm)"
extract_deb "http://deb.debian.org/debian/pool/main/h/hidapi/${DEBIAN_PKG}amd64.deb" "$WORK/linux-x64"
cp "$WORK/linux-x64/usr/lib/x86_64-linux-gnu/libhidapi-hidraw.so.0" \
   "$NATIVES/linux-x64/native/libhidapi-hidraw.so.0"

echo "==> Linux arm64 (Debian bookworm)"
extract_deb "http://deb.debian.org/debian/pool/main/h/hidapi/${DEBIAN_PKG}arm64.deb" "$WORK/linux-arm64"
cp "$WORK/linux-arm64/usr/lib/aarch64-linux-gnu/libhidapi-hidraw.so.0" \
   "$NATIVES/linux-arm64/native/libhidapi-hidraw.so.0"

echo "==> macOS bottles (Homebrew)"
BREW_JSON="$WORK/hidapi-formula.json"
curl -fsSL -o "$BREW_JSON" "https://formulae.brew.sh/api/formula/hidapi.json"
HIDAPI_BREW_VERSION=$(jq -r '.versions.stable' "$BREW_JSON")

fetch_bottle() {
  # $1 = bottle key (e.g. sonoma, arm64_sonoma), $2 = dest RID (osx-x64 / osx-arm64)
  local key="$1" rid="$2"
  local url
  url=$(jq -r ".bottle.stable.files[\"${key}\"].url // empty" "$BREW_JSON")
  if [ -z "$url" ]; then
    echo "No bottle for $key — pick a different macOS version"; exit 1
  fi
  echo "    bottle key=$key  url=$url"
  curl -fsSL -H "Authorization: Bearer $HOMEBREW_TOKEN" -o "$WORK/$rid.tar.gz" "$url"
  mkdir -p "$WORK/$rid"
  tar -xzf "$WORK/$rid.tar.gz" -C "$WORK/$rid"
  # Bottle layout: hidapi/<version>/lib/libhidapi.0.dylib
  # HidApi.Net probes for "libhidapi.dylib" on macOS (no version suffix), so
  # we rename on copy. See HidApi.Net's NativeHidApiLibrary.GetOsxNames().
  cp "$WORK/$rid/hidapi/${HIDAPI_BREW_VERSION}/lib/libhidapi.0.dylib" \
     "$NATIVES/$rid/native/libhidapi.dylib"
}

# Bottle keys (macOS version names) — bump as Homebrew rolls forward.
fetch_bottle "sonoma"        "osx-x64"
fetch_bottle "arm64_sonoma"  "osx-arm64"

echo
echo "==> Done. Natives populated:"
find "$NATIVES" -type f | sort
