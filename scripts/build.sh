#!/usr/bin/env bash
set -euo pipefail

configuration="Release"
profile_path="${DESKTOPBUDDY_PROFILE_PATH:-$HOME/.local/share/com.kesomannen.gale/resonite/profiles/Default}"
no_deploy=0
restart=0
resonite_appid="${RESONITE_APPID:-2519830}"

if [[ -x "$HOME/.dotnet/dotnet" ]]; then
  export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
  case ":$PATH:" in
    *":$DOTNET_ROOT:"*) ;;
    *) export PATH="$DOTNET_ROOT:$PATH" ;;
  esac
  case ":$PATH:" in
    *":$DOTNET_ROOT/tools:"*) ;;
    *) export PATH="$DOTNET_ROOT/tools:$PATH" ;;
  esac
fi

usage() {
  cat <<'EOF'
Usage: scripts/build.sh [options]

Options:
  -c, --configuration NAME   Build configuration. Default: Release.
  -p, --profile PATH         r2modman/Gale profile root to deploy into.
                             Default: $DESKTOPBUDDY_PROFILE_PATH or
                             /home/devl0rd/.local/share/com.kesomannen.gale/resonite/profiles/Default
      --no-deploy            Build only.
  -r, --restart              After building/deploying, kill Resonite and relaunch via Steam.
  -h, --help                 Show this help.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    -c|--configuration)
      configuration="${2:?missing configuration}"
      shift 2
      ;;
    -p|--profile)
      profile_path="${2:?missing profile path}"
      shift 2
      ;;
    --no-deploy)
      no_deploy=1
      shift
      ;;
    -r|--restart)
      restart=1
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage >&2
      exit 2
      ;;
  esac
done

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
resonite_path="${RESONITE_PATH:-$HOME/.local/share/Steam/steamapps/common/Resonite}"
no_deploy_resonite_path='C:\__DesktopBuddyNoDeploy__'

require_tool() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "Required tool '$1' was not found on PATH." >&2
    exit 127
  fi
}

find_mod_output() {
  local base="$root/DesktopBuddy/bin/$configuration"
  find "$base" -maxdepth 1 -type d -name 'net10.0-windows*' 2>/dev/null |
    sort -r |
    while read -r dir; do
      [[ -f "$dir/DesktopBuddy.dll" ]] && printf '%s\n' "$dir" && return 0
    done
}

copy_file() {
  local source="$1"
  local dest="$2"
  mkdir -p "$(dirname "$dest")"
  cp -f "$source" "$dest"
}

copy_tree_files() {
  local source_dir="$1"
  local dest_dir="$2"
  mkdir -p "$dest_dir"
  find "$source_dir" -type f -print0 | while IFS= read -r -d '' file; do
    local rel="${file#$source_dir/}"
    copy_file "$file" "$dest_dir/$rel"
  done
}

deploy_profile() {
  local mod_out="$1"
  local bridge_dll="$root/DesktopBuddySharedTextureBridge/bin/$configuration/net472/DesktopBuddySharedTextureBridge.dll"
  local linux_bridge_dir="$root/DesktopBuddyLinuxBridge/bin/$configuration"
  local runtime_source="$root/DesktopBuddyRuntime"
  local plugins_root="$profile_path/BepInEx/plugins"
  local game_plugin_dir="$plugins_root/DevL0rd-DesktopBuddy/DesktopBuddy"
  local runtime_target="$plugins_root/DevL0rd-DesktopBuddyRuntime/DesktopBuddy/DesktopBuddyRuntime"
  local bridge_target="$profile_path/Renderer/BepInEx/plugins/DevL0rd-DesktopBuddy/DesktopBuddySharedTextureBridge"

  for required in \
    "$mod_out/DesktopBuddy.dll" \
    "$mod_out/icon_transparent.png" \
    "$mod_out/plus.png" \
    "$root/scripts/CollectDesktopBuddyDiagnostics.ps1" \
    "$bridge_dll" \
    "$linux_bridge_dir/DesktopBuddyLinuxBridge.so" \
    "$linux_bridge_dir/libdesktopbuddy_linux_native.so" \
    "$linux_bridge_dir/libdesktopbuddy_linux_stream.so" \
    "$runtime_source"; do
    if [[ ! -e "$required" ]]; then
      echo "Required deploy input not found: $required" >&2
      exit 1
    fi
  done

  for dependency in FFmpeg.AutoGen.dll Microsoft.Windows.SDK.NET.dll WinRT.Runtime.dll; do
    if [[ ! -f "$mod_out/$dependency" ]]; then
      echo "DesktopBuddy build dependency not found: $mod_out/$dependency" >&2
      exit 1
    fi
  done

  echo "Deploying DesktopBuddy to profile: $profile_path"
  mkdir -p "$game_plugin_dir" "$runtime_target" "$bridge_target"

  copy_file "$mod_out/DesktopBuddy.dll" "$game_plugin_dir/DesktopBuddy.dll"
  [[ -f "$mod_out/DesktopBuddy.sha" ]] && copy_file "$mod_out/DesktopBuddy.sha" "$game_plugin_dir/DesktopBuddy.sha"
  copy_file "$mod_out/icon_transparent.png" "$game_plugin_dir/icon_transparent.png"
  copy_file "$mod_out/plus.png" "$game_plugin_dir/plus.png"
  copy_file "$root/scripts/CollectDesktopBuddyDiagnostics.ps1" "$game_plugin_dir/CollectDesktopBuddyDiagnostics.ps1"

  copy_tree_files "$runtime_source" "$runtime_target"
  copy_file "$linux_bridge_dir/DesktopBuddyLinuxBridge.so" "$runtime_target/DesktopBuddyLinuxBridge.so"
  copy_file "$linux_bridge_dir/libdesktopbuddy_linux_native.so" "$runtime_target/libdesktopbuddy_linux_native.so"
  copy_file "$linux_bridge_dir/libdesktopbuddy_linux_stream.so" "$runtime_target/libdesktopbuddy_linux_stream.so"
  for dependency in FFmpeg.AutoGen.dll Microsoft.Windows.SDK.NET.dll WinRT.Runtime.dll; do
    copy_file "$mod_out/$dependency" "$runtime_target/$dependency"
  done

  copy_file "$bridge_dll" "$bridge_target/DesktopBuddySharedTextureBridge.dll"
  copy_file "$linux_bridge_dir/DesktopBuddyLinuxBridge.so" "$bridge_target/DesktopBuddyLinuxBridge.so"
  copy_file "$linux_bridge_dir/libdesktopbuddy_linux_native.so" "$bridge_target/libdesktopbuddy_linux_native.so"
  copy_file "$linux_bridge_dir/libdesktopbuddy_linux_stream.so" "$bridge_target/libdesktopbuddy_linux_stream.so"

  # The guard's strike count describes the binaries that crashed, and the copies above just
  # replaced them. Keeping it would have a fresh build — quite possibly the fix — refuse to
  # attempt native capture at all, with only a startup log line to say why.
  local capture_guard="$bridge_target/linux-capture.guard"
  if [[ -f "$capture_guard" ]]; then
    echo "Cleared Linux capture guard from a previous crash: $capture_guard"
    rm -f "$capture_guard"
  fi

  rm -f \
    "$profile_path/BepInEx/cache/chainloader_typeloader.dat" \
    "$profile_path/Renderer/BepInEx/cache/chainloader_typeloader.dat"
}

require_tool dotnet
require_tool cargo
require_tool cc
require_tool pkg-config

cd "$root"

build_args=(
  -c "$configuration"
  /p:EnableWindowsTargeting=true
  /p:DesktopBuddySkipDeploy=true
)

if [[ -d "$resonite_path" ]]; then
  build_args+=(/p:ResonitePath="$resonite_path")
else
  build_args+=(/p:ResonitePath="$no_deploy_resonite_path")
fi

echo "Building DesktopBuddy ($configuration)"
dotnet build "$root/DesktopBuddy/DesktopBuddy.csproj" "${build_args[@]}"

echo "Building DesktopBuddySharedTextureBridge ($configuration)"
dotnet build "$root/DesktopBuddySharedTextureBridge/DesktopBuddySharedTextureBridge.csproj" "${build_args[@]}"

"$root/scripts/build-native.sh" "$configuration"

"$root/scripts/fetch-thirdparty.sh"

if [[ "$no_deploy" -eq 0 ]]; then
  if [[ ! -d "$profile_path/BepInEx/plugins" ]]; then
    echo "Profile does not look like a Resonite BepInEx profile: $profile_path" >&2
    exit 1
  fi

  mod_out="$(find_mod_output)"
  if [[ -z "${mod_out:-}" ]]; then
    echo "DesktopBuddy.dll not found under DesktopBuddy/bin/$configuration/net10.0-windows*" >&2
    exit 1
  fi

  deploy_profile "$mod_out"
fi

restart_resonite() {
  echo "Stopping any running Resonite..."
  pkill -f 'common/Resonite' 2>/dev/null || true
  pkill -f 'Renderite.Host' 2>/dev/null || true
  sleep 2

  local steam_cmd="steam"
  case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*) steam_cmd="steam.exe" ;;
  esac

  if ! command -v "$steam_cmd" >/dev/null 2>&1; then
    echo "Cannot restart: '$steam_cmd' not found on PATH." >&2
    return 1
  fi

  echo "Launching Resonite ($resonite_appid) via $steam_cmd..."
  setsid "$steam_cmd" -applaunch "$resonite_appid" >/dev/null 2>&1 &
  echo "Launch requested."
}

if [[ $restart -eq 1 ]]; then
  restart_resonite
fi

echo "Done."
