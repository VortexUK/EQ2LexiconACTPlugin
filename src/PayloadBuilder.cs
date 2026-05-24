using System;
using System.Collections.Generic;

namespace EQ2Lexicon.ACTPlugin
{
    /// <summary>
    /// Pure transformation from <see cref="EncounterSnapshot"/> to the dict
    /// shape the EQ2 Lexicon server's /api/parses/ingest endpoint expects.
    /// All methods are static and side-effect-free — designed to be unit
    /// tested without ACT.
    /// </summary>
    public static class PayloadBuilder
    {
        /// <summary>
        /// ACT's combatant.Items keys identify the category of each
        /// AttackType grouping. The swing_type is implicit in the category —
        /// AttackType itself doesn't expose SwingType as a property at this
        /// version. These values match what ACT's ODBC export plugin writes
        /// into attacktype_table.swingtype.
        ///
        /// Aggregate rollups ("Outgoing Damage", "All Outgoing") and
        /// incoming groupings ("Healed (Inc)" etc.) are deliberately
        /// absent — incoming would double-count what other combatants
        /// already reported as outgoing.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, int> OutgoingGroupToSwingType =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "Auto-Attack (Out)", 1 },
            { "Skill/Ability (Out)", 2 },
            { "Healed (Out)", 3 },
            { "Cure/Dispel (Out)", 20 },
            { "Threat (Out)", 100 },
        };

        public static Dictionary<string, object?> BuildPayload(string loggerName, EncounterSnapshot enc)
        {
            return new Dictionary<string, object?>
            {
                ["logger_name"] = loggerName,
                ["encounter"] = BuildEncounter(enc),
                ["combatants"] = BuildCombatants(enc),
                ["damage_types"] = BuildDamageTypes(enc),
                ["attack_types"] = BuildAttackTypes(enc),
            };
        }

        public static Dictionary<string, object?> BuildEncounter(EncounterSnapshot enc)
        {
            return new Dictionary<string, object?>
            {
                ["encid"] = enc.EncId,
                ["title"] = enc.Title,
                ["zone"] = enc.Zone,
                ["starttime"] = FormatTime(enc.StartTime),
                ["endtime"] = FormatTime(enc.EndTime),
                ["duration"] = (int)enc.Duration.TotalSeconds,
                ["damage"] = enc.Damage,
                ["encdps"] = enc.DPS,
                ["kills"] = enc.AlliedKills,
                ["deaths"] = enc.AlliedDeaths,
                ["success"] = enc.SuccessLevel,
            };
        }

        public static List<Dictionary<string, object?>> BuildCombatants(EncounterSnapshot enc)
        {
            var result = new List<Dictionary<string, object?>>();
            foreach (var c in enc.Combatants)
            {
                var isAlly = !string.IsNullOrEmpty(c.Name) && enc.AllyNames.Contains(c.Name);
                result.Add(new Dictionary<string, object?>
                {
                    ["name"] = c.Name,
                    ["ally"] = isAlly ? "T" : "F",
                    ["starttime"] = FormatTime(c.EncStartTime),
                    ["endtime"] = FormatTime(c.EncEndTime),
                    ["duration"] = (int)c.Duration.TotalSeconds,
                    ["damage"] = c.Damage,
                    ["damageperc"] = c.DamagePercent,
                    ["kills"] = c.Kills,
                    ["healed"] = c.Healed,
                    ["healedperc"] = c.HealedPercent,
                    ["critheals"] = c.CritHeals,
                    ["heals"] = c.Heals,
                    ["curedispels"] = c.CureDispels,
                    ["powerdrain"] = c.PowerDamage,
                    ["powerreplenish"] = c.PowerReplenish,
                    ["dps"] = c.DPS,
                    ["encdps"] = c.EncDPS,
                    ["enchps"] = c.EncHPS,
                    ["hits"] = c.Hits,
                    ["crithits"] = c.CritHits,
                    ["blocked"] = c.Blocked,
                    ["misses"] = c.Misses,
                    ["swings"] = c.Swings,
                    ["healstaken"] = c.HealsTaken,
                    ["damagetaken"] = c.DamageTaken,
                    ["deaths"] = c.Deaths,
                    ["tohit"] = c.ToHit,
                    ["critdamperc"] = c.CritDamPerc,
                    ["crithealperc"] = c.CritHealPerc,
                    // crittypes/threatstr/threatdelta not cleanly exposed on
                    // CombatantData at this ACT version — server accepts as optional.
                    ["crittypes"] = "",
                    ["threatstr"] = "",
                    ["threatdelta"] = 0,
                });
            }
            return result;
        }

        /// <summary>
        /// Per-category aggregate rollups for the "By Type" tab. ACT
        /// pre-populates a row per category per combatant even if nothing
        /// happened — those have StartTime=DateTime.MaxValue and every
        /// counter at zero. Drop them to keep payloads slim.
        /// </summary>
        public static List<Dictionary<string, object?>> BuildDamageTypes(EncounterSnapshot enc)
        {
            var result = new List<Dictionary<string, object?>>();
            foreach (var c in enc.Combatants)
            {
                foreach (var kv in c.DamageTypeAggregates)
                {
                    var dt = kv.Value;
                    if (dt == null) continue;
                    var typeName = !string.IsNullOrEmpty(dt.Type) ? dt.Type : kv.Key;
                    if (string.IsNullOrEmpty(typeName)) continue;
                    if (dt.Damage == 0 && dt.Hits == 0 && dt.Swings == 0
                        && dt.Misses == 0 && dt.Blocked == 0) continue;

                    result.Add(new Dictionary<string, object?>
                    {
                        ["combatant"] = c.Name,
                        ["grouping"] = "",
                        ["type"] = typeName,
                        ["starttime"] = FormatTime(dt.StartTime),
                        ["endtime"] = FormatTime(dt.EndTime),
                        ["duration"] = (int)dt.Duration.TotalSeconds,
                        ["damage"] = dt.Damage,
                        ["encdps"] = dt.EncDPS,
                        ["chardps"] = dt.CharDPS,
                        ["dps"] = dt.DPS,
                        ["average"] = dt.Average,
                        ["median"] = dt.Median,
                        ["minhit"] = dt.MinHit,
                        ["maxhit"] = dt.MaxHit,
                        ["hits"] = dt.Hits,
                        ["crithits"] = dt.CritHits,
                        ["blocked"] = dt.Blocked,
                        ["misses"] = dt.Misses,
                        ["swings"] = dt.Swings,
                        ["tohit"] = dt.ToHit,
                        ["averagedelay"] = dt.AverageDelay,
                        ["critperc"] = dt.CritPerc,
                        ["crittypes"] = "",
                    });
                }
            }
            return result;
        }

        /// <summary>
        /// Per-ability rows. Only the outgoing categories in
        /// <see cref="OutgoingGroupToSwingType"/> are emitted (incoming
        /// would double-count). The 'All' rollup row inside each category
        /// is skipped, as are zero-activity entries.
        /// </summary>
        public static List<Dictionary<string, object?>> BuildAttackTypes(EncounterSnapshot enc)
        {
            var result = new List<Dictionary<string, object?>>();
            foreach (var c in enc.Combatants)
            {
                foreach (var kv in c.DamageTypeAggregates)
                {
                    if (!OutgoingGroupToSwingType.TryGetValue(kv.Key, out var swingType))
                        continue;
                    var agg = kv.Value;
                    if (agg?.Attacks == null) continue;
                    foreach (var at in agg.Attacks)
                    {
                        if (at == null) continue;
                        if (string.Equals(at.Type, "All", StringComparison.OrdinalIgnoreCase)) continue;
                        if (at.Damage == 0 && at.Hits == 0 && at.Misses == 0 && at.Blocked == 0) continue;

                        result.Add(new Dictionary<string, object?>
                        {
                            ["attacker"] = c.Name,
                            ["victim"] = "",
                            ["swingtype"] = swingType,
                            ["type"] = at.Type,
                            ["starttime"] = FormatTime(at.StartTime),
                            ["endtime"] = FormatTime(at.EndTime),
                            ["duration"] = (int)at.Duration.TotalSeconds,
                            ["damage"] = at.Damage,
                            ["encdps"] = at.EncDPS,
                            ["chardps"] = at.CharDPS,
                            ["dps"] = at.DPS,
                            ["average"] = at.Average,
                            ["median"] = at.Median,
                            ["minhit"] = at.MinHit,
                            ["maxhit"] = at.MaxHit,
                            ["resist"] = at.Resist,
                            ["hits"] = at.Hits,
                            ["crithits"] = at.CritHits,
                            ["blocked"] = at.Blocked,
                            ["misses"] = at.Misses,
                            ["swings"] = at.Swings,
                            ["tohit"] = at.ToHit,
                            ["averagedelay"] = at.AverageDelay,
                            ["critperc"] = at.CritPerc,
                            ["crittypes"] = "",
                        });
                    }
                }
            }
            return result;
        }

        /// <summary>Counts the attack-type rows that <see cref="BuildAttackTypes"/> would emit.</summary>
        public static int CountAttackTypes(EncounterSnapshot enc)
        {
            int count = 0;
            foreach (var c in enc.Combatants)
            {
                foreach (var kv in c.DamageTypeAggregates)
                {
                    if (!OutgoingGroupToSwingType.ContainsKey(kv.Key)) continue;
                    var agg = kv.Value;
                    if (agg?.Attacks == null) continue;
                    foreach (var at in agg.Attacks)
                    {
                        if (at == null) continue;
                        if (string.Equals(at.Type, "All", StringComparison.OrdinalIgnoreCase)) continue;
                        if (at.Damage == 0 && at.Hits == 0 && at.Misses == 0 && at.Blocked == 0) continue;
                        count++;
                    }
                }
            }
            return count;
        }

        /// <summary>
        /// ACT timestamps come from EQ2's log file, which the client writes
        /// in the player's local clock. Convert to UTC and tag with a 'Z'
        /// suffix so the server stores an absolute instant. <see cref="DateTime.MinValue"/>
        /// (ACT's "never happened" sentinel for unfired categories) maps to
        /// the empty string. ToUniversalTime() treats <see cref="DateTimeKind.Unspecified"/>
        /// as Local, which is what we want here.
        /// </summary>
        public static string FormatTime(DateTime t)
        {
            if (t == DateTime.MinValue) return "";
            var utc = t.Kind == DateTimeKind.Utc ? t : t.ToUniversalTime();
            return utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        }

        /// <summary>
        /// ACT returns NaN for any computed stat that divided by zero
        /// (e.g. a miss-only attempt → average = 0/0). The .NET
        /// JavaScriptSerializer happily emits the literal "NaN" — but that
        /// is invalid JSON, so the server's json.loads rejects the upload.
        /// Walk the payload once and swap any non-finite double/float for 0.
        /// </summary>
        public static void SanitizePayload(object? node)
        {
            if (node is IDictionary<string, object?> dict)
            {
                var keys = new List<string>(dict.Keys);
                foreach (var key in keys)
                {
                    var v = dict[key];
                    if (v is double d && (double.IsNaN(d) || double.IsInfinity(d)))
                    {
                        dict[key] = 0.0;
                    }
                    else if (v is float f && (float.IsNaN(f) || float.IsInfinity(f)))
                    {
                        dict[key] = 0f;
                    }
                    else
                    {
                        SanitizePayload(v);
                    }
                }
            }
            else if (node is System.Collections.IList list)
            {
                foreach (var item in list) SanitizePayload(item);
            }
        }
    }
}
