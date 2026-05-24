using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Advanced_Combat_Tracker;

namespace EQ2Lexicon.ACTPlugin
{
    /// <summary>
    /// Settings UI hosted inside the EQ2 Lexicon plugin tab.
    ///
    /// Three editable fields (Server URL, API Token, Enable Upload) plus a
    /// Save button. Changes are not persisted until Save is clicked — keeps
    /// the model simple (no debounced auto-save), avoids partial writes
    /// while the user is mid-edit.
    /// </summary>
    public class SettingsPanel : UserControl
    {
        private readonly PluginConfig _config;
        private readonly Action<PluginConfig> _onSave;

        private readonly TextBox _serverUrl;
        private readonly TextBox _apiToken;
        private readonly CheckBox _uploadEnabled;
        private readonly TextBox _blacklist;
        private readonly Label _currentCharLabel;
        private readonly Label _statusLabel;

        public SettingsPanel(PluginConfig config, Action<PluginConfig> onSave)
        {
            _config = config;
            _onSave = onSave;

            Dock = DockStyle.Fill;
            Padding = new Padding(16);
            BackColor = SystemColors.Control;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                Padding = new Padding(0),
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            // Header
            var header = new Label
            {
                Text = "EQ2 Lexicon Uploader",
                Font = new Font("Segoe UI", 14, FontStyle.Bold),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 4),
            };
            layout.Controls.Add(header);
            layout.SetColumnSpan(header, 2);

            var subhead = new Label
            {
                Text =
                    "Uploads each finished ACT encounter to the EQ2 Lexicon site.\r\n" +
                    "Generate an API token under your profile → API Tokens on the site, then paste it below.",
                AutoSize = true,
                MaximumSize = new Size(620, 0),
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, 0, 0, 16),
            };
            layout.Controls.Add(subhead);
            layout.SetColumnSpan(subhead, 2);

            // Server URL
            layout.Controls.Add(MakeLabel("Server URL:"));
            _serverUrl = MakeTextBox(_config.ServerUrl);
            layout.Controls.Add(_serverUrl);

            // API Token
            layout.Controls.Add(MakeLabel("API Token:"));
            _apiToken = MakeTextBox(_config.ApiToken);
            _apiToken.UseSystemPasswordChar = false; // visible on purpose for v1 — easier to verify
            _apiToken.Font = new Font(FontFamily.GenericMonospace, 9);
            layout.Controls.Add(_apiToken);

            // Spacer + checkbox
            layout.Controls.Add(new Label { AutoSize = true }); // empty label in col 0
            _uploadEnabled = new CheckBox
            {
                Text = "Enable automatic upload after each encounter",
                Checked = _config.UploadEnabled,
                AutoSize = true,
                Margin = new Padding(0, 6, 0, 16),
            };
            layout.Controls.Add(_uploadEnabled);

            // Blacklist: characters to never upload as
            layout.Controls.Add(MakeLabel("Don't upload as:"));
            var blacklistGroup = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Margin = new Padding(0, 4, 0, 4),
            };
            _blacklist = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                AcceptsReturn = true,
                Width = 520,
                Height = 90,
                Font = new Font(FontFamily.GenericMonospace, 9),
                Text = string.Join(Environment.NewLine, _config.BlacklistedCharacters ?? new System.Collections.Generic.List<string>()),
            };
            blacklistGroup.Controls.Add(_blacklist);

            var blacklistHelp = new Label
            {
                Text = "One character name per line. Encounters logged as these characters are skipped.",
                AutoSize = true,
                MaximumSize = new Size(520, 0),
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, 2, 0, 0),
            };
            blacklistGroup.Controls.Add(blacklistHelp);

            _currentCharLabel = new Label
            {
                AutoSize = true,
                Margin = new Padding(0, 4, 0, 16),
                ForeColor = SystemColors.GrayText,
                Text = "",
            };
            UpdateCurrentCharLabel();
            blacklistGroup.Controls.Add(_currentCharLabel);

            layout.Controls.Add(blacklistGroup);

            // Save button + status
            layout.Controls.Add(new Label { AutoSize = true });
            var buttonRow = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                Margin = new Padding(0),
            };
            var saveBtn = new Button
            {
                Text = "Save settings",
                AutoSize = true,
                Padding = new Padding(8, 4, 8, 4),
            };
            saveBtn.Click += OnSaveClicked;
            buttonRow.Controls.Add(saveBtn);

            _statusLabel = new Label
            {
                AutoSize = true,
                Margin = new Padding(12, 8, 0, 0),
                ForeColor = SystemColors.GrayText,
                Text = "",
            };
            buttonRow.Controls.Add(_statusLabel);

            layout.Controls.Add(buttonRow);

            Controls.Add(layout);
        }

        private static Label MakeLabel(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 8, 8, 4),
                Anchor = AnchorStyles.Left,
            };
        }

        private static TextBox MakeTextBox(string initial)
        {
            return new TextBox
            {
                Text = initial,
                Width = 520,
                Margin = new Padding(0, 4, 0, 4),
            };
        }

        private void OnSaveClicked(object sender, EventArgs e)
        {
            _config.ServerUrl = (_serverUrl.Text ?? "").Trim().TrimEnd('/');
            _config.ApiToken = (_apiToken.Text ?? "").Trim();
            _config.UploadEnabled = _uploadEnabled.Checked;
            _config.BlacklistedCharacters = (_blacklist.Text ?? "")
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            try
            {
                _config.Save();
                _onSave?.Invoke(_config);
                _statusLabel.ForeColor = Color.DarkGreen;
                _statusLabel.Text = $"Saved at {DateTime.Now:HH:mm:ss}";
                UpdateCurrentCharLabel();
            }
            catch (Exception ex)
            {
                _statusLabel.ForeColor = Color.DarkRed;
                _statusLabel.Text = "Save failed: " + ex.Message;
            }
        }

        /// <summary>
        /// Show the current ACT logging character below the blacklist so the
        /// user can easily see what name to add. Refreshed on save and on
        /// settings-panel mount.
        /// </summary>
        private void UpdateCurrentCharLabel()
        {
            var currentChar = ActHelpers.GetLoggingCharacterName();

            if (string.IsNullOrWhiteSpace(currentChar))
            {
                _currentCharLabel.Text = "(ACT has not detected a logging character yet)";
                _currentCharLabel.ForeColor = SystemColors.GrayText;
                return;
            }

            if (_config.IsBlacklisted(currentChar))
            {
                _currentCharLabel.Text = $"Currently logging as: {currentChar}  —  BLACKLISTED";
                _currentCharLabel.ForeColor = Color.DarkRed;
            }
            else
            {
                _currentCharLabel.Text = $"Currently logging as: {currentChar}";
                _currentCharLabel.ForeColor = SystemColors.GrayText;
            }
        }
    }
}
