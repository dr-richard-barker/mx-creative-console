# Logitech MX Creative Console Profiles

A fork of [jamesjingyi/mx-creative-console](https://github.com/jamesjingyi/mx-creative-console)
that adds three things:

1. **Profiles for Claude Code, VS Code and Adobe Bridge** &mdash; keypad and dialpad.
2. **`lp5kit`** &mdash; a generator that builds `.lp5` profiles from a JSON spec, with a
   documented reverse-engineering of the format and a structural verifier.
3. **A notification path for Claude Code** &mdash; hooks that publish session state, and a
   Logi Actions SDK plugin that flashes a key when Claude is waiting on you.

There is also a [GitHub Pages site](https://dr-richard-barker.github.io/mx-creative-console/)
with a browser-based profile builder.

The original profiles (General, Sketch, Safari, Slack, Arc, Mail, Notion, Music)
and James's icon Sketch file are untouched in `Console/`, `Dialpad/` and `Icons/`.

---

## The Claude Code layout

The keypad's bottom row is the permission prompt, so answering Claude never means
reaching for the keyboard.

```
┌──────────────┬──────────────┬──────────────┐
│ Focus Claude │   Continue   │   Compact    │   attention and flow
├──────────────┼──────────────┼──────────────┤
│  Plan Mode   │  Transcript  │    Rewind    │   workflow
├──────────────┼──────────────┼──────────────┤
│     Deny     │ Always Allow │  Allow Once  │   permissions
└──────────────┴──────────────┴──────────────┘
      red           amber          green
```

| Key | Sends | Notes |
|---|---|---|
| Focus Claude | `Cmd+Tab` | The plugin upgrades this into a flashing notification light. |
| Continue | `Return` | Accepts the highlighted option, or submits an empty prompt. |
| Compact | types `/compact` + `Return` | |
| Plan Mode | `Shift+Tab` | Cycles default &rarr; accept-edits &rarr; plan. |
| Transcript | `Ctrl+O` | Expands the full tool-call transcript. |
| Rewind | `Escape` `Escape` | Opens the rewind menu on an empty prompt. |
| Deny | `Escape` | |
| Always Allow | `2` | "Yes, and don't ask again". |
| Allow Once | `1` | "Yes". |

The dialpad carries `/clear`, `/cost`, `/diff` and `/context`, plus dials for
scrolling the transcript and switching windows.

> **On the permission keys.** Claude Code's prompt is numbered `1` yes, `2` yes and
> don't ask again, `3` no. Deny is bound to `Escape` rather than `3` because the
> number of options varies between prompt types, while Escape always declines.
> If a future version renumbers them, edit `tools/lp5kit/specs.json` and rebuild.
> Always Allow sits in the middle of the bottom row because that is where it was
> asked for &mdash; it is also the easiest key to hit by accident, hence the amber.

---

## Building profiles

```bash
python3 tools/lp5kit/build_profiles.py          # build everything
python3 tools/lp5kit/build_profiles.py --list   # available ids
python3 tools/lp5kit/verify.py --all            # structural self-check
```

Layouts live in [`tools/lp5kit/specs.json`](tools/lp5kit/specs.json), which is the
single source of truth &mdash; the web builder fetches the same file. Pillow is used for
key icons if present; without it, icons fall back to flat colour swatches.

The format is documented in [`tools/lp5kit/FORMAT.md`](tools/lp5kit/FORMAT.md),
including the keystroke encoding, the modifier masks and the parts that are still
guesswork.

### How much to trust the output

`verify.py` passes on all 21 profiles in the repository &mdash; the six generated ones
*and* the fifteen that Logi Options+ itself exported. The Python and JavaScript
generators produce byte-identical keystroke strings, and those strings match ones
read out of real exports character for character.

And **Logi Options+ imports them.** The Claude Code keypad profile was imported
successfully on real hardware on 29 July 2026 &mdash; generated from scratch, not
round-tripped through Options+'s own exporter. That was the format's one
load-bearing assumption, and it holds.

What that does not yet prove is that every key fires the right keystroke when
pressed; importing shows the file deserialises correctly. Since the encodings are
byte-identical to ones Options+ wrote itself, that is a much weaker assumption.
If a key misbehaves, open an issue saying which one.

---

## Making a key flash when Claude needs you

A `.lp5` is static configuration: it maps a key to a keystroke, and that is all. To
light a key up in response to something happening, a process has to be running that
can push a new image to the device.

```
Claude Code hook  ──writes──▶  ~/.claude/mx-console/state.json
                                        │
                              polled 4x/second
                                        ▼
                         Claude Console plugin (C#)
                                        │
                            ActionImageChanged()
                                        ▼
                              key repaints / flashes
```

### Install the hooks

```bash
./claude/hooks/install.sh
```

Merges into `~/.claude/settings.json` rather than replacing it, keeps a timestamped
backup, and is safe to run twice. Undo with `--uninstall`, preview with `--print`.

It maps Claude Code's hook events onto four states:

| State | Set by | Meaning |
|---|---|---|
| `needs_input` | `Notification`, `PermissionRequest` | Claude is blocked on you. **This is the one that flashes.** |
| `busy` | `UserPromptSubmit`, `PreToolUse` | Working. |
| `done` | `Stop` | Turn finished. |
| `idle` | `SessionStart`, `SessionEnd` | Nothing happening; `SessionEnd` clears the file. |

### Focus the session from anywhere

```bash
./claude/scripts/focus-claude.sh          # bring Claude's terminal forward
./claude/scripts/focus-claude.sh --print  # show what it would focus
```

Works on its own without the plugin &mdash; bind it to a Smart Action or a system
shortcut. It identifies the host terminal from `TERM_PROGRAM`, and recognises
Terminal, iTerm, VS Code, Cursor, Windsurf, Zed, Warp, Ghostty, WezTerm, Hyper,
Alacritty and kitty.

### The plugin

Source is in [`claude/plugin/`](claude/plugin/).

> **Status: written but never compiled.** It needs the .NET 8 SDK, which was not
> available on the machine this was built on, so it has not been built, run, or
> tested against real hardware. Expect to fix build errors on the first
> `dotnet build`. See `claude/plugin/README.md` for what is verified against the
> official SDK docs and what is not.

The hooks and the focus script do not depend on it and are tested.

---

## Repository layout

| Path | |
|---|---|
| `Console/`, `Dialpad/` | Profiles, `.lp5`. Original ones plus Claude, VS Code, Adobe Bridge. |
| `Icons/`, `*.sketch` | James's original icon set and source file. |
| `tools/lp5kit/` | Generator, verifier, format documentation, layout specs. |
| `claude/hooks/` | Claude Code hooks that publish session state. |
| `claude/scripts/` | `focus-claude.sh`. |
| `claude/plugin/` | Logi Actions SDK plugin (uncompiled). |
| `docs/` | GitHub Pages site and the browser-based builder. |

## Credits

Original profiles, icons and Sketch source by
[James Jingyi](https://github.com/jamesjingyi). The MX Creative Console plugin API
is Logitech's [Actions SDK](https://logitech.github.io/actions-sdk-docs/).
