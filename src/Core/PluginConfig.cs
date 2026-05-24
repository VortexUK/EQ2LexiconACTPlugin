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
        /// <summary>Base URL of the EQ2 Lexicon site (no trailing slash).</summary>
        public string ServerUrl { get; set; } = "https://eq2lexicon.up.railway.app";

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
