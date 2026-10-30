using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace ScamWYF.Translate
{
    /// <summary>
    /// Adds languages to Scam With Your Friends from folders of JSON, so that translating the game
    /// does not mean writing a mod.
    /// </summary>
    /// <remarks>
    /// The game ships 22 languages and players keep asking for ones it does not have. Each of
    /// those requests has so far been answered, if at all, by a separate mod that patches game
    /// files and re-applies itself after every Steam update. This does the same job from
    /// BepInEx, where nothing is written into the game folder and there is nothing for an update
    /// to overwrite.
    /// </remarks>
    [BepInPlugin(PluginId, "SWYF Translate", PluginVersion)]
    [BepInProcess("Scam With Your Friends.exe")]
    public sealed class TranslatePlugin : BaseUnityPlugin
    {
        public const string PluginId = "net.swyf.translate";
        public const string PluginVersion = "0.4.0";

        /// <summary>Where packs live. Under BepInEx\config so a game update never touches them.</summary>
        public static string PacksRoot
        {
            get { return Path.Combine(ModRoot, "packs"); }
        }

        /// <summary>Where the template exporter writes.</summary>
        public static string TemplateDir
        {
            get { return Path.Combine(ModRoot, "template"); }
        }

        private static string ModRoot
        {
            get { return Path.Combine(Paths.ConfigPath, "SWYFTranslate"); }
        }

        private ConfigEntry<Key> _exportKey;
        private bool _exporting;

        private void Awake()
        {
            _exportKey = Config.Bind(
                "Authoring", "ExportTemplateKey", Key.F8,
                "Writes a blank pack containing every string the game can show, ready to translate. " +
                "Set to None to disable. Press it at the main menu, where all the text has loaded.");

            try
            {
                Directory.CreateDirectory(PacksRoot);
            }
            catch (Exception ex)
            {
                Logger.LogError("Could not create " + PacksRoot + ": " + ex.Message);
                return;
            }

            // Before anything is read: a download is one DLL, and the packs it carries are
            // written out here so that they are ordinary editable files from this point on.
            EmbeddedPacks.Install(PacksRoot, Logger);

            List<string> problems;
            List<LanguagePack> packs = LanguagePack.LoadAll(PacksRoot, out problems);

            foreach (string problem in problems) Logger.LogWarning(problem);

            if (packs.Count == 0)
            {
                // Not an error, and not a reason to stop: the export hotkey is how someone with no
                // packs gets their first one started.
                Logger.LogInfo("No language packs installed. Put one in " + PacksRoot + ", or press " +
                               _exportKey.Value + " in game to write a blank one to translate.");
                return;
            }

            try
            {
                GameBridge.Install(packs, Logger);
            }
            catch (Exception ex)
            {
                Logger.LogError("SWYF Translate could not start: " + ex.Message);
                return;
            }

            StartCoroutine(GameBridge.RegisterLocales());


            Logger.LogInfo("Ready: " + packs.Count + " language pack" + (packs.Count == 1 ? "" : "s") + ".");
        }

        private void Update()
        {
            if (_exporting || _exportKey.Value == Key.None) return;

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return;

            KeyControl key = keyboard[_exportKey.Value];
            if (key == null || !key.wasPressedThisFrame) return;

            _exporting = true;
            StartCoroutine(ExportThenRelease());
        }

        private IEnumerator ExportThenRelease()
        {
            // The guard is held across the whole export rather than just the keypress: the tables
            // load asynchronously, and a second press partway through would write the same files
            // from two coroutines at once.
            yield return TemplateExporter.Export(TemplateDir, Logger);
            _exporting = false;
        }

        private void OnDestroy()
        {
            GameBridge.Uninstall();
        }
    }
}
