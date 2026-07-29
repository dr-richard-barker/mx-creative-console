#!/usr/bin/env bash
#
# Build the Claude Console plugin and sideload it into the Logi Plugin Service.
#
# Usage:
#   ./build.sh              # Release build + sideload + reload
#   ./build.sh Debug        # Debug build
#   ./build.sh Release nolink   # build only, do not touch the Plugins directory
#
# What "sideloading" means here: the Logi Plugin Service reads every *.link file in its Plugins
# directory. A .link file contains a single line — the absolute path of a directory holding the
# plugin DLL and its metadata/ folder. That is how the official Logitech DemoPlugin and the
# CodexBarUsage sample install a locally-built plugin without packaging an .lplug4.
#
#   macOS   ~/Library/Application Support/Logi/LogiPluginService/Plugins/ClaudeConsolePlugin.link
#   Windows %LOCALAPPDATA%\Logi\LogiPluginService\Plugins\ClaudeConsolePlugin.link
#           (PowerShell equivalent of the two lines below:
#              New-Item -ItemType Directory -Force "$env:LOCALAPPDATA\Logi\LogiPluginService\Plugins"
#              Set-Content "$env:LOCALAPPDATA\Logi\LogiPluginService\Plugins\ClaudeConsolePlugin.link" "$OUT"
#              Start-Process "loupedeck:plugin/ClaudeConsolePlugin/reload" )
#
set -euo pipefail

CONFIGURATION="${1:-Release}"
LINK_MODE="${2:-link}"

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$HERE/src/ClaudeConsolePlugin.csproj"
OUT="$HERE/bin/$CONFIGURATION"

PLUGIN_SHORT_NAME="ClaudeConsolePlugin"
PLUGINS_DIR="$HOME/Library/Application Support/Logi/LogiPluginService/Plugins"
LINK_FILE="$PLUGINS_DIR/$PLUGIN_SHORT_NAME.link"
PLUGIN_API="/Applications/Utilities/LogiPluginService.app/Contents/MonoBundle/PluginApi.dll"

info()  { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
warn()  { printf '\033[1;33m warn\033[0m %s\n' "$*" >&2; }
fail()  { printf '\033[1;31merror\033[0m %s\n' "$*" >&2; exit 1; }

# ---- Preflight ---------------------------------------------------------------------------------

command -v dotnet >/dev/null 2>&1 || fail \
  "dotnet not found. Install the .NET 8 SDK first: brew install --cask dotnet-sdk  (or https://dotnet.microsoft.com/download/dotnet/8.0)"

if [[ "$(uname -s)" == "Darwin" && ! -f "$PLUGIN_API" ]]; then
  fail "PluginApi.dll not found at $PLUGIN_API. Install Logi Options+ (it installs the Logi Plugin Service) and try again."
fi

info "dotnet $(dotnet --version)"

# ---- Build -------------------------------------------------------------------------------------

info "Building $PLUGIN_SHORT_NAME ($CONFIGURATION)"
dotnet build "$PROJECT" -c "$CONFIGURATION" --nologo

[[ -f "$OUT/$PLUGIN_SHORT_NAME.dll" ]] || fail "Build produced no $OUT/$PLUGIN_SHORT_NAME.dll"
[[ -f "$OUT/metadata/LoupedeckPackage.yaml" ]] || warn "No metadata/LoupedeckPackage.yaml in $OUT — the service will refuse to load the plugin."

# ---- Sideload ----------------------------------------------------------------------------------

if [[ "$LINK_MODE" == "nolink" ]]; then
  info "Skipping sideload (nolink). Output: $OUT"
  exit 0
fi

mkdir -p "$PLUGINS_DIR"
printf '%s\n' "$OUT" > "$LINK_FILE"
info "Sideloaded: $LINK_FILE -> $OUT"

# ---- Reload ------------------------------------------------------------------------------------

if open "loupedeck:plugin/$PLUGIN_SHORT_NAME/reload" 2>/dev/null; then
  info "Asked the Logi Plugin Service to reload the plugin."
else
  warn "Could not send the reload URL. Quit Logi Options+ AND the Logi Plugin Service, then relaunch Logi Options+."
fi

cat <<EOF

Next:
  1. Open Logi Options+ -> your MX Creative Console keypad.
  2. Plugins / Installed plugins -> "Claude Console".
  3. Drag "Claude Attention" and "Claude Continue" onto adjacent keys.
  4. Verify with:
       mkdir -p ~/.claude/mx-console
       printf '%s' '{"state":"needs_input","session_id":"test","cwd":"'"\$PWD"'","title":"demo","app":"Terminal","ts":'"\$(date +%s)"'}' > ~/.claude/mx-console/state.json
     The Attention key should start flashing amber within ~250 ms.
     Then:
       printf '%s' '{"state":"idle","ts":'"\$(date +%s)"'}' > ~/.claude/mx-console/state.json
     and it should go dark.

Logs: ~/Library/Application Support/Logi/LogiPluginService/Logs
EOF
