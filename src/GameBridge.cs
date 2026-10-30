using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Kolkata.Speech;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.Tables;

namespace ScamWYF.Translate
{
    /// <summary>
    /// Everything that reaches into the game. Kept in one file so the rest of the mod can be read
    /// and tested without knowing what the game's internals look like.
    /// </summary>
    /// <remarks>
    /// The game is friendlier to this than it looks. One list, <see cref="CallLanguage.Options"/>,
    /// drives the language dropdown, the interface language, the speech recogniser and the choice
    /// of text-to-speech engine, and that list is built from a JSON file rather than compiled in.
    /// Adding a language is therefore mostly a matter of getting entries into that list early
    /// enough, which is what <see cref="CatalogPatch"/> does.
    ///
    /// Translations themselves need no patch on any text path. The game memoises every lookup in
    /// three static dictionaries keyed by the English string, and returns a hit before it consults
    /// its own string tables. Pre-filling those dictionaries is therefore a complete substitution
    /// that costs nothing per call. The dictionaries are emptied whenever the language changes, so
    /// the one hook needed is a postfix on the clear, to fill them again.
    /// </remarks>
    internal static class GameBridge
    {
        private const string HarmonyId = "net.swyf.translate";

        private static ManualLogSource _log;
        private static Harmony _harmony;

        /// <summary>Packs by language code. Read from the patch callbacks, so it is set first.</summary>
        private static readonly Dictionary<string, LanguagePack> Packs =
            new Dictionary<string, LanguagePack>(StringComparer.Ordinal);

        internal static long FontCalls, FontTicks, CultureCalls, CultureTicks, FillCalls, FillTicks;

        private static FieldInfo _menuCache, _commandsCache, _controlsCache;
        private static FieldInfo _optionsField, _profilesField;

        /// <summary>
        /// Makes every pack visible to the game. Call once, as early as a plugin can run: the
        /// catalogue is built the first time any game code asks for a language, and patching after
        /// that costs a write to a readonly field instead.
        /// </summary>
        internal static void Install(IEnumerable<LanguagePack> packs, ManualLogSource log)
        {
            _log = log;
            foreach (LanguagePack pack in packs) Packs[pack.Code] = pack;
            if (Packs.Count == 0) return;

            ResolveFields();

            _harmony = new Harmony(HarmonyId);
            _harmony.PatchAll(typeof(CatalogPatch));
            _harmony.PatchAll(typeof(CacheFillPatch));
            _harmony.PatchAll(typeof(FontPatch));

            // Only when something needs it. This is a detour on a .NET method the whole game formats
            // text through, and it exists for codes .NET does not know - an invented language, not
            // Dutch. With every installed pack being a real language, installing it would be pure
            // overhead on a hot path for a case that cannot happen.
            if (NeedsCulturePatch()) _harmony.PatchAll(typeof(CulturePatch));

            EnsureCatalogue();
            InstallSentinelProfiles();
        }

        internal static string CostLine()
        {
            double hz = System.Diagnostics.Stopwatch.Frequency;
            return string.Format("fill {0} ({1:F1}ms) culture {2} ({3:F1}ms) font {4} ({5:F1}ms)",
                FillCalls, FillTicks / hz * 1000.0, CultureCalls, CultureTicks / hz * 1000.0,
                FontCalls, FontTicks / hz * 1000.0);
        }

        internal static void Uninstall()
        {
            if (_harmony != null) _harmony.UnpatchSelf();
            _harmony = null;
            Packs.Clear();
        }

        private static void ResolveFields()
        {
            const BindingFlags Statics = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

            _menuCache = typeof(LanguageManager).GetField("menuTextCache", Statics);
            _commandsCache = typeof(LanguageManager).GetField("commandsTextCache", Statics);
            _controlsCache = typeof(LanguageManager).GetField("controlsTextCache", Statics);
            _optionsField = typeof(CallLanguage).GetField("Options", Statics);
            _profilesField = typeof(SentinelSpeechLanguage).GetField("Profiles", Statics);

            // Named individually: "a field moved" is a game-update problem a pack author will
            // report, and the name tells whoever reads the log which part stopped working.
            Require(_menuCache, "LanguageManager.menuTextCache");
            Require(_commandsCache, "LanguageManager.commandsTextCache");
            Require(_controlsCache, "LanguageManager.controlsTextCache");
            Require(_optionsField, "CallLanguage.Options");
            Require(_profilesField, "SentinelSpeechLanguage.Profiles");
        }

        private static void Require(FieldInfo field, string name)
        {
            if (field == null)
                throw new InvalidOperationException(
                    name + " is not where this mod expects it. The game has changed and SWYF-Translate needs updating.");
        }

        // ---- the language catalogue ------------------------------------------------------

        /// <summary>
        /// Appends the packs to the catalogue the game builds from its own JSON file. A postfix
        /// rather than a rewrite of the file: the file is a game asset, and editing it would be
        /// undone by the next Steam update and would break the game's integrity checks besides.
        /// </summary>
        [HarmonyPatch(typeof(CallLanguage), "LoadCatalog")]
        private static class CatalogPatch
        {
            private static void Postfix(ref IReadOnlyList<CallLanguageOption> __result)
            {
                __result = Extend(__result);
            }
        }

        /// <summary>
        /// Puts the packs in the catalogue, by replacing the list if the postfix was too late to
        /// extend it - which it usually is.
        /// </summary>
        /// <remarks>
        /// The catalogue is a static field with an initialiser, and the game resets other statics
        /// on the same class from a [RuntimeInitializeOnLoadMethod], which touches the class and
        /// builds the catalogue before any BepInEx plugin exists. So the postfix is the unusual
        /// path and this is the ordinary one. Both are kept: the postfix costs nothing, and it is
        /// the one that works if a future version stops resetting statics that early.
        ///
        /// Writing a static readonly field through reflection is allowed under Mono. It is
        /// checked rather than assumed, because a runtime that refuses would otherwise leave the
        /// mod reporting success and doing nothing.
        /// </remarks>
        private static void EnsureCatalogue()
        {
            if (Listed(CallLanguage.Options)) return;

            try
            {
                _optionsField.SetValue(null, Extend(CallLanguage.Options));
            }
            catch (Exception ex)
            {
                _log.LogError("Could not add the packs to the game's language list: " + ex.Message);
                return;
            }

            if (!Listed(CallLanguage.Options))
                _log.LogError("The game's language list did not accept the new languages, so they will not " +
                              "appear in Settings. Translations for languages the game already has still work.");
        }

        /// <summary>True when every pack's language is in the list, from a pack or from the game.</summary>
        private static bool Listed(IReadOnlyList<CallLanguageOption> options)
        {
            return options != null && Packs.Keys.All(code => options.Any(o => o.Code == code));
        }

        private static IReadOnlyList<CallLanguageOption> Extend(IReadOnlyList<CallLanguageOption> existing)
        {
            var list = new List<CallLanguageOption>(existing ?? (IReadOnlyList<CallLanguageOption>)new CallLanguageOption[0]);
            foreach (LanguagePack pack in Packs.Values.OrderBy(p => p.EnglishName, StringComparer.Ordinal))
            {
                // A pack for a language the game already has is not an error and is not ignored:
                // its translations still fill the caches, which is how a pack fixes a shipped
                // language rather than adding one. Only the dropdown entry is skipped, because
                // the game already has one and two would be confusing.
                if (list.Any(o => o.Code == pack.Code))
                {
                    _log.LogInfo(pack.EnglishName + " ships with the game, so the pack in " + pack.Directory +
                                 " will override its text rather than add a second entry.");
                    continue;
                }
                list.Add(new CallLanguageOption(pack.Code, pack.NativeName, pack.EnglishName, pack.HasPocket, pack.PocketFolder));
                _log.LogInfo("Added " + pack.EnglishName + " (" + pack.Code + ").");
            }
            return list.AsReadOnly();
        }

        // ---- how the caller reads characters aloud ---------------------------------------

        /// <summary>
        /// Gives each pack a pronunciation table. The game throws for a language it has none for,
        /// and it throws during a phone call, so this runs at load rather than on demand.
        /// </summary>
        private static void InstallSentinelProfiles()
        {
            var profiles = _profilesField.GetValue(null) as IDictionary;
            if (profiles == null)
            {
                _log.LogError("Could not read the pronunciation tables; calls in an added language would fail.");
                return;
            }

            ConstructorInfo ctor = typeof(SentinelSpeechLanguage).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(string) },
                null);
            if (ctor == null)
            {
                _log.LogError("The pronunciation table format has changed; calls in an added language would fail.");
                return;
            }

            foreach (LanguagePack pack in Packs.Values)
            {
                if (profiles.Contains(pack.Code)) continue;
                SentinelTable table = pack.Sentinel;
                try
                {
                    profiles[pack.Code] = ctor.Invoke(new object[]
                    {
                        pack.Code, table.Digits, table.Letters, table.Symbols, table.Questions, table.Colors
                    });
                }
                catch (TargetInvocationException ex)
                {
                    _log.LogError(pack.Code + ": the game rejected the pronunciation table: " +
                                  (ex.InnerException != null ? ex.InnerException.Message : ex.Message));
                    continue;
                }

                if (table.IsEnglishFallback)
                    _log.LogWarning(pack.Code + " has no \"sentinel\" section, so the caller will read out " +
                                    "email addresses and security answers in English. See docs/authoring.md.");
            }
        }

        // ---- the translations themselves -------------------------------------------------

        /// <summary>
        /// Refills the game's lookup caches every time it empties them, which it does on each
        /// language change. Filling them is the whole translation mechanism: the game checks the
        /// cache before its own string tables and returns a hit unchanged.
        /// </summary>
        [HarmonyPatch(typeof(LanguageManager), "ClearTextCaches")]
        private static class CacheFillPatch
        {
            private static void Postfix()
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                try { FillCaches(); }
                finally { FillCalls++; FillTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0; }
            }
        }

        /// <summary>
        /// Loads the current language's translations into the game's caches, or leaves them empty
        /// when the current language is one the game ships itself.
        /// </summary>
        internal static void FillCaches()
        {
            // Everything here runs inside the game's own call stack, including one path that runs
            // before the first scene loads. An exception escaping into that would take the game's
            // language handling down with it, so nothing is allowed out - not even reading which
            // language is current, which loads the saved selection the first time it is asked.
            try
            {
                LanguagePack pack;
                if (!Packs.TryGetValue(LanguageManager.EffectiveCode ?? "", out pack)) return;

                Fill(_menuCache, pack.Menu, "Menu");
                Fill(_commandsCache, pack.Commands, "Commands");
                Fill(_controlsCache, pack.Controls, "Controls");

                if (pack.TablesError != null)
                {
                    _log.LogWarning(pack.Code + ": " + pack.TablesError + " Nothing was translated for it.");
                }
            }
            catch (Exception ex)
            {
                _log.LogError("Could not apply the language pack: " + ex.Message);
            }
        }

        private static void Fill(FieldInfo field, IDictionary<string, string> translations, string tableName)
        {
            var cache = (Dictionary<string, string>)field.GetValue(null);
            if (cache == null) return;

            Dictionary<string, string> keys = EntryKeys(tableName);
            foreach (var entry in translations)
            {
                cache[entry.Key] = entry.Value;

                // Almost every call site passes the English text, which is what a pack is keyed
                // by. A few pass the table's own entry key instead, and those would miss. Filling
                // both costs one extra entry per string and removes a whole class of "this one
                // screen stayed in English" report.
                string entryKey;
                if (keys != null && keys.TryGetValue(entry.Key, out entryKey) && entryKey != entry.Key)
                    cache[entryKey] = entry.Value;
            }
        }

        /// <summary>English text to table key, for one table, read once from the English table.</summary>
        private static Dictionary<string, string> EntryKeys(string tableName)
        {
            Dictionary<string, string> keys;
            return _entryKeys.TryGetValue(tableName, out keys) ? keys : null;
        }

        private static readonly Dictionary<string, Dictionary<string, string>> _entryKeys =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        // ---- cultures --------------------------------------------------------------------

        /// <summary>
        /// Stops the main menu failing to build for a language .NET has no culture for.
        /// </summary>
        /// <remarks>
        /// The game upper-cases button text, title-cases setting labels and formats dates with
        /// <c>CultureInfo.GetCultureInfo(LanguageManager.EffectiveCode)</c>, which throws
        /// CultureNotFoundException for a code .NET does not know. The exception escapes
        /// MainMenuToolkitController.InitializeUi, so the menu never appears, and it is written to
        /// Unity's Player.log rather than the BepInEx log, which reports a clean start.
        ///
        /// The lookup is patched rather than its three callers because the game adds callers, and
        /// a language pack should not stop working because a screen was added.
        /// </remarks>
        [HarmonyPatch(typeof(CultureInfo), "GetCultureInfo", new[] { typeof(string) })]
        private static class CulturePatch
        {
            private static bool Prefix(string name, ref CultureInfo __result)
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                try { return PrefixCore(name, ref __result); }
                finally { CultureCalls++; CultureTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0; }
            }

            private static bool PrefixCore(string name, ref CultureInfo __result)
            {
                LanguagePack pack;
                if (name == null || !Packs.TryGetValue(name, out pack)) return true;

                CultureInfo culture = CultureFor(pack);
                if (culture == null) return true;

                __result = culture;
                return false;
            }
        }

        /// <summary>Whether any installed pack's language is one .NET has no culture for.</summary>
        private static bool NeedsCulturePatch()
        {
            foreach (LanguagePack pack in Packs.Values)
            {
                if (!string.IsNullOrEmpty(pack.Culture) && Create(pack.Culture) != null) continue;
                if (Create(pack.Code) != null) continue;

                _log.LogInfo(pack.Code + " is not a .NET culture, so the culture lookup is patched for it.");
                return true;
            }
            return false;
        }

        private static readonly Dictionary<string, CultureInfo> _cultures =
            new Dictionary<string, CultureInfo>(StringComparer.Ordinal);

        /// <summary>
        /// The culture a pack's language should use, or null to leave the game's lookup alone
        /// because .NET already knows the code.
        /// </summary>
        private static CultureInfo CultureFor(LanguagePack pack)
        {
            CultureInfo cached;
            if (_cultures.TryGetValue(pack.Code, out cached)) return cached;

            CultureInfo chosen = null;
            if (!string.IsNullOrEmpty(pack.Culture))
            {
                chosen = Create(pack.Culture);
                if (chosen == null)
                    _log.LogWarning(pack.Code + ": culture '" + pack.Culture + "' is not one .NET knows, so it is being ignored.");
            }

            if (chosen == null && Create(pack.Code) == null)
            {
                // Casing and dates in an invented language have no correct answer. The invariant
                // culture is the neutral one, and the log says so rather than leaving a pack author
                // to wonder why their dates are in English.
                chosen = CultureInfo.InvariantCulture;
                _log.LogInfo(pack.EnglishName + " (" + pack.Code + ") is not a .NET culture, so text casing and dates use the invariant " +
                             "culture. Set \"culture\" in pack.json to choose another.");
            }

            _cultures[pack.Code] = chosen;
            return chosen;
        }

        /// <summary>Builds a culture without going through the patched lookup, or null if there is none.</summary>
        private static CultureInfo Create(string name)
        {
            try
            {
                return new CultureInfo(name);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        // ---- fonts -----------------------------------------------------------------------

        /// <summary>
        /// Lets a pack borrow one of the four fonts the game bundles. The game returns null for a
        /// language it does not know, which gives the Latin font and renders Greek, Cyrillic or
        /// Devanagari as empty boxes.
        /// </summary>
        [HarmonyPatch(typeof(LanguageManager), "GetUiFontForCode")]
        private static class FontPatch
        {
            private static void Postfix(string code, ref Font __result)
            {
                if (__result != null) return;
                LanguagePack pack;
                if (!Packs.TryGetValue(code ?? "", out pack) || string.IsNullOrEmpty(pack.Font)) return;

                LanguageManager instance = LanguageManager.Instance;
                if (instance == null) return;

                FieldInfo field = typeof(LanguageManager).GetField(
                    GameFonts.FieldFor(pack.Font), BindingFlags.Instance | BindingFlags.NonPublic);
                if (field != null) __result = field.GetValue(instance) as Font;
            }
        }

        // ---- the Unity locale ------------------------------------------------------------

        /// <summary>
        /// Registers a locale per pack, so the game's own lookup finds one and does not quietly
        /// fall back to English before the caches are consulted. Waits for Unity's localisation
        /// system, which is not ready when plugins load.
        /// </summary>
        internal static IEnumerator RegisterLocales()
        {
            yield return LocalizationSettings.InitializationOperation;

            // AddLocale is on the concrete provider rather than the interface. Anything else
            // implementing ILocalesProvider is not ours to append to, so say so and carry on:
            // translations still work, the game just treats the language as English underneath.
            var provider = LocalizationSettings.AvailableLocales as LocalesProvider;
            if (provider == null)
            {
                _log.LogWarning("The game is not using Unity's standard locale list, so no locales were registered.");
                yield break;
            }

            foreach (LanguagePack pack in Packs.Values)
            {
                if (provider.GetLocale(new LocaleIdentifier(pack.Code)) != null) continue;
                Locale locale = Locale.CreateLocale(new LocaleIdentifier(pack.Code));
                if (locale == null)
                {
                    _log.LogWarning("Unity would not create a locale for '" + pack.Code + "'; " + pack.EnglishName +
                                    " will still be translated, but the game will treat it as English underneath.");
                    continue;
                }
                locale.name = pack.EnglishName + " (" + pack.Code + ")";
                provider.AddLocale(locale);
            }

            yield return ReadEntryKeys();

            // The caches were filled before the locale existed if this language was already
            // selected at startup, and the game clears them during its own startup. Fill again.
            FillCaches();
        }

        /// <summary>
        /// Builds the English-text-to-entry-key map the dual-key fill needs. The game builds the
        /// same map for itself, but keeps it private and clears it whenever the language changes.
        /// </summary>
        private static IEnumerator ReadEntryKeys()
        {
            Locale english = LocalizationSettings.AvailableLocales.GetLocale(new LocaleIdentifier("en"));
            if (english == null) yield break;

            foreach (string tableName in TemplateExporter.Tables)
            {
                var handle = LocalizationSettings.StringDatabase.GetTableAsync(tableName, english);
                yield return handle;

                StringTable table = handle.Result;
                if (table == null) continue;

                var keys = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (StringTableEntry entry in table.Values)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.Value) || string.IsNullOrEmpty(entry.Key)) continue;
                    // First wins, matching the game's own map: two entries sharing English text
                    // are the same string said twice, and either key resolves it.
                    if (!keys.ContainsKey(entry.Value)) keys[entry.Value] = entry.Key;
                }
                _entryKeys[tableName] = keys;
            }
        }
    }
}
