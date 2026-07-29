#!/usr/bin/env bash
# Bring the app hosting the current Claude Code session to the front.
#
# Reads ~/.claude/mx-console/state.json (written by ../hooks/mx_claude_state.py)
# and activates the recorded terminal. The Claude Console plugin calls this on
# a key press; you can also run it yourself or bind it to a Smart Action.
#
#   focus-claude.sh            focus the recorded app
#   focus-claude.sh --print    show what it would focus, activate nothing
#
# Exit codes: 0 focused, 1 nothing recorded, 2 recorded but activation failed.
set -uo pipefail

STATE_FILE="${HOME}/.claude/mx-console/state.json"
PRINT_ONLY=0
[[ "${1:-}" == "--print" ]] && PRINT_ONLY=1

if [[ ! -f "${STATE_FILE}" ]]; then
  echo "no active Claude Code session recorded at ${STATE_FILE}" >&2
  echo "run claude/hooks/install.sh, then start a Claude Code session." >&2
  exit 1
fi

# Unit-separator delimited, not tab: tab is an IFS *whitespace* character, so
# bash collapses runs of them and an empty field (a null "app", which is what
# you get when TERM_PROGRAM is unset) would silently shift every later field
# along by one. \x1f is not whitespace, so empty fields survive.
IFS=$'\x1f' read -r BUNDLE_ID APP_NAME STATE <<<"$(
  python3 - "${STATE_FILE}" <<'PY'
import json, sys
try:
    with open(sys.argv[1], encoding="utf-8") as fh:
        d = json.load(fh)
except Exception:
    d = {}
print("\x1f".join([d.get("bundle_id") or "", d.get("app") or "",
                   d.get("state") or "unknown"]))
PY
)"

if [[ -z "${BUNDLE_ID}" && -z "${APP_NAME}" ]]; then
  echo "state file records no app to focus; is the session running under a" >&2
  echo "terminal mx_claude_state.py recognises? See TERMINALS in that file." >&2
  exit 1
fi

if (( PRINT_ONLY )); then
  echo "state:     ${STATE}"
  echo "app:       ${APP_NAME:-<none>}"
  echo "bundle id: ${BUNDLE_ID:-<none>}"
  exit 0
fi

if [[ "${OSTYPE}" != darwin* ]]; then
  echo "focus-claude.sh currently only supports macOS" >&2
  exit 2
fi

ERRORS=""
if [[ -n "${BUNDLE_ID}" ]]; then
  if err=$(osascript -e "tell application id \"${BUNDLE_ID}\" to activate" 2>&1); then
    exit 0
  fi
  ERRORS+="  by bundle id ${BUNDLE_ID}: ${err}"$'\n'
fi

if [[ -n "${APP_NAME}" ]]; then
  if err=$(osascript -e "tell application \"${APP_NAME}\" to activate" 2>&1); then
    exit 0
  fi
  ERRORS+="  by name ${APP_NAME}: ${err}"$'\n'
fi

# Getting here means the app was recorded but macOS would not activate it -
# usually it is not installed, or Automation permission has not been granted.
echo "could not activate the recorded app:" >&2
printf '%s' "${ERRORS}" >&2
echo "If the app is installed, grant Automation permission under System" >&2
echo "Settings > Privacy & Security > Automation." >&2
exit 2
