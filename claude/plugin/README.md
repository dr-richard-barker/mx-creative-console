# Claude Console — MX Creative Console plugin

A Logi Actions SDK (Loupedeck) plugin that makes a keypad key **flash amber when Claude Code is
waiting on you**, and gives you one-press keys to jump back to the session and answer it.

---

## ⚠️ UNCOMPILED / UNTESTED — expect to fix build errors on the first `dotnet build`

This plugin was written **without a .NET toolchain on the authoring machine**. It has never been
compiled, never been loaded by the Logi Plugin Service, and never been run against real hardware.

What *was* verified:

- Every SDK type, method, overload and parameter name used here was read out of the **actual
  shipped `PluginApi.dll`** (`/Applications/Utilities/LogiPluginService.app/Contents/MonoBundle/PluginApi.dll`)
  by parsing its ECMA-335 metadata tables directly. The rendering code deliberately passes *every*
  optional argument explicitly so it does not depend on SDK default values.
- The project layout, `.csproj` shape, `LoupedeckPackage.yaml` field set and the `.link` sideload
  mechanism were copied from working plugins (see [References](#references)).

What is **not** verified — treat these as the risk list:

| Risk | Detail |
| --- | --- |
| Nothing compiles yet | No `dotnet` was available. Expect typos, missing usings, accessibility slips. |
| `base()` / `base(displayName, description, groupName)` on `PluginDynamicCommand` | The real ctors are `(DeviceType)` and `(String, String, String, DeviceType)`. Both call sites rely on `DeviceType` having a **default value**. Both forms are used by shipping plugins, so this is very likely fine — but it is an assumption, not something readable from metadata. |
| `BitmapBuilder.DrawText(..., String fontName)` with `fontName: null` | The 10-argument overload exists and its signature is exact. Passing `null` for the font name to mean "SDK default face" is an assumption. If text vanishes, drop to the 5-argument overload `DrawText(text, color, fontSize, lineHeight, spaceHeight)`. |
| Text fitting | The SDK exposes no text-measuring API, so `KeyFaceRenderer.Truncate` estimates a 0.55 em average advance. Long project names may still clip. Tune the constant. |
| Glyph coverage | The Continue key draws `⏎` and truncation uses `…`. If the device font renders these as boxes, replace them with `RET` and `...` in `ClaudeContinueCommand.cs` / `KeyFaceRenderer.Truncate`. |
| Permission-prompt keystrokes | `1` / `2` / Escape are Claude Code's current permission-dialog answers. They are isolated in a marked constant block — see [Approval keys](#approval-keys). |
| macOS Accessibility permission | `osascript … keystroke` silently does nothing until the Logi Plugin Service is granted Accessibility rights. See [Verify](#verify). |

---

## What it does

Four actions, all in the **Claude Code** group in Logi Options+:

| Action | Behaviour | Press |
| --- | --- | --- |
| **Claude Attention** | Flashes between bright amber and dimmed amber twice a second while Claude is waiting. Muted blue "working" face while busy, green "done" face when the turn ends, dark neutral when idle. Shows the project name. | Brings the Claude Code window to the front. |
| **Claude Continue** | A `Continue ⏎` face, subtly highlighted amber while Claude is waiting. | Focuses Claude, waits 250 ms, then presses Return. |
| **Claude Status** | Widget tile: `WAITING` / `WORKING` / `DONE` / `IDLE` plus time elapsed in that state, plus the project name. | Opens the session's working directory. |
| **Claude Approval** | One action with three selectable variants: **Allow once**, **Always allow**, **Deny**. | Focuses Claude, then sends the matching permission-prompt key. |

Put **Claude Attention** and **Claude Continue** on adjacent keys — that is the pair the design is
built around.

### How the flashing works

A single shared `ClaudeStateService` singleton polls the state file on a `PeriodicTimer` every
**250 ms**. On each tick it:

1. Re-reads and re-parses the file **only if** its last-write time or length changed.
2. Flips a blink phase every **500 ms**, but **only while the state is `needs_input`**.
3. Calls `ActionImageChanged(null)` **only when something actually changed** — a new state, a blink
   flip, or (while a session is live) a 1 Hz heartbeat so the Status tile's elapsed timer stays
   honest. When there is no session at all the plugin generates **zero** USB traffic.

Keys that do not blink (Continue, Approval) skip redraws triggered by blink flips, using a state
revision counter.

---

## State-file contract

The plugin is a **pure consumer**. Claude Code hooks (a separate part of this project) write:

```
~/.claude/mx-console/state.json
```

```json
{
  "state": "needs_input",
  "session_id": "abc",
  "cwd": "/Users/x/proj",
  "title": "my-project",
  "app": "Terminal",
  "bundle_id": "com.apple.Terminal",
  "ts": 1738000000
}
```

| Field | Required | Meaning |
| --- | --- | --- |
| `state` | yes | `needs_input` \| `busy` \| `done` \| `idle`. Anything unrecognised, or an absent file, is treated as idle. |
| `session_id` | no | Opaque session identifier. Not rendered; used only for change detection. |
| `cwd` | no | Project directory. Opened by the Status key; also the fallback source of the on-key project label. |
| `title` | no | Preferred project label shown on the keys, truncated to fit. |
| `app` | no | macOS application display name used by the `osascript` focus fallback. Defaults to `Terminal`. |
| `bundle_id` | no | Preferred focus target (`open -b <bundle_id>`), more reliable than the display name. |
| `ts` | no | Unix seconds (milliseconds are auto-detected). A file older than **2 hours** is treated as idle. |

Robustness guarantees:

- Missing file, missing directory, malformed JSON, empty file, and **torn reads mid-write** are all
  handled. A failed parse is retried up to 3× (20 ms apart) and then the **last good state is kept**
  rather than flickering to idle. Nothing ever throws out of the timer loop.
- The file is opened with `FileShare.ReadWrite | Delete`, so the plugin never blocks the hook writing it.
- Hooks should write atomically (write to a temp file in the same directory, then `mv`) — the retry
  logic exists because they might not.

### Optional focus helper

If `~/.claude/mx-console/focus-claude.sh` exists it is run (via `/bin/sh`) instead of the built-in
focus logic. Use it when your terminal needs more than "activate the app" — e.g. selecting a tmux
window or a specific iTerm2 session. Make it fast; it is given 4 seconds.

Without it, the fallback order is: `open -b <bundle_id>` → `osascript -e 'tell application "<app>" to activate'`.

### Approval keys

`src/Actions/ClaudeApprovalCommand.cs` opens with a clearly-marked constant block:

```csharp
private const String AllowOnceCharacter   = "1";
private const String AlwaysAllowCharacter = "2";
private const Int32  DenyMacKeyCode       = 53;      // kVK_Escape
private const String DenyWindowsSendKeys  = "{ESC}";
```

**If Claude Code changes its permission prompt, change only those four constants.**

AppleScript's `keystroke` cannot express Escape, so Deny is sent as a raw
`tell application "System Events" to key code 53`. The printable answers use
`keystroke "1"` / `keystroke "2"`.

---

## Cross-platform behaviour

| | macOS | Windows | Linux |
| --- | --- | --- | --- |
| Focus | `focus-claude.sh`, else `open -b <bundle>`, else `osascript … activate` | PowerShell `[Microsoft.VisualBasic.Interaction]::AppActivate` | no-op + log line |
| Keystrokes | `osascript … keystroke` / `key code` | PowerShell `[System.Windows.Forms.SendKeys]::SendWait` | no-op + log line |
| Open folder | `open` | `explorer.exe` | no-op + log line |

Every branch is behind a `RuntimeInformation.IsOSPlatform` check. Linux never crashes — it logs and
does nothing. Processes are launched with `ProcessStartInfo.ArgumentList` (a real argv, not a shell
string), so paths and app names with spaces or quotes cannot break the command.

The Windows paths are written from the documented SDK conventions and are **not tested at all**.

---

## Build

### 1. Install the .NET 8 SDK

```bash
brew install --cask dotnet-sdk
# or download from https://dotnet.microsoft.com/download/dotnet/8.0
dotnet --version    # expect 8.x
```

### 2. Install Logi Options+

The build references `PluginApi.dll` straight out of the installed Logi Plugin Service. **There is
no public NuGet package for the Logi Actions SDK** — this is exactly how the official Logitech
`DemoPlugin` and the CodexBarUsage sample do it.

- macOS: `/Applications/Utilities/LogiPluginService.app/Contents/MonoBundle/PluginApi.dll`
- Windows: `C:\Program Files\Logi\LogiPluginService\PluginApi.dll`

The `.csproj` fails with a clear message if that file is missing.

### 3. Build and sideload

```bash
cd /Users/drb_laptop/Documents/mx-creative-console/claude/plugin
./build.sh              # Release, sideload, reload
./build.sh Debug        # Debug build
./build.sh Release nolink   # build only, don't touch the Plugins directory
```

`dotnet build` alone also works — the `.csproj` post-build target does the same sideload.

---

## Sideload

The Logi Plugin Service reads every `*.link` file in its Plugins directory. A `.link` file contains
one line: the absolute path of a directory holding the plugin DLL and its `metadata/` folder.

**macOS (this project's target):**

```
~/Library/Application Support/Logi/LogiPluginService/Plugins/ClaudeConsolePlugin.link
```

containing

```
/Users/drb_laptop/Documents/mx-creative-console/claude/plugin/bin/Release
```

**Windows equivalent:**

```
%LOCALAPPDATA%\Logi\LogiPluginService\Plugins\ClaudeConsolePlugin.link
```

The build then fires `open loupedeck:plugin/ClaudeConsolePlugin/reload`. If that URL does nothing,
**quit Logi Options+ *and* the Logi Plugin Service**, then relaunch Logi Options+.

---

## Verify

1. **Grant Accessibility permission.** System Settings → Privacy & Security → Accessibility → enable
   **Logi Plugin Service** (and Logi Options+). Without this, `keystroke` and `key code` do nothing
   at all, silently. This is the number one reason the Continue/Approval keys appear dead.

2. **Add the keys.** Logi Options+ → your MX Creative Console keypad → Plugins → **Claude Console**.
   Drag *Claude Attention* and *Claude Continue* onto adjacent keys.

3. **Drive the state file by hand:**

   ```bash
   mkdir -p ~/.claude/mx-console

   # should start flashing amber within ~250 ms
   printf '%s' "{\"state\":\"needs_input\",\"session_id\":\"test\",\"cwd\":\"$PWD\",\"title\":\"demo\",\"app\":\"Terminal\",\"bundle_id\":\"com.apple.Terminal\",\"ts\":$(date +%s)}" \
     > ~/.claude/mx-console/state.json

   # muted blue "working" face
   printf '%s' "{\"state\":\"busy\",\"title\":\"demo\",\"ts\":$(date +%s)}" > ~/.claude/mx-console/state.json

   # green "done" face
   printf '%s' "{\"state\":\"done\",\"title\":\"demo\",\"ts\":$(date +%s)}" > ~/.claude/mx-console/state.json

   # dark neutral
   printf '%s' "{\"state\":\"idle\",\"ts\":$(date +%s)}" > ~/.claude/mx-console/state.json

   # stale-file check: a needs_input more than 2 h old must render as idle
   printf '%s' "{\"state\":\"needs_input\",\"ts\":$(( $(date +%s) - 10000 ))}" > ~/.claude/mx-console/state.json

   # torn-write / garbage check: must NOT crash, must keep the last good face
   printf '%s' '{"state":"needs_' > ~/.claude/mx-console/state.json
   ```

4. **Logs:** `~/Library/Application Support/Logi/LogiPluginService/Logs`. The plugin logs its state
   transitions at Verbose and every shell-out failure at Warning.

---

## Layout

```
plugin/
├── build.sh                       # build + sideload + reload
├── ClaudeConsolePlugin.sln
├── README.md
└── src/
    ├── ClaudeConsolePlugin.csproj  # net8.0, direct PluginApi.dll reference
    ├── Directory.Build.props
    ├── ClaudeConsolePlugin.cs             # Plugin subclass; Load/Unload wire the state service
    ├── ClaudeConsolePluginApplication.cs  # required ClientApplication placeholder
    ├── ClaudeStateService.cs              # 250 ms poll, parsing, blink phase, change events
    ├── PlatformShell.cs                   # osascript / PowerShell / open shelling helper
    ├── Helpers/PluginLog.cs
    ├── Rendering/KeyFaceRenderer.cs       # all BitmapBuilder drawing
    ├── Actions/
    │   ├── ClaudeCommandBase.cs           # subscribe / redraw policy / focus helpers
    │   ├── ClaudeAttentionCommand.cs      # the flashing key
    │   ├── ClaudeContinueCommand.cs       # focus + Return
    │   ├── ClaudeStatusCommand.cs         # state + elapsed, opens cwd
    │   └── ClaudeApprovalCommand.cs       # 3 variants via AddParameter
    └── package/
        └── metadata/
            ├── LoupedeckPackage.yaml
            └── Icon256x256.png
```

### Icons

- `src/package/metadata/Icon256x256.png` is a **generated placeholder** (amber rounded square, three
  ink dots). It is a valid 256×256 RGBA PNG, so the package loads — replace it with real artwork
  before sharing. There is Logitech icon-template artwork alongside this repo at
  `mx-creative-console/Icons/` and `Logitech CC Icons v.0.1.sketch`.
- Per-action icons are **optional**. This plugin draws its key faces entirely in code via
  `BitmapBuilder`, so no `actionicons/` or `actionsymbols/` folders are needed. If you want icons in
  the Logi Options+ action *list*, add `src/package/actionsymbols/<ActionName>.svg` — the build's
  `CopyPackage` target already copies everything under `package/` verbatim.

---

## References

Documentation and source relied on while writing this:

- Logi Actions SDK docs — https://logitech.github.io/actions-sdk-docs/
- C# plugin development introduction (.NET 8 requirement) — https://logitech.github.io/actions-sdk-docs/csharp/plugin-development/introduction/
- Plugin structure & `LoupedeckPackage.yaml` — https://logitech.github.io/actions-sdk-docs/csharp/tutorial/plugin-structure/
- Changing a button image (`new BitmapBuilder(imageSize)` … `ToImage()`) — https://logitech.github.io/actions-sdk-docs/csharp/tutorial/change-a-button-image/
- Add a command with a parameter (`AddParameter`) — https://logitech.github.io/actions-sdk-docs/csharp/tutorial/add-a-command-with-a-parameter/
- Testing and debugging — https://logitech.github.io/actions-sdk-docs/csharp/plugin-development/testing-and-debugging-the-plugin/
- Logging — https://logitech.github.io/actions-sdk-docs/csharp/plugin-features/logging/
- Official example plugin — https://github.com/Logitech/actions-sdk (`DemoPlugin/`)
- CodexBarUsage — external-CLI polling on a `PeriodicTimer` + `ActionImageChanged`, macOS, `.link` sideload — https://github.com/sswadkar/CodexBarUsage
- Loupedeck-HomeAssistant — websocket events → `ActionImageChanged`, `AddParameter`, `OnLoad`/`OnUnload` — https://github.com/schmic/Loupedeck-HomeAssistant

Exact API signatures were additionally confirmed by reading the metadata of the installed
`PluginApi.dll` rather than trusting the docs alone.

## Licence

MIT.
