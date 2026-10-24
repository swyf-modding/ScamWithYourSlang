using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx.Logging;
using Newtonsoft.Json;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.Tables;

namespace ScamWYF.Translate
{
    /// <summary>
    /// Writes a blank pack containing every string the game can show, so that starting a
    /// translation is a matter of filling in values rather than finding out what to translate.
    /// </summary>
    /// <remarks>
    /// The strings are read out of the running game's own English tables. That is deliberate: a
    /// list extracted once from a build goes stale the first time the game adds a line, and a
    /// pack author has no way to tell. Exporting from the game they are running means the
    /// template always matches the version they are translating.
    /// </remarks>
    internal static class TemplateExporter
    {
        /// <summary>The game's three string tables, in the order a translator meets them.</summary>
        internal static readonly string[] Tables = { "Menu", "Commands", "Controls" };

        internal static IEnumerator Export(string outputDir, ManualLogSource log)
        {
            Locale english = LocalizationSettings.AvailableLocales.GetLocale(new LocaleIdentifier("en"));
            if (english == null)
            {
                log.LogError("The game's English locale is not loaded yet; try again once you are at the main menu.");
                yield break;
            }

            var exported = new Dictionary<string, Dictionary<string, string>>();

            foreach (string tableName in Tables)
            {
                var handle = LocalizationSettings.StringDatabase.GetTableAsync(tableName, english);
                yield return handle;

                StringTable table = handle.Result;
                if (table == null)
                {
                    log.LogWarning("The " + tableName + " table could not be read; it will be missing from the template.");
                    continue;
                }

                var entries = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (StringTableEntry entry in table.Values)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.Value)) continue;
                    if (entry.Value.Trim().Length == 0) continue;
                    // Keyed by the English text, because that is what the game passes to its own
                    // lookup and therefore what a translation has to match.
                    entries[entry.Value] = "";
                }
                exported[tableName] = entries;
            }

            if (exported.Count == 0)
            {
                log.LogError("No tables could be read, so no template was written.");
                yield break;
            }

            try
            {
                Directory.CreateDirectory(outputDir);
                WriteTable(Path.Combine(outputDir, "menu.json"), Table(exported, "Menu"));
                WriteTable(Path.Combine(outputDir, "commands.json"), Table(exported, "Commands"));
                WriteTable(Path.Combine(outputDir, "controls.json"), Table(exported, "Controls"));
                WriteManifest(Path.Combine(outputDir, "pack.json"));
            }
            catch (Exception ex)
            {
                log.LogError("Could not write the template to " + outputDir + ": " + ex.Message);
                yield break;
            }

            int total = exported.Values.Sum(t => t.Count);
            log.LogInfo("Wrote a template for " + total + " strings to " + outputDir);
            log.LogInfo("Copy that folder into " + TranslatePlugin.PacksRoot + "\\<code>, fill in pack.json and the values, and restart.");
        }

        private static Dictionary<string, string> Table(
            Dictionary<string, Dictionary<string, string>> exported, string name)
        {
            Dictionary<string, string> table;
            return exported.TryGetValue(name, out table) ? table : new Dictionary<string, string>(StringComparer.Ordinal);
        }

        /// <summary>
        /// Writes a table with its keys in ordinal order, so the file is stable between runs and
        /// diffs cleanly when the game adds a line: a pack author reviewing an update should see
        /// only what changed. Sorting here rather than in a sorted collection because managed
        /// stripping removed SortedDictionary from the game's mscorlib.
        /// </summary>
        private static void WriteTable(string path, Dictionary<string, string> table)
        {
            var keys = new List<string>(table.Keys);
            keys.Sort(StringComparer.Ordinal);

            using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
            using (var json = new JsonTextWriter(writer) { Formatting = Formatting.Indented })
            {
                json.WriteStartObject();
                foreach (string key in keys)
                {
                    json.WritePropertyName(key);
                    json.WriteValue(table[key]);
                }
                json.WriteEndObject();
            }
        }

        private static void WriteJson(string path, object value)
        {
            // UTF-8 without a BOM: these files are edited by hand and read by other tools, and a
            // BOM turns the first key into something no lookup will ever match.
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
            using (var json = new JsonTextWriter(writer) { Formatting = Formatting.Indented })
            {
                new JsonSerializer().Serialize(json, value);
            }
        }

        /// <summary>
        /// A manifest with the shape filled in and the language-specific parts left blank, next to
        /// a comment saying what each one is for. JSON has no comments, so the explanations live
        /// in keys the loader ignores.
        /// </summary>
        private static void WriteManifest(string path)
        {
            var manifest = new Dictionary<string, object>
            {
                { "_readme", "Fill in code, nativeName and englishName. See docs/authoring.md for the rest." },
                { "code", "" },
                { "nativeName", "" },
                { "englishName", "" },
                { "author", "" },
                { "version", "0.1.0" },
                { "rtl", false },
                { "_font", "One of eastAsian, korean, indic, extendedScript. Leave out for a Latin script." },
                { "speech", new Dictionary<string, object>
                    {
                        { "_readme", "Only set hasPocket if a Pocket TTS model for this language is installed in StreamingAssets. Otherwise the caller uses MOSS." },
                        { "hasPocket", false },
                        { "pocketFolder", "" },
                    }
                },
                { "sentinel", BlankSentinel() },
            };
            WriteJson(path, manifest);
        }

        private static Dictionary<string, object> BlankSentinel()
        {
            var digits = new string[10];
            for (int i = 0; i < digits.Length; i++) digits[i] = "";

            var letters = new string[26];
            for (int i = 0; i < letters.Length; i++) letters[i] = "";

            var symbols = new Dictionary<string, string>();
            foreach (string key in SentinelTable.SymbolKeys) symbols[key] = "";

            var questions = new Dictionary<string, string>();
            foreach (string key in SentinelTable.QuestionKeys) questions[key] = "";

            var colors = new Dictionary<string, string>();
            foreach (string key in SentinelTable.ColorKeys) colors[key] = "";

            return new Dictionary<string, object>
            {
                { "_readme", "How the caller reads characters aloud. Delete this whole section to use English." },
                { "digits", digits },
                { "letters", letters },
                { "symbols", symbols },
                { "questions", questions },
                { "colors", colors },
            };
        }
    }
}
