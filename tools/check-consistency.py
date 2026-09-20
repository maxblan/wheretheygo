#!/usr/bin/env python3
"""Cross-file checks that no compiler and no test can make.

Three couplings in this mod are held together by matching STRINGS across files
in different languages, so every one of them fails silently: a typo shows up as
a blank row or a dead control in the running game, never as a build error.

  1. Six locale files must carry the same key set. A key missing from one
     language is a raw key on that player's screen.
  2. Every t("Key", …) in the UI module must have WhereTheyGo.Panel[Key] in
     LocaleEN. The module passes English inline as a fallback, so a missing key
     is invisible until someone plays in another language.
  3. Every binding and trigger name registered in PanelUISystem must be the one
     the UI module asks for, in both directions. A mismatch is a control that
     does nothing.

The purity rule needs no check here: tests/WhereTheyGo.Tests LINKS every
Planning/ file, so a Unity or ECS type in one breaks that project's build.

    python3 tools/check-consistency.py

Exit code is the number of problems found, like the test harness.
"""

import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
DOTNET = ROOT / "dotnet"
MJS = DOTNET / "Presentation" / "UI" / "WhereTheyGo.mjs"
PANEL = DOTNET / "Presentation" / "PanelUISystem.cs"
EN = DOTNET / "Setting.cs"
TRANSLATIONS = ["LocaleDE", "LocaleFR", "LocalePTBR", "LocaleRU", "LocaleZHHANS"]

KEY = re.compile(r'"((?:WhereTheyGo|Infoviews)\.[^"]*)"')


def read(path):
    return path.read_text(encoding="utf-8")


def keys_of(path):
    return set(KEY.findall(read(path)))


def check_locales(problems):
    english = keys_of(EN)
    if not english:
        problems.append(f"{EN.name}: no locale keys found at all. Has the format changed?")
        return
    for name in TRANSLATIONS:
        path = DOTNET / f"{name}.cs"
        theirs = keys_of(path)
        for key in sorted(english - theirs):
            problems.append(f"{name}: missing key {key}")
        for key in sorted(theirs - english):
            problems.append(f"{name}: key {key} is not in LocaleEN")
    print(f"  locales: {len(english)} keys x {len(TRANSLATIONS) + 1} files")


def check_panel_keys(problems):
    mjs = read(MJS)
    english = keys_of(EN)

    # t("Key", "English fallback"): the module's own translate helper.
    used = set(re.findall(r'\bt\(\s*"([A-Za-z0-9_]+)"', mjs))

    # The purposes are the one table that feeds t() a key instead of writing it
    # out, so its entries are read from the table itself. Scoped to that const on
    # purpose: a bare `key:` pattern also matches React's list keys, which are not
    # locale keys and are not supposed to resolve to anything.
    table = re.search(r'const PURPOSES\s*=\s*\[(.*?)\];', mjs, re.S)
    if not table:
        problems.append("WhereTheyGo.mjs: the PURPOSES table has moved, so this check no longer covers it")
    else:
        used |= set(re.findall(r'key:\s*"([A-Za-z0-9_]+)"', table.group(1)))

    for key in sorted(used):
        if f"WhereTheyGo.Panel[{key}]" not in english:
            problems.append(f"WhereTheyGo.mjs: t(\"{key}\") has no WhereTheyGo.Panel[{key}] in LocaleEN")
    print(f"  panel keys: {len(used)} asked for by the UI module")


def check_bindings(problems):
    panel, mjs = read(PANEL), read(MJS)

    cs_values = set(re.findall(r'(?:GetterValueBinding<[^>]+>|RawValueBinding)\(\s*Group\s*,\s*"([A-Za-z0-9_]+)"', panel))
    cs_triggers = set(re.findall(r'TriggerBinding(?:<[^>]*>)?\(\s*Group\s*,\s*"([A-Za-z0-9_]+)"', panel))

    js_values = set(re.findall(r'\b(?:useBound|binding)\(\s*"([A-Za-z0-9_]+)"', mjs))
    js_triggers = set(re.findall(r'\btrigger\(\s*"([A-Za-z0-9_]+)"', mjs))

    for name in sorted(cs_values - js_values):
        problems.append(f"binding \"{name}\" is registered in PanelUISystem but nothing reads it")
    for name in sorted(js_values - cs_values):
        problems.append(f"binding \"{name}\" is read by the UI module but never registered")
    for name in sorted(cs_triggers - js_triggers):
        problems.append(f"trigger \"{name}\" is registered in PanelUISystem but nothing fires it")
    for name in sorted(js_triggers - cs_triggers):
        problems.append(f"trigger \"{name}\" is fired by the UI module but never registered")

    if not cs_values or not cs_triggers:
        problems.append("PanelUISystem: no bindings matched. Has the registration shape changed?")
    print(f"  bindings: {len(cs_values)} values, {len(cs_triggers)} triggers")


def main():
    for path in (MJS, PANEL, EN):
        if not path.exists():
            print(f"FAIL  missing {path.relative_to(ROOT)}")
            return 1

    problems = []
    check_locales(problems)
    check_panel_keys(problems)
    check_bindings(problems)

    if problems:
        print()
        for problem in problems:
            print(f"FAIL  {problem}")
        print(f"\n{len(problems)} problem(s).")
        return len(problems)

    print("\nAll consistency checks passed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
