#!/usr/bin/env bash
# Register the MX Creative Console state hooks in ~/.claude/settings.json.
#
# Merges into whatever is already there rather than overwriting it, keeps a
# timestamped backup, and is safe to run twice - existing MX hook entries are
# replaced, not duplicated.
#
#   ./claude/hooks/install.sh            # install
#   ./claude/hooks/install.sh --uninstall
#   ./claude/hooks/install.sh --print    # show the merged result, write nothing
set -euo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SETTINGS="${HOME}/.claude/settings.json"
MODE="install"

for arg in "$@"; do
  case "${arg}" in
    --uninstall) MODE="uninstall" ;;
    --print)     MODE="print" ;;
    -h|--help)   sed -n '2,10p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "unknown option: ${arg}" >&2; exit 2 ;;
  esac
done

command -v python3 >/dev/null || { echo "python3 is required" >&2; exit 1; }

chmod +x "${REPO_DIR}/claude/hooks/mx_claude_state.py" \
         "${REPO_DIR}/claude/scripts/focus-claude.sh" 2>/dev/null || true

python3 - "${SETTINGS}" "${REPO_DIR}" "${MODE}" <<'PY'
import json, os, shutil, sys, time

settings_path, repo_dir, mode = sys.argv[1], sys.argv[2], sys.argv[3]
script = os.path.join(repo_dir, "claude", "hooks", "mx_claude_state.py")
marker = "mx_claude_state.py"

# Which Claude Code hook event maps to which published state.
EVENTS = {
    "SessionStart":     "idle",
    "UserPromptSubmit": "busy",
    "PreToolUse":       "busy",
    "PermissionRequest": "needs_input",
    "Notification":     "needs_input",
    "Stop":             "done",
    "SessionEnd":       "idle",
}

try:
    with open(settings_path, encoding="utf-8") as fh:
        settings = json.load(fh)
except FileNotFoundError:
    settings = {}
except ValueError as exc:
    sys.exit("%s is not valid JSON (%s); fix or move it first" % (settings_path, exc))

hooks = settings.setdefault("hooks", {})

# Drop any entry we previously installed, so re-running is idempotent.
for event in list(hooks):
    groups = []
    for group in hooks.get(event) or []:
        kept = [h for h in (group.get("hooks") or [])
                if marker not in str(h.get("command", ""))]
        if kept:
            group = dict(group, hooks=kept)
            groups.append(group)
    if groups:
        hooks[event] = groups
    else:
        hooks.pop(event, None)

if mode != "uninstall":
    for event, state in EVENTS.items():
        entry = {
            "matcher": "*",
            "hooks": [{
                "type": "command",
                "command": 'python3 "%s" %s' % (script, state),
            }],
        }
        hooks.setdefault(event, []).append(entry)

if not hooks:
    settings.pop("hooks", None)

blob = json.dumps(settings, indent=2) + "\n"

if mode == "print":
    print(blob)
    sys.exit(0)

os.makedirs(os.path.dirname(settings_path), exist_ok=True)
if os.path.exists(settings_path):
    backup = "%s.backup-%s" % (settings_path, time.strftime("%Y%m%d-%H%M%S"))
    shutil.copy2(settings_path, backup)
    print("backed up existing settings to %s" % backup)

with open(settings_path, "w", encoding="utf-8") as fh:
    fh.write(blob)

print("%s hooks in %s" % ("removed" if mode == "uninstall" else "installed",
                          settings_path))
if mode != "uninstall":
    print("state file: ~/.claude/mx-console/state.json")
    print("restart any running Claude Code session to pick the hooks up.")
PY
