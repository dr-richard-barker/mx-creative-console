#!/usr/bin/env python3
"""Build every ``.lp5`` profile described in ``specs.json``.

    python3 tools/lp5kit/build_profiles.py            # build all
    python3 tools/lp5kit/build_profiles.py claude-code vscode
    python3 tools/lp5kit/build_profiles.py --list
"""

from __future__ import annotations

import argparse
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from lp5kit import (  # noqa: E402
    DEVICE_DIALPAD, DEVICE_KEYPAD, Button, Dial, Key, Profile, Text, write_lp5,
)

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
SPECS = os.path.join(HERE, "specs.json")

DEVICES = {"keypad": DEVICE_KEYPAD, "dialpad": DEVICE_DIALPAD}


def hex_to_argb(value: str) -> int:
    """'#B3261E' -> 0xFFB3261E"""
    value = value.lstrip("#")
    if len(value) == 6:
        value = "FF" + value
    return int(value, 16)


def parse_steps(raw):
    if not raw:
        return None
    steps = []
    for item in raw:
        if "text" in item:
            steps.append(Text(item["text"]))
        elif "key" in item:
            steps.append(Key(item["key"], tuple(item.get("mods", ())),
                             item.get("label")))
        else:
            raise ValueError(f"step needs 'key' or 'text': {item!r}")
    return steps


def build(spec: dict, out_root: str) -> str:
    device = DEVICES[spec["device"]]

    buttons = [
        Button(
            title=b["title"],
            steps=parse_steps(b.get("steps")),
            glyph=b.get("glyph"),
            color=hex_to_argb(b.get("color", "#000000")),
            note=b.get("note", ""),
        )
        for b in spec.get("buttons", [])
    ]
    dials = [
        Dial(
            title=d["title"],
            left=parse_steps(d.get("left")),
            right=parse_steps(d.get("right")),
            press=parse_steps(d.get("press")),
            glyph=d.get("glyph"),
            color=hex_to_argb(d.get("color", "#000000")),
            note=d.get("note", ""),
        )
        for d in spec.get("dials", [])
    ]

    profile = Profile(
        name=spec["name"],
        device=device,
        buttons=buttons,
        dials=dials,
        description=spec.get("description", ""),
    )

    suffix = "dialpad" if spec["device"] == "dialpad" else "keypad"
    filename = f"{spec['name']} Profile_{suffix}.lp5"
    out_path = os.path.join(out_root, spec["folder"], filename)
    return write_lp5(profile, out_path)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("ids", nargs="*", help="profile ids to build (default: all)")
    ap.add_argument("--list", action="store_true", help="list available ids")
    ap.add_argument("--out", default=REPO, help="output root (default: repo root)")
    args = ap.parse_args()

    with open(SPECS, encoding="utf-8") as fh:
        specs = json.load(fh)["profiles"]

    if args.list:
        for s in specs:
            print(f"{s['id']:22s} {s['device']:8s} {s['folder']}")
        return 0

    wanted = set(args.ids)
    if wanted:
        unknown = wanted - {s["id"] for s in specs}
        if unknown:
            print(f"unknown profile id(s): {', '.join(sorted(unknown))}",
                  file=sys.stderr)
            return 1
        specs = [s for s in specs if s["id"] in wanted]

    for spec in specs:
        path = build(spec, args.out)
        size = os.path.getsize(path)
        print(f"  {os.path.relpath(path, args.out):58s} {size / 1024:6.1f} KB")

    sync_web_specs(args.out)
    print(f"\nBuilt {len(specs)} profile(s).")
    return 0


def sync_web_specs(out_root: str) -> None:
    """Keep the copy the web builder fetches identical to the source of truth."""
    import shutil
    dest = os.path.join(out_root, "docs", "assets", "specs.json")
    if os.path.isdir(os.path.dirname(dest)):
        shutil.copyfile(SPECS, dest)
        print(f"  {os.path.relpath(dest, out_root):58s} (web copy)")


if __name__ == "__main__":
    raise SystemExit(main())
