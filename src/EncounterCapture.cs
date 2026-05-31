using System;
using System.Collections.Generic;
using System.Threading;
using Advanced_Combat_Tracker;

namespace EQ2Lexicon.ACTPlugin
{
    /// <summary>
    /// Payload-and-reason bundle raised by
    /// <see cref="EncounterCapture.OnTamperDetected"/>. Carries the
    /// serialized JSON payload (identical to what /ingest would have
    /// received) and the heuristic that fired.
    ///
    /// Lives in the UI assembly rather than Core because its only
    /// consumer is <see cref="Plugin"/>; pushing it down to Core would
    /// just add a DTO file with no testable behaviour.
    /// </summary>
    internal sealed class TamperEvent
    {
        public string PayloadJson { get; }
        public string Reason { get; }
        public string EncId { get; }

        public TamperEvent(string payloadJson, string reason, string encid)
        {
            PayloadJson = payloadJson ?? "";
            Reason = reason ?? "";
            EncId = encid ?? "";
        }
    }

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

        // Wall-clock-relative gate for "is this clearly imported rather
        // than legitimately mid-session?". The pre-startup check above
        // catches both cases identically; this additional check
        // separates them. An encounter that ENDED more than this long
        // ago is, in practice, an import — a real "user enabled plugin
        // mid-session" encounter ended seconds-to-minutes before now.
        // The investigation dump showed live captures with
        // (Now-EndTime)=16s vs imports at (Now-EndTime)=547088s (6.3
        // days). 1 hour is comfortably between the two extremes.
        public const double StaleEncounterHours = 1.0;

        // Owned by Plugin (via constructor injection) so the same
        // instance can be queried by both the polling path and the
        // manual right-click upload path. Probed once per poll tick
        // here — no separate timer.
        private readonly ImportActivityWatcher _importWatcher = new ImportActivityWatcher();

        /// <summary>
        /// Returns true if ACT's <c>ActiveControl</c> was an import-related
        /// control within the last 30 seconds. Exposed so the manual
        /// upload path in <see cref="Plugin"/> can apply the same check.
        /// </summary>
        public bool WasRecentImportActivity() => _importWatcher.WasRecentImportActivity();

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

        /// <summary>
        /// Raised when a captured encounter trips a tamper heuristic
        /// (currently: title doesn't match any enemy combatant — the
        /// rename-detection signal). Carries the full serialized
        /// payload that the leaderboard ingest *would* have received,
        /// plus a reason code, so Plugin can POST it to
        /// <see cref="UploadClient.ReportTamperAsync"/> for the
        /// audit trail. The encounter itself is NOT uploaded to the
        /// regular ingest endpoint — <see cref="OnCaptured"/> does not
        /// fire on this path.
        ///
        /// Fired on the same worker thread as the polling tick;
        /// subscribers must not block.
        /// </summary>
        public event Action<TamperEvent>? OnTamperDetected;

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
            // Sample ACT's ActiveControl once per tick — cheap, and the
            // window in which an import-related control is focused is
            // typically several seconds (the user holds the import
            // dialog open while ACT replays). One probe per 2s tick
            // gives plenty of resolution.
            _importWatcher.Probe();

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
                //
                // Additionally: if the encounter is wall-clock stale
                // (>1h since EndTime) OR an import control was focused
                // recently, it's almost certainly an import attempt
                // rather than legitimate mid-session play. Fire a
                // tamper report alongside the skip so the admin sees
                // the audit trail. The skip itself still happens
                // regardless (we never auto-upload pre-startup
                // encounters), but only the import-flavoured ones are
                // reported as tamper.
                if (enc.StartTime < _instanceStartedAt.AddSeconds(-InstanceStartGraceSeconds))
                {
                    MarkProcessedNoCapture(encid);
                    var importReason = ClassifyImportTamper(enc, now);
                    if (importReason != null)
                    {
                        FireTamperForEncounter(enc, encid, importReason);
                    }
                    OnSkipped?.Invoke(
                        $"skipped ({encid}: started before plugin enabled — likely import or pre-existing)");
                    continue;
                }

                // Post-startup tamper gate. Catches the case where the
                // plugin has been running for hours, then the user
                // imports a log whose encounters happen to land in
                // this session's zone tree with recent StartTimes
                // (rare, but possible if the user merges or
                // re-stamps timestamps). Also catches "user clicked
                // import in the last 30s, an encounter appeared".
                var importReasonPost = ClassifyImportTamper(enc, now);
                if (importReasonPost != null)
                {
                    MarkProcessedNoCapture(encid);
                    FireTamperForEncounter(enc, encid, importReasonPost);
                    OnSkipped?.Invoke($"skipped ({encid}: E1002)");
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
        /// Decide whether <paramref name="enc"/> looks like an import
        /// attempt, and return the reason code to attach to the tamper
        /// report. Returns null when neither signal trips.
        ///
        /// Two checks, in priority order:
        ///   1. <c>"stale_encounter"</c> — wall-clock age of EndTime
        ///      exceeds <see cref="StaleEncounterHours"/>. The defining
        ///      signal: live captures end seconds ago, imports end
        ///      hours-to-days ago.
        ///   2. <c>"recent_import_activity"</c> — the import-control
        ///      watcher recorded a sighting in the last 30s.
        ///      Corroborates Tier 1 and also catches the corner case
        ///      where an imported log happens to be very recent (e.g.
        ///      user imports a log from earlier today).
        /// </summary>
        private string? ClassifyImportTamper(EncounterData enc, DateTime now)
        {
            if ((now - enc.EndTime).TotalHours > StaleEncounterHours)
                return "stale_encounter";
            if (_importWatcher.WasRecentImportActivity())
                return "recent_import_activity";
            return null;
        }

        /// <summary>
        /// Build the same JSON payload the leaderboard ingest path would
        /// have produced, and raise <see cref="OnTamperDetected"/> with
        /// it. Used by every tamper branch except rename (which already
        /// has the payload in hand from the main pipeline). Swallows
        /// every exception — tamper reporting is best-effort and must
        /// never crash the capture path.
        /// </summary>
        private void FireTamperForEncounter(EncounterData enc, string encid, string reason)
        {
            try
            {
                var snapshot = CaptureSnapshot(enc);
                var payload = PayloadBuilder.BuildPayload(
                    ActHelpers.GetLoggingCharacterName(),
                    ActHelpers.GetLoggingServerName(),
                    snapshot);
                PayloadBuilder.SanitizePayload(payload);
                var json = PayloadBuilder.SerializeJson(payload);
                OnTamperDetected?.Invoke(new TamperEvent(json, reason, encid));
            }
            catch
            {
                // Best-effort — don't crash on payload-build failure
                // (e.g. an EncounterData whose Items dict mutated mid-
                // snapshot). The skip itself has already happened.
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

        /// <summary>
        /// Read <c>enc.HistoryRecord.FolderHint</c> via reflection.
        ///
        /// ACT's <c>EncounterData.HistoryRecord</c> is undocumented
        /// metadata that tags an encounter with the folder name ACT
        /// associated its log lines with — live tailing stamps the
        /// EQ2 per-server log directory (e.g. "Varsoon"); an imported
        /// log stamps the source folder (e.g. "Downloads"). We want
        /// the second value as a "flag on the play" when it diverges
        /// from the active log path's parent server name, but the
        /// property's presence/shape isn't part of ACT's stable
        /// surface area — hence reflection rather than a direct ref
        /// (a future ACT could rename or move the field without
        /// breaking us).
        ///
        /// Returns "" when anything goes wrong — caller treats empty
        /// as "no signal, don't warn" and the upload proceeds
        /// unaffected.
        /// </summary>
        private static string ReadFolderHint(EncounterData enc)
        {
            try
            {
                var historyProp = enc.GetType().GetProperty("HistoryRecord");
                var record = historyProp?.GetValue(enc, null);
                if (record == null) return "";
                var hintProp = record.GetType().GetProperty("FolderHint");
                var value = hintProp?.GetValue(record, null) as string;
                return value ?? "";
            }
            catch
            {
                return "";
            }
        }

        private void ProcessEncounter(EncounterData enc, string encid)
        {
            // Snapshot the ACT state THEN build the payload — keeps the
            // ACT-coupled extraction separate from the pure transformation
            // so the latter is unit-testable.
            var snapshot = CaptureSnapshot(enc);

            // Build the payload before the rename gate so the tamper
            // report (if it fires) has the same JSON shape the admin
            // would see in a regular upload — server-side audit code
            // can reuse the existing parse-row writer.
            var payload = PayloadBuilder.BuildPayload(
                ActHelpers.GetLoggingCharacterName(),
                ActHelpers.GetLoggingServerName(),
                snapshot);
            PayloadBuilder.SanitizePayload(payload);
            var json = PayloadBuilder.SerializeJson(payload);

            // Rename-detection gate: ACT's right-click → Rename Encounter
            // mutates EncounterData.Title with no audit trail (confirmed
            // empirically via reflection dump). A title that doesn't
            // appear in any enemy combatant's name is the only signal
            // we have that someone retitled the fight.
            //
            // When tripped: block the leaderboard upload (do NOT fire
            // OnCaptured — the regular ingest path never sees this
            // parse) and raise OnTamperDetected so Plugin can POST
            // the payload to the dedicated tamper-report endpoint
            // for the admin's evidence trail.
            //
            // User-visible message is the opaque code E1001 (rather
            // than the literal "title doesn't match any enemy") so a
            // user who knows the heuristic can't trivially game it.
            if (!EncounterTitle.MatchesAnEnemy(snapshot.Title, snapshot.EnumerateEnemyNames()))
            {
                MarkProcessedNoCapture(encid);
                OnTamperDetected?.Invoke(new TamperEvent(json, "title_enemy_mismatch", encid));
                OnSkipped?.Invoke($"skipped ({encid}: E1001)");
                return;
            }

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
                FolderHint = ReadFolderHint(enc),
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
