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

        // After settle, give the title up to this long to resolve past the
        // placeholder "Encounter" / "" before giving up. Observed on
        // evac-cut-short fights: ACT marks the encounter complete before
        // its EQ2 log scanner has identified the mob, so we briefly see a
        // settled-but-untitled fight. ACT does eventually fill it in; just
        // not always within the 15s settle window. Past this cap, mark
        // processed (don't loop forever) and surface a skip reason to the
        // UI so the user knows we didn't quietly drop a real fight.
        private const double MaxPlaceholderWaitSeconds = 60.0;

        // Grace window for "is this encounter pre-existing or imported?".
        // An encounter whose StartTime is more than this far before
        // _instanceStartedAt is treated as not-ours: either it was already
        // in zone.Items when the plugin loaded (user enabled mid-session),
        // or the user just imported a log file with old fights. Either
        // way the auto path skips it — the manual right-click path stays
        // as the escape hatch.
        //
        // 5 minutes accommodates an in-progress fight that started
        // shortly before plugin-enable (we don't want to drop the current
        // raid pull just because the plugin loaded 30s into it).
        private const double InstanceStartGraceSeconds = 300.0;

        // Stamped once in the ctor — used to draw the "before me" line.
        // DateTime.Now (local) because every other timestamp from ACT in
        // this class is also local (StartTime/EndTime are ACT-emitted
        // local-clock values).
        private readonly DateTime _instanceStartedAt = DateTime.Now;

        // Last captured artefact — read by the settings panel for display.
        public string LastCapturedEncId { get; private set; } = "";
        public string LastCapturedTitle { get; private set; } = "";
        public DateTime LastCapturedAt { get; private set; } = DateTime.MinValue;
        public int LastCombatantCount { get; private set; }
        public int LastAttackTypeCount { get; private set; }
        public string LastCapturedPayloadJson { get; private set; } = "";

        /// <summary>Raised after a successful capture. Off-UI-thread.</summary>
        public event Action? OnCaptured;

        /// <summary>
        /// Raised when we intentionally skip an encounter we COULD have
        /// captured (placeholder title that never resolved, etc.). The
        /// Plugin routes the reason to the SettingsPanel so the user
        /// knows a real fight was dropped and why.
        /// </summary>
        public event Action<string>? OnSkipped;

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

                // Import/Merge zone is ACT's bucket for imported logs
                // and merged/edited encounters. Never auto-upload — those
                // are user-customised, not authoritative parses. The
                // manual right-click path enforces this too (greyed out).
                if (EncounterZone.IsImportOrMerge(enc.ZoneName))
                {
                    MarkProcessedNoCapture(encid);
                    OnSkipped?.Invoke(
                        $"skipped ({encid}: Import/Merge zone — customised parses aren't uploaded)");
                    continue;
                }

                // Pre-existing or freshly-imported encounter — the user
                // didn't actively play this one under our watch. Either
                // they enabled the plugin mid-session (these were already
                // here), or they just imported a log file. Auto path
                // skips; manual right-click upload still works as the
                // escape hatch for "yes actually, please upload that".
                if (enc.StartTime < _instanceStartedAt.AddSeconds(-InstanceStartGraceSeconds))
                {
                    MarkProcessedNoCapture(encid);
                    OnSkipped?.Invoke(
                        $"skipped ({encid}: started before plugin enabled — likely import or pre-existing)");
                    continue;
                }

                // Placeholder title: defer in the hope ACT fills it in on
                // a later poll. Past MaxPlaceholderWaitSeconds, give up
                // and surface a skip reason so the user doesn't think the
                // upload silently disappeared.
                if (EncounterTitle.IsPlaceholder(enc.Title))
                {
                    if ((now - enc.EndTime).TotalSeconds < MaxPlaceholderWaitSeconds)
                    {
                        // Don't add to _processed — retry next tick.
                        continue;
                    }
                    MarkProcessedNoCapture(encid);
                    OnSkipped?.Invoke(
                        $"skipped ({encid}: title never resolved past '{enc.Title}')");
                    continue;
                }

                ProcessEncounter(enc, encid);
            }
        }

        /// <summary>
        /// Add an encid to the processed set without producing a capture.
        /// Used by the three "skip reasons" branches above (Import/Merge
        /// zone, pre-plugin-startup encounter, placeholder title that
        /// never resolved) — all of them need to evict the encid from
        /// future poll iterations, none want to fire OnCaptured.
        /// </summary>
        private void MarkProcessedNoCapture(string encid)
        {
            lock (_lock)
            {
                _processed.Add(encid);
                _processedQueue.Enqueue(encid);
                while (_processedQueue.Count > ProcessedCap)
                {
                    _processed.Remove(_processedQueue.Dequeue());
                }
            }
        }

        // The placeholder-title predicate lives in Core
        // (EncounterTitle.IsPlaceholder) so the test project can
        // exercise it without ACT installed. Both the polling path
        // above and Plugin.OnManualUploadRequested call into it.

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
        //
        // Public + static so the manual-upload path (right-click "Upload
        // to EQ2 Lexicon" → ActMenuExtension) reuses the exact same
        // conversion as the polling path. If these two ever diverge,
        // the manual upload produces a subtly different payload from
        // the automatic one, which would be a debugging nightmare.
        // -------------------------------------------------------------------

        public static EncounterSnapshot CaptureSnapshot(EncounterData enc)
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
