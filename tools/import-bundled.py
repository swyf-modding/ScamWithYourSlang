#!/usr/bin/env python3
"""Convert the languages the game ships into packs, so a shipped language can be corrected
without retranslating it.

The game keeps one Unity Localization table per language in an Addressables bundle, keyed by an
numeric entry id rather than by text. A pack is keyed by the English text, because that is what
the game passes to its own lookup. Pairing the two is the whole job of this script: the English
bundle supplies id -> English, the language's bundle supplies id -> translation, and the English
side names each entry.

    pip install UnityPy
    python tools/import-bundled.py --game "C:/.../Scam With Your Friends"

It writes a folder per language under --out (default: packs/). An existing folder is left alone
unless --force is given, because a pack in this repository is a translation somebody has edited
and a re-extract would throw that work away.

What it cannot supply, and why it is not supplied:

  sentinel        The game already has a pronunciation table for every language it ships, so a
                  pack for one of those languages never needs its own. The mod skips the section
                  when the game's table exists, and only invented languages need one written.
  font, culture   The game picks the font and the culture for codes it knows, which is all of
                  these. Both are only needed by added languages.
  rtl             Recorded from the script the language is written in, and unused by the mod.

Strings the game left untranslated are written as absent rather than empty, so the game falls
through to its own text for them. That is the same state a half-finished pack is in, and it is
reported per language.
"""
import argparse
import json
import os
import re
import sys

BUNDLE_DIR = os.path.join("Scam With Your Friends_Data", "StreamingAssets", "aa", "StandaloneWindows64")
CATALOGUE = os.path.join("Scam With Your Friends_Data", "StreamingAssets", "kolkata-speech-languages.json")

BUNDLE_PREFIX = "localization-string-tables-"
BUNDLE_SUFFIX = "_assets_all.bundle"

# The game's three string tables, and the file each one is written to. DemoStringTable_xx is
# excluded deliberately: it is a sample table the game never shows.
TABLES = {"Menu": "menu.json", "Commands": "commands.json", "Controls": "controls.json"}

# The two languages in the game's own list that are written right to left.
RTL = {"ar", "fa"}

# The game's source language. A pack for it would be a pack that translates English to English.
SOURCE = "en"

# What string.Format is handed. A translation that gains or loses one of these throws while the
# screen that needs it is being drawn, so such an entry is left to the game rather than ported.
PLACEHOLDER = re.compile(r"\{[^{}]*\}")


def placeholders(text):
    return sorted(PLACEHOLDER.findall(text))


def bundle_locales(game_dir):
    """Returns {bundle locale, bundle path} for every language the game ships string tables for."""
    directory = os.path.join(game_dir, BUNDLE_DIR)
    if not os.path.isdir(directory):
        sys.exit("No string table bundles at %s\nPass --game <the game folder>." % directory)

    found = {}
    for name in os.listdir(directory):
        if not name.startswith(BUNDLE_PREFIX) or not name.endswith(BUNDLE_SUFFIX):
            continue
        # The locale is the last parenthesised part: "chinese(simplified)(zh-hans)" carries a
        # human name before it, and taking the first group would file Chinese under "simplified".
        groups = re.findall(r"\(([^)]+)\)", name[len(BUNDLE_PREFIX):-len(BUNDLE_SUFFIX)])
        if groups:
            found[groups[-1]] = os.path.join(directory, name)
    return found


def read_catalogue(game_dir):
    """The game's own language list: code, names and the voice data a pack manifest wants."""
    path = os.path.join(game_dir, CATALOGUE)
    if not os.path.exists(path):
        sys.exit("No language catalogue at %s" % path)
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)["languages"]


def read_bundle(path):
    """Returns {table name: {entry id: text}} for one language's bundle."""
    try:
        import UnityPy
    except ImportError:
        sys.exit("UnityPy is not installed. Run: pip install UnityPy")

    tables = {}
    for obj in UnityPy.load(path).objects:
        if obj.type.name != "MonoBehaviour":
            continue
        try:
            tree = obj.read_typetree()
        except Exception:
            continue
        name = (tree.get("m_Name") or "")
        table = name.rsplit("_", 1)[0]
        if table not in TABLES or not tree.get("m_TableData"):
            continue
        entries = {}
        for entry in tree["m_TableData"]:
            text = entry.get("m_Localized") or ""
            if not text.strip():
                # Untranslated. Absent from the pack means "show the game's text", which is what
                # an empty value would also mean - but absent keeps the file smaller and honest.
                continue
            entries[entry["m_Id"]] = text
        tables[table] = entries
    return tables


def match(code, catalogue_codes):
    """Finds the game's language code for a bundle's locale id, or None."""
    if code in catalogue_codes:
        return code
    # Unity splits a locale into language and script - "zh-hans" for a Chinese that the game's
    # own list calls "zh". The primary subtag is what the game's language is named.
    primary = code.split("-", 1)[0]
    return primary if primary in catalogue_codes else None


def locale_of(bundle_path):
    """The locale id a bundle file name carries, for messages and for the manifest note."""
    groups = re.findall(r"\(([^)]+)\)", os.path.basename(bundle_path))
    return groups[-1] if groups else "?"


def bundles_by_code(catalogue_codes, bundles):
    """Returns {the game's language code: bundle path}, joining the two namings the game uses."""
    joined = {}
    for locale, path in bundles.items():
        code = match(locale, catalogue_codes)
        if code is not None:
            joined[code] = path
    return joined


def build_pack(english, translated):
    """Turns one language's tables into a pack: {file name: {English text: translation}}."""
    files = {}
    stats = {"translated": 0, "untranslatable": 0, "expression": 0, "missing": 0}

    for table, file_name in TABLES.items():
        source = english.get(table, {})
        target = translated.get(table, {})

        pairs = {}
        for entry_id, text in target.items():
            key = source.get(entry_id)
            if key is None:
                # The entry exists in this language and not in English, so there is no English
                # text to key it by and the game has no key it could look it up with either.
                stats["untranslatable"] += 1
                continue
            if placeholders(key) != placeholders(text):
                # The game's own translations use Unity smart strings where the English uses a
                # plain argument: "participant{(count == 1 ? "" : "s")}". That only works on the
                # game's table path, which evaluates it. A pack is handed to string.Format as
                # text, so porting those verbatim would throw or print the expression. The
                # entry is left out and the game's own translation shows instead.
                stats["expression"] += 1
                continue
            pairs[key] = text

        stats["translated"] += len(pairs)
        stats["missing"] += len([k for k in source.values() if k not in pairs])
        files[file_name] = dict(sorted(pairs.items(), key=lambda item: item[0]))
    return files, stats


def write_json(path, value):
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(value, handle, ensure_ascii=False, indent=2)
        handle.write("\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--game", required=True, help="the game folder")
    parser.add_argument("--out", default="packs", help="where the pack folders are written (default: packs)")
    parser.add_argument("--only", nargs="+", metavar="CODE", help="convert just these languages")
    parser.add_argument("--force", action="store_true", help="overwrite a pack that already exists")
    args = parser.parse_args()

    catalogue = read_catalogue(args.game)
    by_code = {language["code"]: language for language in catalogue}
    bundles = bundle_locales(args.game)

    english_bundle = bundles.get(SOURCE)
    if not english_bundle:
        sys.exit("No English string tables in %s. The game's layout may have changed." % args.game)
    english = read_bundle(english_bundle)
    paths = bundles_by_code(set(by_code), bundles)

    wanted = args.only or sorted(by_code)
    unknown = [code for code in wanted if code not in by_code]
    if unknown:
        sys.exit("Not a language the game ships: %s" % ", ".join(unknown))

    converted = 0
    for code in wanted:
        if code == SOURCE:
            continue

        if code not in paths:
            print("%-4s skipped: the game has no string tables for it" % code)
            continue

        pack_dir = os.path.join(args.out, code)
        if os.path.exists(pack_dir) and not args.force:
            print("%-4s skipped: %s already exists (--force replaces it)" % (code, pack_dir))
            continue

        language = by_code[code]
        locale = locale_of(paths[code])
        translated = read_bundle(paths[code])
        files, stats = build_pack(english, translated)

        manifest = {
            "code": code,
            "nativeName": language["nativeName"],
            "englishName": language["englishName"],
            "author": "",
            "version": "0.1.0",
            "rtl": code in RTL,

            "_source": "Extracted from the game's own " + locale + " string tables by "
                       "tools/import-bundled.py. Edit this pack to correct the shipped language, or "
                       "copy it to a new code to start a new one.",
            "_sentinel": "The game already has a pronunciation table for " + code + ", so this pack "
                         "needs none. Only a language the game does not ship needs a sentinel section.",
            "speech": {
                "hasPocket": bool(language.get("hasPocket")),
                "pocketFolder": language.get("pocketFolder") or "",
            },
        }

        os.makedirs(pack_dir, exist_ok=True)
        write_json(os.path.join(pack_dir, "pack.json"), manifest)
        for file_name, pairs in files.items():
            write_json(os.path.join(pack_dir, file_name), pairs)

        print("%-4s %-18s %4d strings  %3d missing  %d expression  %d unkeyed  (%s)" % (
            code, language["englishName"], stats["translated"], stats["missing"],
            stats["expression"], stats["untranslatable"], locale))
        converted += 1

    print("\n%d pack(s) written to %s" % (converted, args.out))
    if converted:
        print("Check them with: python tools/check-pack.py %s/* --game \"%s\"" % (args.out, args.game))


if __name__ == "__main__":
    main()
