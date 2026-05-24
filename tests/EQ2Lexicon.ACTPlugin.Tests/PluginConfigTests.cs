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
    }
}
