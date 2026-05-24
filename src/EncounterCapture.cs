using System;
using System.Collections.Generic;
using System.Threading;
using Advanced_Combat_Tracker;

namespace EQ2Lexicon.ACTPlugin
{
    /// <summary>
    /// Polls ACT's active zone every couple of seconds. When an encounter
    /// has settled (no EndTime updates for <see cref="SettleSeconds"/>),
    /// converts it into an <see cref="EncounterSnapshot"/> and hands it
    /// to <see cref="PayloadBuilder"/> for the upload-shaped JSON.
    ///
    /// All ACT-coupled glue lives here. The shape of the produced JSON
    /// lives in PayloadBuilder and is unit-tested separately.
    ///
    /// Threading: Timer fires on a worker thread. We do all the walking on
    /// that thread (safe because the encounter is closed — ACT won't mutate
    /// it further). On completion we raise <see cref="OnCaptured"/>;
    /// subscribers that need to touch the UI must marshal back themselves.
    /// </summary>
    internal class EncounterCapture : IDisposable
    {
        private readonly Timer _timer;
        private readonly TimeSpan _interval = TimeSpan.FromSeconds(2);
        private readonly object _lock = new object();

        // Track which encids we've already processed so we don't re-emit.
        // Capped via _processedQueue eviction to avoid unbounded growth.
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
            // Snapshot the ACT state THEN build the payload — keeps the
            // ACT-coupled extraction separate from the pure transformation
            // so the latter is unit-testable.
            var snapshot = CaptureSnapshot(enc);
            var payload = PayloadBuilder.BuildPayload(ActHelpers.GetLoggingCharacterName(), snapshot);
            PayloadBuilder.SanitizePayload(payload);
            var json = PayloadBuilder.SerializeJson(payload);

            lock (_lock)
            {
                _processed.Add(encid);
                _processedQueue.Enqueue(encid);
                while (_processedQueue.Count > ProcessedCap)
                {
                    _processed.Remove(_processedQueue.Dequeue());
                }

                LastCapturedEncId = encid;
                LastCapturedTitle = snapshot.Title;
                LastCapturedAt = DateTime.Now;
                LastCombatantCount = snapshot.Combatants.Count;
                LastAttackTypeCount = PayloadBuilder.CountAttackTypes(snapshot);
                LastCapturedPayloadJson = json;
            }

            OnCaptured?.Invoke();
        }

        // -------------------------------------------------------------------
        // ACT → snapshot conversion. Mechanical; the only "logic" here is
        // pulling fields and using ACT's GetAllies() for the ally split.
        // -------------------------------------------------------------------

        private static EncounterSnapshot CaptureSnapshot(EncounterData enc)
        {
            var snap = new EncounterSnapshot
            {
                EncId = enc.EncId ?? "",
                Title = enc.Title ?? "",
                Zone = enc.ZoneName,
                StartTime = enc.StartTime,
                EndTime = enc.EndTime,
                Duration = enc.Duration,
                Damage = enc.Damage,
                DPS = enc.DPS,
                AlliedKills = enc.AlliedKills,
                AlliedDeaths = enc.AlliedDeaths,
                SuccessLevel = enc.GetEncounterSuccessLevel(),
            };

            // ACT's authoritative ally list (already pet-attributed by the
            // EQ2 parser). If ACT throws we fall through to an empty set →
            // every combatant marked enemy.
            try
            {
                foreach (var ally in enc.GetAllies())
                {
                    if (ally?.Name != null) snap.AllyNames.Add(ally.Name);
                }
            }
            catch { /* swallow */ }

            if (enc.Items == null) return snap;
            foreach (var combatantKv in enc.Items)
            {
                var c = combatantKv.Value;
                if (c == null) continue;
                var cs = new CombatantSnapshot
                {
                    Name = c.Name ?? "",
                    EncStartTime = c.EncStartTime,
                    EncEndTime = c.EncEndTime,
                    Duration = c.Duration,
                    Damage = c.Damage,
                    DamagePercent = c.DamagePercent ?? "",
                    Kills = c.Kills,
                    Healed = c.Healed,
                    HealedPercent = c.HealedPercent ?? "",
                    CritHeals = c.CritHeals,
                    Heals = c.Heals,
                    CureDispels = c.CureDispels,
                    PowerDamage = c.PowerDamage,
                    PowerReplenish = c.PowerReplenish,
                    DPS = c.DPS,
                    EncDPS = c.EncDPS,
                    EncHPS = c.EncHPS,
                    Hits = c.Hits,
                    CritHits = c.CritHits,
                    Blocked = c.Blocked,
                    Misses = c.Misses,
                    Swings = c.Swings,
                    HealsTaken = c.HealsTaken,
                    DamageTaken = c.DamageTaken,
                    Deaths = c.Deaths,
                    ToHit = c.ToHit,
                    CritDamPerc = c.CritDamPerc,
                    CritHealPerc = c.CritHealPerc,
                };

                if (c.Items != null)
                {
                    foreach (var dtKv in c.Items)
                    {
                        var dt = dtKv.Value;
                        if (dt == null) continue;
                        var key = dtKv.Key ?? dt.Type ?? "";
                        if (string.IsNullOrEmpty(key)) continue;

                        var agg = new DamageTypeAggregate
                        {
                            Type = dt.Type ?? key,
                            StartTime = dt.StartTime,
                            EndTime = dt.EndTime,
                            Duration = dt.Duration,
                            Damage = dt.Damage,
                            EncDPS = dt.EncDPS,
                            CharDPS = dt.CharDPS,
                            DPS = dt.DPS,
                            Average = dt.Average,
                            Median = dt.Median,
                            MinHit = dt.MinHit,
                            MaxHit = dt.MaxHit,
                            Hits = dt.Hits,
                            CritHits = dt.CritHits,
                            Blocked = dt.Blocked,
                            Misses = dt.Misses,
                            Swings = dt.Swings,
                            ToHit = dt.ToHit,
                            AverageDelay = dt.AverageDelay,
                            CritPerc = dt.CritPerc,
                        };

                        if (dt.Items != null)
                        {
                            foreach (var atKv in dt.Items)
                            {
                                var at = atKv.Value;
                                if (at == null) continue;
                                agg.Attacks.Add(new AttackTypeSnapshot
                                {
                                    Type = at.Type ?? "",
                                    StartTime = at.StartTime,
                                    EndTime = at.EndTime,
                                    Duration = at.Duration,
                                    Damage = at.Damage,
                                    EncDPS = at.EncDPS,
                                    CharDPS = at.CharDPS,
                                    DPS = at.DPS,
                                    Average = at.Average,
                                    Median = at.Median,
                                    MinHit = at.MinHit,
                                    MaxHit = at.MaxHit,
                                    Resist = at.Resist ?? "",
                                    Hits = at.Hits,
                                    CritHits = at.CritHits,
                                    Blocked = at.Blocked,
                                    Misses = at.Misses,
                                    Swings = at.Swings,
                                    ToHit = at.ToHit,
                                    AverageDelay = at.AverageDelay,
                                    CritPerc = at.CritPerc,
                                });
                            }
                        }
                        cs.DamageTypeAggregates[key] = agg;
                    }
                }
                snap.Combatants.Add(cs);
            }
            return snap;
        }
    }
}
