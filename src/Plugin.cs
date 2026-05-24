using System;
using System.IO;
using System.Windows.Forms;
using Advanced_Combat_Tracker;

namespace EQ2Lexicon.ACTPlugin
{
    /// <summary>
    /// Entry point. ACT instantiates this class, calls InitPlugin with the
    /// tab page and status label that the plugin owns, and later calls
    /// DeInitPlugin on shutdown / unload.
    ///
    /// This skeleton just verifies the load/unload lifecycle works — we
    /// add config persistence, encounter hooks, and HTTP upload in
    /// subsequent commits.
    /// </summary>
    public class Plugin : IActPluginV1
    {
        private Label? _statusLabel;
        private TabPage? _pluginTab;

        public void InitPlugin(TabPage pluginScreenSpace, Label pluginStatusText)
        {
            _pluginTab = pluginScreenSpace;
            _statusLabel = pluginStatusText;

            _pluginTab.Text = "EQ2 Lexicon";

            // Placeholder UI — the real settings form lands in a later commit.
            var label = new Label
            {
                Text = "EQ2 Lexicon Uploader\n\nv0.1.0 — scaffold loaded successfully.\nSettings UI will appear here once we wire it up.",
                Dock = DockStyle.Fill,
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                AutoSize = false,
            };
            _pluginTab.Controls.Add(label);

            _statusLabel.Text = "EQ2 Lexicon: scaffold loaded";
        }

        public void DeInitPlugin()
        {
            if (_statusLabel != null)
            {
                _statusLabel.Text = "EQ2 Lexicon: unloaded";
            }
            _pluginTab?.Controls.Clear();
        }
    }
}
