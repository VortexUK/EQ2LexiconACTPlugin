using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;

namespace EQ2Lexicon.ACTPlugin
{
    /// <summary>
    /// One row of the (Character, Server) blacklist surfaced in the
    /// settings panel. Plain DTO; equality / serialization use the
    /// two string properties directly. A blank Server means "match
    /// any EQ2 server" — the legacy v0.1.13 semantics.
    /// </summary>
    public class BlacklistedEntry
    {
        [XmlAttribute("character")]
        public string Character { get; set; } = "";

        [XmlAttribute("server")]
        public string Server { get; set; } = "";
    }

    /// <summary>
    /// User-configurable settings, persisted as XML.
    ///
    /// The CALLER supplies the on-disk path to Load/Save — see
    /// <c>ActHelpers.GetConfigPath()</c> in the UI assembly for the
    /// production location under ACT's AppDataFolder. Keeping path
    /// resolution out of this class lets it live in the ACT-free
    /// <c>Core</c> assembly so it's unit-testable without ACT.
    ///
    /// API token is encrypted at rest via DPAPI (current-user scope)
    /// before being written to disk. Legacy plaintext tokens from
    /// v0.1.4 and earlier are still loaded, and the next Save
    /// transparently re-writes them encrypted.
    /// </summary>
    public class PluginConfig
    {
        /// <summary>
        /// Canonical plugin API endpoint. Owned domain — points at
        /// whatever host serves the API today (Railway as of v0.1.11)
        /// and stays stable across future infrastructure moves. Plugin
        /// installs hardcoded against this won't break if the backend
        /// host changes.
        /// </summary>
        internal const string DefaultServerUrl = "https://parses.eq2lexicon.com";

        /// <summary>
        /// The previous default, kept for one-shot auto-migration in
        /// <see cref="Load"/>. Users on the implicit Railway URL get
        /// silently rewritten to <see cref="DefaultServerUrl"/> the
        /// next time their config is loaded.
        /// </summary>
        internal const string LegacyDefaultServerUrl = "https://eq2lexicon.up.railway.app";

        /// <summary>Base URL of the EQ2 Lexicon site (no trailing slash).</summary>
        public string ServerUrl { get; set; } = DefaultServerUrl;

        /// <summary>
        /// API token from /settings/tokens. Treat as a password. The
        /// in-memory value is plaintext; the on-disk value is DPAPI-
        /// encrypted (current-user scope) — see EncryptToken / DecryptToken.
        /// </summary>
        public string ApiToken { get; set; } = "";

        /// <summary>
        /// When false, the plugin loads but stays inert — no uploads.
        /// Defaults to off so first-time users have to opt in deliberately.
        /// </summary>
        public bool UploadEnabled { get; set; } = false;

        /// <summary>
        /// LEGACY — character-name-only blacklist. v0.1.13 and earlier
        /// stored a flat list of names which matched regardless of EQ2
        /// server, so a player with the same character name on two
        /// servers (e.g. an alt on Wuoshi sharing a name with a main on
        /// Varsoon) couldn't blacklist one without blacklisting both.
        ///
        /// Still serialized as <c>&lt;BlacklistedCharacters&gt;</c> in
        /// the XML so an existing v0.1.13 config file can be loaded
        /// without data loss. On <see cref="Load"/>, any entries here
        /// are migrated into <see cref="BlacklistedEntries"/> (with
        /// Server="" = "any server", preserving the legacy semantics)
        /// and this list is cleared before the next Save persists.
        /// </summary>
        [XmlArray("BlacklistedCharacters")]
        [XmlArrayItem("Character")]
        public List<string> BlacklistedCharacters { get; set; } = new List<string>();

        /// <summary>
        /// Structured blacklist replacing <see cref="BlacklistedCharacters"/>.
        /// Each entry is a (Character, Server) pair. A blank Server
        /// matches any server (i.e. the legacy semantics). Encounters
        /// where ACT's active logging character matches an entry AND
        /// the current server is allowed by that entry are skipped
        /// silently. Match is case-insensitive on both fields.
        /// </summary>
        [XmlArray("BlacklistedEntries")]
        [XmlArrayItem("Entry")]
        public List<BlacklistedEntry> BlacklistedEntries { get; set; } = new List<BlacklistedEntry>();

        /// <summary>
        /// Returns true when <paramref name="characterName"/> on
        /// <paramref name="serverName"/> should NOT upload. Matches:
        /// (a) any BlacklistedEntries row whose Character equals the
        ///     given name AND whose Server is blank (= any) or equals
        ///     the given server name; PLUS
        /// (b) any legacy BlacklistedCharacters entry that equals the
        ///     given name — kept so a unit test that constructs a
        ///     PluginConfig in memory without going through Load()
        ///     still gets the old semantics. After Load() migrates
        ///     them, the legacy list is empty and (b) is a no-op.
        /// </summary>
        public bool IsBlacklisted(string characterName, string serverName)
        {
            if (string.IsNullOrWhiteSpace(characterName)) return false;
            var cName = characterName.Trim();
            var sName = (serverName ?? "").Trim();

            foreach (var entry in BlacklistedEntries)
            {
                if (entry == null) continue;
                if (string.IsNullOrWhiteSpace(entry.Character)) continue;
                if (!string.Equals(entry.Character.Trim(), cName, StringComparison.OrdinalIgnoreCase)) continue;
                // Blank server in the entry means "any server".
                if (string.IsNullOrWhiteSpace(entry.Server)) return true;
                if (string.Equals(entry.Server.Trim(), sName, StringComparison.OrdinalIgnoreCase)) return true;
            }

            // Legacy list — treated as "any server" matches. Empty
            // after Load() runs the migration.
            foreach (var legacy in BlacklistedCharacters)
            {
                if (string.IsNullOrWhiteSpace(legacy)) continue;
                if (string.Equals(legacy.Trim(), cName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // -------------------------------------------------------------------
        // Token encryption (DPAPI, current-user scope)
        // -------------------------------------------------------------------

        // Discriminator written before the base64 ciphertext on disk. Its
        // presence is what tells Load() the value is encrypted (vs a
        // pre-v0.1.5 plaintext leftover).
        private const string EncryptedPrefix = "DPAPI:";

        // Additional entropy mixed into Protect/Unprotect. Not a secret on
        // its own — just makes ciphertext non-portable to other apps using
        // the same DPAPI scope, so an unrelated tool can't trivially decrypt
        // an EQ2 Lexicon token by accident.
        private static readonly byte[] _entropy = Encoding.UTF8.GetBytes("EQ2Lexicon.ACTPlugin v1");

        internal static string EncryptToken(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            var bytes = Encoding.UTF8.GetBytes(plain);
            var protectedBytes = ProtectedData.Protect(bytes, _entropy, DataProtectionScope.CurrentUser);
            return EncryptedPrefix + Convert.ToBase64String(protectedBytes);
        }

        internal static string DecryptToken(string onDisk)
        {
            if (string.IsNullOrEmpty(onDisk)) return "";
            if (!onDisk.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
            {
                // Legacy plaintext token from v0.1.4 or earlier — use as-is.
                // The next Save will re-write it encrypted.
                return onDisk;
            }
            try
            {
                var base64 = onDisk.Substring(EncryptedPrefix.Length);
                var protectedBytes = Convert.FromBase64String(base64);
                var bytes = ProtectedData.Unprotect(protectedBytes, _entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                // Decryption failed — typically because the config was copied
                // from another user account or machine (DPAPI keys are
                // per-user-per-machine). Drop the token; the user will
                // re-enter and re-save.
                return "";
            }
        }

        // -------------------------------------------------------------------
        // Persistence
        // -------------------------------------------------------------------

        public static PluginConfig Load(string path)
        {
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
                    if (loaded == null) return new PluginConfig();
                    // The on-disk ApiToken is either DPAPI-encrypted (new) or
                    // legacy plaintext (pre-v0.1.5). DecryptToken handles both.
                    loaded.ApiToken = DecryptToken(loaded.ApiToken);
                    // One-shot migration: users on the v0.1.10-and-earlier
                    // implicit Railway URL get silently rewritten to the
                    // owned domain. The next Save persists it. We rewrite
                    // ONLY the literal legacy default — if a user has
                    // explicitly typed the Railway URL into the settings
                    // panel they get the same migration, which is fine
                    // (the new URL is just better for them too).
                    if (string.Equals(
                            (loaded.ServerUrl ?? "").TrimEnd('/'),
                            LegacyDefaultServerUrl,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        loaded.ServerUrl = DefaultServerUrl;
                    }
                    loaded.MigrateLegacyBlacklist();
                    return loaded;
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

        /// <summary>
        /// One-shot migration: copy any names from the legacy
        /// <see cref="BlacklistedCharacters"/> list into the
        /// structured <see cref="BlacklistedEntries"/> with Server=""
        /// (= match any server, preserving the v0.1.13 semantics),
        /// then clear the legacy list so the next Save persists only
        /// the new shape. Idempotent — running twice is a no-op
        /// because the second pass sees an empty legacy list.
        ///
        /// De-duplicates: a legacy name that's already represented
        /// (same character, any-server entry) in the new list is
        /// skipped rather than added twice.
        /// </summary>
        internal void MigrateLegacyBlacklist()
        {
            if (BlacklistedCharacters == null || BlacklistedCharacters.Count == 0) return;
            if (BlacklistedEntries == null) BlacklistedEntries = new List<BlacklistedEntry>();

            foreach (var legacy in BlacklistedCharacters)
            {
                if (string.IsNullOrWhiteSpace(legacy)) continue;
                var name = legacy.Trim();
                bool alreadyPresent = BlacklistedEntries.Any(e =>
                    e != null
                    && !string.IsNullOrWhiteSpace(e.Character)
                    && string.IsNullOrWhiteSpace(e.Server)
                    && string.Equals(e.Character.Trim(), name, StringComparison.OrdinalIgnoreCase));
                if (alreadyPresent) continue;
                BlacklistedEntries.Add(new BlacklistedEntry { Character = name, Server = "" });
            }
            BlacklistedCharacters.Clear();
        }

        public void Save(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            // Swap to encrypted form for serialization, then restore the
            // plaintext in memory so the rest of the plugin keeps working.
            var plain = ApiToken;
            ApiToken = EncryptToken(plain);
            try
            {
                var ser = new XmlSerializer(typeof(PluginConfig));
                using (var fs = File.Create(path))
                {
                    ser.Serialize(fs, this);
                }
            }
            finally
            {
                ApiToken = plain;
            }
        }
    }
}
