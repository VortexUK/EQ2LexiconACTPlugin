using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;
using Advanced_Combat_Tracker;

namespace EQ2Lexicon.ACTPlugin
{
    /// <summary>
    /// Entry point. ACT instantiates this class, calls InitPlugin with the
    /// tab page and status label that the plugin owns, and later calls
    /// DeInitPlugin on shutdown / unload.
    /// </summary>
    public class Plugin : IActPluginV1
    {
        private Label? _statusLabel;
        private TabPage? _pluginTab;
        private PluginConfig? _config;
        private string _configPath = "";
        private SettingsPanel? _settingsPanel;
        private EncounterCapture? _capture;
        private UploadClient? _uploadClient;
        private ActMenuExtension? _menuExtension;
        private EventLog? _eventLog;

        // Transient — never persisted. True only after a successful
        // whoami round-trip that reported is_admin:true. Fail-CLOSED:
        // any error (no token, offline, old server without the field)
        // leaves this false, which keeps the Server URL field locked
        // in the settings panel. The whoami call is fired
        // opportunistically on startup (if a token exists) and
        // refreshed on every Test Connection / Save click.
        private bool _isAdmin;

        // EQ2 servers the user is allowed to upload parses from. The
        // canonical list comes from the whoami response's
        // `allowed_servers` field. Until the server starts returning
        // it (see CLAUDE.md — required server-side changes), we fall
        // back to the two active English-language EQ2 TLE servers as
        // of 2026 so the UI never shows an empty list.
        private static readonly IReadOnlyList<string> DefaultAllowedServers =
            new[] { "Varsoon", "Wuoshi" };
        private IReadOnlyList<string> _allowedServers = DefaultAllowedServers;

        public void InitPlugin(TabPage pluginScreenSpace, Label pluginStatusText)
        {
            _pluginTab = pluginScreenSpace;
            _statusLabel = pluginStatusText;

            _pluginTab.Text = "EQ2 Lexicon";

            // Resolve the path once at init — PluginConfig itself is ACT-free,
            // so the caller (us) decides where it lives on disk.
            _configPath = ActHelpers.GetConfigPath();

            try
            {
                _config = PluginConfig.Load(_configPath);
            }
            catch (Exception ex)
            {
                // Should never happen — Load swallows its own errors and returns
                // defaults — but defensive anyway.
                _config = new PluginConfig();
                SetStatus("Config load error: " + ex.Message, isError: true);
            }

            _uploadClient = new UploadClient();

            // One log instance per plugin lifetime; the settings panel
            // takes a reference and renders its entries. Forwarded
            // event sources are wired below.
            _eventLog = new EventLog();
            _uploadClient.RequestCompleted += OnHttpRequestCompleted;

            _settingsPanel = new SettingsPanel(_config, OnConfigSaved, _uploadClient, _eventLog);
            _settingsPanel.OnInstallUpdateClicked = BeginAutoUpdateAsync;
            _pluginTab.Controls.Add(_settingsPanel);

            // Encounter capture: polls every 2s for newly-closed encounters
            // and builds the upload payload. No HTTP yet — surfaced to the
            // UI for verification.
            _capture = new EncounterCapture();
            _settingsPanel.GetLastCapturedPayloadJson = () => _capture?.LastCapturedPayloadJson ?? "";
            _capture.OnCaptured += OnEncounterCaptured;
            _capture.OnSkipped += reason =>
            {
                _settingsPanel?.SetUploadStatus(reason, success: false);
                _eventLog?.Log(EventSeverity.Warning, "capture", "skipped: " + reason);
            };

            // Right-click "Upload to EQ2 Lexicon" on ACT's encounter
            // tree. Attaches 5s later — controls aren't built yet.
            _menuExtension = new ActMenuExtension(OnManualUploadRequested);

            UpdateStatusFromConfig();

            // Seed the allowed-servers card with the defaults before
            // whoami completes — beats showing an empty list during
            // the brief window between plugin init and the first
            // round-trip to the server.
            _settingsPanel.SetAllowedServers(_allowedServers);

            _eventLog.Log(EventSeverity.Info, "plugin",
                $"EQ2 Lexicon plugin v{UpdateChecker.GetCurrentAssemblyVersion().ToString(3)} loaded.");

            // Fire-and-forget update check. Fails open: any error
            // (offline, GitHub 5xx, rate-limit) leaves UpdateStatus null,
            // which UploadClient interprets as "don't gate".
            _ = Task.Run(CheckForUpdatesAsync);

            // Opportunistic whoami so the URL-edit gate has an answer
            // without the user having to click Test Connection first.
            // Fails CLOSED — no token, network down, old server with
            // no is_admin field → field stays locked. Runs after the
            // settings panel exists so we can push the result to it.
            _ = Task.Run(RefreshAdminStateAsync);
        }

        /// <summary>
        /// User clicked "Install update" in the version banner. Runs
        /// the full download → verify → stage pipeline:
        ///
        ///   1. Pull the latest release URL + SHA-256 we cached during
        ///      the startup update check (UploadClient.UpdateStatus).
        ///      Refuse if either is missing — installer enforces this
        ///      too but we want a clear UI message early.
        ///   2. Core PluginUpdater.DownloadAndVerifyAsync downloads the
        ///      DLL bytes and verifies the SHA-256 matches.
        ///   3. UpdateInstaller.StageUpdate writes the .new file +
        ///      spawns the on-exit swap helper.
        ///   4. SettingsPanel surfaces the result. On success the
        ///      buttons disable themselves — single staged update per
        ///      session is enough.
        ///
        /// All exceptions caught and surfaced via SetUpdateInstallStatus
        /// — clicking Install must never crash ACT or leave the UI in
        /// a half-disabled state.
        /// </summary>
        private async Task BeginAutoUpdateAsync()
        {
            if (_settingsPanel == null || _uploadClient == null) return;
            var status = _uploadClient.UpdateStatus;
            if (status == null ||
                string.IsNullOrWhiteSpace(status.LatestDllUrl) ||
                string.IsNullOrWhiteSpace(status.LatestDllSha256))
            {
                _settingsPanel.SetUpdateInstallStatus(
                    "No verifiable download available — try the browser link instead.",
                    success: false);
                return;
            }

            _settingsPanel.SetUpdateInstallStatus("Downloading update…", success: false);

            DownloadResult download;
            try
            {
                var ver = UpdateChecker.GetCurrentAssemblyVersion().ToString(3);
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
                {
                    var fetcher = new HttpDllAssetFetcher(http, $"EQ2LexiconACTPlugin/{ver}");
                    download = await PluginUpdater
                        .DownloadAndVerifyAsync(status.LatestDllUrl, status.LatestDllSha256, fetcher)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _settingsPanel.SetUpdateInstallStatus("Download failed: " + ex.Message, success: false);
                return;
            }

            if (!download.Success || download.DllBytes == null)
            {
                _settingsPanel.SetUpdateInstallStatus(download.Message, success: false);
                return;
            }

            _settingsPanel.SetUpdateInstallStatus("Verified. Staging…", success: false);

            try
            {
                var stage = UpdateInstaller.StageUpdate(
                    download.DllBytes,
                    UpdateInstaller.ResolveRunningDllPath());
                _settingsPanel.SetUpdateInstallStatus(stage.Message, stage.Success);
            }
            catch (Exception ex)
            {
                _settingsPanel.SetUpdateInstallStatus(
                    "Couldn't stage update: " + ex.Message, success: false);
            }
        }

        /// <summary>
        /// Fetch the GitHub release list, compute where we sit, and
        /// publish the result to the SettingsPanel + UploadClient. Runs
        /// once per ACT session — no caching needed because every
        /// startup is fresh and the request budget is generous.
        /// </summary>
        private async Task CheckForUpdatesAsync()
        {
            if (_uploadClient == null || _settingsPanel == null) return;
            try
            {
                var version = UpdateChecker.GetCurrentAssemblyVersion();
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
                {
                    var fetcher = new GitHubReleaseFetcher(http, $"EQ2LexiconACTPlugin/{version}");
                    var result = await UpdateChecker.CheckAsync(version, fetcher).ConfigureAwait(false);
                    _uploadClient.UpdateStatus = result;
                    _settingsPanel.SetUpdateStatus(result);
                }
            }
            catch
            {
                // CheckAsync swallows its own exceptions and returns
                // Unknown — this catch is a belt-and-brace against the
                // HttpClient ctor or any reflection lookup throwing
                // before we get into the checker.
            }
        }

        public void DeInitPlugin()
        {
            // Unsubscribe BEFORE disposing so a request completing
            // mid-shutdown can't fire into a half-disposed handler.
            if (_uploadClient != null)
            {
                _uploadClient.RequestCompleted -= OnHttpRequestCompleted;
            }
            _menuExtension?.Dispose();
            _menuExtension = null;
            _capture?.Dispose();
            _capture = null;
            _uploadClient?.Dispose();
            _uploadClient = null;
            _eventLog = null;
            _pluginTab?.Controls.Clear();
            SetStatus("EQ2 Lexicon: unloaded");
        }

        /// <summary>
        /// Run a whoami call with the current saved token (no UI
        /// state — uses the on-disk config, not the in-progress
        /// edit). Cache the resulting admin flag and push it to the
        /// settings panel so the URL-field gate updates without
        /// waiting for the user to click Test Connection.
        ///
        /// Fail-CLOSED at every branch: empty token / invalid URL /
        /// network error / non-admin → _isAdmin stays false (URL
        /// locked). Only a successful whoami that reports
        /// is_admin:true flips it.
        /// </summary>
        private async Task RefreshAdminStateAsync()
        {
            if (_uploadClient == null || _settingsPanel == null || _config == null) return;
            if (string.IsNullOrWhiteSpace(_config.ApiToken)) return;

            try
            {
                var result = await _uploadClient
                    .TestConnectionAsync(_config.ServerUrl, _config.ApiToken)
                    .ConfigureAwait(false);
                _isAdmin = result.Success && result.IsAdmin;
                _settingsPanel.SetAdminState(_isAdmin);

                // If the server returned an allowed_servers list, use
                // it; otherwise keep the built-in default. We never
                // *replace* the displayed list with an empty one on
                // failure — better UX to show the defaults than a
                // blank card.
                if (result.Success && result.AllowedServers != null && result.AllowedServers.Count > 0)
                {
                    _allowedServers = result.AllowedServers;
                    _settingsPanel.SetAllowedServers(_allowedServers);
                    _eventLog?.Log(EventSeverity.Info, "auth",
                        $"allowed servers from site: {string.Join(", ", _allowedServers)}");
                }

                _eventLog?.Log(
                    EventSeverity.Info,
                    "auth",
                    _isAdmin
                        ? "admin authenticated — Server URL is editable"
                        : "non-admin (Server URL locked)");
            }
            catch
            {
                // TestConnectionAsync wraps the expected exception
                // types into a Result, but belt-and-brace.
            }
        }

        /// <summary>
        /// Forward an UploadClient HTTP exchange to the event log.
        /// Anonymised payload — verb, URL, status code, duration ms,
        /// success bool, ~200-char response excerpt. Bearer token
        /// lives in headers and is never on this path; payload body
        /// (encounter JSON) is excluded by UploadClient.
        /// </summary>
        private void OnHttpRequestCompleted(HttpExchangeInfo info)
        {
            if (_eventLog == null) return;
            var sev = info.Success ? EventSeverity.Success : EventSeverity.Error;
            var msg = info.StatusCode > 0
                ? $"{info.Verb} {info.Url} → {info.StatusCode} ({info.DurationMs} ms)"
                : $"{info.Verb} {info.Url} → failed ({info.DurationMs} ms): {info.ResponseExcerpt}";
            _eventLog.Log(sev, "http", msg);
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private void OnConfigSaved(PluginConfig config)
        {
            // SettingsPanel mutates the config in place then invokes this
            // callback — persistence lives here so PluginConfig stays
            // path-free (and ACT-free) in the Core assembly.
            config.Save(_configPath);
            _config = config;
            UpdateStatusFromConfig();
            _eventLog?.Log(EventSeverity.Info, "config", "settings saved");

            // Token / URL may have changed — re-check admin state in
            // the background. Don't block Save; the user's URL field
            // updates a moment later when the response lands.
            _ = Task.Run(RefreshAdminStateAsync);
        }

        /// <summary>
        /// User right-clicked an encounter in ACT's tree and chose
        /// "Upload to EQ2 Lexicon". Runs on the UI thread; we kick the
        /// HTTP work to a worker.
        ///
        /// Manual-upload gate matrix (different from the auto path):
        ///   * blacklist                 → BYPASSED (deliberate action)
        ///   * upload-enabled toggle      → BYPASSED (deliberate action)
        ///   * placeholder title check    → ENFORCED (no point uploading
        ///                                  an "Encounter"-titled fight;
        ///                                  user is told to rename in ACT)
        ///   * token configured           → ENFORCED (can't sign without)
        ///   * version not too old        → ENFORCED in UploadClient
        ///                                  (server-compat, same path)
        ///   * HMAC signing               → ENFORCED in UploadClient
        /// </summary>
        private void OnManualUploadRequested(EncounterData enc)
        {
            if (_settingsPanel == null || _config == null || _uploadClient == null) return;

            // Defensive re-check of the Import/Merge gate even though
            // the menu item is greyed out for these encounters — a
            // fast click could in principle race the Opening handler's
            // enable computation.
            if (EncounterZone.IsImportOrMerge(enc.ZoneName))
            {
                const string msg = "manual upload skipped (Import/Merge zone — customised parses can't be uploaded)";
                _settingsPanel.SetUploadStatus(msg, success: false);
                _eventLog?.Log(EventSeverity.Warning, "upload", msg);
                return;
            }

            // Placeholder check uses the SAME predicate as the
            // automatic path so the rule stays consistent. The
            // message tells the user how to fix it themselves.
            if (EncounterTitle.IsPlaceholder(enc.Title))
            {
                var msg = $"manual upload skipped (title is '{enc.Title}' — rename it in ACT first)";
                _settingsPanel.SetUploadStatus(msg, success: false);
                _eventLog?.Log(EventSeverity.Warning, "upload", msg);
                return;
            }
            if (string.IsNullOrWhiteSpace(_config.ApiToken))
            {
                const string msg = "manual upload skipped (API token not configured)";
                _settingsPanel.SetUploadStatus(msg, success: false);
                _eventLog?.Log(EventSeverity.Warning, "upload", msg);
                return;
            }

            // Reuse the same ACT→snapshot conversion the polling path
            // uses. CaptureSnapshot is public static specifically so
            // these two paths stay byte-identical in their payload
            // construction.
            string json;
            string title;
            try
            {
                var snapshot = EncounterCapture.CaptureSnapshot(enc);
                title = snapshot.Title;

                // Rename-detection gate — mirrors the polling path.
                // ACT's right-click → Rename Encounter mutates Title
                // with no audit trail, so a title that doesn't appear
                // in any enemy combatant is the only signal we have
                // that the user retitled the fight. Block here too:
                // the manual upload path skips the blacklist + the
                // upload-enabled toggle, but a deliberate rename is
                // a different category — clearly the user typed
                // something, and uploading it would mislabel the parse
                // on the site.
                var enemyNames = EncounterCapture.EnumerateEnemyNames(snapshot);
                if (!EncounterTitle.MatchesAnEnemy(title, enemyNames))
                {
                    var msg = $"manual upload skipped (title '{title}' doesn't match any enemy — looks renamed in ACT)";
                    _settingsPanel.SetUploadStatus(msg, success: false);
                    _eventLog?.Log(EventSeverity.Warning, "upload", msg);
                    return;
                }

                var payload = PayloadBuilder.BuildPayload(
                    ActHelpers.GetLoggingCharacterName(),
                    ActHelpers.GetLoggingServerName(),
                    snapshot);
                PayloadBuilder.SanitizePayload(payload);
                json = PayloadBuilder.SerializeJson(payload);
            }
            catch (Exception ex)
            {
                var msg = "manual upload failed (snapshot error): " + ex.Message;
                _settingsPanel.SetUploadStatus(msg, success: false);
                _eventLog?.Log(EventSeverity.Error, "upload", msg);
                return;
            }

            var url = _config.ServerUrl;
            var token = _config.ApiToken;
            var startMsg = $"manual upload starting ({title})…";
            _settingsPanel.SetUploadStatus(startMsg, success: true);
            _eventLog?.Log(EventSeverity.Info, "upload", startMsg);

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await _uploadClient.UploadEncounterAsync(url, token, json);
                    _settingsPanel?.SetUploadStatus(
                        "manual: " + result.Message,
                        result.Success);
                    _eventLog?.Log(
                        result.Success ? EventSeverity.Success : EventSeverity.Error,
                        "upload",
                        "manual: " + result.Message);
                }
                catch (Exception ex)
                {
                    var msg = "manual upload error: " + ex.Message;
                    _settingsPanel?.SetUploadStatus(msg, success: false);
                    _eventLog?.Log(EventSeverity.Error, "upload", msg);
                }
            });
        }

        private void OnEncounterCaptured()
        {
            if (_capture == null || _settingsPanel == null) return;
            _settingsPanel.RefreshLastCaptured(
                _capture.LastCapturedEncId,
                _capture.LastCapturedTitle,
                _capture.LastCapturedAt,
                _capture.LastCombatantCount,
                _capture.LastAttackTypeCount);

            _eventLog?.Log(
                EventSeverity.Success,
                "capture",
                $"captured \"{_capture.LastCapturedTitle}\" ({_capture.LastCombatantCount} combatants)");

            // Decide whether to upload. Skip reasons get surfaced in the
            // settings panel so the user knows nothing was sent (and why).
            if (_config == null || _uploadClient == null) return;

            if (!_config.UploadEnabled)
            {
                const string msg = "skipped (uploads disabled)";
                _settingsPanel.SetUploadStatus(msg, success: false);
                _eventLog?.Log(EventSeverity.Warning, "upload", msg);
                return;
            }
            var charName = ActHelpers.GetLoggingCharacterName();
            var serverName = ActHelpers.GetLoggingServerName();
            if (!string.IsNullOrWhiteSpace(charName) && _config.IsBlacklisted(charName, serverName))
            {
                var msg = string.IsNullOrWhiteSpace(serverName)
                    ? $"skipped ({charName} is blacklisted)"
                    : $"skipped ({charName} on {serverName} is blacklisted)";
                _settingsPanel.SetUploadStatus(msg, success: false);
                _eventLog?.Log(EventSeverity.Warning, "upload", msg);
                return;
            }

            var url = _config.ServerUrl;
            var token = _config.ApiToken;
            var json = _capture.LastCapturedPayloadJson;
            if (string.IsNullOrEmpty(json))
            {
                const string msg = "skipped (no payload built)";
                _settingsPanel.SetUploadStatus(msg, success: false);
                _eventLog?.Log(EventSeverity.Warning, "upload", msg);
                return;
            }

            // Fire-and-forget upload. The HttpClient handles concurrent
            // requests, so back-to-back encounters won't step on each other.
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await _uploadClient.UploadEncounterAsync(url, token, json);
                    _settingsPanel?.SetUploadStatus(result.Message, result.Success);
                    _eventLog?.Log(
                        result.Success ? EventSeverity.Success : EventSeverity.Error,
                        "upload",
                        result.Message);
                }
                catch (Exception ex)
                {
                    _settingsPanel?.SetUploadStatus("error: " + ex.Message, success: false);
                    _eventLog?.Log(EventSeverity.Error, "upload", "error: " + ex.Message);
                }
            });
        }

        private void UpdateStatusFromConfig()
        {
            if (_config == null)
            {
                SetStatus("EQ2 Lexicon: no config");
                return;
            }
            if (string.IsNullOrEmpty(_config.ApiToken))
            {
                SetStatus("EQ2 Lexicon: API token not set");
                return;
            }
            if (!_config.UploadEnabled)
            {
                SetStatus("EQ2 Lexicon: uploads disabled");
                return;
            }

            // If the current logging character is on the blacklist, surface
            // that prominently — easy to miss otherwise.
            var charName = ActHelpers.GetLoggingCharacterName();
            var serverName = ActHelpers.GetLoggingServerName();
            if (!string.IsNullOrWhiteSpace(charName) && _config.IsBlacklisted(charName, serverName))
            {
                SetStatus($"EQ2 Lexicon: {charName} is blacklisted — uploads skipped");
                return;
            }

            SetStatus("EQ2 Lexicon: ready (uploads pending wire-up)");
        }

        private void SetStatus(string text, bool isError = false)
        {
            if (_statusLabel == null) return;
            _statusLabel.Text = text;
            // ACT's pluginStatusText is a standard Label — we could colour it,
            // but ACT may overwrite styling, so just set the text for now.
        }
    }
}
