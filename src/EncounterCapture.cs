using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using Advanced_Combat_Tracker;

namespace EQ2Lexicon.ACTPlugin
{
    /// <summary>
    /// Polls ACT's active zone every couple of seconds. When the most recent
    /// encounter has been closed by ACT (EndTime set) and we haven't seen
    /// it before, walks the EncounterData into the JSON shape our server's
    /// /api/parses/ingest endpoint expects.
    ///
    /// This commit only CAPTURES — there's no HTTP upload here. The captured
    /// payload is exposed via LastCapturedPayloadJson / LastCapturedSummary
    /// for the settings panel to display, so we can verify the shape before
    /// wiring the upload in B2.3.
    ///
    /// Threading: Timer fires on a worker thread. We do all the walking on
    /// that thread (safe because the encounter is closed — ACT won't mutate
    /// it further). On completion we raise OnCaptured; subscribers that need
    /// to touch the UI must marshal back to the UI thread themselves.
    /// </summary>
    internal class EncounterCapture : IDisposable
    {
        private readonly Timer _timer;
        private readonly TimeSpan _interval = TimeSpan.FromSeconds(2);
        private readonly object _lock = new object();
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer
        {
            // Allow large payloads — raid encounters with many combatants
            // can produce JSON in the hundreds of KB.
            MaxJsonLength = 8 * 1024 * 1024,
        };

        // Track which encids we've already processed so we don't re-emit.
        // Keyed by encid; value unused. Capped via _processedQueue eviction
        // to avoid unbounded growth across long ACT sessions.
        private readonly HashSet<string> _processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<string> _processedQueue = new Queue<string>();
        private const int ProcessedCap = 500;

        // "Settled" threshold: ACT advances EncounterData.EndTime to "time
        // of last combat action" each time a swing/cast is added. While a
        // fight is in progress, EndTime is always ~now; when combat stops,
        // it freezes. Waiting SettleSeconds after the last EndTime update
        // gives ACT time to merge any late actions into the same encid
        // before we upload — otherwise we'd upload mid-fight snapshots.
        private const double SettleSeconds = 15.0;

        // Last captured artefact — read by the settings panel for display.
        public string LastCapturedEncId { get; private set; } = "";
        public string LastCapturedTitle { get; private set; } = "";
        public DateTime LastCapturedAt { get; private set; } = DateTime.MinValue;
        public int LastCombatantCount { get; private set; }
        public int LastAttackTypeCount { get; private set; }
        public string LastCapturedPayloadJson { get; private set; } = "";

        /// <summary>Raised after a successful capture. Off-UI-thread.</summary>
        public event Action? OnCaptured;

        public EncounterCapture()
        {
            _timer = new Timer(_ => PollSafe(), null, _interval, _interval);
        }

        public void Dispose()
        {
            _timer.Dispose();
        }

        private void PollSafe()
        {
            try { Poll(); }
            catch (Exception)
            {
                // Swallow — never let a capture failure crash ACT.
                // TODO: surface errors in the UI status area.
            }
        }

        private void Poll()
        {
            var zone = ActGlobals.oFormActMain?.ActiveZone;
            if (zone?.Items == null || zone.Items.Count == 0) return;

            // Walk every encounter in the zone, not just the latest — handles
            // back-to-back fights where an earlier one settled while the next
            // was already in progress.
            var now = DateTime.Now;
            for (int i = 0; i < zone.Items.Count; i++)
            {
                var enc = zone.Items[i];
                if (enc == null) continue;
                // ACT puts a zone-aggregate pseudo-encounter (Title="All") at
                // the front of zone.Items that sums every fight in the zone.
                // Never upload it — it duplicates data and has no real mob.
                if (string.Equals(enc.Title, "All", StringComparison.OrdinalIgnoreCase)) continue;
                if (enc.EndTime == DateTime.MinValue) continue;
                var encid = enc.EncId ?? "";
                if (string.IsNullOrEmpty(encid)) continue;
                if (enc.Items == null || enc.Items.Count == 0) continue;

                lock (_lock)
                {
                    if (_processed.Contains(encid)) continue;
                }

                // Not settled yet — ACT may still be appending actions.
                if ((now - enc.EndTime).TotalSeconds < SettleSeconds) continue;

                ProcessEncounter(enc, encid);
            }
        }

        private void ProcessEncounter(EncounterData enc, string encid)
        {
            // Build the payload before claiming the encid — if walking
            // throws, we want to retry on the next tick rather than
            // silently swallow.
            var payload = BuildPayload(enc);
            SanitizePayload(payload);
            var json = _serializer.Serialize(payload);

            lock (_lock)
            {
                _processed.Add(encid);
                _processedQueue.Enqueue(encid);
                while (_processedQueue.Count > ProcessedCap)
                {
                    _processed.Remove(_processedQueue.Dequeue());
                }

                LastCapturedEncId = encid;
                LastCapturedTitle = enc.Title ?? "";
                LastCapturedAt = DateTime.Now;
                LastCombatantCount = enc.Items?.Count ?? 0;
                LastAttackTypeCount = CountAttackTypes(enc);
                LastCapturedPayloadJson = json;
            }

            OnCaptured?.Invoke();
        }

        // -------------------------------------------------------------------
        // Payload construction
        // -------------------------------------------------------------------

        private Dictionary<string, object?> BuildPayload(EncounterData enc)
        {
            return new Dictionary<string, object?>
            {
                ["logger_name"] = ActHelpers.GetLoggingCharacterName(),
                ["encounter"] = BuildEncounter(enc),
                ["combatants"] = BuildCombatants(enc),
                ["damage_types"] = BuildDamageTypes(enc),
                ["attack_types"] = BuildAttackTypes(enc),
            };
        }

        private Dictionary<string, object?> BuildEncounter(EncounterData enc)
        {
            return new Dictionary<string, object?>
            {
                ["encid"] = enc.EncId ?? "",
                ["title"] = enc.Title ?? "",
                ["zone"] = enc.ZoneName,
                ["starttime"] = FormatTime(enc.StartTime),
                ["endtime"] = FormatTime(enc.EndTime),
                ["duration"] = (int)enc.Duration.TotalSeconds,
                ["damage"] = enc.Damage,
                ["encdps"] = enc.DPS,
                // Previously mis-wired: kills was the success-level enum, and
                // deaths was AlliedKills. Now using ACT's actual counters.
                ["kills"] = enc.AlliedKills,
                ["deaths"] = enc.AlliedDeaths,
                // ACT's outcome enum. Known values: 0 = unknown / no result,
                // 1 = allies killed the enemy (win), 2 = enemy killed allies
                // (loss), 3 = both sides took kills. Frontend uses this to
                // colour the encounter title on /parses.
                ["success"] = enc.GetEncounterSuccessLevel(),
            };
        }

        private List<Dictionary<string, object?>> BuildCombatants(EncounterData enc)
        {
            var result = new List<Dictionary<string, object?>>();
            if (enc.Items == null) return result;

            // Use ACT's own ally classification (EncounterData.GetAllies()).
            // The EQ2 ACT parser already attributes pets to their owner, so
            // they show up in the allies list correctly — no name-shape
            // heuristics needed.
            var allyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var ally in enc.GetAllies())
                {
                    if (ally?.Name != null) allyNames.Add(ally.Name);
                }
            }
            catch { /* fall back to all-enemy if ACT throws on this encounter */ }

            foreach (var kv in enc.Items)
            {
                var c = kv.Value;
                if (c == null) continue;
                var name = c.Name ?? "";
                var isAlly = !string.IsNullOrEmpty(name) && allyNames.Contains(name);
                result.Add(new Dictionary<string, object?>
                {
                    ["name"] = name,
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
                    // crittypes/threatstr/threatdelta aren't exposed cleanly
                    // on CombatantData at the version we target — server
                    // accepts these as optional. Revisit in B2.4 polish.
                    ["crittypes"] = "",
                    ["threatstr"] = "",
                    ["threatdelta"] = 0,
                });
            }
            return result;
        }

        // Per-category aggregate rollups for the "By Type" tab. ACT exposes
        // these as combatant.Items[<category>] where each value is a
        // DamageTypeData with summary stats already aggregated. We send
        // every category (incoming + outgoing + rollups) — the UI groups
        // by name and the server stores them verbatim.
        private List<Dictionary<string, object?>> BuildDamageTypes(EncounterData enc)
        {
            var result = new List<Dictionary<string, object?>>();
            if (enc.Items == null) return result;

            foreach (var combatantKv in enc.Items)
            {
                var combatant = combatantKv.Value;
                if (combatant?.Items == null) continue;

                foreach (var dtKv in combatant.Items)
                {
                    var dt = dtKv.Value;
                    if (dt == null) continue;
                    var typeName = dtKv.Key ?? dt.Type ?? "";
                    if (string.IsNullOrEmpty(typeName)) continue;
                    // ACT pre-populates a row per category per combatant even
                    // if nothing happened — those rows have StartTime set to
                    // DateTime.MaxValue ("9999-12-31") and every counter at
                    // zero. Drop them to keep payloads slim.
                    if (dt.Damage == 0 && dt.Hits == 0 && dt.Swings == 0
                        && dt.Misses == 0 && dt.Blocked == 0) continue;

                    result.Add(new Dictionary<string, object?>
                    {
                        ["combatant"] = combatant.Name ?? "",
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

        // ACT's CombatantData.Items dictionary keys identify the category
        // of each AttackType grouping. The swing_type is implicit in the
        // category — AttackType itself doesn't expose SwingType as a
        // property at this version. These values match what ACT's ODBC
        // export plugin writes into attacktype_table.swingtype.
        private static readonly Dictionary<string, int> OutgoingGroupToSwingType =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "Auto-Attack (Out)", 1 },
            { "Skill/Ability (Out)", 2 },
            { "Healed (Out)", 3 },
            { "Cure/Dispel (Out)", 20 },
            { "Threat (Out)", 100 },
            // Skipping aggregate rollups ("Outgoing Damage", "All Outgoing")
            // and incoming groupings ("Healed (Inc)" etc.) — the latter
            // would double-count what other combatants already reported as
            // outgoing.
        };

        private List<Dictionary<string, object?>> BuildAttackTypes(EncounterData enc)
        {
            var result = new List<Dictionary<string, object?>>();
            if (enc.Items == null) return result;

            foreach (var combatantKv in enc.Items)
            {
                var combatant = combatantKv.Value;
                if (combatant?.Items == null) continue;

                foreach (var groupKv in combatant.Items)
                {
                    if (!OutgoingGroupToSwingType.TryGetValue(groupKv.Key, out var swingType))
                        continue;

                    var attackTypeDict = groupKv.Value;
                    if (attackTypeDict?.Items == null) continue;
                    foreach (var atKv in attackTypeDict.Items)
                    {
                        var at = atKv.Value;
                        if (at == null) continue;
                        // Skip the 'All' rollup row — server filters it anyway,
                        // but trimming payload size here helps for raid exports.
                        if (string.Equals(at.Type, "All", StringComparison.OrdinalIgnoreCase)) continue;
                        // Skip rows where nothing actually happened — ACT
                        // sometimes records an attack name with zero damage,
                        // hits, misses, and blocked (e.g. spells that fizzled
                        // or interrupted). No info value for the UI.
                        if (at.Damage == 0 && at.Hits == 0 && at.Misses == 0
                            && at.Blocked == 0) continue;

                        result.Add(new Dictionary<string, object?>
                        {
                            ["attacker"] = combatant.Name ?? "",
                            ["victim"] = "",  // ACT's AttackType doesn't carry a single victim
                            ["swingtype"] = swingType,
                            ["type"] = at.Type ?? "",
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
                            ["resist"] = at.Resist ?? "",
                            ["hits"] = at.Hits,
                            ["crithits"] = at.CritHits,
                            ["blocked"] = at.Blocked,
                            ["misses"] = at.Misses,
                            ["swings"] = at.Swings,
                            ["tohit"] = at.ToHit,
                            ["averagedelay"] = at.AverageDelay,
                            ["critperc"] = at.CritPerc,
                            ["crittypes"] = "",  // not exposed on AttackType
                        });
                    }
                }
            }
            return result;
        }

        private int CountAttackTypes(EncounterData enc)
        {
            if (enc.Items == null) return 0;
            int count = 0;
            foreach (var c in enc.Items.Values)
            {
                if (c?.Items == null) continue;
                foreach (var groupKv in c.Items)
                {
                    if (!OutgoingGroupToSwingType.ContainsKey(groupKv.Key)) continue;
                    var attackTypeDict = groupKv.Value;
                    if (attackTypeDict?.Items == null) continue;
                    foreach (var at in attackTypeDict.Items.Values)
                    {
                        if (at != null && !string.Equals(at.Type, "All", StringComparison.OrdinalIgnoreCase))
                            count++;
                    }
                }
            }
            return count;
        }

        private static string FormatTime(DateTime t)
        {
            if (t == DateTime.MinValue) return "";
            // ACT's DateTime comes from the EQ2 log line, which is written in
            // the player's local clock. Convert to UTC and tag with a 'Z'
            // suffix so the server can store an absolute timestamp — without
            // this, cross-timezone viewers would see times shifted by the
            // uploader's local-vs-UTC offset. ToUniversalTime() treats Kind
            // == Unspecified as Local, which is what we want here.
            var utc = t.Kind == DateTimeKind.Utc ? t : t.ToUniversalTime();
            return utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        }

        // -------------------------------------------------------------------
        // NaN / Infinity scrubber. ACT returns NaN for any computed stat that
        // divided by zero (a miss-only attempt → average = 0/0). The .NET
        // JavaScriptSerializer happily emits the literal "NaN" — but that's
        // invalid JSON, so the server's json.loads will reject the upload.
        // Walk the payload once and swap any non-finite double for 0.
        // -------------------------------------------------------------------

        private static void SanitizePayload(object? node)
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
