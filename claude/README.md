# Claude Code integration

Three pieces, in increasing order of how much has actually been tested:

| | What it does | Status |
|---|---|---|
| `hooks/` | Publishes Claude Code's state to a file | Tested |
| `scripts/focus-claude.sh` | Brings the Claude session's window forward | Tested |
| `plugin/` | Flashes a key on the device when Claude is waiting | **Never compiled** |

The first two work on their own. The plugin is what turns a key into a
notification light, and it is the part that needs the .NET 8 SDK.

## The state file

Everything here communicates through one small file:

```
~/.claude/mx-console/state.json
```

```json
{
  "state": "needs_input",
  "session_id": "3f1c…",
  "cwd": "/Users/you/project",
  "title": "project",
  "app": "iTerm",
  "bundle_id": "com.googlecode.iterm2",
  "reason": "permission_prompt",
  "ts": 1769600000
}
```

| Field | |
|---|---|
| `state` | `needs_input`, `busy`, `done` or `idle`. |
| `title` | Basename of `cwd`, so a key can show which project is asking. |
| `app` / `bundle_id` | The host terminal, for focusing it. Always written as a pair. |
| `reason` | The hook's `notification_type`, e.g. `permission_prompt`, `idle_prompt`. |
| `ts` | Unix seconds. Consumers should treat a very old file as stale. |

Writes are atomic (write to `.tmp`, then rename), so a reader polling the file
never sees a half-written record.

## Hooks

```bash
./hooks/install.sh              # install
./hooks/install.sh --print      # preview the merged settings, write nothing
./hooks/install.sh --uninstall  # remove
```

The installer merges into your existing `~/.claude/settings.json`, backs it up
first, and strips any previously installed MX entries before adding new ones, so
running it repeatedly will not pile up duplicates. Other hooks and settings are
left alone.

It also symlinks the focus helper to `~/.claude/mx-console/focus-claude.sh`. That
fixed path is where the plugin looks for it, so the plugin does not need to know
where this repository lives. Because it is a symlink, edits to the script in the
repo take effect immediately.

| Hook event | State written |
|---|---|
| `SessionStart` | `idle` |
| `UserPromptSubmit` | `busy` |
| `PreToolUse` | `busy` |
| `PermissionRequest` | `needs_input` |
| `Notification` | `needs_input` |
| `Stop` | `done` |
| `SessionEnd` | `idle`, and deletes the file on logout/exit |

`hooks/mx_claude_state.py` never exits non-zero and swallows every exception. A
hook that fails loudly would break the session it is reporting on.

Restart any running Claude Code session after installing &mdash; hooks are read at
startup.

### Which terminal am I in?

The state file records the host app so it can be focused later. Detection reads
`TERM_PROGRAM`, falling back to `__CFBundleIdentifier`.

The order matters: `__CFBundleIdentifier` is inherited from whatever launched the
process tree, so starting Claude from a desktop app leaves it pointing at that
app rather than the visible terminal. `TERM_PROGRAM` therefore wins, except for
VS Code forks &mdash; Cursor, Windsurf and Zed all report `TERM_PROGRAM=vscode`, so
there the bundle id is what tells them apart.

To add a terminal, extend `TERMINALS` (or `BUNDLE_OVERRIDES`) in
`hooks/mx_claude_state.py`.

## Focus script

```bash
./scripts/focus-claude.sh          # activate the recorded app
./scripts/focus-claude.sh --print  # show what it would do
```

Exit codes: `0` focused, `1` nothing recorded, `2` recorded but activation failed.

A failure at `2` usually means the app is not installed, or macOS has not been
granted Automation permission &mdash; System Settings &rarr; Privacy & Security &rarr;
Automation. The script prints whatever AppleScript actually said rather than
guessing.

macOS only at present. The structure is there for a Windows branch; nobody has
written it.

## Using it without the plugin

The hooks are useful on their own. Anything that can read a JSON file can react
to Claude's state &mdash; a menu-bar app, a `tmux` status line, a shell prompt:

```bash
jq -r .state ~/.claude/mx-console/state.json
```

And `focus-claude.sh` can be bound to a key in Logi Options+ as a Smart Action,
or to a system-wide shortcut, without any of the rest of this.

## Plugin

See [`plugin/README.md`](plugin/README.md). Short version: it is written against
the [Logi Actions SDK](https://logitech.github.io/actions-sdk-docs/), it polls the
state file, and it has never been compiled &mdash; treat it as a starting point.
