using System;
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
        private SettingsPanel? _settingsPanel;

        public void InitPlugin(TabPage pluginScreenSpace, Label pluginStatusText)
        {
            _pluginTab = pluginScreenSpace;
            _statusLabel = pluginStatusText;

            _pluginTab.Text = "EQ2 Lexicon";

            try
            {
                _config = PluginConfig.Load();
            }
            catch (Exception ex)
            {
                // Should never happen — Load swallows its own errors and returns
                // defaults — but defensive anyway.
                _config = new PluginConfig();
                SetStatus("Config load error: " + ex.Message, isError: true);
            }

            _settingsPanel = new SettingsPanel(_config, OnConfigSaved);
            _pluginTab.Controls.Add(_settingsPanel);

            UpdateStatusFromConfig();
        }

        public void DeInitPlugin()
        {
            _pluginTab?.Controls.Clear();
            SetStatus("EQ2 Lexicon: unloaded");
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private void OnConfigSaved(PluginConfig config)
        {
            _config = config;
            UpdateStatusFromConfig();
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
