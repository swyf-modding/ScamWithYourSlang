#!/usr/bin/env python3
"""Check a language pack for the mistakes that only show up in game.

Run it on every pack before release. With --game it also checks the pack against the game's own
English strings, which is how a pack finds out that an update moved the ground under it.

    python tools/check-pack.py packs/nl
    python tools/check-pack.py packs/* --game "C:/.../Scam With Your Friends"

Exits non-zero if anything is wrong, so it works as a CI gate.
"""
import argparse
import json
import os
import re
import sys

SYMBOL_KEYS = ["@", ".", "-", "_", "+", "/"]
QUESTION_KEYS = ["mothers_maiden_name", "first_pet_name", "favorite_color"]
COLOR_KEYS = ["RED", "BLUE", "GREEN", "PURPLE", "YELLOW", "ORANGE", "PINK", "BLACK",
              "WHITE", "RAINBOW", "TEAL", "GOLD", "SILVER", "BROWN", "INDIGO"]
FONTS = ["eastAsian", "korean", "indic", "extendedScript"]
TABLES = ["menu.json", "commands.json", "controls.json"]

# {0}, {name} and {0:N0} all reach string.Format. A translation that loses one of these, or
# invents one, throws at the moment the game tries to show it.
PLACEHOLDER = re.compile(r"\{[^{}]*\}")

# The game's own rich text. Dropping a closing tag bleeds the style across the rest of the screen.
RICH_TAG = re.compile(r"</?[a-zA-Z][^<>]*>")


class Report:
    def __init__(self):
        self.errors = []
        self.warnings = []

    def error(self, where, message):
        self.errors.append("%s: %s" % (where, message))

    def warn(self, where, message):
        self.warnings.append("%s: %s" % (where, message))


def read_json(path, report, where):
    try:
        with open(path, encoding="utf-8-sig") as handle:
            return json.load(handle)
    except FileNotFoundError:
        return None
    except json.JSONDecodeError as exc:
        report.error(where, "not valid JSON - %s" % exc)
        return None


def check_manifest(pack_dir, report):
    name = os.path.basename(pack_dir.rstrip("/\\"))
    manifest = read_json(os.path.join(pack_dir, "pack.json"), report, "%s/pack.json" % name)
    if manifest is None:
        report.error(name, "no pack.json")
        return None

    where = "%s/pack.json" % name
    code = manifest.get("code", "")
    if not re.fullmatch(r"[a-z]{2}", code or ""):
        report.error(where, "code must be exactly two lowercase letters, not %r" % code)
    if not (manifest.get("nativeName") or "").strip():
        report.error(where, "nativeName is required - the language's name written in itself")
    if not (manifest.get("englishName") or "").strip():
        report.error(where, "englishName is required")

    font = manifest.get("font")
    if font and font not in FONTS:
        report.error(where, "font must be one of %s" % ", ".join(FONTS))

    speech = manifest.get("speech") or {}
    folder = speech.get("pocketFolder", "") or ""
    if folder and not re.fullmatch(r"[a-z]+", folder):
        report.error(where, "speech.pocketFolder must be lowercase letters only")
    if speech.get("hasPocket") and not folder:
        report.error(where, "speech.hasPocket is set but no pocketFolder is named")

    check_sentinel(manifest.get("sentinel"), where, report)
    return manifest


def check_sentinel(sentinel, where, report):
    if sentinel is None:
        report.warn(where, "no sentinel section, so the caller spells out email addresses in English")
        return

    for key, count in (("digits", 10), ("letters", 26)):
        values = sentinel.get(key)
        if not isinstance(values, list):
            report.error(where, "sentinel.%s must be a list of %d entries" % (key, count))
        elif len(values) != count:
            report.error(where, "sentinel.%s has %d entries, the game requires %d" % (key, len(values), count))
        else:
            check_entries(values, "sentinel.%s" % key, where, report)

    for key, expected in (("symbols", SYMBOL_KEYS), ("questions", QUESTION_KEYS), ("colors", COLOR_KEYS)):
        values = sentinel.get(key)
        if not isinstance(values, dict):
            report.error(where, "sentinel.%s must be an object keyed by %s" % (key, ", ".join(expected)))
            continue
        missing = [k for k in expected if k not in values]
        if missing:
            report.error(where, "sentinel.%s is missing %s" % (key, ", ".join(missing)))
        extra = [k for k in values if k not in expected]
        if extra:
            report.warn(where, "sentinel.%s has entries the game ignores: %s" % (key, ", ".join(extra)))
        check_entries([values[k] for k in expected if k in values], "sentinel.%s" % key, where, report)


def check_entries(values, label, where, report):
    for value in values:
        if not isinstance(value, str) or not value.strip():
            report.error(where, "%s has an empty entry - the caller would say nothing there" % label)
            return
        if "|" in value:
            report.error(where, "%s contains a '|', which the game reads as a separator" % label)
            return


def check_table(pack_dir, file_name, report, english=None):
    name = os.path.basename(pack_dir.rstrip("/\\"))
    where = "%s/%s" % (name, file_name)
    table = read_json(os.path.join(pack_dir, file_name), report, where)
    if table is None:
        return 0, 0

    translated = 0
    for source, value in table.items():
        if not isinstance(value, str):
            report.error(where, "%r does not map to text" % source[:50])
            continue
        if not value:
            continue
        translated += 1

        want = sorted(PLACEHOLDER.findall(source))
        got = sorted(PLACEHOLDER.findall(value))
        if want != got:
            # Not a warning: string.Format throws on a placeholder with no argument, and it
            # throws while drawing the screen that needs it.
            report.error(where, "%r uses %s but the English uses %s" % (source[:40], got or "none", want or "none"))

        if sorted(RICH_TAG.findall(source)) != sorted(RICH_TAG.findall(value)):
            report.warn(where, "%r has different markup from the English" % source[:40])

    if english is not None:
        unknown = [s for s, v in table.items() if v and s not in english]
        if unknown:
            report.warn(where, "%d translated strings are no longer in the game (run make-template.py)" % len(unknown))

    return translated, len(table)


def check_pack(pack_dir, english_tables, report):
    name = os.path.basename(pack_dir.rstrip("/\\"))
    manifest = check_manifest(pack_dir, report)
    if manifest is None:
        return

    translated = total = 0
    for file_name in TABLES:
        english = english_tables.get(file_name) if english_tables else None
        done, count = check_table(pack_dir, file_name, report, english)
        translated += done
        total += count

    if total == 0:
        print("%-10s %s - no strings yet, adds the language only" % (name, manifest.get("englishName", "?")))
    else:
        print("%-10s %s - %d of %d strings (%d%%)" % (
            name, manifest.get("englishName", "?"), translated, total, round(100 * translated / total)))


def read_english(game_dir):
    # Repeated from make-template.py rather than shared: a module named with a hyphen cannot be
    # imported, and renaming it would break the command people already have in their history.
    try:
        import UnityPy
    except ImportError:
        sys.exit("UnityPy is not installed, so --game cannot be used. Run: pip install UnityPy")

    bundle = os.path.join(game_dir, "Scam With Your Friends_Data", "StreamingAssets", "aa",
                          "StandaloneWindows64", "localization-string-tables-english(en)_assets_all.bundle")
    if not os.path.exists(bundle):
        sys.exit("No English string tables at %s" % bundle)

    names = {"Menu_en": "menu.json", "Commands_en": "commands.json", "Controls_en": "controls.json"}
    tables = {}
    for obj in UnityPy.load(bundle).objects:
        if obj.type.name != "MonoBehaviour":
            continue
        try:
            tree = obj.read_typetree()
        except Exception:
            continue
        if tree.get("m_Name") in names and tree.get("m_TableData"):
            tables[names[tree["m_Name"]]] = {
                e["m_Localized"] for e in tree["m_TableData"] if e.get("m_Localized", "").strip()
            }
    return tables


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("packs", nargs="+", help="pack folders to check")
    parser.add_argument("--game", help="also check against the installed game's English strings")
    args = parser.parse_args()

    english = read_english(args.game) if args.game else None
    report = Report()

    for pack_dir in args.packs:
        if not os.path.isdir(pack_dir):
            report.error(pack_dir, "not a folder")
            continue
        check_pack(pack_dir, english, report)

    for warning in report.warnings:
        print("warning: %s" % warning)
    for error in report.errors:
        print("ERROR:   %s" % error)

    if report.errors:
        print("\n%d problem(s) that would break the pack in game." % len(report.errors))
        return 1
    print("\nOK%s" % (" (%d warnings)" % len(report.warnings) if report.warnings else ""))
    return 0


if __name__ == "__main__":
    sys.exit(main())
