using System;
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

            UpdateStatusFromConfig();
        }

        public void DeInitPlugin()
        {
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
