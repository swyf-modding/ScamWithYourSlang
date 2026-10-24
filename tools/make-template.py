#!/usr/bin/env python3
"""Write (or refresh) a language pack's string files from the game's own English tables.

Reads the English strings straight out of the installed game, so a template always matches the
version being translated rather than whatever the last person extracted. Run it again after a
game update and it keeps every translation already written, adds whatever is new and tells you
what the game dropped.

    pip install UnityPy
    python tools/make-template.py --game "C:/.../Scam With Your Friends" --pack packs/nl

The in-game exporter (F8 by default) does the same job without Python, and is the better route
for anyone who is translating rather than maintaining this repo. This exists so the repo can
generate and check packs in CI, where there is no running game.
"""
import argparse
import json
import os
import sys

BUNDLE = "localization-string-tables-english(en)_assets_all.bundle"
BUNDLE_DIR = os.path.join("Scam With Your Friends_Data", "StreamingAssets", "aa", "StandaloneWindows64")

# The game's three tables, and the file each one is written to.
TABLES = {"Menu_en": "menu.json", "Commands_en": "commands.json", "Controls_en": "controls.json"}


def read_english(game_dir):
    """Returns {file name: [English string, ...]} from the game's bundle."""
    try:
        import UnityPy
    except ImportError:
        sys.exit("UnityPy is not installed. Run: pip install UnityPy")

    bundle = os.path.join(game_dir, BUNDLE_DIR, BUNDLE)
    if not os.path.exists(bundle):
        sys.exit("No English string tables at %s\nPass --game <the game folder>." % bundle)

    tables = {}
    for obj in UnityPy.load(bundle).objects:
        if obj.type.name != "MonoBehaviour":
            continue
        try:
            tree = obj.read_typetree()
        except Exception:
            continue
        name = tree.get("m_Name") or ""
        if name not in TABLES or tree.get("m_TableData") is None:
            continue
        # Ordinal order, so the file is stable between runs and a game update shows up as a
        # clean diff rather than a reshuffle.
        values = sorted(
            {e["m_Localized"] for e in tree["m_TableData"] if e.get("m_Localized", "").strip()}
        )
        tables[TABLES[name]] = values
    return tables


def load_existing(path):
    if not os.path.exists(path):
        return {}
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)


def write(path, english, existing):
    """Writes the template, carrying over anything already translated."""
    merged = {}
    for source in english:
        merged[source] = existing.get(source, "")

    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(merged, handle, ensure_ascii=False, indent=2)
        handle.write("\n")

    kept = sum(1 for value in merged.values() if value)
    added = sum(1 for source in english if source not in existing)
    dropped = [source for source in existing if source not in merged and existing[source]]
    return kept, added, dropped


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--game", required=True, help="the game folder")
    parser.add_argument("--pack", required=True, help="the pack folder to write into")
    args = parser.parse_args()

    tables = read_english(args.game)
    if not tables:
        sys.exit("The bundle was read but held no string tables. The game's layout may have changed.")

    os.makedirs(args.pack, exist_ok=True)
    total_dropped = []

    for file_name, english in sorted(tables.items()):
        path = os.path.join(args.pack, file_name)
        kept, added, dropped = write(path, english, load_existing(path))
        print("%-14s %4d strings  %4d translated  %4d new" % (file_name, len(english), kept, added))
        total_dropped += [(file_name, source) for source in dropped]

    if total_dropped:
        # Not removed automatically: a string the game stopped using may come back, and a
        # translation is worth more than a tidy file.
        print("\nThe game no longer uses these, and their translations are now unused:")
        for file_name, source in total_dropped:
            print("  %s: %s" % (file_name, source[:70]))


if __name__ == "__main__":
    main()
