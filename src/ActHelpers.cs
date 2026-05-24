using System;
using System.IO;
using Advanced_Combat_Tracker;

namespace EQ2Lexicon.ACTPlugin
{
    /// <summary>
    /// Small wrappers around ACT globals that paper over EQ2-specific quirks.
    /// </summary>
    internal static class ActHelpers
    {
        /// <summary>
        /// Return the actual EQ2 character name ACT is currently parsing
        /// the log for.
        ///
        /// `ActGlobals.charName` is unreliable for EQ2 — ACT substitutes
        /// "YOU" for the local player in display contexts. The authoritative
        /// source is the active log file path: EQ2 writes
        /// `eq2log_&lt;charname&gt;.txt`, and ACT exposes the path it's
        /// currently tailing via `ActGlobals.oFormActMain.LogFilePath`.
        ///
        /// Returns empty string if nothing usable is available (ACT just
        /// started, no log file picked up yet, etc.).
        /// </summary>
        public static string GetLoggingCharacterName()
        {
            // 1. If ACT has a sensible charName, use it. EQ2 normally writes
            //    "YOU"; other games may set this to the real name.
            try
            {
                var n = ActGlobals.charName;
                if (!string.IsNullOrWhiteSpace(n) &&
                    !n.Equals("YOU", StringComparison.OrdinalIgnoreCase))
                {
                    return n;
                }
            }
            catch { /* fall through */ }

            // 2. Parse the active log filename — eq2log_<character>.txt
            try
            {
                var logPath = ActGlobals.oFormActMain.LogFilePath;
                if (!string.IsNullOrWhiteSpace(logPath))
                {
                    var stem = Path.GetFileNameWithoutExtension(logPath);
                    if (stem != null &&
                        stem.StartsWith("eq2log_", StringComparison.OrdinalIgnoreCase) &&
                        stem.Length > "eq2log_".Length)
                    {
                        return stem.Substring("eq2log_".Length);
                    }
                }
            }
            catch { /* ACT.oFormActMain.LogFilePath may not exist in older builds */ }

            return "";
        }

        /// <summary>
        /// Production location for the plugin config XML. Lives in
        /// %APPDATA%\Advanced Combat Tracker\Config\ so it survives ACT
        /// reinstalls and plugin upgrades. Kept here (in the ACT-coupled
        /// assembly) rather than on <see cref="PluginConfig"/> so the
        /// config class itself can stay ACT-free + unit-testable.
        /// </summary>
        public static string GetConfigPath()
        {
            var dir = Path.Combine(
                ActGlobals.oFormActMain.AppDataFolder.FullName,
                "Config");
            return Path.Combine(dir, "EQ2Lexicon.ACTPlugin.config.xml");
        }
    }
}
