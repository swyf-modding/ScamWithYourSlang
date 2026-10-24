# ScamWithYourSlang (SWYF-Translate)

Adds languages to *Scam With Your Friends* from folders of JSON.

The game ships 22 languages. Players keep asking for ones it does not have — Dutch, Thai,
Vietnamese — and each request has so far been answered, when it has been answered at all, by a
separate mod that rewrites the game's own files and re-applies itself after every Steam update.

This does the same job as a BepInEx plugin. Nothing is written into the game folder, so there is
nothing for an update to overwrite, and adding a language means writing a folder of text rather
than writing a mod.

```
BepInEx\config\SWYFTranslate\packs\nl\
  pack.json  menu.json  commands.json  controls.json
```

**[docs/authoring.md](docs/authoring.md) is the guide for translators.** The rest of this file is
for whoever maintains the mod.

---

## Installing

1. Put `ScamWYF.Translate.dll` in `BepInEx\plugins`.
2. Put a pack folder in `BepInEx\config\SWYFTranslate\packs\`.
3. Start the game. The language appears in Settings, in the same dropdown as the built-in ones.

With no packs installed the mod does nothing except offer the **F8** export, which writes a blank
pack with every string in it. That is how a pack starts.

## Building

No .NET SDK needed; the game's own assemblies are the reference set.

```powershell
.\build.ps1 -GameDir "C:\Program Files (x86)\Steam\steamapps\common\Scam With Your Friends"
```

It compiles with `-nostdlib` against the game's `mscorlib`, so the compiler sees exactly the API
surface Unity shipped. Anything managed stripping removed fails to compile here rather than at
runtime in someone else's game. (`SortedDictionary` is gone, for instance.)

The game's DLLs are not in this repo and must not be. CI cannot build the plugin for that reason;
releases are built by hand, which is what every other mod in the org does.

## Tools

| | |
|---|---|
| `tools/make-template.py` | Writes or refreshes a pack's string files from the installed game. Keeps existing translations. |
| `tools/check-pack.py` | Checks a pack for mistakes that otherwise only appear in game. Exits non-zero, so it works as a CI gate. |

Both need `pip install UnityPy`. Neither needs the game running.

## How it works

The game turned out to be unusually friendly to this.

**One list drives everything.** `CallLanguage.Options` feeds the language dropdown, the interface
language, the speech recogniser and the choice of text-to-speech engine — and it is built from a
JSON file in StreamingAssets rather than compiled in. A Harmony postfix on the method that builds
it is enough to add a language everywhere at once.

**Translations need no patch on any text path.** The game memoises every string lookup in three
static dictionaries keyed by the English text, and checks them before consulting its own string
tables:

```csharp
private static string GetCachedLocalizedText(string tableName, string key, Dictionary<string, string> cache)
{
    if (string.IsNullOrEmpty(key)) return "";
    if (cache.TryGetValue(key, out var value)) return value;   // ← a pack's entries land here
    ...
}
```

Pre-filling those dictionaries is a complete substitution that costs nothing per call. They are
emptied on every language change, so the one hook needed is a postfix on the clear.

**Three more things each need a line.** A new language needs a Unity `Locale` registered, or the
game's own lookup falls through to English before the caches are consulted. It needs a
pronunciation table in `SentinelSpeechLanguage`, which throws rather than guessing — and throws
mid-call. And it can borrow one of the four fonts the game bundles, because
`GetUiFontForCode` returns null for a language it does not know, which renders Greek, Cyrillic or
Devanagari as empty boxes.

That is the whole mod: `src/GameBridge.cs` is 300 lines and is the only file that knows what the
game looks like inside.

## Load order, which is the fragile part

The catalogue is a static field with an initialiser, and the game resets other statics on the
same class from a `[RuntimeInitializeOnLoadMethod]`. That touches the class and builds the
catalogue before any BepInEx plugin exists, so the Harmony postfix usually arrives too late and
the list is replaced by reflection instead. Both paths are kept and the result is checked, so a
runtime that refused the write would say so rather than report success and do nothing.

The same early-reset behaviour means there is one case worth knowing about: if anything read
`LanguageManager.SelectedCode` before this plugin loaded, a saved preference naming a pack
language would be normalised to `auto` and the saved value overwritten, losing the choice. Plugin
`Awake` runs before the first scene's own scripts, so this should not happen — but if players
report their language resetting once after installing a pack, this is where to look.

## What it does not do yet

- **Hard-coded strings.** A few hundred labels live in the game's code rather than its string
  tables. Reaching them means hooking text at display time, across UI Toolkit, TextMeshPro and
  legacy uGUI — a different mechanism, and the next piece of work.
- **Custom fonts.** A pack can borrow one of the game's four bundled fonts. It cannot load its
  own, so a script none of them covers (Thai, for one) has nowhere to go.
- **Right-to-left layout.** `rtl` is recorded in the manifest and otherwise unused. Arabic ships
  with the game and is reportedly already wrong, so this is worth doing properly rather than
  quickly.
- **Voices.** See the table at the end of [docs/authoring.md](docs/authoring.md). A new language
  gets the MOSS fallback, and no pack can change that.

## Packs here

| | | |
|---|---|---|
| `packs/nl` | Dutch | 134 of 3,399 strings. A seed, not a translation — it needs a native speaker. |

Requested and not yet started: Thai, Vietnamese.
