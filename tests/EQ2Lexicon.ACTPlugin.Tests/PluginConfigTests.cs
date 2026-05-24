using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using Xunit;

namespace EQ2Lexicon.ACTPlugin.Tests
{
    public class PluginConfigTests
    {
        // ── Defaults ───────────────────────────────────────────────────────

        [Fact]
        public void Defaults_AreSafe()
        {
            // First-time users must opt in to uploads — never default-on.
            var cfg = new PluginConfig();
            Assert.False(cfg.UploadEnabled);
            Assert.Equal("", cfg.ApiToken);
            Assert.NotNull(cfg.BlacklistedCharacters);
            Assert.Empty(cfg.BlacklistedCharacters);
        }

        [Fact]
        public void DefaultServerUrl_PointsAtProduction()
        {
            // If this trips, double-check we didn't accidentally ship
            // localhost in a release. Plugin user installs depend on it.
            var cfg = new PluginConfig();
            Assert.Equal("https://eq2lexicon.up.railway.app", cfg.ServerUrl);
        }

        // ── IsBlacklisted ──────────────────────────────────────────────────

        [Fact]
        public void IsBlacklisted_FalseForEmptyOrNull()
        {
            var cfg = new PluginConfig { BlacklistedCharacters = new List<string> { "Bob" } };
            Assert.False(cfg.IsBlacklisted(""));
            Assert.False(cfg.IsBlacklisted("   "));
            Assert.False(cfg.IsBlacklisted(null!));
        }

        [Fact]
        public void IsBlacklisted_TrueForExactMatch()
        {
            var cfg = new PluginConfig { BlacklistedCharacters = new List<string> { "Menludiir" } };
            Assert.True(cfg.IsBlacklisted("Menludiir"));
        }

        [Fact]
        public void IsBlacklisted_IsCaseInsensitive()
        {
            var cfg = new PluginConfig { BlacklistedCharacters = new List<string> { "Menludiir" } };
            Assert.True(cfg.IsBlacklisted("menludiir"));
            Assert.True(cfg.IsBlacklisted("MENLUDIIR"));
        }

        [Fact]
        public void IsBlacklisted_TrimsWhitespace()
        {
            // Defensive: users editing the textbox can easily leave a
            // trailing space when they delete a name and the blank line
            // ends up persisted.
            var cfg = new PluginConfig { BlacklistedCharacters = new List<string> { "  Menludiir  " } };
            Assert.True(cfg.IsBlacklisted("Menludiir"));
            Assert.True(cfg.IsBlacklisted("  Menludiir  "));
        }

        [Fact]
        public void IsBlacklisted_IgnoresEmptyEntriesInList()
        {
            // Empty-string and whitespace entries must not act as a wildcard
            // that blacklists every character.
            var cfg = new PluginConfig { BlacklistedCharacters = new List<string> { "", "   ", "Menludiir" } };
            Assert.False(cfg.IsBlacklisted("Sihtric"));
            Assert.True(cfg.IsBlacklisted("Menludiir"));
        }

        [Fact]
        public void IsBlacklisted_NoMatchReturnsFalse()
        {
            var cfg = new PluginConfig { BlacklistedCharacters = new List<string> { "Bob", "Alice" } };
            Assert.False(cfg.IsBlacklisted("Menludiir"));
        }

        // ── XML serialization roundtrip ────────────────────────────────────
        // PluginConfig.Save/Load rely on ACT's AppDataFolder; we can't call
        // them in tests. Instead, exercise the same XmlSerializer contract
        // directly — that's what catches an [XmlIgnore]/[XmlArray]
        // attribute regression that would lose data on disk.

        [Fact]
        public void XmlRoundtrip_PreservesAllFields()
        {
            var original = new PluginConfig
            {
                ServerUrl = "https://example.test",
                ApiToken = "eq2c_secret_token",
                UploadEnabled = true,
                BlacklistedCharacters = new List<string> { "Alt1", "Alt2" },
            };

            PluginConfig deserialized;
            var ser = new XmlSerializer(typeof(PluginConfig));
            using (var ms = new MemoryStream())
            {
                ser.Serialize(ms, original);
                ms.Position = 0;
                deserialized = (PluginConfig)ser.Deserialize(ms)!;
            }

            Assert.Equal(original.ServerUrl, deserialized.ServerUrl);
            Assert.Equal(original.ApiToken, deserialized.ApiToken);
            Assert.Equal(original.UploadEnabled, deserialized.UploadEnabled);
            Assert.Equal(original.BlacklistedCharacters, deserialized.BlacklistedCharacters);
        }

        [Fact]
        public void XmlRoundtrip_EmptyBlacklistStillRoundtrips()
        {
            var original = new PluginConfig();
            PluginConfig deserialized;
            var ser = new XmlSerializer(typeof(PluginConfig));
            using (var ms = new MemoryStream())
            {
                ser.Serialize(ms, original);
                ms.Position = 0;
                deserialized = (PluginConfig)ser.Deserialize(ms)!;
            }
            Assert.NotNull(deserialized.BlacklistedCharacters);
            Assert.Empty(deserialized.BlacklistedCharacters);
        }

        // ── DPAPI token encryption ─────────────────────────────────────────
        // DPAPI is per-user / per-machine, so these tests pass on whatever
        // dev machine runs them. They do *not* exercise cross-user behaviour.

        [Fact]
        public void EncryptToken_RoundtripsViaDecryptToken()
        {
            var plain = "eq2c_xfpgeXZbh0AoOyIw7Rfnm98CV3RTxEgy";
            var encrypted = PluginConfig.EncryptToken(plain);
            Assert.NotEqual(plain, encrypted);
            Assert.StartsWith("DPAPI:", encrypted);
            Assert.Equal(plain, PluginConfig.DecryptToken(encrypted));
        }

        [Fact]
        public void EncryptToken_EmptyStaysEmpty()
        {
            // Don't encrypt empty — keeps the XML output stable for users
            // who haven't yet set a token.
            Assert.Equal("", PluginConfig.EncryptToken(""));
            Assert.Equal("", PluginConfig.EncryptToken(null!));
        }

        [Fact]
        public void DecryptToken_LegacyPlaintextReturnsAsIs()
        {
            // Pre-v0.1.5 configs on disk have plaintext tokens with no
            // DPAPI: prefix. Loading those must still work; the next Save
            // will then re-write them encrypted.
            var legacy = "eq2c_legacy_token_no_prefix";
            Assert.Equal(legacy, PluginConfig.DecryptToken(legacy));
        }

        [Fact]
        public void DecryptToken_GarbageReturnsEmpty()
        {
            // Encrypted blob from a different user / machine / corrupted
            // bytes — Unprotect throws, we swallow and return empty so the
            // plugin loads with an empty token (user re-enters and re-saves).
            Assert.Equal("", PluginConfig.DecryptToken("DPAPI:not-real-base64!"));
            Assert.Equal("", PluginConfig.DecryptToken("DPAPI:" + System.Convert.ToBase64String(new byte[] { 1, 2, 3, 4 })));
        }

        [Fact]
        public void DecryptToken_EmptyStaysEmpty()
        {
            Assert.Equal("", PluginConfig.DecryptToken(""));
            Assert.Equal("", PluginConfig.DecryptToken(null!));
        }

        [Fact]
        public void EncryptToken_ProducesDifferentCiphertextEachCall()
        {
            // DPAPI mixes in a random IV per call, so the same plaintext
            // shouldn't produce identical ciphertext twice. This is a small
            // hedge against a future "compare ciphertexts to detect token
            // reuse" attack.
            var plain = "eq2c_some_token";
            var a = PluginConfig.EncryptToken(plain);
            var b = PluginConfig.EncryptToken(plain);
            Assert.NotEqual(a, b);
            // But both decrypt back to the same plain text.
            Assert.Equal(plain, PluginConfig.DecryptToken(a));
            Assert.Equal(plain, PluginConfig.DecryptToken(b));
        }
    }
}
