#!/usr/bin/env python3
"""Publish Claude Code's current state where the MX Creative Console can see it.

Claude Code hooks invoke this with the state name as ``argv[1]`` and the hook
payload as JSON on stdin. It maintains a single small file:

    ~/.claude/mx-console/state.json

which the Claude Console plugin polls a few times a second to decide whether
the attention key should be flashing.

    {"state": "needs_input",
     "session_id": "...", "cwd": "/Users/you/project", "title": "project",
     "app": "iTerm", "bundle_id": "com.googlecode.iterm2",
     "reason": "permission_prompt", "ts": 1769600000}

States
------
``needs_input``  Claude is blocked on you - a permission prompt or an idle
                 prompt. This is the one that flashes.
``busy``         Claude is working.
``done``         Claude finished its turn.
``idle``         A session exists but nothing is happening.

Nothing here should ever fail loudly: a broken hook must not break the
session, so every path exits 0.
"""

from __future__ import annotations

import json
import os
import sys
import time

STATE_DIR = os.path.join(os.path.expanduser("~"), ".claude", "mx-console")
STATE_FILE = os.path.join(STATE_DIR, "state.json")

VALID_STATES = {"needs_input", "busy", "done", "idle"}

# TERM_PROGRAM -> (AppleScript application name, bundle id)
TERMINALS = {
    "Apple_Terminal": ("Terminal", "com.apple.Terminal"),
    "iTerm.app": ("iTerm", "com.googlecode.iterm2"),
    "vscode": ("Visual Studio Code", "com.microsoft.VSCode"),
    "Warp": ("Warp", "dev.warp.Warp-Stable"),
    "WarpTerminal": ("Warp", "dev.warp.Warp-Stable"),
    "ghostty": ("Ghostty", "com.mitchellh.ghostty"),
    "WezTerm": ("WezTerm", "com.github.wez.wezterm"),
    "Hyper": ("Hyper", "co.zeit.hyper"),
    "Alacritty": ("Alacritty", "org.alacritty"),
    "kitty": ("kitty", "net.kovidgoyal.kitty"),
}

# Bundle ids that identify a VS Code fork more precisely than TERM_PROGRAM does.
BUNDLE_OVERRIDES = {
    "com.todesktop.230313mzl4w4u92": ("Cursor", "com.todesktop.230313mzl4w4u92"),
    "com.exafunction.windsurf": ("Windsurf", "com.exafunction.windsurf"),
    "dev.zed.Zed": ("Zed", "dev.zed.Zed"),
}


def detect_terminal() -> tuple[str | None, str | None]:
    """Best guess at the app hosting this session, for focusing it later.

    ``__CFBundleIdentifier`` is inherited from whatever launched the process
    tree, which is not necessarily the terminal you can see - launching from
    a desktop app leaves it pointing at that app. So TERM_PROGRAM wins, and
    the ambient bundle id is only consulted when it names a known VS Code
    fork (which all report ``TERM_PROGRAM=vscode``) or when TERM_PROGRAM is
    no help at all.
    """
    bundle = os.environ.get("__CFBundleIdentifier")
    term = os.environ.get("TERM_PROGRAM", "")

    if term == "vscode" and bundle in BUNDLE_OVERRIDES:
        return BUNDLE_OVERRIDES[bundle]

    if term in TERMINALS:
        return TERMINALS[term]

    if bundle in BUNDLE_OVERRIDES:
        return BUNDLE_OVERRIDES[bundle]

    if bundle:
        # Unknown host, but the bundle id alone is enough to activate it.
        return None, bundle
    return None, None


def read_existing() -> dict:
    try:
        with open(STATE_FILE, encoding="utf-8") as fh:
            data = json.load(fh)
        return data if isinstance(data, dict) else {}
    except (OSError, ValueError):
        return {}


def main() -> int:
    state = (sys.argv[1] if len(sys.argv) > 1 else "idle").strip()
    if state not in VALID_STATES:
        state = "idle"

    payload = {}
    if not sys.stdin.isatty():
        try:
            raw = sys.stdin.read()
            if raw.strip():
                payload = json.loads(raw)
        except (ValueError, OSError):
            payload = {}
    if not isinstance(payload, dict):
        payload = {}

    # SessionEnd clears the file so a stale key does not keep glowing.
    if state == "idle" and payload.get("reason") in {
        "logout", "prompt_input_exit", "other", "clear",
    }:
        try:
            os.remove(STATE_FILE)
        except OSError:
            pass
        return 0

    previous = read_existing()
    cwd = payload.get("cwd") or os.getcwd()

    # app and bundle_id are one answer, not two independent fields - mixing a
    # freshly detected bundle id with a remembered app name would point
    # focus-claude.sh at the wrong window.
    app, bundle_id = detect_terminal()
    if not (app or bundle_id):
        app, bundle_id = previous.get("app"), previous.get("bundle_id")

    record = {
        "state": state,
        "session_id": payload.get("session_id") or previous.get("session_id"),
        "cwd": cwd,
        "title": os.path.basename(cwd.rstrip("/")) or cwd,
        "app": app,
        "bundle_id": bundle_id,
        "reason": payload.get("notification_type") or payload.get("reason") or "",
        "ts": int(time.time()),
    }

    try:
        os.makedirs(STATE_DIR, exist_ok=True)
        # Write-then-rename so the plugin never reads a half-written file.
        tmp = STATE_FILE + ".tmp"
        with open(tmp, "w", encoding="utf-8") as fh:
            json.dump(record, fh)
        os.replace(tmp, STATE_FILE)
    except OSError:
        pass

    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception:  # noqa: BLE001 - a hook must never break the session
        sys.exit(0)
