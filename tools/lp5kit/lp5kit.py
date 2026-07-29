"""
lp5kit - generate Logi Options+ ``.lp5`` profiles for the MX Creative Console.

A ``.lp5`` file is a ZIP archive containing Newtonsoft.Json-serialised .NET
objects. Everything in here was derived by unpacking the real profiles that
ship in this repository (``Console/*/*.lp5`` and ``Dialpad/*/*.lp5``) and
mirroring their structure exactly.

Device types
------------
``Loupedeck70``  MX Creative Keypad  - 9 press controls, ids 0..8 (3x3 grid)
``Loupedeck71``  MX Creative Dialpad - 4 press controls (0..3) + 2 dials (0..1)

See ``FORMAT.md`` in this directory for the full reverse-engineered schema.
"""

from __future__ import annotations

import io
import json
import os
import uuid
import zipfile
from dataclasses import dataclass, field
from typing import Iterable

# --------------------------------------------------------------------------
# Constants lifted verbatim from real profiles
# --------------------------------------------------------------------------

# Field separator inside an encoded keyboardKey string. Literally "#¤%&+?".
KK_SEP = "#¤%&+?"

# The field between the logical combo and the display label is the keyboard
# LCID, not a magic number: 2057 = English (UK), 1033 = English (US).
# Profiles authored on Windows stop after the label and omit the "mac-..."
# tail entirely, which is why that tail is optional on read.
LCID_EN_GB = 2057
LCID_EN_US = 1033

DEFAULT_LCID = LCID_EN_GB
DEFAULT_KEY_LAYOUT = "com.apple.keylayout.British"

# Convenience pairings so callers do not have to remember which LCID goes with
# which macOS input source.
LOCALES = {
    "en-GB": (LCID_EN_GB, "com.apple.keylayout.British"),
    "en-US": (LCID_EN_US, "com.apple.keylayout.US"),
}

DEVICE_KEYPAD = "Loupedeck70"
DEVICE_DIALPAD = "Loupedeck71"

# Control counts per device, confirmed against shipped profiles.
DEVICE_LAYOUT = {
    DEVICE_KEYPAD: {"press": 9, "rotate": 0},
    DEVICE_DIALPAD: {"press": 4, "rotate": 2},
}

T_PROFILE = "Loupedeck.Service.ApplicationProfile, LoupedeckService"
T_LAYOUT = "Loupedeck.Service.Devices.Loupedeck7Devices.ProfileLayout7, LoupedeckService"
T_LAYOUT_MODE = "Loupedeck.Service.Devices.Loupedeck7Devices.ProfileLayoutMode7, LoupedeckService"
T_WORKSPACE = "Loupedeck.Service.Devices.Loupedeck7Devices.ProfileLayoutWorkspace7, LoupedeckService"
T_PAGE = "Loupedeck.Service.Devices.Loupedeck7Devices.ProfileLayoutPage7, LoupedeckService"
T_CONTROL = "Loupedeck.Service.Devices.Loupedeck7Devices.ProfileLayoutControl7, LoupedeckService"
T_APPINFO = "Loupedeck.Service.SupportedApplicationInfo, LoupedeckService"
T_APPMODE = "Loupedeck.Service.ApplicationMode, LoupedeckService"
T_CMD = "Loupedeck.Service.ApplicationProfileCommand, LoupedeckService"
T_MACRO = "Loupedeck.Service.ApplicationProfileMacroCommand, LoupedeckService"
T_ADJ = "Loupedeck.Service.ApplicationProfileMacroAdjustment, LoupedeckService"
T_MACRO_STEP = "Loupedeck.Service.MacroActionEditorCommand, LoupedeckService"
T_ACTION_PARAMS = "Loupedeck.ActionEditorActionParameters, PluginApi"
T_STRDICT_NC = "Loupedeck.StringDictionaryNoCase, PluginApi"
T_DICT_NC = "Loupedeck.DictionaryNoCase`1[[System.String, System.Private.CoreLib]], PluginApi"
T_DICT_SS = (
    "System.Collections.Generic.Dictionary`2"
    "[[System.String, System.Private.CoreLib],"
    "[System.String, System.Private.CoreLib]], System.Private.CoreLib"
)
T_ICON_IMAGE = "Loupedeck.Service.ActionIconImageItem, LoupedeckShared"
T_ICON_TEXT = "Loupedeck.Service.ActionIconTextItem, LoupedeckShared"

TEMPLATE_KEYBOARD_KEY = "$@Generic___@KeyboardKey"

# --------------------------------------------------------------------------
# Keyboard encoding
# --------------------------------------------------------------------------

# Extra modifier bits macOS attaches to certain key classes. Confirmed from
# shipped profiles: a bare ArrowDown carries 0xA00000, a bare Delete 0x800000.
FLAG_FUNCTION = 0x800000
FLAG_NUMPAD = 0x200000

# key name -> (macOS virtual keycode, character, always-on mask bits)
#
# Every entry below was cross-checked against encoded strings found in the
# profiles shipped in this repository. See FORMAT.md.
_LETTER_CODES = {
    "A": 0, "S": 1, "D": 2, "F": 3, "H": 4, "G": 5, "Z": 6, "X": 7, "C": 8,
    "V": 9, "B": 11, "Q": 12, "W": 13, "E": 14, "R": 15, "Y": 16, "T": 17,
    "O": 31, "U": 32, "I": 34, "P": 35, "L": 37, "J": 38, "K": 40, "N": 45,
    "M": 46,
}
_DIGIT_CODES = {"0": 29, "1": 18, "2": 19, "3": 20, "4": 21, "5": 23,
                "6": 22, "7": 26, "8": 28, "9": 25}
_FN_CODES = {1: 122, 2: 120, 3: 99, 4: 118, 5: 96, 6: 97, 7: 98, 8: 100,
             9: 101, 10: 109, 11: 103, 12: 111}

KEY_TABLE = {}
for _ch, _code in _LETTER_CODES.items():
    KEY_TABLE["Key" + _ch] = (_code, _ch.lower(), 0)
for _ch, _code in _DIGIT_CODES.items():
    KEY_TABLE["Key" + _ch] = (_code, _ch, 0)
for _n, _code in _FN_CODES.items():
    KEY_TABLE["F%d" % _n] = (_code, chr(0xF704 + _n - 1), FLAG_FUNCTION)
KEY_TABLE.update({
    "Return": (36, "\r", 0),
    "Tab": (48, "\t", 0),
    "Space": (49, " ", 0),
    "Escape": (53, "\u001b", 0),
    "Backspace": (51, "\b", 0),
    "Delete": (117, "\uf728", FLAG_FUNCTION),
    "ArrowUp": (126, "\uf700", FLAG_FUNCTION | FLAG_NUMPAD),
    "ArrowDown": (125, "\uf701", FLAG_FUNCTION | FLAG_NUMPAD),
    "ArrowLeft": (123, "\uf702", FLAG_FUNCTION | FLAG_NUMPAD),
    "ArrowRight": (124, "\uf703", FLAG_FUNCTION | FLAG_NUMPAD),
    "Minus": (27, "-", 0),
    "Equals": (24, "=", 0),
    "Period": (47, ".", 0),
    "Comma": (43, ",", 0),
    "Oem2": (44, "/", 0),
    "Oem4": (33, "[", 0),
    "Oem5": (50, "`", 0),
    "Oem6": (30, "]", 0),
    "Oem7": (42, "\\", 0),
})

# Display labels for keys whose label is not simply the trailing character.
KEY_LABELS = {
    "Return": "Return", "Tab": "Tab", "Space": "Space", "Escape": "Escape",
    "Backspace": "Backspace", "Delete": "Delete",
    "ArrowUp": "ArrowUp", "ArrowDown": "ArrowDown",
    "ArrowLeft": "ArrowLeft", "ArrowRight": "ArrowRight",
}
# Function keys label as F1..F12, not as the private-use character they emit.
KEY_LABELS.update({"F%d" % n: "F%d" % n for n in _FN_CODES})

# NSEvent/CGEvent modifier masks OR'd with the left-hand device-dependent bit,
# exactly as Options+ writes them. Verified against real profiles:
#   Cmd = 1048584   Cmd+Control = 1310729   Cmd+Shift = 1179658
MOD_MASKS = {
    "cmd": 0x100000 | 0x000008,    # 1048584
    "ctrl": 0x040000 | 0x000001,   # 262145
    "alt": 0x080000 | 0x000020,    # 524320
    "shift": 0x020000 | 0x000002,  # 131074
}

# The logical combo and the display label use DIFFERENT modifier orders. Both
# were read straight off real profiles:
#   logical: ControlOrCommand+Control+AltOrOption+Shift+ArrowUp
#   label:   Cmd+Opt+Ctrl+Shift+ArrowUp
MOD_ORDER_LOGICAL = ["cmd", "ctrl", "alt", "shift"]
MOD_ORDER_LABEL = ["cmd", "alt", "ctrl", "shift"]
MOD_LOGICAL = {"cmd": "ControlOrCommand", "ctrl": "Control",
               "alt": "AltOrOption", "shift": "Shift"}
MOD_LABEL = {"cmd": "Cmd", "ctrl": "Ctrl", "alt": "Opt", "shift": "Shift"}

MOD_ALIASES = {
    "command": "cmd", "meta": "cmd", "super": "cmd",
    "control": "ctrl",
    "option": "alt", "opt": "alt",
}


def _ctrl_char(key):
    """Control character generated when Ctrl is held (Ctrl+H -> 0x08).

    Real profiles store this rather than the plain character.
    """
    if len(key) == 4 and key.startswith("Key") and key[3].isalpha():
        return chr(ord(key[3].upper()) - 64)
    return None


def encode_key(key, mods=(), label=None, layout=DEFAULT_KEY_LAYOUT,
               lcid=DEFAULT_LCID):
    """Encode one keystroke into the ``keyboardKey`` wire format.

    >>> encode_key("KeyN", ["cmd"]).split("___")[:3]
    ['ControlOrCommand+KeyN', '2057', 'Cmd+N']
    """
    mods = [MOD_ALIASES.get(m.lower(), m.lower()) for m in mods]
    unknown = set(mods) - set(MOD_MASKS)
    if unknown:
        raise ValueError("unknown modifier(s): %s" % sorted(unknown))
    if key not in KEY_TABLE:
        raise ValueError(
            "unknown key %r; add it to KEY_TABLE with its macOS virtual "
            "keycode. Known: %s" % (key, ", ".join(sorted(KEY_TABLE)))
        )

    code, char, extra = KEY_TABLE[key]

    logical = "+".join(
        [MOD_LOGICAL[m] for m in MOD_ORDER_LOGICAL if m in mods] + [key]
    )

    if label is None:
        pretty = KEY_LABELS.get(key)
        if pretty is None:
            pretty = (key[3:].upper()
                      if key.startswith("Key") and len(key) == 4 else char)
        label = "+".join(
            [MOD_LABEL[m] for m in MOD_ORDER_LABEL if m in mods] + [pretty]
        )

    mask = extra
    for m in mods:
        mask |= MOD_MASKS[m]

    if "ctrl" in mods and _ctrl_char(key):
        char = _ctrl_char(key)

    return (
        "%s___%s___%s___mac-%d%s%d%s%s%s%s"
        % (logical, lcid, label, code, KK_SEP, mask, KK_SEP, char,
           KK_SEP, layout)
    )


# --------------------------------------------------------------------------
# Action specifications
# --------------------------------------------------------------------------


@dataclass
class Key:
    """One keystroke step."""
    key: str
    mods: tuple[str, ...] = ()
    label: str | None = None

    def encoded(self, layout: str, lcid: int = DEFAULT_LCID) -> str:
        return encode_key(self.key, self.mods, self.label, layout, lcid)


@dataclass
class Text:
    """Type a literal string. Rendered as the inline ``@TypeText`` action."""
    value: str


@dataclass
class Button:
    """A single control on the device.

    ``steps`` is a sequence of :class:`Key` / :class:`Text`. One lone ``Key``
    becomes a lightweight ``profileActions`` entry; anything else becomes a
    ``macroCommands`` entry. ``None`` leaves the control unassigned.
    """
    title: str
    steps: list | None = None
    icon: str | None = None          # path to a PNG/SVG to embed
    glyph: str | None = None         # large character drawn above the title
    color: int = 0xFF000000          # ARGB background
    text_color: int = 0xFFFFFFFF
    note: str = ""

    @property
    def is_empty(self) -> bool:
        return not self.steps


@dataclass
class Dial:
    """A rotary control: actions for left turn, right turn and press."""
    title: str
    left: list | None = None
    right: list | None = None
    press: list | None = None
    icon: str | None = None
    glyph: str | None = None
    color: int = 0xFF000000
    text_color: int = 0xFFFFFFFF
    note: str = ""


@dataclass
class Profile:
    name: str
    device: str
    buttons: list[Button] = field(default_factory=list)
    dials: list[Dial] = field(default_factory=list)
    description: str = ""
    application: str = "@_defaultmac"
    application_display: str = "System plugin"
    native_plugin: str = "DefaultMac"
    mode: str = "System"
    key_layout: str = DEFAULT_KEY_LAYOUT
    lcid: int = DEFAULT_LCID
    version: str = "1.0.0.0"


# --------------------------------------------------------------------------
# Builder
# --------------------------------------------------------------------------


def _guid() -> str:
    return uuid.uuid4().hex.upper()


def _short_id(n: int = 21) -> str:
    """Nanoid-ish identifier, matching the style Options+ uses for macro steps."""
    import random
    import string
    alphabet = string.ascii_letters + string.digits + "-_"
    return "".join(random.choice(alphabet) for _ in range(n))


class ProfileBuilder:
    def __init__(self, profile: Profile):
        self.p = profile
        if profile.device not in DEVICE_LAYOUT:
            raise ValueError(f"unknown device {profile.device!r}")
        self.profile_actions: list[dict] = []
        self.macro_commands: list[dict] = []
        self.macro_adjustments: list[dict] = []
        self.icons: dict[str, tuple] = {}   # actionName -> (title, icon, color, text_color)
        self.profile_guid = _guid()

    # -- action emitters ---------------------------------------------------

    def _add_single_key(self, step: Key, title: str) -> str:
        action_name = f"$@Generic___@ProfileAction___{_guid()}"
        self.profile_actions.append({
            "$type": T_CMD,
            "isCommand": True,
            "name": action_name,
            "templateActionName": TEMPLATE_KEYBOARD_KEY,
            "actionParameters": {
                "$type": T_ACTION_PARAMS,
                "parameters": {
                    "$type": T_STRDICT_NC,
                    "keyboardKey": step.encoded(self.p.key_layout, self.p.lcid),
                },
                "count": 1,
            },
            "displayName": title,
            "description": (
                "Activate a keyboard shortcut with a single press or hold down "
                "for continuous use like a keyboard key"
            ),
            "groupName": "",
            "superGroupName": "@macro",
            "isProfileAction": True,
            "isMultiState": False,
            "isResetCommand": False,
            "adjustmentName": None,
            "states": None,
        })
        return action_name

    def _compile_steps(self, steps: list) -> tuple[list[dict], list[str]]:
        """Turn a step list into (actionEditorCommands, actions)."""
        editor: list[dict] = []
        actions: list[str] = []
        for step in steps:
            if isinstance(step, Text):
                actions.append(f"$@Generic___@TypeText___{step.value}")
            elif isinstance(step, Key):
                step_id = _short_id()
                editor.append({
                    "$type": T_MACRO_STEP,
                    "name": step_id,
                    "templateName": TEMPLATE_KEYBOARD_KEY,
                    "actionParameters": {
                        "$type": T_DICT_SS,
                        "keyboardKey": step.encoded(self.p.key_layout, self.p.lcid),
                    },
                })
                actions.append(step_id)
            else:
                raise TypeError(f"step must be Key or Text, got {type(step).__name__}")
        return editor, actions

    def _add_macro(self, steps: list, title: str) -> str:
        guid = _guid()
        editor, actions = self._compile_steps(steps)
        self.macro_commands.append({
            "$type": T_MACRO,
            "isCommand": True,
            "name": guid,
            "displayName": title,
            "description": "",
            "groupName": "",
            "superGroupName": "@macro",
            "supportedOs": "All",
            "supportedModes": [self.p.mode],
            "showAsSingleAction": False,
            "actionEditorCommands": editor,
            "isMultiState": False,
            "actions": actions,
        })
        return f"$@Generic___@Macro___{guid}"

    def _add_adjustment(self, dial: Dial) -> str:
        guid = _guid()
        editor: list[dict] = []
        compiled: dict[str, list[str]] = {}
        for slot, steps in (("actionsLeft", dial.left),
                            ("actionsRight", dial.right),
                            ("actionsBefore", dial.press)):
            if steps:
                e, a = self._compile_steps(steps)
                editor.extend(e)
                compiled[slot] = a
            else:
                compiled[slot] = [""] if slot == "actionsBefore" else []
        self.macro_adjustments.append({
            "$type": T_ADJ,
            "isCommand": False,
            "name": guid,
            "displayName": dial.title,
            "description": "",
            "groupName": "",
            "superGroupName": "@macro",
            "supportedOs": "All",
            "supportedModes": [],
            "showAsSingleAction": False,
            "actionEditorCommands": editor,
            "isMultiState": False,
            "actionsBefore": compiled["actionsBefore"],
            "actionsLeft": compiled["actionsLeft"],
            "actionsRight": compiled["actionsRight"],
            "actionsReset": [],
            "clickRateLimit": 1,
        })
        return f"$@Generic___@MacroAdjustment___{guid}"

    def _emit(self, control) -> str | None:
        """Register a Button/Dial's action(s) and return its action name."""
        if isinstance(control, Dial):
            if not (control.left or control.right or control.press):
                return None
            name = self._add_adjustment(control)
        else:
            if control.is_empty:
                return None
            steps = control.steps
            if len(steps) == 1 and isinstance(steps[0], Key):
                name = self._add_single_key(steps[0], control.title)
            else:
                name = self._add_macro(steps, control.title)
        self.icons[name] = (control.title, control.icon, control.color,
                            getattr(control, "text_color", 0xFFFFFFFF))
        return name

    # -- document assembly -------------------------------------------------

    def build_profile_info(self) -> tuple[dict, list[tuple[int, str, str]]]:
        p = self.p
        limits = DEVICE_LAYOUT[p.device]

        press_controls = []
        preview: list[tuple[int, str, str]] = []
        for i in range(limits["press"]):
            btn = p.buttons[i] if i < len(p.buttons) else Button("", None)
            action = self._emit(btn)
            press_controls.append({
                "$type": T_CONTROL,
                "controlId": i,
                "pressAction": action,
                "rotateAction": None,
            })
            if action:
                preview.append((i, action, btn.title))

        rotate_controls = []
        for i in range(limits["rotate"]):
            dial = p.dials[i] if i < len(p.dials) else Dial("", None, None)
            action = self._emit(dial)
            rotate_controls.append({
                "$type": T_CONTROL,
                "controlId": i,
                "pressAction": None,
                "rotateAction": action,
            })

        workspace = {
            "$type": T_WORKSPACE,
            "name": _guid(),
            "displayName": "Workspace 1",
            "description": "",
            "pressPages": [{
                "$type": T_PAGE,
                "name": _guid(),
                "displayName": "Page (1)",
                "description": "",
                "controls": press_controls,
            }],
            "rotatePages": ([{
                "$type": T_PAGE,
                "name": _guid(),
                "displayName": "Dial Page",
                "description": "",
                "controls": rotate_controls,
            }] if rotate_controls else []),
        }

        info = {
            "$type": T_PROFILE,
            "name": self.profile_guid,
            "profileFlags": "None",
            "displayName": p.name,
            "description": p.description,
            "deviceType": p.device,
            "applicationName": p.application,
            "nativePluginName": p.native_plugin,
            "hasNativePlugin": bool(p.native_plugin),
            "additionalNativePluginNames": [],
            "lastModifiedTimeUtc": "2026-01-01T00:00:00.000000Z",
            "profileSettings": {"$type": T_DICT_NC},
            "actionImages90": None,
            "actionImages60": None,
            "wheelImages": None,
            "actionColors": None,
            "layout": {
                "$type": T_LAYOUT,
                "deviceType": p.device,
                "profileFlags": "None",
                "layoutModes": [{
                    "$type": T_LAYOUT_MODE,
                    "deviceType": p.device,
                    "modeName": p.mode,
                    "parentModeName": None,
                    "actions": None,
                    "dynamicButtonPages": None,
                    "dynamicEncoderPages": None,
                    "workspaces": [workspace],
                    "homeWorkspaceName": workspace["name"],
                }],
                "folderPages": [],
            },
            "macroCommands": self.macro_commands,
            "macroAdjustments": self.macro_adjustments,
            "profileCommands": [],
            "profileAdjustments": [],
            "conversionHistory": None,
            "packageName": _guid(),
            "packageVersion": p.version,
            "profileActions": self.profile_actions,
        }
        return info, preview

    def build_application_info(self) -> dict:
        p = self.p
        return {
            "$type": T_APPINFO,
            "name": p.application,
            "displayName": p.application_display,
            "description": None,
            "deviceType": p.device,
            "nativePluginName": p.native_plugin,
            "hasNativePlugin": bool(p.native_plugin),
            "processOrBundleName": None,
            "modes": [{
                "$type": T_APPMODE,
                "name": p.mode,
                "parentModeName": None,
                "displayName": p.mode,
            }],
            "defaultProfileName": self.profile_guid,
            "isEnabled": True,
        }


# --------------------------------------------------------------------------
# Icon rendering
# --------------------------------------------------------------------------


def _render_icon_png(title: str, color: int, text_color: int, size: int = 116,
                     glyph: str | None = None) -> bytes:
    """Render a key face. Falls back to a flat colour swatch without Pillow."""
    a, r, g, b = (color >> 24) & 0xFF, (color >> 16) & 0xFF, (color >> 8) & 0xFF, color & 0xFF
    ta, tr, tg, tb = ((text_color >> 24) & 0xFF, (text_color >> 16) & 0xFF,
                      (text_color >> 8) & 0xFF, text_color & 0xFF)
    try:
        from PIL import Image, ImageDraw, ImageFont
    except ImportError:
        return _flat_png(size, (r, g, b, a))

    img = Image.new("RGBA", (size, size), (r, g, b, a))
    draw = ImageDraw.Draw(img)

    def _font(px: int):
        for path in ("/System/Library/Fonts/Supplemental/Arial Bold.ttf",
                     "/System/Library/Fonts/Helvetica.ttc",
                     "/System/Library/Fonts/SFNS.ttf",
                     "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"):
            if os.path.exists(path):
                try:
                    return ImageFont.truetype(path, px)
                except OSError:
                    continue
        return ImageFont.load_default()

    y = 14
    if glyph:
        gf = _font(46)
        bbox = draw.textbbox((0, 0), glyph, font=gf)
        draw.text(((size - (bbox[2] - bbox[0])) / 2 - bbox[0], y), glyph,
                  font=gf, fill=(tr, tg, tb, ta))
        y += 54
    else:
        y = 30

    # Word-wrap the title into at most three lines.
    words, lines, cur = title.split(), [], ""
    tf = _font(17)
    for w in words:
        trial = f"{cur} {w}".strip()
        if draw.textlength(trial, font=tf) <= size - 12 or not cur:
            cur = trial
        else:
            lines.append(cur)
            cur = w
    if cur:
        lines.append(cur)
    lines = lines[:3]

    for line in lines:
        wpx = draw.textlength(line, font=tf)
        draw.text(((size - wpx) / 2, y), line, font=tf, fill=(tr, tg, tb, ta))
        y += 20

    buf = io.BytesIO()
    img.save(buf, format="PNG")
    return buf.getvalue()


def _flat_png(size: int, rgba: tuple[int, int, int, int]) -> bytes:
    """Minimal PNG encoder so icon output degrades gracefully without Pillow."""
    import struct
    import zlib
    r, g, b, a = rgba
    row = bytes([r, g, b, a]) * size
    raw = b"".join(b"\x00" + row for _ in range(size))

    def chunk(tag: bytes, data: bytes) -> bytes:
        return (struct.pack(">I", len(data)) + tag + data
                + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))

    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9))
            + chunk(b"IEND", b""))


def _build_ict(title: str, icon_path: str | None, color: int, text_color: int,
               glyph: str | None = None) -> dict:
    import base64
    items = []
    if icon_path and os.path.exists(icon_path):
        with open(icon_path, "rb") as fh:
            items.append({
                "$type": T_ICON_IMAGE,
                "image": base64.b64encode(fh.read()).decode("ascii"),
                "imageFileName": os.path.basename(icon_path),
                "imageColor": 0xFFFFFFFF,
                "imageRotation": "None",
                "isVisible": True,
                "itemType": "Image",
                "area": {"x": 18, "y": 4, "width": 64, "height": 64,
                         "isFullScreen": False},
            })
        text_area = {"x": 0, "y": 72, "width": 100, "height": 24, "isFullScreen": False}
    else:
        # Text sits below the glyph when there is one, so the two do not overlap
        # in the 100x100 icon coordinate space.
        text_area = {"x": 0, "y": 58 if glyph else 34, "width": 100,
                     "height": 30 if glyph else 32, "isFullScreen": False}
        if glyph:
            items.append({
                "$type": T_ICON_TEXT,
                "text": glyph,
                "textColor": text_color,
                "fontSize": 14,
                "fontName": "Brown Logitech Pan Light",
                "isVisible": True,
                "itemType": "Text",
                "area": {"x": 0, "y": 8, "width": 100, "height": 30,
                         "isFullScreen": False},
            })

    items.append({
        "$type": T_ICON_TEXT,
        "text": title,
        "textColor": text_color,
        "fontSize": 6,
        "fontName": "Brown Logitech Pan Light",
        "isVisible": True,
        "itemType": "Text",
        "area": text_area,
    })
    return {"backgroundColor": color, "items": items}


# --------------------------------------------------------------------------
# Packaging
# --------------------------------------------------------------------------


def write_lp5(profile: Profile, out_path: str, app_icon: str | None = None) -> str:
    """Build ``profile`` and write it to ``out_path``. Returns the path."""
    import base64

    builder = ProfileBuilder(profile)
    info, preview = builder.build_profile_info()
    app_info = builder.build_application_info()

    glyphs = {}
    for ctrl in list(profile.buttons) + list(profile.dials):
        glyphs[ctrl.title] = getattr(ctrl, "glyph", None)

    os.makedirs(os.path.dirname(os.path.abspath(out_path)), exist_ok=True)

    with zipfile.ZipFile(out_path, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("ProfileInfo.json", json.dumps(info, indent=4))
        z.writestr("ApplicationInfo.json", json.dumps(app_info, indent=4))
        z.writestr("metadata/AdvancedInfo.json",
                   json.dumps({"additionalPluginNames": []}, indent=4))
        z.writestr(
            "metadata/LoupedeckPackage.yaml",
            f"type: Profile5\n"
            f"name: {builder.profile_guid}\n"
            f"displayName: {profile.name}\n"
            f"version: {profile.version}\n",
        )

        if app_icon and os.path.exists(app_icon):
            with open(app_icon, "rb") as fh:
                z.writestr("ApplicationIcon.png", fh.read())
        else:
            z.writestr("ApplicationIcon.png",
                       _render_icon_png(profile.name, 0xFF1A1A1A, 0xFFFFFFFF, 250))

        for action, (title, icon, color, text_color) in builder.icons.items():
            z.writestr(f"ActionIcons/{action}.ict",
                       json.dumps(_build_ict(title, icon, color, text_color,
                                             glyphs.get(title)), indent=4))

        buttons_by_title = {b.title: b for b in profile.buttons if b.title}
        pages = []
        for control_id, action, title in preview:
            btn = buttons_by_title.get(title)
            png = _render_icon_png(
                title,
                btn.color if btn else 0xFF000000,
                btn.text_color if btn else 0xFFFFFFFF,
                116,
                glyphs.get(title),
            )
            pages.append({
                "controlId": control_id,
                "actionName": action,
                "displayName": title,
                "description": "",
                "image": base64.b64encode(png).decode("ascii"),
            })
        z.writestr("metadata/ProfilePreview.json",
                   json.dumps({"buttonPages": pages}, indent=4))

    return out_path
