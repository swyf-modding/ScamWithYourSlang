using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ScamWYF.Translate
{
    /// <summary>
    /// One language, as it exists on disk. A pack is a folder, not a DLL, so adding a language
    /// never means shipping code.
    /// </summary>
    /// <remarks>
    /// The game keeps three string tables (Menu, Commands, Controls) and looks each one up by the
    /// English text itself rather than by a key, so a pack is three English-to-translation maps
    /// plus the metadata the speech and font paths need.
    /// </remarks>
    public sealed class LanguagePack
    {
        /// <summary>Two lowercase letters. The game's catalogue validator accepts nothing else.</summary>
        public string Code { get; private set; }

        /// <summary>The name shown in the language dropdown, written in the language itself.</summary>
        public string NativeName { get; private set; }

        public string EnglishName { get; private set; }

        /// <summary>Where the pack was loaded from. Shown in the mod list and used by hot reload.</summary>
        public string Directory { get; private set; }

        public string Author { get; private set; }

        public string PackVersion { get; private set; }

        /// <summary>
        /// The .NET culture used for upper-casing, title-casing and date formatting, for example
        /// "nl-NL". Optional: when it is left out the language code is used if .NET knows it, and
        /// the invariant culture otherwise.
        /// </summary>
        /// <remarks>
        /// The game calls CultureInfo.GetCultureInfo with the language code from three places, and
        /// throws for a code .NET does not recognise. A real language is fine; an invented one, or a
        /// regional code, takes the main menu down while it builds its UI.
        /// </remarks>
        public string Culture { get; private set; }

        /// <summary>Right-to-left script. Recorded for callers; layout support is not here yet.</summary>
        public bool Rtl { get; private set; }

        /// <summary>
        /// Which of the game's four bundled UI fonts to use, or null for the default Latin one.
        /// One of eastAsian, korean, indic, extendedScript.
        /// </summary>
        public string Font { get; private set; }

        /// <summary>
        /// A folder under StreamingAssets/pocket-tts-unity/lookbe/pocket-tts-onnx-v2. Only set it
        /// if a Pocket TTS model for this language is actually installed there; otherwise the
        /// caller falls back to MOSS, which speaks anything but speaks it less well.
        /// </summary>
        public string PocketFolder { get; private set; }

        public bool HasPocket { get; private set; }

        /// <summary>
        /// How the automated caller reads out digits, letters, symbols, security questions and
        /// colours. The game throws rather than guessing, so this is required.
        /// </summary>
        public SentinelTable Sentinel { get; private set; }

        private IDictionary<string, string> _menu, _commands, _controls;
        private bool _tablesRead;
        private string _tablesError;

        /// <summary>
        /// The pack's three string tables, read the first time one is asked for.
        /// </summary>
        /// <remarks>
        /// Lazily, because only the selected language is ever read. A player with twenty-three
        /// installed packs is playing in one of them, and parsing the other twenty-two costs about
        /// 400ms of startup and tens of megabytes for text nothing will ask for. Every access goes
        /// through here rather than through a field, so the cost lands on the language in use.
        /// </remarks>
        public IDictionary<string, string> Menu { get { ReadTables(); return _menu; } }

        public IDictionary<string, string> Commands { get { ReadTables(); return _commands; } }

        public IDictionary<string, string> Controls { get { ReadTables(); return _controls; } }

        /// <summary>Why the tables could not be read, or null. Only meaningful once they were asked for.</summary>
        public string TablesError { get { ReadTables(); return _tablesError; } }

        private void ReadTables()
        {
            if (_tablesRead) return;
            _tablesRead = true;

            try
            {
                _menu = Table(Directory, "menu.json");
                _commands = Table(Directory, "commands.json");
                _controls = Table(Directory, "controls.json");
            }
            catch (Exception ex)
            {
                // Named rather than thrown: this runs from a game callback that a missing file must
                // not take down. A pack that cannot be read shows the game's own text, which is what
                // an unfinished pack does anyway.
                _tablesError = ex.Message;
                _menu = new Dictionary<string, string>(StringComparer.Ordinal);
                _commands = new Dictionary<string, string>(StringComparer.Ordinal);
                _controls = new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }

        private LanguagePack() { }

        /// <summary>
        /// Reads a pack folder. Throws <see cref="PackException"/> with a message meant for a pack
        /// author rather than a programmer: these files are hand-edited by people who are
        /// translating, not debugging.
        /// </summary>
        public static LanguagePack Load(string directory)
        {
            string manifestPath = Path.Combine(directory, "pack.json");
            if (!File.Exists(manifestPath))
                throw new PackException(Path.GetFileName(directory) + ": no pack.json in " + directory);

            JObject manifest;
            try
            {
                manifest = JObject.Parse(File.ReadAllText(manifestPath));
            }
            catch (JsonException ex)
            {
                throw new PackException("pack.json is not valid JSON: " + ex.Message);
            }

            var pack = new LanguagePack
            {
                Directory = directory,
                Code = Str(manifest, "code"),
                NativeName = Str(manifest, "nativeName"),
                EnglishName = Str(manifest, "englishName"),
                Author = Str(manifest, "author") ?? "",
                PackVersion = Str(manifest, "version") ?? "0.0.0",
                Rtl = manifest.Value<bool?>("rtl") ?? false,
                Font = Str(manifest, "font"),
                Culture = Str(manifest, "culture"),
            };

            ValidateCode(pack.Code);
            if (string.IsNullOrEmpty(pack.NativeName))
                throw new PackException(pack.Code + ": pack.json needs a nativeName, the language's name in itself.");
            if (string.IsNullOrEmpty(pack.EnglishName))
                throw new PackException(pack.Code + ": pack.json needs an englishName.");
            ValidateFont(pack);

            var speech = manifest["speech"] as JObject;
            if (speech != null)
            {
                pack.PocketFolder = Str(speech, "pocketFolder") ?? "";
                pack.HasPocket = speech.Value<bool?>("hasPocket") ?? false;
            }
            else
            {
                pack.PocketFolder = "";
            }

            // The catalogue validator rejects a folder with anything but lowercase letters, and it
            // rejects hasPocket with no folder. Catching it here names the pack; letting the game
            // catch it takes the whole language list down with an unattributed exception.
            if (pack.PocketFolder.Any(c => c < 'a' || c > 'z'))
                throw new PackException(pack.Code + ": speech.pocketFolder must be lowercase letters only.");
            if (pack.HasPocket && pack.PocketFolder.Length == 0)
                throw new PackException(pack.Code + ": speech.hasPocket is true but no pocketFolder is named.");

            pack.Sentinel = SentinelTable.Parse(pack.Code, manifest["sentinel"] as JObject);

            // No translations is allowed. A pack with only a manifest still adds the language to
            // the dropdown, which gives it speech recognition, a caller voice and a pronunciation
            // table - useful on its own, and the state every pack starts in.
            return pack;
        }

        /// <summary>Loads every pack folder under <paramref name="root"/>, skipping broken ones.</summary>
        /// <param name="problems">One human-readable line per pack that failed to load.</param>
        public static List<LanguagePack> LoadAll(string root, out List<string> problems)
        {
            problems = new List<string>();
            var packs = new List<LanguagePack>();
            if (!System.IO.Directory.Exists(root)) return packs;

            foreach (string dir in System.IO.Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
            {
                try
                {
                    LanguagePack pack = Load(dir);
                    if (packs.Any(p => p.Code == pack.Code))
                    {
                        problems.Add(Path.GetFileName(dir) + ": another pack already claims language code '" + pack.Code + "'.");
                        continue;
                    }
                    packs.Add(pack);
                }
                catch (PackException ex)
                {
                    problems.Add(ex.Message);
                }
                catch (Exception ex)
                {
                    problems.Add(Path.GetFileName(dir) + ": " + ex.Message);
                }
            }
            return packs;
        }

        private static void ValidateCode(string code)
        {
            if (string.IsNullOrEmpty(code))
                throw new PackException("pack.json needs a two-letter code, for example \"nl\".");
            if (code.Length != 2 || code.Any(c => c < 'a' || c > 'z'))
                throw new PackException("'" + code + "' is not a valid language code. The game accepts exactly two lowercase letters.");
        }

        private static void ValidateFont(LanguagePack pack)
        {
            if (string.IsNullOrEmpty(pack.Font)) return;
            if (!GameFonts.Names.Contains(pack.Font))
                throw new PackException(pack.Code + ": font must be one of " + string.Join(", ", GameFonts.Names) + ", or left out for the default.");
        }

        private static string Str(JObject o, string name)
        {
            JToken token;
            if (!o.TryGetValue(name, out token) || token.Type != JTokenType.String) return null;
            string value = token.Value<string>();
            return string.IsNullOrEmpty(value) ? null : value.Trim();
        }

        private static IDictionary<string, string> Table(string directory, string fileName)
        {
            string path = Path.Combine(directory, fileName);
            // Ordinal, not the default comparer: the game looks these up with the exact English
            // string it compiled in, so a culture-sensitive match would be wrong as often as right.
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!File.Exists(path)) return table;

            JObject parsed;
            try
            {
                parsed = JObject.Parse(File.ReadAllText(path));
            }
            catch (JsonException ex)
            {
                throw new PackException(fileName + " is not valid JSON: " + ex.Message);
            }

            foreach (var entry in parsed)
            {
                if (entry.Value == null || entry.Value.Type != JTokenType.String) continue;
                string translated = entry.Value.Value<string>();
                // An empty value means "not translated yet", which is different from "translate to
                // nothing". Leaving it out lets the game fall through to English.
                if (string.IsNullOrEmpty(entry.Key) || string.IsNullOrEmpty(translated)) continue;
                table[entry.Key] = translated;
            }
            return table;
        }
    }

    /// <summary>A pack could not be loaded, with a message aimed at whoever wrote the pack.</summary>
    public sealed class PackException : Exception
    {
        public PackException(string message) : base(message) { }
    }

    /// <summary>The four UI fonts the game ships, by the name a pack uses to ask for one.</summary>
    internal static class GameFonts
    {
        public static readonly string[] Names = { "eastAsian", "korean", "indic", "extendedScript" };

        /// <summary>The private LanguageManager field backing each name.</summary>
        public static string FieldFor(string name)
        {
            switch (name)
            {
                case "eastAsian": return "eastAsianFont";
                case "korean": return "koreanFont";
                case "indic": return "indicFont";
                case "extendedScript": return "extendedScriptFont";
                default: return null;
            }
        }
    }
}
