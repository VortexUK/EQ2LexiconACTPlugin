using System;
using System.IO;

namespace EQ2Lexicon.ACTPlugin
{
    /// <summary>
    /// Pure parsers for ACT's active log file path. Lives in Core (no
    /// ACT references) so the path-shape rules are unit-testable
    /// without an ACT install.
    ///
    /// EQ2's standard log layout is:
    ///   &lt;install&gt;/logs/&lt;server&gt;/eq2log_&lt;character&gt;.txt
    ///
    /// e.g.   C:\Program Files\...\EverQuest II\logs\Varsoon\eq2log_Kayleigh.txt
    ///
    /// The character-name parsing lives in ActHelpers because it has
    /// other fallbacks (ActGlobals.charName) that need ACT to be loaded.
    /// </summary>
    public static class LogPathParser
    {
        /// <summary>
        /// Extract the EQ2 server name from an ACT log file path.
        /// Returns "" when the path doesn't look like the per-server
        /// layout — caller treats that as "unknown" and the server
        /// falls back to its EQ2_WORLD env-var default.
        ///
        /// Returns "" for:
        ///   * null / whitespace / unparseable paths
        ///   * the legacy generic log at &lt;install&gt;/logs/eq2log.txt
        ///     (parent dir is "logs" — not a real server name)
        ///   * paths where Path.GetDirectoryName / Path.GetFileName
        ///     produce nothing usable
        ///
        /// Server names with spaces (e.g. "Antonia Bayle") pass through
        /// unmodified — Daybreak's Census API accepts them as-is.
        /// </summary>
        public static string ParseServerName(string? logPath)
        {
            if (string.IsNullOrWhiteSpace(logPath)) return "";

            string? dir;
            try
            {
                dir = Path.GetDirectoryName(logPath);
            }
            catch (ArgumentException)
            {
                // Path contains invalid characters — treat as unknown.
                return "";
            }
            if (string.IsNullOrEmpty(dir)) return "";

            var name = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(name)) return "";

            // Legacy fallback: a user who hasn't run /log in-game gets
            // <install>/logs/eq2log.txt, where the parent directory is
            // literally "logs" rather than a server name. Don't stamp
            // "logs" onto uploads — return empty so the server falls
            // back to its configured default.
            if (name.Equals("logs", StringComparison.OrdinalIgnoreCase)) return "";

            return name;
        }
    }
}
