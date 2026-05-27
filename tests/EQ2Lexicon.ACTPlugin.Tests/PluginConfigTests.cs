using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            Assert.NotNull(cfg.BlacklistedEntries);
            Assert.Empty(cfg.BlacklistedEntries);
        }

        [Fact]
        public void DefaultServerUrl_PointsAtProduction()
        {
            // If this trips, double-check we didn't accidentally ship
            // localhost in a release. Plugin user installs depend on it.
            // The canonical endpoint is the owned subdomain — Railway
            // is the host today but the plugin shouldn't bake that in.
            var cfg = new PluginConfig();
            Assert.Equal("https://parses.eq2lexicon.com", cfg.ServerUrl);
        }

        [Fact]
        public void Load_MigratesLegacyRailwayUrlToOwnedDomain()
        {
            // v0.1.10-and-earlier users had the implicit Railway URL
            // saved in their config XML. On load, we silently rewrite
            // it to the owned subdomain so they're insulated from any
            // future Railway URL change. The next Save persists it.
            var tmp = Path.GetTempFileName();
            try
            {
                // Write a config XML with the LEGACY default — exactly
                // what an upgrading user's on-disk file looks like.
                var legacy = new PluginConfig { ServerUrl = "https://eq2lexicon.up.railway.app" };
                legacy.Save(tmp);

                var loaded = PluginConfig.Load(tmp);
                Assert.Equal("https://parses.eq2lexicon.com", loaded.ServerUrl);
            }
            finally
            {
                File.Delete(tmp);
            }
        }

        [Fact]
        public void Load_DoesNotRewriteCustomServerUrls()
        {
            // Self-hosted dev backend, alternative deployment, anything
            // that ISN'T the legacy Railway default — must be preserved
            // verbatim. The migration is narrowly scoped on purpose.
            var tmp = Path.GetTempFileName();
            try
            {
                var custom = new PluginConfig { ServerUrl = "http://localhost:8000" };
                custom.Save(tmp);

                var loaded = PluginConfig.Load(tmp);
                Assert.Equal("http://localhost:8000", loaded.ServerUrl);
            }
            finally
            {
                File.Delete(tmp);
            }
        }

        [Fact]
        public void Load_MigrationIsTolerantOfTrailingSlash()
        {
            // The Save path TrimEnds slashes, but if a user manually
            // edited their XML to include one, we still want to migrate.
            var tmp = Path.GetTempFileName();
            try
            {
                var legacy = new PluginConfig();
                legacy.Save(tmp);
                // Hand-edit the file to add a trailing slash to the URL.
                var text = File.ReadAllText(tmp);
                text = text.Replace(
                    "https://eq2lexicon.up.railway.app",
                    "https://eq2lexicon.up.railway.app/");
                // (If the saved default already changed away from the
                // legacy URL — which it has — there's nothing to replace.
                // Synthesise the legacy config explicitly instead.)
                text = text.Replace(
                    "<ServerUrl>https://parses.eq2lexicon.com</ServerUrl>",
                    "<ServerUrl>https://eq2lexicon.up.railway.app/</ServerUrl>");
                File.WriteAllText(tmp, text);

                var loaded = PluginConfig.Load(tmp);
                Assert.Equal("https://parses.eq2lexicon.com", loaded.ServerUrl);
            }
            finally
            {
                File.Delete(tmp);
            }
        }

        // ── IsBlacklisted ──────────────────────────────────────────────────
        // Tests cover both the structured BlacklistedEntries (new shape) and
        // the legacy BlacklistedCharacters list (still honoured at query
        // time so configs constructed in-memory pre-Load() behave correctly).

        private static PluginConfig WithEntries(params (string character, string server)[] entries)
        {
            return new PluginConfig
            {
                BlacklistedEntries = entries
                    .Select(e => new BlacklistedEntry { Character = e.character, Server = e.server })
                    .ToList(),
            };
        }

        [Fact]
        public void IsBlacklisted_FalseForEmptyOrNull()
        {
            var cfg = WithEntries(("Bob", ""));
            Assert.False(cfg.IsBlacklisted("", "Varsoon"));
            Assert.False(cfg.IsBlacklisted("   ", "Varsoon"));
            Assert.False(cfg.IsBlacklisted(null!, "Varsoon"));
        }

        [Fact]
        public void IsBlacklisted_TrueForExactMatch_AnyServerEntry()
        {
            // Server="" entry = match any server (legacy semantics).
            var cfg = WithEntries(("Menludiir", ""));
            Assert.True(cfg.IsBlacklisted("Menludiir", "Varsoon"));
            Assert.True(cfg.IsBlacklisted("Menludiir", "Wuoshi"));
            Assert.True(cfg.IsBlacklisted("Menludiir", ""));
        }

        [Fact]
        public void IsBlacklisted_CharacterMatchesButWrongServer_ReturnsFalse()
        {
            // Entry pins Server=Varsoon → a same-named alt on Wuoshi
            // must NOT be blacklisted. The whole point of the schema
            // change.
            var cfg = WithEntries(("Menludiir", "Varsoon"));
            Assert.True(cfg.IsBlacklisted("Menludiir", "Varsoon"));
            Assert.False(cfg.IsBlacklisted("Menludiir", "Wuoshi"));
            Assert.False(cfg.IsBlacklisted("Menludiir", ""));
        }

        [Fact]
        public void IsBlacklisted_MultipleEntriesUnioned()
        {
            // Different entries cover different (char, server) pairs;
            // a query against any one should be blacklisted.
            var cfg = WithEntries(
                ("Menludiir", "Varsoon"),
                ("Sihtric", "Wuoshi"),
                ("Bankerbob", ""));
            Assert.True(cfg.IsBlacklisted("Menludiir", "Varsoon"));
            Assert.True(cfg.IsBlacklisted("Sihtric", "Wuoshi"));
            Assert.True(cfg.IsBlacklisted("Bankerbob", "Varsoon"));   // any-server entry
            Assert.False(cfg.IsBlacklisted("Menludiir", "Wuoshi"));
            Assert.False(cfg.IsBlacklisted("Sihtric", "Varsoon"));
        }

        [Fact]
        public void IsBlacklisted_IsCaseInsensitive_OnBothFields()
        {
            var cfg = WithEntries(("Menludiir", "Varsoon"));
            Assert.True(cfg.IsBlacklisted("menludiir", "varsoon"));
            Assert.True(cfg.IsBlacklisted("MENLUDIIR", "VARSOON"));
            Assert.True(cfg.IsBlacklisted("MenLudiir", "VarSoon"));
        }

        [Fact]
        public void IsBlacklisted_TrimsWhitespace()
        {
            // Defensive: a stray trailing space typed into the editor
            // shouldn't cause the match to silently fail.
            var cfg = WithEntries(("  Menludiir  ", "  Varsoon  "));
            Assert.True(cfg.IsBlacklisted("Menludiir", "Varsoon"));
            Assert.True(cfg.IsBlacklisted("  Menludiir  ", "  Varsoon  "));
        }

        [Fact]
        public void IsBlacklisted_IgnoresEmptyEntriesInList()
        {
            // An entry with a blank Character must NOT act as a wildcard
            // that blacklists every character.
            var cfg = WithEntries(("", "Varsoon"), ("   ", ""), ("Menludiir", ""));
            Assert.False(cfg.IsBlacklisted("Sihtric", "Varsoon"));
            Assert.True(cfg.IsBlacklisted("Menludiir", "Varsoon"));
        }

        [Fact]
        public void IsBlacklisted_NoMatchReturnsFalse()
        {
            var cfg = WithEntries(("Bob", ""), ("Alice", "Varsoon"));
            Assert.False(cfg.IsBlacklisted("Menludiir", "Varsoon"));
        }

        [Fact]
        public void IsBlacklisted_LegacyListStillHonouredAtQueryTime()
        {
            // A config constructed in memory (no Load → no migration)
            // with the legacy property must still produce correct
            // matches — preserves backward compat for code paths that
            // never go through Save/Load.
            var cfg = new PluginConfig { BlacklistedCharacters = new List<string> { "Menludiir" } };
            Assert.True(cfg.IsBlacklisted("Menludiir", "Varsoon"));
            Assert.True(cfg.IsBlacklisted("Menludiir", "Wuoshi"));
            Assert.True(cfg.IsBlacklisted("MENLUDIIR", ""));
        }

        // ── Legacy blacklist migration on Load ─────────────────────────────

        [Fact]
        public void Load_MigratesLegacyBlacklistIntoEntriesWithAnyServer()
        {
            // v0.1.13 config file format on disk — legacy names, no
            // structured entries. After Load(), the legacy list should
            // be empty and BlacklistedEntries should hold the same
            // names as any-server entries.
            var tmp = Path.GetTempFileName();
            try
            {
                var legacyCfg = new PluginConfig
                {
                    BlacklistedCharacters = new List<string> { "Menludiir", "Sihtric" },
                };
                legacyCfg.Save(tmp);

                var loaded = PluginConfig.Load(tmp);
                Assert.Empty(loaded.BlacklistedCharacters);
                Assert.Equal(2, loaded.BlacklistedEntries.Count);
                Assert.Contains(loaded.BlacklistedEntries, e =>
                    e.Character == "Menludiir" && e.Server == "");
                Assert.Contains(loaded.BlacklistedEntries, e =>
                    e.Character == "Sihtric" && e.Server == "");
            }
            finally
            {
                File.Delete(tmp);
            }
        }

        [Fact]
        public void Load_LegacyMigrationDoesNotDuplicateExistingEntries()
        {
            // A config that already has BlacklistedEntries for some
            // names + legacy entries for the same names should NOT
            // end up with two entries per name.
            var tmp = Path.GetTempFileName();
            try
            {
                var cfg = new PluginConfig
                {
                    BlacklistedEntries = new List<BlacklistedEntry>
                    {
                        new BlacklistedEntry { Character = "Menludiir", Server = "" },
                    },
                    BlacklistedCharacters = new List<string> { "Menludiir", "Sihtric" },
                };
                cfg.Save(tmp);

                var loaded = PluginConfig.Load(tmp);
                Assert.Empty(loaded.BlacklistedCharacters);
                // Menludiir already had an any-server entry → not
                // duplicated. Sihtric was new → added.
                Assert.Equal(2, loaded.BlacklistedEntries.Count);
                Assert.Equal(1, loaded.BlacklistedEntries.Count(e =>
                    string.Equals(e.Character, "Menludiir", StringComparison.OrdinalIgnoreCase)));
            }
            finally
            {
                File.Delete(tmp);
            }
        }

        [Fact]
        public void MigrateLegacyBlacklist_IsIdempotent()
        {
            // Run twice → same result as once. Protects against a
            // future caller that re-invokes the migration after the
            // first one already ran.
            var cfg = new PluginConfig
            {
                BlacklistedCharacters = new List<string> { "Menludiir" },
            };
            cfg.MigrateLegacyBlacklist();
            cfg.MigrateLegacyBlacklist();
            Assert.Empty(cfg.BlacklistedCharacters);
            Assert.Single(cfg.BlacklistedEntries);
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
                BlacklistedEntries = new List<BlacklistedEntry>
                {
                    new BlacklistedEntry { Character = "Alt1", Server = "Varsoon" },
                    new BlacklistedEntry { Character = "Alt2", Server = "" },
                },
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
            Assert.Equal(original.BlacklistedEntries.Count, deserialized.BlacklistedEntries.Count);
            for (int i = 0; i < original.BlacklistedEntries.Count; i++)
            {
                Assert.Equal(original.BlacklistedEntries[i].Character, deserialized.BlacklistedEntries[i].Character);
                Assert.Equal(original.BlacklistedEntries[i].Server, deserialized.BlacklistedEntries[i].Server);
            }
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
            Assert.NotNull(deserialized.BlacklistedEntries);
            Assert.Empty(deserialized.BlacklistedEntries);
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
