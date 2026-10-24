using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ScamWYF.Translate
{
    /// <summary>
    /// How the automated caller reads out characters aloud: "W as in letter W, at, dot, dash".
    /// </summary>
    /// <remarks>
    /// The game stores one of these per language and throws for a language it has no table for,
    /// so a pack that adds a language has to bring one. A pack may leave it out, in which case
    /// English is used and the caller spells out an email address in English in the middle of an
    /// otherwise translated call - wrong, but audible and fixable, which beats an exception
    /// during a phone call.
    ///
    /// The shapes here are fixed by the game's own constructor, which counts the entries and
    /// refuses a short table. Keys are used rather than positions wherever the game has a name
    /// for the slot, so a pack author cannot silently shift every colour by one.
    /// </remarks>
    public sealed class SentinelTable
    {
        /// <summary>Order is fixed by the game: the index is the digit.</summary>
        private static readonly int DigitCount = 10;

        /// <summary>Order is fixed by the game: the index is the letter, A to Z.</summary>
        private static readonly int LetterCount = 26;

        /// <summary>The game indexes these by position in "@.-_+/".</summary>
        public static readonly string[] SymbolKeys = { "@", ".", "-", "_", "+", "/" };

        public static readonly string[] QuestionKeys = { "mothers_maiden_name", "first_pet_name", "favorite_color" };

        /// <summary>Must stay in this order: the game matches by index into its own list.</summary>
        public static readonly string[] ColorKeys =
        {
            "RED", "BLUE", "GREEN", "PURPLE", "YELLOW", "ORANGE", "PINK", "BLACK",
            "WHITE", "RAINBOW", "TEAL", "GOLD", "SILVER", "BROWN", "INDIGO"
        };

        public string Digits { get; private set; }
        public string Letters { get; private set; }
        public string Symbols { get; private set; }
        public string Questions { get; private set; }
        public string Colors { get; private set; }

        /// <summary>True when the pack did not supply a table and English is standing in.</summary>
        public bool IsEnglishFallback { get; private set; }

        private SentinelTable() { }

        public static SentinelTable Parse(string code, JObject sentinel)
        {
            if (sentinel == null) return English();

            var table = new SentinelTable
            {
                Digits = Ordered(code, sentinel, "digits", DigitCount),
                Letters = Ordered(code, sentinel, "letters", LetterCount),
                Symbols = Keyed(code, sentinel, "symbols", SymbolKeys),
                Questions = Keyed(code, sentinel, "questions", QuestionKeys),
                Colors = Keyed(code, sentinel, "colors", ColorKeys),
            };
            return table;
        }

        /// <summary>
        /// The game's own English table, used when a pack supplies none. Copied rather than read
        /// out of the game so that a pack can be validated by the authoring tools with no game
        /// installed.
        /// </summary>
        public static SentinelTable English()
        {
            return new SentinelTable
            {
                IsEnglishFallback = true,
                Digits = "zero|one|two|three|four|five|six|seven|eight|nine",
                Letters = string.Join("|", Enumerable.Range(0, 26).Select(i => "letter " + (char)('A' + i)).ToArray()),
                Symbols = "at|dot|dash|underscore|plus|slash",
                Questions = "mother's maiden name|first pet name|favorite color",
                Colors = "Red|Blue|Green|Purple|Yellow|Orange|Pink|Black|White|Rainbow|Teal|Gold|Silver|Brown|Indigo",
            };
        }

        private static string Ordered(string code, JObject sentinel, string name, int expected)
        {
            var array = sentinel[name] as JArray;
            if (array == null)
                throw new PackException(code + ": sentinel." + name + " must be a list of " + expected + " entries.");
            if (array.Count != expected)
                throw new PackException(code + ": sentinel." + name + " has " + array.Count + " entries, but the game requires exactly " + expected + ".");

            var values = new string[expected];
            for (int i = 0; i < expected; i++)
            {
                values[i] = Clean(code, name, array[i]);
            }
            return string.Join("|", values);
        }

        private static string Keyed(string code, JObject sentinel, string name, string[] keys)
        {
            var o = sentinel[name] as JObject;
            if (o == null)
                throw new PackException(code + ": sentinel." + name + " must be an object with the keys " + string.Join(", ", keys) + ".");

            var values = new string[keys.Length];
            var missing = new List<string>();
            for (int i = 0; i < keys.Length; i++)
            {
                JToken token;
                if (!o.TryGetValue(keys[i], out token))
                {
                    missing.Add(keys[i]);
                    continue;
                }
                values[i] = Clean(code, name, token);
            }
            if (missing.Count > 0)
                throw new PackException(code + ": sentinel." + name + " is missing " + string.Join(", ", missing.ToArray()) + ".");

            return string.Join("|", values);
        }

        private static string Clean(string code, string name, JToken token)
        {
            if (token == null || token.Type != JTokenType.String)
                throw new PackException(code + ": every entry in sentinel." + name + " must be text.");
            string value = token.Value<string>();
            if (string.IsNullOrEmpty(value) || value.Trim().Length == 0)
                throw new PackException(code + ": sentinel." + name + " has an empty entry. The caller would say nothing where it should read a character.");
            // A pipe would be read by the game as a separator and shift every later entry.
            if (value.IndexOf('|') >= 0)
                throw new PackException(code + ": sentinel." + name + " contains a '|', which the game uses to separate entries.");
            return value.Trim();
        }
    }
}
