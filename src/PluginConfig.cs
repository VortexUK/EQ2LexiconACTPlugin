using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using Advanced_Combat_Tracker;

namespace EQ2Lexicon.ACTPlugin
{
    /// <summary>
    /// User-configurable settings, persisted as XML under ACT's
    /// AppDataFolder so the plugin survives ACT reinstalls / version
    /// upgrades.
    /// </summary>
    public class PluginConfig
    {
        /// <summary>Base URL of the EQ2 Lexicon site (no trailing slash).</summary>
        public string ServerUrl { get; set; } = "https://eq2censusbot.up.railway.app";

        /// <summary>API token from /settings/tokens. Treat as a password.</summary>
        public string ApiToken { get; set; } = "";

        /// <summary>
        /// When false, the plugin loads but stays inert — no uploads.
        /// Defaults to off so first-time users have to opt in deliberately.
        /// </summary>
        public bool UploadEnabled { get; set; } = false;

        /// <summary>
        /// Character names to NEVER upload as. Encounters where ACT's
        /// active logging character is in this list are skipped silently.
        /// Useful for alts you don't want to attribute parses to (e.g. a
        /// non-guild bank toon, low-level alts, etc.). Match is
        /// case-insensitive on exact name.
        /// </summary>
        [XmlArray("BlacklistedCharacters")]
        [XmlArrayItem("Character")]
        public List<string> BlacklistedCharacters { get; set; } = new List<string>();

        public bool IsBlacklisted(string characterName)
        {
            if (string.IsNullOrWhiteSpace(characterName)) return false;
            return BlacklistedCharacters.Any(b =>
                !string.IsNullOrWhiteSpace(b) &&
                string.Equals(b.Trim(), characterName.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        // -------------------------------------------------------------------
        // Persistence
        // -------------------------------------------------------------------

        public static string GetConfigPath()
        {
            // ActGlobals.oFormActMain.AppDataFolder points at
            // %APPDATA%\Advanced Combat Tracker
            var dir = Path.Combine(
                ActGlobals.oFormActMain.AppDataFolder.FullName,
                "Config");
            return Path.Combine(dir, "EQ2Lexicon.ACTPlugin.config.xml");
        }

        public static PluginConfig Load()
        {
            var path = GetConfigPath();
            if (!File.Exists(path))
            {
                return new PluginConfig();
            }
            try
            {
                var ser = new XmlSerializer(typeof(PluginConfig));
                using (var fs = File.OpenRead(path))
                {
                    var loaded = ser.Deserialize(fs) as PluginConfig;
                    return loaded ?? new PluginConfig();
                }
            }
            catch (Exception)
            {
                // Corrupt config — fall back to defaults rather than refusing
                // to load the plugin entirely. The user can re-enter values
                // and re-save.
                return new PluginConfig();
            }
        }

        public void Save()
        {
            var path = GetConfigPath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var ser = new XmlSerializer(typeof(PluginConfig));
            using (var fs = File.Create(path))
            {
                ser.Serialize(fs, this);
            }
        }
    }
}
