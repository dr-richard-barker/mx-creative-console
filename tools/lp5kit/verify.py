#!/usr/bin/env python3
"""Structural self-check for generated ``.lp5`` files.

Compares a built profile against the invariants observed in the profiles that
Logi Options+ itself exported, so schema drift is caught before an import
fails silently.

    python3 tools/lp5kit/verify.py "Console/Claude/Claude Code Profile_keypad.lp5"
    python3 tools/lp5kit/verify.py --all
"""

from __future__ import annotations

import glob
import json
import os
import sys
import zipfile

REQUIRED_MEMBERS = {
    "ProfileInfo.json",
    "ApplicationInfo.json",
    "metadata/LoupedeckPackage.yaml",
    "metadata/ProfilePreview.json",
    "metadata/AdvancedInfo.json",
}

# Action ids the profile itself must define. Anything else - @ChangeTouchPage,
# @TypeText, #DynamicFolder, $DefaultMac___*, $AppleMusic___* and friends - is
# supplied by Options+ or by an installed plugin, so its absence is expected.
USER_DEFINED_PREFIXES = (
    "$@Generic___@ProfileAction___",
    "$@Generic___@Macro___",
    "$@Generic___@MacroAdjustment___",
)


def _is_user_defined(action: str) -> bool:
    return action.startswith(USER_DEFINED_PREFIXES)

EXPECTED_CONTROLS = {"Loupedeck70": (9, 0), "Loupedeck71": (4, 2)}

KK_SEP = "#¤%&+?"


def check(path: str) -> list[str]:
    errs: list[str] = []
    warns: list[str] = []

    def err(msg: str) -> None:
        errs.append(msg)

    def warn(msg: str) -> None:
        warns.append(msg)

    if not zipfile.is_zipfile(path):
        return [f"not a zip archive: {path}"]

    z = zipfile.ZipFile(path)
    names = set(z.namelist())

    missing = REQUIRED_MEMBERS - names
    for m in sorted(missing):
        err(f"missing archive member: {m}")

    try:
        info = json.loads(z.read("ProfileInfo.json"))
    except Exception as exc:  # noqa: BLE001
        return errs + [f"ProfileInfo.json does not parse: {exc}"]

    device = info.get("deviceType")
    if device not in EXPECTED_CONTROLS:
        err(f"unknown deviceType {device!r}")
        return errs

    # Every $type-annotated object must carry a non-empty assembly-qualified name.
    def walk_types(node, path_="$"):
        if isinstance(node, dict):
            if "$type" in node and not str(node["$type"]).strip():
                err(f"empty $type at {path_}")
            for k, v in node.items():
                walk_types(v, f"{path_}.{k}")
        elif isinstance(node, list):
            for i, v in enumerate(node):
                walk_types(v, f"{path_}[{i}]")

    walk_types(info)

    # Collect every action the layout references, and every action defined.
    defined = set()
    for a in info.get("profileActions", []):
        defined.add(a["name"])
    for m in info.get("macroCommands", []):
        defined.add(f"$@Generic___@Macro___{m['name']}")
    for a in info.get("macroAdjustments", []):
        defined.add(f"$@Generic___@MacroAdjustment___{a['name']}")

    referenced = set()
    n_press = n_rotate = 0
    for mode in info["layout"]["layoutModes"]:
        for ws in mode["workspaces"]:
            for page in ws.get("pressPages") or []:
                n_press = max(n_press, len(page["controls"]))
                for c in page["controls"]:
                    if c.get("pressAction"):
                        referenced.add(c["pressAction"])
            for page in ws.get("rotatePages") or []:
                n_rotate = max(n_rotate, len(page["controls"]))
                for c in page["controls"]:
                    if c.get("rotateAction"):
                        referenced.add(c["rotateAction"])

    exp_press, exp_rotate = EXPECTED_CONTROLS[device]
    if n_press != exp_press:
        err(f"{device} should have {exp_press} press controls, found {n_press}")
    if n_rotate != exp_rotate:
        err(f"{device} should have {exp_rotate} rotate controls, found {n_rotate}")

    for ref in sorted(referenced - defined):
        if _is_user_defined(ref):
            err(f"control references undefined action: {ref}")

    # homeWorkspaceName must point at a real workspace.
    for mode in info["layout"]["layoutModes"]:
        ws_names = {w["name"] for w in mode["workspaces"]}
        if mode.get("homeWorkspaceName") not in ws_names:
            err(f"homeWorkspaceName {mode.get('homeWorkspaceName')!r} "
                "is not one of the workspaces")

    # Macro step ids referenced in `actions` must be defined, or be inline
    # TypeText actions.
    for m in info.get("macroCommands", []) + info.get("macroAdjustments", []):
        step_ids = {s["name"] for s in m.get("actionEditorCommands", [])}
        slots = (["actions"] if "actions" in m
                 else ["actionsBefore", "actionsLeft", "actionsRight", "actionsReset"])
        for slot in slots:
            for a in m.get(slot) or []:
                if not a or a.startswith("$@Generic___@"):
                    continue
                if a.startswith("$"):
                    continue
                if a not in step_ids:
                    err(f"macro {m['displayName']!r} {slot} references "
                        f"unknown step {a!r}")

    # Every encoded keystroke must have the 4-field tail and 4 x '___' sections.
    raw = z.read("ProfileInfo.json").decode("utf-8")
    for m in json.loads(raw).get("profileActions", []):
        kk = m.get("actionParameters", {}).get("parameters", {}).get("keyboardKey")
        if kk:
            _check_kk(kk, err)
    for m in info.get("macroCommands", []) + info.get("macroAdjustments", []):
        for s in m.get("actionEditorCommands", []):
            kk = s.get("actionParameters", {}).get("keyboardKey")
            if kk:
                _check_kk(kk, err)

    # Icons should exist for each referenced action.
    for ref in sorted(referenced):
        if _is_user_defined(ref) and f"ActionIcons/{ref}.ict" not in names:
            # Options+ falls back to a default face, so this is cosmetic only.
            warn(f"no icon template for {ref} (key will use a default face)")

    # Preview entries must match real controls.
    try:
        prev = json.loads(z.read("metadata/ProfilePreview.json"))
        for p in prev.get("buttonPages") or []:
            if not p or not p.get("actionName"):
                continue
            if _is_user_defined(p["actionName"]) and p["actionName"] not in defined:
                err(f"preview references undefined action {p['actionName']}")
    except KeyError:
        pass

    return errs + [f"(warning) {w}" for w in warns]


def _check_kk(kk: str, err) -> None:
    if not kk:
        return                      # an unassigned step; Options+ writes these
    head = kk.split("___")
    if len(head) != 4:
        err(f"keyboardKey should have 4 '___' sections, has {len(head)}: {kk[:60]}")
        return
    if not head[1].isdigit():
        err(f"keyboardKey LCID field should be numeric: {head[1]!r}")
    if not head[3]:
        return       # Windows-authored profile: the mac-specific tail is absent
    tail = head[3].split(KK_SEP)
    if len(tail) != 4:
        err(f"keyboardKey tail should have 4 fields, has {len(tail)}: {head[3][:60]}")
        return
    if not tail[0].startswith("mac-"):
        err(f"keyboardKey keycode should start with 'mac-': {tail[0]!r}")
    if not tail[1].isdigit():
        err(f"keyboardKey modifier mask should be an integer: {tail[1]!r}")


def main() -> int:
    args = sys.argv[1:]
    if not args or args == ["--all"]:
        repo = os.path.dirname(os.path.dirname(os.path.dirname(
            os.path.abspath(__file__))))
        paths = sorted(glob.glob(os.path.join(repo, "Console/**/*.lp5"), recursive=True)
                       + glob.glob(os.path.join(repo, "Dialpad/**/*.lp5"), recursive=True))
    else:
        paths = args

    total = 0
    for p in paths:
        errs = check(p)
        total += len(errs)
        hard = [e for e in errs if not e.startswith("(warning)")]
        total -= len(errs) - len(hard)
        status = "OK  " if not hard else "FAIL"
        print(f"{status} {os.path.basename(p)}")
        for e in errs:
            print(f"       - {e}")
    print(f"\n{len(paths)} file(s), {total} problem(s).")
    return 1 if total else 0


if __name__ == "__main__":
    raise SystemExit(main())
