# The `.lp5` profile format

Reverse-engineered by unpacking the profiles Logi Options+ exports. There is no
published schema, so everything here was derived from real files &mdash; the ones in
`Console/` and `Dialpad/` that came with the upstream repository, plus the ones
this toolkit generates.

Where something is inferred rather than observed, it says so.

## Archive layout

A `.lp5` is a ZIP archive (deflate). Members:

| Path | Required | Contents |
|---|---|---|
| `ProfileInfo.json` | yes | The profile itself. Everything below hangs off this. |
| `ApplicationInfo.json` | yes | Which application the profile binds to. |
| `metadata/LoupedeckPackage.yaml` | yes | `type`, `name`, `displayName`, `version`. |
| `metadata/ProfilePreview.json` | yes | Per-key preview images for the Options+ gallery. |
| `metadata/AdvancedInfo.json` | yes | `{"additionalPluginNames": []}` |
| `ApplicationIcon.png` | no | Profile icon. Several shipped profiles omit it. |
| `ActionIcons/<actionId>.ict` | no | Key face template. Missing means a default face. |

## Serialisation

`ProfileInfo.json` is Newtonsoft.Json with type-name handling: nearly every
object carries a `$type` naming an assembly-qualified .NET type, e.g.

```json
"$type": "Loupedeck.Service.ApplicationProfile, LoupedeckService"
```

These strings must be exact. `lp5kit.py` keeps them all in one block of
constants near the top of the file.

## Devices

| `deviceType` | Device | Press controls | Rotate controls |
|---|---|---|---|
| `Loupedeck70` | MX Creative **Keypad** | 9 (`controlId` 0&ndash;8, a 3&times;3 grid, top-left first) | 0 |
| `Loupedeck71` | MX Creative **Dialpad** | 4 (`controlId` 0&ndash;3) | 2 (`controlId` 0&ndash;1) |

## Layout tree

```
ProfileInfo.json
└── layout
    └── layoutModes[]
        ├── modeName            "System" for the built-in System plugin
        ├── homeWorkspaceName   must equal one of workspaces[].name
        └── workspaces[]
            ├── pressPages[].controls[]   { controlId, pressAction, rotateAction: null }
            └── rotatePages[].controls[]  { controlId, pressAction: null, rotateAction }
```

A control's `pressAction` / `rotateAction` is either `null` (unassigned) or an
**action id**.

## Action ids

```
$@Generic___@ProfileAction___<32 hex>      one keystroke        -> profileActions[]
$@Generic___@Macro___<32 hex>              a sequence           -> macroCommands[]
$@Generic___@MacroAdjustment___<32 hex>    a dial               -> macroAdjustments[]
$@Generic___@TypeText___<literal text>     inline, no definition needed
$@Generic___@ChangeTouchPage___<mode>|<guid>|<guid>   built in
$DefaultMac___Finder, $AppleMusic___…      supplied by a plugin
```

Only the first three need a matching entry elsewhere in the file. The rest are
resolved by Options+ or by an installed plugin, which is why `verify.py` only
demands definitions for those three prefixes.

`@TypeText` is the useful one: it needs no definition at all, so a macro's
`actions` array can mix step ids with inline text actions.

### `profileActions[]` &mdash; a single keystroke

```json
{
  "$type": "Loupedeck.Service.ApplicationProfileCommand, LoupedeckService",
  "isCommand": true,
  "name": "$@Generic___@ProfileAction___<32 hex>",
  "templateActionName": "$@Generic___@KeyboardKey",
  "actionParameters": {
    "$type": "Loupedeck.ActionEditorActionParameters, PluginApi",
    "parameters": {
      "$type": "Loupedeck.StringDictionaryNoCase, PluginApi",
      "keyboardKey": "<encoded, see below>"
    },
    "count": 1
  },
  "displayName": "Deny",
  "superGroupName": "@macro",
  "isProfileAction": true
}
```

### `macroCommands[]` &mdash; a sequence

`actionEditorCommands[]` defines the keystroke steps, each with a random
nanoid-style `name`. `actions[]` then lists those names in order, and may
contain inline `$@Generic___@TypeText___…` entries:

```json
"actionEditorCommands": [
  { "$type": "Loupedeck.Service.MacroActionEditorCommand, LoupedeckService",
    "name": "ZuNvGmKpuoGUJwlQJjO2W",
    "templateName": "$@Generic___@KeyboardKey",
    "actionParameters": {
      "$type": "System.Collections.Generic.Dictionary`2[[System.String, …]],…",
      "keyboardKey": "Return___2057___Return___mac-36#¤%&+?0#¤%&+?\r#¤%&+?…"
    } }
],
"actions": ["$@Generic___@TypeText___/compact", "ZuNvGmKpuoGUJwlQJjO2W"]
```

Note the `$type` on a macro step's `actionParameters` is a plain
`Dictionary<string,string>`, **not** the `StringDictionaryNoCase` used by
`profileActions`. They are not interchangeable.

### `macroAdjustments[]` &mdash; a dial

Same shape, but the ordered lists are split by direction:

| Field | Fires on |
|---|---|
| `actionsLeft` | anticlockwise |
| `actionsRight` | clockwise |
| `actionsBefore` | press. `[""]` when nothing is bound. |
| `actionsReset` | reset; `[]` in every profile examined |

## Keystroke encoding

One keystroke is a single string of four `___`-separated sections, the last of
which has four more fields separated by the literal `#¤%&+?`:

```
ControlOrCommand+KeyN___2057___Cmd+N___mac-45#¤%&+?1048584#¤%&+?n#¤%&+?com.apple.keylayout.British
└──── logical ────┘   └LCID┘  └label┘  └keycode┘└─ modifier mask ─┘└char┘└─── input source ───┘
```

**logical** &mdash; modifiers then key name. Modifier order is fixed:
`ControlOrCommand`, `Control`, `AltOrOption`, `Shift`. Note it is
`AltOrOption`, not `Alt`.

**LCID** &mdash; keyboard locale, not a magic constant. `2057` is English (UK),
`1033` English (US).

**label** &mdash; what Options+ displays. It uses a *different* modifier order:
`Cmd`, `Opt`, `Ctrl`, `Shift`.

**keycode** &mdash; the macOS virtual keycode (Carbon `kVK_*`). **The whole
`mac-…` tail is optional**: profiles authored on Windows stop after the label,
leaving `Escape___1033___Escape___`.

**modifier mask** &mdash; CGEvent flags OR'd with the left-hand device bits:

| Modifier | Mask | = |
|---|---|---|
| Cmd | `0x100000 \| 0x8` | 1048584 |
| Ctrl | `0x040000 \| 0x1` | 262145 |
| Opt | `0x080000 \| 0x20` | 524320 |
| Shift | `0x020000 \| 0x2` | 131074 |

Some keys carry extra bits with no modifier held at all:

| Key class | Extra bits | Observed |
|---|---|---|
| Arrow keys | `0xA00000` (function + numpad) | bare `ArrowDown` &rarr; `10485760` |
| `Delete`, `F1`&ndash;`F12` | `0x800000` (function) | bare `Delete` &rarr; `8388608` |

**char** &mdash; the character the key produces. With Ctrl held, profiles store the
*control* character instead: `Cmd+Ctrl+H` has char `\b` (0x08), not `h`. Special
keys use macOS private-use codepoints:

| Key | Codepoint |
|---|---|
| `ArrowUp` / `Down` / `Left` / `Right` | `U+F700` / `U+F701` / `U+F702` / `U+F703` |
| `F1`&ndash;`F12` | `U+F704` upwards |
| `Delete` | `U+F728` |
| `Escape` | `U+001B` |
| `Return` | `U+000D` |
| `Tab` | `U+0009` |

### Key names

Confirmed present across the shipped profiles:

- `KeyA`&ndash;`KeyZ`, `Key0`&ndash;`Key9` (digits really are `Key1`, not `Digit1` or `D1`)
- `Return`, `Tab`, `Space`, `Escape`, `Delete`, `Backspace`
- `ArrowUp` / `ArrowDown` / `ArrowLeft` / `ArrowRight`
- `F1`&ndash;`F12`
- `Minus`, `Equals`, `Period`, `Comma`
- `Oem2` `/` (44), `Oem4` `[` (33), `Oem5` `` ` `` (50), `Oem6` `]` (30), `Oem7` `\` (42)
- `None` as the base of a modifier-only binding, e.g. `ControlOrCommand+None`

The `Oem*` numbering does **not** match WPF's `Key` enum &mdash; `Oem5` is backtick
here, and the codes above were read off real files rather than assumed.

## Icon templates (`.ict`)

JSON, named after the action id. `backgroundColor` is a uint32 ARGB
(`4278190080` = `0xFF000000`). `items[]` holds image and text items in draw
order:

```json
{ "backgroundColor": 4278190080,
  "items": [
    { "$type": "Loupedeck.Service.ActionIconImageItem, LoupedeckShared",
      "image": "<base64 PNG or SVG>", "imageFileName": "Isotoma.png",
      "imageColor": 4294967295, "imageRotation": "None", "isVisible": true,
      "itemType": "Image",
      "area": { "x": 8, "y": 0, "width": 84, "height": 84, "isFullScreen": false } },
    { "$type": "Loupedeck.Service.ActionIconTextItem, LoupedeckShared",
      "text": "Isotoma", "textColor": 4294967295, "fontSize": 5,
      "fontName": "Brown Logitech Pan Light", "isVisible": true,
      "itemType": "Text",
      "area": { "x": 0, "y": 80, "width": 100, "height": 16, "isFullScreen": false } }
  ] }
```

`area` is in a 100&times;100 coordinate space, independent of the key's real
pixel size.

## What is still unverified

- **Whether Options+ imports a from-scratch profile.** Every structural
  invariant matches and `verify.py` passes on generated and original files
  alike, but no import onto real hardware has been confirmed. The `$type`
  annotations imply strict .NET deserialisation that could reject something
  subtle.
- **`packageName` / `packageVersion`.** Generated fresh each build. Whether
  Options+ attaches meaning to them is unknown.
- **Multiple pages per workspace** (`@ChangeTouchPage`) and `folderPages`.
  Present in the upstream Arc and Safari profiles; not generated here.
- **`profileSettings`, `actionImages90/60`, `wheelImages`, `actionColors`.**
  Always `null` or an empty typed dictionary in everything examined.

## Checking your own assumptions

The fastest way to settle a question is to let Options+ answer it: bind the key
by hand, export the profile, and read what it wrote.

```bash
unzip -o "Exported Profile_keypad.lp5" -d /tmp/probe
python3 -c "
import json; d=json.load(open('/tmp/probe/ProfileInfo.json'))
for a in d['profileActions']:
    print(a['displayName'], '->', a['actionParameters']['parameters']['keyboardKey'])"
```

That is how every table above was produced.
