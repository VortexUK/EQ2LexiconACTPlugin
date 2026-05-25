using System;
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
            _settingsPanel = new SettingsPanel(_config, OnConfigSaved, _uploadClient);
            _pluginTab.Controls.Add(_settingsPanel);

            // Encounter capture: polls every 2s for newly-closed encounters
            // and builds the upload payload. No HTTP yet — surfaced to the
            // UI for verification.
            _capture = new EncounterCapture();
            _settingsPanel.GetLastCapturedPayloadJson = () => _capture?.LastCapturedPayloadJson ?? "";
            _capture.OnCaptured += OnEncounterCaptured;
            _capture.OnSkipped += reason =>
                _settingsPanel?.SetUploadStatus(reason, success: false);

            // Right-click "Upload to EQ2 Lexicon" on ACT's encounter
            // tree. Attaches 5s later — controls aren't built yet.
            _menuExtension = new ActMenuExtension(OnManualUploadRequested);

            UpdateStatusFromConfig();

            // Fire-and-forget update check. Fails open: any error
            // (offline, GitHub 5xx, rate-limit) leaves UpdateStatus null,
            // which UploadClient interprets as "don't gate".
            _ = Task.Run(CheckForUpdatesAsync);
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
            _menuExtension?.Dispose();
            _menuExtension = null;
            _capture?.Dispose();
            _capture = null;
            _uploadClient?.Dispose();
            _uploadClient = null;
            _pluginTab?.Controls.Clear();
            SetStatus("EQ2 Lexicon: unloaded");
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
                _settingsPanel.SetUploadStatus(
                    "manual upload skipped (Import/Merge zone — customised parses can't be uploaded)",
                    success: false);
                return;
            }

            // Placeholder check uses the SAME predicate as the
            // automatic path so the rule stays consistent. The
            // message tells the user how to fix it themselves.
            if (EncounterTitle.IsPlaceholder(enc.Title))
            {
                _settingsPanel.SetUploadStatus(
                    $"manual upload skipped (title is '{enc.Title}' — rename it in ACT first)",
                    success: false);
                return;
            }
            if (string.IsNullOrWhiteSpace(_config.ApiToken))
            {
                _settingsPanel.SetUploadStatus(
                    "manual upload skipped (API token not configured)",
                    success: false);
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
                var payload = PayloadBuilder.BuildPayload(ActHelpers.GetLoggingCharacterName(), snapshot);
                PayloadBuilder.SanitizePayload(payload);
                json = PayloadBuilder.SerializeJson(payload);
            }
            catch (Exception ex)
            {
                _settingsPanel.SetUploadStatus(
                    "manual upload failed (snapshot error): " + ex.Message,
                    success: false);
                return;
            }

            var url = _config.ServerUrl;
            var token = _config.ApiToken;
            _settingsPanel.SetUploadStatus($"manual upload starting ({title})…", success: true);

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await _uploadClient.UploadEncounterAsync(url, token, json);
                    _settingsPanel?.SetUploadStatus(
                        "manual: " + result.Message,
                        result.Success);
                }
                catch (Exception ex)
                {
                    _settingsPanel?.SetUploadStatus(
                        "manual upload error: " + ex.Message,
                        success: false);
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

            // Decide whether to upload. Skip reasons get surfaced in the
            // settings panel so the user knows nothing was sent (and why).
            if (_config == null || _uploadClient == null) return;

            if (!_config.UploadEnabled)
            {
                _settingsPanel.SetUploadStatus("skipped (uploads disabled)", success: false);
                return;
            }
            var charName = ActHelpers.GetLoggingCharacterName();
            if (!string.IsNullOrWhiteSpace(charName) && _config.IsBlacklisted(charName))
            {
                _settingsPanel.SetUploadStatus($"skipped ({charName} is blacklisted)", success: false);
                return;
            }

            var url = _config.ServerUrl;
            var token = _config.ApiToken;
            var json = _capture.LastCapturedPayloadJson;
            if (string.IsNullOrEmpty(json))
            {
                _settingsPanel.SetUploadStatus("skipped (no payload built)", success: false);
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
                }
                catch (Exception ex)
                {
                    _settingsPanel?.SetUploadStatus("error: " + ex.Message, success: false);
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
            if (!string.IsNullOrWhiteSpace(charName) && _config.IsBlacklisted(charName))
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
