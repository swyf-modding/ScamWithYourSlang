using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using Newtonsoft.Json.Linq;

namespace ScamWYF.Translate
{
    /// <summary>
    /// Unpacks the packs that ship inside the DLL, so that a download is one file rather than a
    /// file and a folder tree.
    /// </summary>
    /// <remarks>
    /// The packs stay editable files on disk rather than being read out of the assembly: a
    /// translator has to be able to open menu.json, and the mod's whole point is that adding a
    /// language is writing text rather than shipping code. So the DLL carries a copy and writes it
    /// out once, then gets out of the way.
    ///
    /// What is written is decided per pack, and never guessed:
    ///
    ///  - no folder                     -> unpack it
    ///  - folder with our marker        -> unpack again only if the bundled version is newer
    ///  - folder without our marker     -> leave it alone, because it is the player's
    ///
    /// The third case is the one that matters. Somebody who installed packs by hand, or edited
    /// one, owns that folder; silently replacing their work because the mod happens to carry the
    /// same language would be the sort of thing that loses a translation.
    /// </remarks>
    internal static class EmbeddedPacks
    {
        /// <summary>Name given to the bundle by build.ps1 -resource:.</summary>
        private const string ResourceName = "ScamWYF.Translate.packs.json.gz";

        /// <summary>Marks a pack folder as one this mod wrote, and says which version it wrote.</summary>
        private const string MarkerName = ".embedded.json";

        /// <summary>Every packed file, keyed by its path under the repository's packs folder.</summary>
        private const string BundlePrefix = "packs/";

        internal static void Install(string packsRoot, ManualLogSource log)
        {
            Dictionary<string, string> files = ReadBundle(log);
            if (files == null || files.Count == 0) return;

            // Grouped by pack folder so that one decision covers the whole pack: half of one
            // language from the DLL and half from disk is worse than either.
            var packs = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                int slash = file.Key.IndexOf('/');
                if (!file.Key.StartsWith(BundlePrefix, StringComparison.Ordinal) || slash < 0) continue;
                string name = file.Key.Substring(BundlePrefix.Length);
                int cut = name.IndexOf('/');
                if (cut <= 0) continue;

                string pack = name.Substring(0, cut);
                Dictionary<string, string> contents;
                if (!packs.TryGetValue(pack, out contents)) packs[pack] = contents = new Dictionary<string, string>(StringComparer.Ordinal);
                contents[name.Substring(cut + 1)] = file.Value;
            }

            int written = 0, updated = 0, kept = 0;
            foreach (var pack in packs.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                try
                {
                    string version = VersionOf(pack.Value);
                    string directory = Path.Combine(packsRoot, pack.Key);
                    string marker = Path.Combine(directory, MarkerName);

                    if (Directory.Exists(directory))
                    {
                        if (!File.Exists(marker))
                        {
                            kept++;
                            continue;
                        }
                        if (Compare(ReadMarker(marker), version) >= 0) continue;
                        Write(directory, pack.Value);
                        updated++;
                        continue;
                    }

                    Write(directory, pack.Value);
                    written++;
                }
                catch (Exception ex)
                {
                    // One pack failing to unpack is not a reason to lose the others, and the packs
                    // that are on disk still load.
                    log.LogWarning("Could not unpack the bundled " + pack.Key + " pack: " + ex.Message);
                }
            }

            if (written > 0 || updated > 0)
                log.LogInfo("Unpacked bundled language packs into " + packsRoot + " (" + written + " new, " + updated + " updated).");
            if (kept > 0)
                log.LogInfo(kept + " bundled pack" + (kept == 1 ? " is" : "s are") + " already installed without a " +
                            MarkerName + " marker, so they were left as they are. Delete such a folder to have the DLL's copy written.");
        }

        private static Dictionary<string, string> ReadBundle(ManualLogSource log)
        {
            try
            {
                using (Stream raw = OpenResource(log))
                {
                    if (raw == null)
                    {
                        // Built without the bundle - for example from a source checkout that ran
                        // csc by hand. Nothing is wrong with the install; there is simply nothing
                        // to unpack.
                        log.LogInfo("This build carries no bundled packs, so only the packs already in " +
                                    "BepInEx\\config\\SWYFTranslate\\packs are used.");
                        return null;
                    }

                    using (var gzip = new GZipStream(raw, CompressionMode.Decompress))
                    using (var text = new StreamReader(gzip, Encoding.UTF8))
                    {
                        var bundle = JObject.Parse(text.ReadToEnd());
                        var files = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var entry in bundle.Properties())
                        {
                            var value = entry.Value as JValue;
                            if (value != null && value.Type == JTokenType.String) files[entry.Name] = value.Value<string>();
                        }
                        return files;
                    }
                }
            }
            catch (Exception ex)
            {
                log.LogError("The bundled packs could not be read: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Opens the bundle inside this assembly.
        /// </summary>
        /// <remarks>
        /// By reflection, which is not a preference: this plugin compiles with -nostdlib against
        /// the game's own mscorlib, and that mscorlib has
        /// <c>Assembly.GetManifestResourceStream</c> stripped, so calling it directly does not
        /// compile. The game itself runs on BepInEx's unstripped corlib, where the method is
        /// there, so the call works as long as it is not bound at compile time.
        /// </remarks>
        private static Stream OpenResource(ManualLogSource log)
        {
            Assembly assembly = typeof(EmbeddedPacks).Assembly;
            MethodInfo open = typeof(Assembly).GetMethod("GetManifestResourceStream", new[] { typeof(string) });
            if (open == null)
            {
                log.LogError("This runtime cannot open embedded resources, so the bundled packs are unavailable.");
                return null;
            }
            return (Stream)open.Invoke(assembly, new object[] { ResourceName });
        }

        private static void Write(string directory, Dictionary<string, string> contents)
        {
            Directory.CreateDirectory(directory);
            foreach (var file in contents)
            {
                // Packs are UTF-8 with LF endings and no BOM; a pack written with a BOM parses
                // under some readers and not others, so the bytes go out as they came in.
                string path = Path.Combine(directory, file.Key.Replace('/', Path.DirectorySeparatorChar));
                string parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                File.WriteAllText(path, file.Value, new UTF8Encoding(false));
            }

            var marker = new JObject();
            marker["version"] = VersionOf(contents);
            marker["source"] = "Bundled with ScamWYF.Translate. Delete this file to keep the folder as it is on the next update.";
            File.WriteAllText(Path.Combine(directory, MarkerName), marker.ToString(Newtonsoft.Json.Formatting.Indented) + "\n",
                              new UTF8Encoding(false));
        }

        private static string VersionOf(Dictionary<string, string> contents)
        {
            string manifest;
            if (!contents.TryGetValue("pack.json", out manifest)) return "0.0.0";
            try
            {
                return (string)JObject.Parse(manifest)["version"] ?? "0.0.0";
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return "0.0.0";
            }
        }

        private static string ReadMarker(string path)
        {
            try
            {
                return (string)JObject.Parse(File.ReadAllText(path))["version"] ?? "0.0.0";
            }
            catch (Exception)
            {
                // A marker we cannot read is a marker we do not trust; "0.0.0" means the bundled
                // copy wins, which is the intended behaviour for a damaged marker.
                return "0.0.0";
            }
        }

        /// <summary>Compares two pack versions, which are dotted numbers written by pack authors.</summary>
        private static int Compare(string left, string right)
        {
            int[] a = Numbers(left), b = Numbers(right);
            for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                int x = i < a.Length ? a[i] : 0;
                int y = i < b.Length ? b[i] : 0;
                if (x != y) return x < y ? -1 : 1;
            }
            return 0;
        }

        private static int[] Numbers(string version)
        {
            if (string.IsNullOrEmpty(version)) return new int[0];
            var parts = new List<int>();
            foreach (string piece in version.Split('.'))
            {
                int value;
                parts.Add(int.TryParse(piece.Trim(), out value) ? value : 0);
            }
            return parts.ToArray();
        }
    }
}
