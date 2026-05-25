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
        /// Return the EQ2 server name (Varsoon, Kaladim, Butcherblock,
        /// etc.) derived from the active log file's parent directory.
        /// Empty when the path doesn't fit the per-server layout (user
        /// is on the legacy /logs/eq2log.txt path, ACT hasn't picked
        /// up a log yet, etc.) — server falls back to its EQ2_WORLD
        /// env-var default in that case.
        ///
        /// Parsing logic lives in the Core <see cref="LogPathParser"/>
        /// so it's unit-testable without ACT.
        /// </summary>
        public static string GetLoggingServerName()
        {
            try
            {
                return LogPathParser.ParseServerName(ActGlobals.oFormActMain?.LogFilePath);
            }
            catch
            {
                return "";
            }
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
