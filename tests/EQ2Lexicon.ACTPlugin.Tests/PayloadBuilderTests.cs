using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace EQ2Lexicon.ACTPlugin.Tests
{
    public class PayloadBuilderTests
    {
        // ── Helpers ────────────────────────────────────────────────────────

        private static EncounterSnapshot MinimalEncounter(string title = "a krait warrior")
        {
            return new EncounterSnapshot
            {
                EncId = "abcd1234",
                Title = title,
                Zone = "Great Divide",
                StartTime = new DateTime(2026, 5, 24, 17, 41, 33, DateTimeKind.Local),
                EndTime = new DateTime(2026, 5, 24, 17, 41, 45, DateTimeKind.Local),
                Duration = TimeSpan.FromSeconds(12),
                Damage = 9239,
                DPS = 769.9,
                AlliedKills = 1,
                AlliedDeaths = 0,
                SuccessLevel = 1,
            };
        }

        private static CombatantSnapshot Player(string name)
        {
            return new CombatantSnapshot
            {
                Name = name,
                EncStartTime = new DateTime(2026, 5, 24, 17, 41, 33, DateTimeKind.Local),
                EncEndTime = new DateTime(2026, 5, 24, 17, 41, 45, DateTimeKind.Local),
                Duration = TimeSpan.FromSeconds(12),
                Damage = 9239,
                DamagePercent = "100%",
                Kills = 1,
                DPS = 769.9,
                EncDPS = 769.9,
                Hits = 5,
                CritHits = 5,
                Swings = 5,
                ToHit = 100f,
                CritDamPerc = 100f,
            };
        }

        private static CombatantSnapshot Enemy(string name)
        {
            return new CombatantSnapshot
            {
                Name = name,
                DamagePercent = "--",
                HealedPercent = "--",
                DamageTaken = 9239,
                Deaths = 1,
            };
        }

        private static AttackTypeSnapshot Attack(string type, long damage, int hits, int swings = -1)
        {
            return new AttackTypeSnapshot
            {
                Type = type,
                Damage = damage,
                Hits = hits,
                Swings = swings < 0 ? hits : swings,
                MaxHit = damage,
                MinHit = damage,
                Average = damage,
                Median = damage,
                Resist = "divine",
            };
        }

        // ── BuildEncounter ─────────────────────────────────────────────────

        [Fact]
        public void BuildEncounter_PassesThroughAllScalarFields()
        {
            var enc = MinimalEncounter();
            var dict = PayloadBuilder.BuildEncounter(enc);

            Assert.Equal("abcd1234", dict["encid"]);
            Assert.Equal("a krait warrior", dict["title"]);
            Assert.Equal("Great Divide", dict["zone"]);
            Assert.Equal(12, dict["duration"]);
            Assert.Equal(9239L, dict["damage"]);
            Assert.Equal(769.9, dict["encdps"]);
            Assert.Equal(1, dict["kills"]);
            Assert.Equal(0, dict["deaths"]);
            Assert.Equal(1, dict["success"]);
        }

        [Fact]
        public void BuildEncounter_FormatsTimesAsIsoUtcWithZ()
        {
            var enc = MinimalEncounter();
            var dict = PayloadBuilder.BuildEncounter(enc);

            // We don't know the test machine's offset so just check the shape.
            var start = (string)dict["starttime"]!;
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", start);
            var end = (string)dict["endtime"]!;
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", end);
        }

        // ── BuildCombatants ─────────────────────────────────────────────────

        [Fact]
        public void BuildCombatants_MarksAllyTrueWhenInAllyNames()
        {
            var enc = MinimalEncounter();
            enc.Combatants.Add(Player("Menludiir"));
            enc.Combatants.Add(Enemy("a krait warrior"));
            enc.AllyNames.Add("Menludiir");

            var rows = PayloadBuilder.BuildCombatants(enc);

            Assert.Equal(2, rows.Count);
            var menl = rows.Single(r => (string?)r["name"] == "Menludiir");
            var krait = rows.Single(r => (string?)r["name"] == "a krait warrior");
            Assert.Equal("T", menl["ally"]);
            Assert.Equal("F", krait["ally"]);
        }

        [Fact]
        public void BuildCombatants_AllyMatchIsCaseInsensitive()
        {
            // ACT's GetAllies may return names with different casing than
            // EncounterData.Items keys — the AllyNames HashSet uses
            // OrdinalIgnoreCase, so this should still match.
            var enc = MinimalEncounter();
            enc.Combatants.Add(Player("Menludiir"));
            enc.AllyNames.Add("MENLUDIIR");

            var rows = PayloadBuilder.BuildCombatants(enc);

            Assert.Equal("T", rows[0]["ally"]);
        }

        [Fact]
        public void BuildCombatants_EmptyNameNeverAlly()
        {
            // Defensive: a combatant with no name shouldn't accidentally
            // match a stray "" entry in AllyNames.
            var enc = MinimalEncounter();
            enc.Combatants.Add(new CombatantSnapshot { Name = "" });
            enc.AllyNames.Add("");

            var rows = PayloadBuilder.BuildCombatants(enc);

            Assert.Equal("F", rows[0]["ally"]);
        }

        // ── BuildDamageTypes ────────────────────────────────────────────────

        [Fact]
        public void BuildDamageTypes_DropsEmptyCategoryRollups()
        {
            // ACT pre-populates a row per (combatant, category) even when
            // nothing happened — flag them with all-zero counters.
            var enc = MinimalEncounter();
            var c = Player("Menludiir");
            c.DamageTypeAggregates["Skill/Ability (Out)"] = new DamageTypeAggregate
            {
                Type = "Skill/Ability (Out)",
                Damage = 5000,
                Hits = 2,
                Swings = 2,
            };
            c.DamageTypeAggregates["Cure/Dispel (Out)"] = new DamageTypeAggregate
            {
                Type = "Cure/Dispel (Out)",
                // All zero — should be filtered.
            };
            enc.Combatants.Add(c);

            var rows = PayloadBuilder.BuildDamageTypes(enc);

            var types = rows.Select(r => (string?)r["type"]).ToList();
            Assert.Contains("Skill/Ability (Out)", types);
            Assert.DoesNotContain("Cure/Dispel (Out)", types);
        }

        [Fact]
        public void BuildDamageTypes_PassesThroughStats()
        {
            var enc = MinimalEncounter();
            var c = Player("Menludiir");
            c.DamageTypeAggregates["Skill/Ability (Out)"] = new DamageTypeAggregate
            {
                Type = "Skill/Ability (Out)",
                Damage = 5000,
                Hits = 2,
                Swings = 2,
                CritHits = 1,
                EncDPS = 416.7,
                CharDPS = 416.7,
                DPS = 416.7,
                Average = 2500,
                MaxHit = 3000,
                MinHit = 2000,
                CritPerc = 50f,
            };
            enc.Combatants.Add(c);

            var rows = PayloadBuilder.BuildDamageTypes(enc);

            var row = rows.Single();
            Assert.Equal("Menludiir", row["combatant"]);
            Assert.Equal(5000L, row["damage"]);
            Assert.Equal(2, row["hits"]);
            Assert.Equal(2500.0, row["average"]);
            Assert.Equal(50f, row["critperc"]);
        }

        // ── BuildAttackTypes ────────────────────────────────────────────────

        [Fact]
        public void BuildAttackTypes_OnlyIncludesOutgoingCategories()
        {
            // Outgoing → emitted; Incoming → silently dropped.
            var enc = MinimalEncounter();
            var c = Player("Menludiir");
            c.DamageTypeAggregates["Skill/Ability (Out)"] = new DamageTypeAggregate
            {
                Attacks = { Attack("Cleanse", 1500, 1) },
            };
            c.DamageTypeAggregates["Healed (Inc)"] = new DamageTypeAggregate
            {
                Attacks = { Attack("Reverence", 700, 1) },
            };
            enc.Combatants.Add(c);

            var rows = PayloadBuilder.BuildAttackTypes(enc);

            Assert.Single(rows);
            Assert.Equal("Cleanse", rows[0]["type"]);
        }

        [Fact]
        public void BuildAttackTypes_MapsCategoryToSwingType()
        {
            var enc = MinimalEncounter();
            var c = Player("Menludiir");
            c.DamageTypeAggregates["Auto-Attack (Out)"] = new DamageTypeAggregate { Attacks = { Attack("crush", 100, 1) } };
            c.DamageTypeAggregates["Skill/Ability (Out)"] = new DamageTypeAggregate { Attacks = { Attack("Cleanse", 200, 1) } };
            c.DamageTypeAggregates["Healed (Out)"] = new DamageTypeAggregate { Attacks = { Attack("Reverence", 300, 1) } };
            c.DamageTypeAggregates["Cure/Dispel (Out)"] = new DamageTypeAggregate { Attacks = { Attack("Cure", 1, 1) } };
            c.DamageTypeAggregates["Threat (Out)"] = new DamageTypeAggregate { Attacks = { Attack("Undeniable Malice", 5000, 1) } };
            enc.Combatants.Add(c);

            var rows = PayloadBuilder.BuildAttackTypes(enc);

            var byType = rows.ToDictionary(r => (string)r["type"]!, r => (int)r["swingtype"]!);
            Assert.Equal(1, byType["crush"]);
            Assert.Equal(2, byType["Cleanse"]);
            Assert.Equal(3, byType["Reverence"]);
            Assert.Equal(20, byType["Cure"]);
            Assert.Equal(100, byType["Undeniable Malice"]);
        }

        [Fact]
        public void BuildAttackTypes_SkipsAllRollup()
        {
            var enc = MinimalEncounter();
            var c = Player("Menludiir");
            c.DamageTypeAggregates["Skill/Ability (Out)"] = new DamageTypeAggregate
            {
                Attacks =
                {
                    Attack("All", 5000, 3),       // server filters this anyway
                    Attack("Cleanse", 2000, 1),
                    Attack("Smite", 3000, 2),
                },
            };
            enc.Combatants.Add(c);

            var rows = PayloadBuilder.BuildAttackTypes(enc);

            var types = rows.Select(r => (string?)r["type"]).ToList();
            Assert.Equal(2, types.Count);
            Assert.DoesNotContain("All", types);
        }

        [Fact]
        public void BuildAttackTypes_SkipsZeroActivityRows()
        {
            // ACT records "Killing" / similar attempts that did 0 damage with
            // 0 hits, 0 misses, 0 blocked. Payload-bloat, no UI value.
            var enc = MinimalEncounter();
            var c = Player("Menludiir");
            c.DamageTypeAggregates["Skill/Ability (Out)"] = new DamageTypeAggregate
            {
                Attacks =
                {
                    Attack("Cleanse", 2000, 1),
                    new AttackTypeSnapshot { Type = "Killing", Swings = 2 },
                },
            };
            enc.Combatants.Add(c);

            var rows = PayloadBuilder.BuildAttackTypes(enc);

            Assert.Single(rows);
            Assert.Equal("Cleanse", rows[0]["type"]);
        }

        // ── CountAttackTypes ────────────────────────────────────────────────

        [Fact]
        public void CountAttackTypes_MatchesWhatBuildAttackTypesEmits()
        {
            var enc = MinimalEncounter();
            var c = Player("Menludiir");
            c.DamageTypeAggregates["Skill/Ability (Out)"] = new DamageTypeAggregate
            {
                Attacks =
                {
                    Attack("Cleanse", 2000, 1),
                    Attack("Smite", 3000, 2),
                    Attack("All", 5000, 3),                              // skipped
                    new AttackTypeSnapshot { Type = "Killing", Swings = 2 }, // skipped (zero activity)
                },
            };
            c.DamageTypeAggregates["Healed (Inc)"] = new DamageTypeAggregate
            {
                Attacks = { Attack("Reverence", 700, 1) },               // skipped (incoming)
            };
            enc.Combatants.Add(c);

            Assert.Equal(PayloadBuilder.BuildAttackTypes(enc).Count, PayloadBuilder.CountAttackTypes(enc));
            Assert.Equal(2, PayloadBuilder.CountAttackTypes(enc));
        }

        // ── FormatTime ─────────────────────────────────────────────────────

        [Fact]
        public void FormatTime_MinValueReturnsEmptyString()
        {
            Assert.Equal("", PayloadBuilder.FormatTime(DateTime.MinValue));
        }

        [Fact]
        public void FormatTime_EmitsIsoWithZSuffix()
        {
            // Pass an explicit UTC time so the result is deterministic.
            var t = new DateTime(2026, 5, 24, 17, 41, 33, DateTimeKind.Utc);
            Assert.Equal("2026-05-24T17:41:33Z", PayloadBuilder.FormatTime(t));
        }

        [Fact]
        public void FormatTime_ConvertsLocalToUtc()
        {
            // Local time → ToUniversalTime applies the machine's offset.
            // Round-trip via the same conversion to make the test
            // timezone-agnostic.
            var local = new DateTime(2026, 5, 24, 17, 41, 33, DateTimeKind.Local);
            var expected = local.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            Assert.Equal(expected, PayloadBuilder.FormatTime(local));
        }

        [Fact]
        public void FormatTime_UnspecifiedTreatedAsLocal()
        {
            // ACT's DateTime values are Unspecified in practice. We rely on
            // ToUniversalTime treating Unspecified as Local — verify that.
            var unspecified = new DateTime(2026, 5, 24, 17, 41, 33, DateTimeKind.Unspecified);
            var asLocal = DateTime.SpecifyKind(unspecified, DateTimeKind.Local);
            Assert.Equal(
                asLocal.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                PayloadBuilder.FormatTime(unspecified));
        }

        // ── SanitizePayload ────────────────────────────────────────────────

        [Fact]
        public void SanitizePayload_ReplacesNaNDouble()
        {
            var dict = new Dictionary<string, object?> { ["x"] = double.NaN, ["y"] = 3.0 };
            PayloadBuilder.SanitizePayload(dict);
            Assert.Equal(0.0, dict["x"]);
            Assert.Equal(3.0, dict["y"]);
        }

        [Fact]
        public void SanitizePayload_ReplacesInfinityDouble()
        {
            var dict = new Dictionary<string, object?> { ["x"] = double.PositiveInfinity, ["y"] = double.NegativeInfinity };
            PayloadBuilder.SanitizePayload(dict);
            Assert.Equal(0.0, dict["x"]);
            Assert.Equal(0.0, dict["y"]);
        }

        [Fact]
        public void SanitizePayload_ReplacesNaNFloat()
        {
            var dict = new Dictionary<string, object?> { ["x"] = float.NaN };
            PayloadBuilder.SanitizePayload(dict);
            Assert.Equal(0f, dict["x"]);
        }

        [Fact]
        public void SanitizePayload_DescendsIntoNestedDictsAndLists()
        {
            var dict = new Dictionary<string, object?>
            {
                ["nested"] = new Dictionary<string, object?> { ["bad"] = double.NaN },
                ["list"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["bad"] = float.NaN },
                },
            };
            PayloadBuilder.SanitizePayload(dict);

            var nested = (Dictionary<string, object?>)dict["nested"]!;
            Assert.Equal(0.0, nested["bad"]);
            var list = (List<object?>)dict["list"]!;
            var item0 = (Dictionary<string, object?>)list[0]!;
            Assert.Equal(0f, item0["bad"]);
        }

        [Fact]
        public void SanitizePayload_LeavesIntsAndStringsAlone()
        {
            var dict = new Dictionary<string, object?>
            {
                ["int"] = 42,
                ["long"] = 99L,
                ["str"] = "NaN",   // string, not a number
                ["bool"] = true,
            };
            PayloadBuilder.SanitizePayload(dict);
            Assert.Equal(42, dict["int"]);
            Assert.Equal(99L, dict["long"]);
            Assert.Equal("NaN", dict["str"]);
            Assert.Equal(true, dict["bool"]);
        }

        // ── End-to-end shape check ─────────────────────────────────────────

        [Fact]
        public void BuildPayload_ProducesEveryTopLevelKey()
        {
            var enc = MinimalEncounter();
            enc.Combatants.Add(Player("Menludiir"));
            enc.AllyNames.Add("Menludiir");

            var payload = PayloadBuilder.BuildPayload("Menludiir", "Varsoon", enc);

            Assert.Equal("Menludiir", payload["logger_name"]);
            Assert.Equal("Varsoon", payload["logger_server"]);
            Assert.IsType<Dictionary<string, object?>>(payload["encounter"]);
            Assert.IsType<List<Dictionary<string, object?>>>(payload["combatants"]);
            Assert.IsType<List<Dictionary<string, object?>>>(payload["damage_types"]);
            Assert.IsType<List<Dictionary<string, object?>>>(payload["attack_types"]);
        }

        [Fact]
        public void BuildPayload_EmptyServerSentAsEmptyString()
        {
            // The plugin sends "" when the log path doesn't fit the
            // per-server layout (legacy generic log, no log picked up
            // yet). Server reads "" as "fall back to EQ2_WORLD".
            // Pinning the wire shape so the server's contract stays
            // unambiguous: it's always a string, never missing/null.
            var enc = MinimalEncounter();
            enc.Combatants.Add(Player("Menludiir"));
            enc.AllyNames.Add("Menludiir");

            var payload = PayloadBuilder.BuildPayload("Menludiir", "", enc);
            Assert.Equal("", payload["logger_server"]);

            // Null defensiveness — caller shouldn't pass null but if
            // they do we must not crash or emit JSON null.
            var payload2 = PayloadBuilder.BuildPayload("Menludiir", null!, enc);
            Assert.Equal("", payload2["logger_server"]);
        }

        // ── OutgoingGroupToSwingType constant ───────────────────────────────

        [Fact]
        public void OutgoingGroupToSwingType_LookupIsCaseInsensitive()
        {
            // Defensive: ACT key casing has shifted between versions.
            Assert.True(PayloadBuilder.OutgoingGroupToSwingType.ContainsKey("auto-attack (out)"));
            Assert.True(PayloadBuilder.OutgoingGroupToSwingType.ContainsKey("SKILL/ABILITY (OUT)"));
        }
    }
}
