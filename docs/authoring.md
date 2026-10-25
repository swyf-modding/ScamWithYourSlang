# Writing a language pack

A pack is a folder of JSON. You do not need to write code, compile anything, or touch the game's
files. If you can edit a text file and you speak the language, you can make one.

```
packs/nl/
  pack.json        which language this is
  menu.json        3,227 strings: menus, the desktop, every screen
  commands.json    39 strings: what you shout at people
  controls.json    295 strings: key bindings and prompts
```

---

## 1. Get the strings

Start the game with the mod installed and press **F8** at the main menu. It writes a blank pack
to:

```
BepInEx\config\SWYFTranslate\template\
```

That folder has all four files, with every English string the game can show and an empty value
beside each one. The strings come out of the game you are running, so they always match your
version.

Copy it to `BepInEx\config\SWYFTranslate\packs\<code>\`, where `<code>` is your language's
two-letter code — `nl` for Dutch, `th` for Thai, `vi` for Vietnamese.

> You can change the key in the mod's config file, or set it to `None` if F8 clashes with
> something. If you have Python and a copy of this repo, `tools/make-template.py` does the same
> job without starting the game.

### If the game already translates the language

`packs/de`, `packs/ja` and the nineteen others are the game's own translations, converted from
its string tables by `tools/import-bundled.py`. They are here so that a language the game already
has can be **corrected** rather than retranslated: edit the pack and the game shows your text
where its own would have shown, with no patch and nothing written into the game folder.

Two things follow from that:

- The game already has a dropdown entry for these codes, so the pack does not add one. At startup
  the log says so — `German ships with the game, so the pack in ...\packs\de will override its
  text rather than add a second entry.`
- After a game update, `tools/import-bundled.py --force` re-extracts a pack from the game and
  **replaces the file, edits and all**. To keep what you have and pick up only what is new, run
  `tools/make-template.py` instead — it merges, and lists the strings the game dropped.

## 2. Fill in `pack.json`

```json
{
  "code": "nl",
  "nativeName": "Nederlands",
  "englishName": "Dutch",
  "author": "your name",
  "version": "0.1.0",
  "rtl": false
}
```

`nativeName` is what appears in the language dropdown, so write it the way a speaker would:
`Nederlands`, not `Dutch`.

`code` must be exactly two lowercase letters. The game rejects anything else, and it rejects it
by refusing to build its whole language list, so a typo here takes every language down with it.
Run the checker (below) before you ship.

`code` does not have to be a real language. Invented ones work (`uw` for UwU speak, say), and the
game gets a neutral stand-in for upper-casing and dates. If you want casing and dates to follow a
real language's rules, add `"culture": "en-GB"` (or `nl-NL`, `th-TH`, and so on).

Delete the `_readme` and `_font` keys when you are done. The mod ignores keys starting with `_`,
so they are notes to you, not settings.

## 3. Translate

Each file maps an English string to your translation:

```json
{
  "Play": "Spelen",
  "Settings": "Instellingen",
  "You earned ${0}": "Je hebt ${0} verdiend"
}
```

**Leave a value empty and the game shows the English.** That is the right thing to do for a
string you are unsure about — a pack does not have to be finished to be useful, and 200 good
translations beat 3,000 guesses.

Three rules:

- **Keep every `{0}`, `{1}` and `{name}` exactly as it appears.** These are where the game drops
  in a number or a name. Lose one and the game throws an error while drawing that screen. You may
  move them: `"Je hebt {0} verdiend"` is fine, `"Je hebt verdiend"` is not.
- **Keep markup like `<b>`, `</b>` and `<sprite name=info>` intact.** Drop a closing tag and the
  style bleeds across the rest of the screen.
- **Watch the length.** Dutch and German run longer than English, and a button does not grow to
  fit. If a translation is much longer than the original, look for a shorter word.

Translations apply when the game next loads the language, so switch to another language and back
to see a change, or restart.

## 4. The caller's pronunciation table

When the caller reads out an email address or a security answer, it reads one character at a
time, and it needs to know how to say each one in your language. That is the `sentinel` section:

```json
"sentinel": {
  "digits":  ["nul", "een", "twee", "drie", "vier", "vijf", "zes", "zeven", "acht", "negen"],
  "letters": ["aa", "bee", "see", "dee", "ee", "ef", "gee", "haa", "ie", "jee",
              "kaa", "el", "em", "en", "oo", "pee", "kuu", "er", "es", "tee",
              "uu", "vee", "wee", "iks", "Griekse ij", "zet"],
  "symbols": { "@": "apenstaartje", ".": "punt", "-": "streepje",
               "_": "liggend streepje", "+": "plus", "/": "schuine streep" },
  "questions": {
    "mothers_maiden_name": "meisjesnaam van je moeder",
    "first_pet_name": "naam van je eerste huisdier",
    "favorite_color": "lievelingskleur"
  },
  "colors": { "RED": "rood", "BLUE": "blauw", "GREEN": "groen", "PURPLE": "paars",
              "YELLOW": "geel", "ORANGE": "oranje", "PINK": "roze", "BLACK": "zwart",
              "WHITE": "wit", "RAINBOW": "regenboog", "TEAL": "turquoise", "GOLD": "goud",
              "SILVER": "zilver", "BROWN": "bruin", "INDIGO": "indigo" }
}
```

Write these the way they are **said**, not spelled. The caller is speaking them aloud.

`digits` must have exactly 10 entries and `letters` exactly 26, in order. The game counts them and
refuses a short table.

**You can delete the whole `sentinel` section.** The caller then spells things out in English in
the middle of an otherwise translated call — wrong, but it works, and you can come back to it.

## 5. Check it

```bash
python tools/check-pack.py packs/nl --game "C:/Program Files (x86)/Steam/steamapps/common/Scam With Your Friends"
```

```
nl         Dutch - 3558 of 3561 strings (100%)

OK
```

It finds the mistakes that otherwise only show up in game: a lost `{0}`, a short pronunciation
table, an invalid language code, broken markup, and strings a game update has removed. `--game`
is optional; without it the pack is checked on its own.

## 6. When the game updates

Run the template generator again, pointing at the same pack:

```bash
python tools/make-template.py --game "C:/.../Scam With Your Friends" --pack packs/nl
```

```
commands.json    39 strings    39 translated     0 new
controls.json   295 strings   295 translated     0 new
menu.json      3227 strings  3045 translated   182 new
```

Everything you have already translated is kept. New strings arrive with empty values, so a diff
of the file is exactly the work the update created. Strings the game dropped are listed but left
alone, in case they come back.

---

## What a pack does not cover

Three things need more than JSON, and knowing which is which saves a lot of time:

**Some text is written into the game's code rather than its string tables** — scattered labels,
a few status lines. They are not in the template because they are not in the tables, and no pack
can reach them yet. A rough count puts them in the low hundreds against 3,561 that a pack does
cover.

**The caller's voice.** Adding a language does not add a voice for it. The game picks a
text-to-speech engine per language:

| | Languages | Quality |
|---|---|---|
| Qwen3 | Chinese, English, French, German, Italian, Japanese, Korean, Portuguese, Russian, Spanish | best |
| Pocket | Czech, English, Estonian, French, German, Hindi, Indonesian, Italian, Korean, Portuguese, Spanish | good |
| MOSS | everything else | the fallback |

A new language gets MOSS, because the Qwen3 list is fixed in the game's code and a Pocket voice
needs a trained model installed alongside the game. This is why the Turkish players report poor
voice quality: Turkish is in the game, but in neither list. Nothing in a pack changes that.

**Understanding the player.** This one is free. Speech recognition is Whisper's multilingual
model, which handles around a hundred languages, and the mod hands it your language code with no
further work. The caller's replies are free too: the game already tells it to answer in whatever
language it just heard.
