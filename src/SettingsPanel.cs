using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace EQ2Lexicon.ACTPlugin
{
    /// <summary>
    /// Settings UI hosted inside the EQ2 Lexicon plugin tab.
    ///
    /// Dark themed, card-sectioned layout that mirrors the website's
    /// gold-on-dark aesthetic. Changes are not persisted until Save is
    /// clicked — keeps the model simple (no debounced auto-save), avoids
    /// partial writes while the user is mid-edit.
    /// </summary>
    public class SettingsPanel : UserControl
    {
        // ── Theme ──────────────────────────────────────────────────────────
        // Centralised so the whole panel stays visually consistent and a
        // future light-mode swap is one block to change.
        private static class T
        {
            public static readonly Color Bg = Color.FromArgb(30, 33, 40);    // page bg
            public static readonly Color Card = Color.FromArgb(38, 42, 51);    // card bg
            public static readonly Color CardBorder = Color.FromArgb(58, 63, 74);
            public static readonly Color Text = Color.FromArgb(216, 216, 216); // body
            public static readonly Color TextMuted = Color.FromArgb(140, 145, 155); // hints / captions
            public static readonly Color Gold = Color.FromArgb(216, 166, 87);  // brand accent
            public static readonly Color GoldSoft = Color.FromArgb(184, 142, 75);
            public static readonly Color Success = Color.FromArgb(102, 187, 106);
            public static readonly Color Warning = Color.FromArgb(255, 193, 88);
            public static readonly Color Danger = Color.FromArgb(229, 115, 115);
            public static readonly Color InputBg = Color.FromArgb(24, 27, 33);
            public static readonly Color InputBorder = Color.FromArgb(74, 79, 90);
            public static readonly Color ButtonBg = Color.FromArgb(54, 60, 72);
            public static readonly Color ButtonHover = Color.FromArgb(72, 80, 96);
            public static readonly Color PrimaryBg = Color.FromArgb(216, 166, 87);
            public static readonly Color PrimaryFg = Color.FromArgb(20, 22, 28);
            public static readonly Color PrimaryHover = Color.FromArgb(232, 184, 110);
        }

        private const int CardWidth = 620;
        private const int CardPad = 16;
        private const int InputWidth = CardWidth - (CardPad * 2);

        private readonly PluginConfig _config;
        private readonly Action<PluginConfig> _onSave;
        private readonly UploadClient _uploadClient;

        // Configuration card
        private readonly TextBox _serverUrl;
        private readonly TextBox _apiToken;
        private readonly CheckBox _uploadEnabled;
        private readonly Button _saveBtn;
        private readonly Button _testConnectionBtn;
        private readonly Label _testStatusLabel;

        // Logging-as card
        private readonly Label _currentCharLabel;
        private readonly TextBox _blacklist;

        // Last-captured card
        private readonly Label _captureLabel;
        private readonly Button _showPayloadBtn;
        private readonly Label _uploadStatusLabel;

        // Header status pill
        private readonly Label _headerStatusGlyph;
        private readonly Label _headerStatusText;

        // Updated by the Plugin when a new encounter is captured. Held as a
        // delegate so we don't take a hard reference on EncounterCapture from
        // the UI layer.
        public Func<string>? GetLastCapturedPayloadJson { get; set; }

        public SettingsPanel(PluginConfig config, Action<PluginConfig> onSave, UploadClient uploadClient)
        {
            _config = config;
            _onSave = onSave;
            _uploadClient = uploadClient;

            Dock = DockStyle.Fill;
            Padding = new Padding(16);
            BackColor = T.Bg;
            ForeColor = T.Text;
            AutoScroll = true;
            DoubleBuffered = true;

            var stack = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = T.Bg,
                Padding = new Padding(0),
                Margin = new Padding(0),
            };

            // ── Header ─────────────────────────────────────────────────────
            stack.Controls.Add(MakeHeader(out _headerStatusGlyph, out _headerStatusText));

            // ── Card: Configuration ────────────────────────────────────────
            var cfgCard = MakeCard("⚙ CONFIGURATION");
            stack.Controls.Add(cfgCard);

            cfgCard.Controls.Add(MakeFieldLabel("Server URL"));
            _serverUrl = MakeTextBox(_config.ServerUrl);
            cfgCard.Controls.Add(_serverUrl);

            cfgCard.Controls.Add(MakeSpacer(8));

            cfgCard.Controls.Add(MakeFieldLabel("API Token"));
            cfgCard.Controls.Add(MakeHint("Generate one on the site under your profile → API Tokens, then paste it here."));
            _apiToken = MakeTextBox(_config.ApiToken);
            _apiToken.Font = new Font(FontFamily.GenericMonospace, 9f);
            cfgCard.Controls.Add(_apiToken);

            cfgCard.Controls.Add(MakeSpacer(12));

            _uploadEnabled = MakeCheckBox("Enable automatic upload after each encounter", _config.UploadEnabled);
            cfgCard.Controls.Add(_uploadEnabled);

            cfgCard.Controls.Add(MakeSpacer(14));

            // Button row: Save (primary) + Test connection (secondary) + inline status
            var cfgButtons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                WrapContents = false,
                BackColor = T.Card,
                Margin = new Padding(0),
                Padding = new Padding(0),
            };
            _saveBtn = MakeButton("Save settings", primary: true);
            _saveBtn.Click += OnSaveClicked;
            cfgButtons.Controls.Add(_saveBtn);

            _testConnectionBtn = MakeButton("Test connection", primary: false);
            _testConnectionBtn.Margin = new Padding(8, 0, 0, 0);
            _testConnectionBtn.Click += OnTestConnectionClicked;
            cfgButtons.Controls.Add(_testConnectionBtn);

            _testStatusLabel = new Label
            {
                AutoSize = true,
                Margin = new Padding(12, 9, 0, 0),
                ForeColor = T.TextMuted,
                BackColor = T.Card,
                Text = "",
                MaximumSize = new Size(InputWidth - 240, 0),
            };
            cfgButtons.Controls.Add(_testStatusLabel);
            cfgCard.Controls.Add(cfgButtons);

            // ── Card: Logging as ───────────────────────────────────────────
            var logCard = MakeCard("ⓘ LOGGING AS");
            stack.Controls.Add(logCard);

            _currentCharLabel = new Label
            {
                AutoSize = true,
                Font = new Font("Segoe UI", 11f, FontStyle.Regular),
                ForeColor = T.Text,
                BackColor = T.Card,
                Margin = new Padding(0, 0, 0, 10),
                Text = "",
            };
            logCard.Controls.Add(_currentCharLabel);

            logCard.Controls.Add(MakeFieldLabel("Don't upload as"));
            logCard.Controls.Add(MakeHint("One character name per line. Encounters logged as these characters are skipped."));
            _blacklist = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                AcceptsReturn = true,
                Width = InputWidth,
                Height = 84,
                Font = new Font(FontFamily.GenericMonospace, 9f),
                Text = string.Join(Environment.NewLine, _config.BlacklistedCharacters ?? new List<string>()),
                BackColor = T.InputBg,
                ForeColor = T.Text,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(0, 6, 0, 4),
            };
            logCard.Controls.Add(_blacklist);
            UpdateCurrentCharLabel();

            // ── Card: Last captured ────────────────────────────────────────
            var capCard = MakeCard("◆ LAST CAPTURED ENCOUNTER");
            stack.Controls.Add(capCard);

            _captureLabel = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(InputWidth, 0),
                Text = "(nothing captured yet — finish an encounter in EQ2)",
                ForeColor = T.TextMuted,
                BackColor = T.Card,
                Margin = new Padding(0, 0, 0, 10),
            };
            capCard.Controls.Add(_captureLabel);

            var capButtons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                WrapContents = false,
                BackColor = T.Card,
                Margin = new Padding(0),
                Padding = new Padding(0),
            };
            _showPayloadBtn = MakeButton("Show payload", primary: false);
            _showPayloadBtn.Enabled = false;
            _showPayloadBtn.Click += OnShowPayloadClicked;
            capButtons.Controls.Add(_showPayloadBtn);
            capCard.Controls.Add(capButtons);

            _uploadStatusLabel = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(InputWidth, 0),
                Margin = new Padding(0, 10, 0, 0),
                ForeColor = T.TextMuted,
                BackColor = T.Card,
                Text = "",
            };
            capCard.Controls.Add(_uploadStatusLabel);

            Controls.Add(stack);
            UpdateHeaderStatus();
        }

        // ──────────────────────────────────────────────────────────────────
        // Public API called from the Plugin
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Called from the Plugin (off the UI thread) when EncounterCapture
        /// has new data ready. We marshal to the UI thread to refresh the
        /// label and enable the show-payload button.
        /// </summary>
        public void RefreshLastCaptured(string encId, string title, DateTime at, int combatants, int attackTypes)
        {
            if (InvokeRequired)
            {
                BeginInvoke((Action)(() => RefreshLastCaptured(encId, title, at, combatants, attackTypes)));
                return;
            }
            var titleText = string.IsNullOrEmpty(title) ? "(no title)" : title;
            _captureLabel.Text =
                $"{titleText}\r\n" +
                $"{encId}  •  {combatants} combatant{(combatants == 1 ? "" : "s")}, " +
                $"{attackTypes} attack type{(attackTypes == 1 ? "" : "s")}  •  " +
                $"captured {at:HH:mm:ss}";
            _captureLabel.ForeColor = T.Text;
            _showPayloadBtn.Enabled = !string.IsNullOrEmpty(encId);
            // New capture: clear any stale upload status until the upload
            // attempt (or skip reason) reports back.
            _uploadStatusLabel.Text = "";
        }

        /// <summary>
        /// Surface the result of an upload attempt (or the reason we skipped
        /// it — e.g. uploads disabled, blacklisted character). Marshals to
        /// the UI thread for callers on worker threads.
        /// </summary>
        public void SetUploadStatus(string message, bool success)
        {
            if (InvokeRequired)
            {
                BeginInvoke((Action)(() => SetUploadStatus(message, success)));
                return;
            }
            var glyph = success ? "●" : "●";
            _uploadStatusLabel.Text = $"{glyph} {message}  •  {DateTime.Now:HH:mm:ss}";
            _uploadStatusLabel.ForeColor = success ? T.Success : T.Danger;
        }

        // ──────────────────────────────────────────────────────────────────
        // Event handlers
        // ──────────────────────────────────────────────────────────────────

        private async void OnTestConnectionClicked(object sender, EventArgs e)
        {
            // Use current TextBox values, not the saved config — lets the user
            // verify an edit before committing it via Save.
            var url = (_serverUrl.Text ?? "").Trim().TrimEnd('/');
            var token = (_apiToken.Text ?? "").Trim();

            _testConnectionBtn.Enabled = false;
            _testStatusLabel.ForeColor = T.TextMuted;
            _testStatusLabel.Text = "Testing…";

            try
            {
                var result = await _uploadClient.TestConnectionAsync(url, token);
                _testStatusLabel.ForeColor = result.Success ? T.Success : T.Danger;
                _testStatusLabel.Text = (result.Success ? "✓ " : "✗ ") + result.Message;
            }
            catch (Exception ex)
            {
                _testStatusLabel.ForeColor = T.Danger;
                _testStatusLabel.Text = "✗ Test failed: " + ex.Message;
            }
            finally
            {
                _testConnectionBtn.Enabled = true;
            }
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
                _testStatusLabel.ForeColor = T.Success;
                _testStatusLabel.Text = $"✓ Saved at {DateTime.Now:HH:mm:ss}";
                UpdateCurrentCharLabel();
                UpdateHeaderStatus();
            }
            catch (Exception ex)
            {
                _testStatusLabel.ForeColor = T.Danger;
                _testStatusLabel.Text = "✗ Save failed: " + ex.Message;
            }
        }

        private void OnShowPayloadClicked(object sender, EventArgs e)
        {
            var json = GetLastCapturedPayloadJson?.Invoke() ?? "(no payload available)";
            using (var dlg = new Form
            {
                Text = "Last captured payload",
                Width = 900,
                Height = 600,
                StartPosition = FormStartPosition.CenterParent,
                BackColor = T.Bg,
                ForeColor = T.Text,
            })
            {
                var tb = new TextBox
                {
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Both,
                    WordWrap = false,
                    Dock = DockStyle.Fill,
                    Font = new Font(FontFamily.GenericMonospace, 9f),
                    Text = json,
                    BackColor = T.InputBg,
                    ForeColor = T.Text,
                    BorderStyle = BorderStyle.None,
                };
                dlg.Controls.Add(tb);
                dlg.ShowDialog(this);
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // Header status — derived from config every time it's relevant
        // ──────────────────────────────────────────────────────────────────

        private void UpdateHeaderStatus()
        {
            // Derive directly from in-memory config (which Save() updates),
            // so the pill stays correct without polling.
            if (string.IsNullOrWhiteSpace(_config.ServerUrl) || string.IsNullOrWhiteSpace(_config.ApiToken))
            {
                SetHeaderStatus("Not configured", T.Danger);
                return;
            }
            if (!_config.UploadEnabled)
            {
                SetHeaderStatus("Uploads disabled", T.Warning);
                return;
            }
            var charName = ActHelpers.GetLoggingCharacterName();
            if (!string.IsNullOrWhiteSpace(charName) && _config.IsBlacklisted(charName))
            {
                SetHeaderStatus($"{charName} is blacklisted", T.Warning);
                return;
            }
            SetHeaderStatus("Ready", T.Success);
        }

        private void SetHeaderStatus(string text, Color colour)
        {
            _headerStatusGlyph.ForeColor = colour;
            _headerStatusText.Text = text;
            _headerStatusText.ForeColor = colour;
        }

        /// <summary>
        /// Show the current ACT logging character at the top of the
        /// blacklist card so the user can easily see what name to add. Also
        /// drives the header pill colour.
        /// </summary>
        private void UpdateCurrentCharLabel()
        {
            var currentChar = ActHelpers.GetLoggingCharacterName();

            if (string.IsNullOrWhiteSpace(currentChar))
            {
                _currentCharLabel.Text = "Currently logging as: (none detected yet)";
                _currentCharLabel.ForeColor = T.TextMuted;
                return;
            }

            if (_config.IsBlacklisted(currentChar))
            {
                _currentCharLabel.Text = $"Currently logging as: {currentChar}  —  blacklisted";
                _currentCharLabel.ForeColor = T.Warning;
            }
            else
            {
                _currentCharLabel.Text = $"Currently logging as: {currentChar}";
                _currentCharLabel.ForeColor = T.Text;
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // Builders — themed widget factories
        // ──────────────────────────────────────────────────────────────────

        private static TableLayoutPanel MakeHeader(out Label statusGlyph, out Label statusText)
        {
            // 2-col TableLayout: title block (autosize fill) | status pill (autosize right).
            // Beats absolute Location + Anchor — TLP handles right-alignment cleanly.
            var tlp = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = T.Bg,
                Margin = new Padding(0, 0, 0, 12),
                Padding = new Padding(0),
                MinimumSize = new Size(CardWidth, 0),
                MaximumSize = new Size(CardWidth, 9999),
            };
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tlp.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var titleBlock = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = T.Bg,
                Margin = new Padding(0),
                Padding = new Padding(0),
            };
            titleBlock.Controls.Add(new Label
            {
                Text = "EQ2 LEXICON UPLOADER",
                Font = new Font("Segoe UI", 12.5f, FontStyle.Bold),
                ForeColor = T.Gold,
                AutoSize = true,
                BackColor = T.Bg,
                Margin = new Padding(0, 0, 0, 2),
            });
            titleBlock.Controls.Add(new Label
            {
                Text = "Uploads each finished ACT encounter to the EQ2 Lexicon site.",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = T.TextMuted,
                AutoSize = true,
                BackColor = T.Bg,
                Margin = new Padding(0),
            });
            tlp.Controls.Add(titleBlock, 0, 0);

            var statusBlock = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = T.Bg,
                Anchor = AnchorStyles.Right,
                Margin = new Padding(0, 8, 0, 0),
                Padding = new Padding(0),
            };
            statusGlyph = new Label
            {
                Text = "●",
                Font = new Font("Segoe UI", 10f),
                ForeColor = T.TextMuted,
                AutoSize = true,
                BackColor = T.Bg,
                Margin = new Padding(0, 0, 4, 0),
            };
            statusText = new Label
            {
                Text = "",
                Font = new Font("Segoe UI", 9f),
                ForeColor = T.TextMuted,
                AutoSize = true,
                BackColor = T.Bg,
                Margin = new Padding(0, 2, 0, 0),
            };
            statusBlock.Controls.Add(statusGlyph);
            statusBlock.Controls.Add(statusText);
            tlp.Controls.Add(statusBlock, 1, 0);

            return tlp;
        }

        /// <summary>
        /// Build a bordered card. Returns a single FlowLayoutPanel that is
        /// both the card frame and the content container — callers add their
        /// own widgets directly to it. The section-header label is added as
        /// the first child.
        ///
        /// Sizing: FlowLayoutPanel AutoSize + Min/MaxSize pins the width to
        /// CardWidth while letting the height grow with content. The earlier
        /// outer-panel-with-docked-body approach hit the classic WinForms
        /// "AutoSize parent + Dock child" zero-size loop.
        /// </summary>
        private static FlowLayoutPanel MakeCard(string title)
        {
            var card = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = T.Card,
                Padding = new Padding(CardPad, 12, CardPad, CardPad),
                Margin = new Padding(0, 0, 0, 12),
                MinimumSize = new Size(CardWidth, 0),
                MaximumSize = new Size(CardWidth, 99999),
            };
            // Custom border — FixedSingle would draw a system-coloured one
            // that fights our theme.
            card.Paint += (s, e) =>
            {
                using (var pen = new Pen(T.CardBorder, 1))
                {
                    var r = card.ClientRectangle;
                    e.Graphics.DrawRectangle(pen, 0, 0, r.Width - 1, r.Height - 1);
                }
            };

            card.Controls.Add(new Label
            {
                Text = title,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = T.Gold,
                AutoSize = true,
                BackColor = T.Card,
                Margin = new Padding(0, 0, 0, 10),
            });

            return card;
        }

        private static Label MakeFieldLabel(string text)
        {
            return new Label
            {
                Text = text,
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                ForeColor = T.TextMuted,
                AutoSize = true,
                BackColor = T.Card,
                Margin = new Padding(0, 0, 0, 4),
            };
        }

        private static Label MakeHint(string text)
        {
            return new Label
            {
                Text = text,
                Font = new Font("Segoe UI", 8.25f),
                ForeColor = T.TextMuted,
                AutoSize = true,
                BackColor = T.Card,
                MaximumSize = new Size(InputWidth, 0),
                Margin = new Padding(0, 0, 0, 4),
            };
        }

        private static Label MakeSpacer(int height)
        {
            return new Label
            {
                Height = height,
                Width = 1,
                BackColor = T.Card,
                Margin = new Padding(0),
            };
        }

        private static TextBox MakeTextBox(string initial)
        {
            return new TextBox
            {
                Text = initial,
                Width = InputWidth,
                BackColor = T.InputBg,
                ForeColor = T.Text,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(0, 0, 0, 0),
            };
        }

        private static CheckBox MakeCheckBox(string text, bool isChecked)
        {
            return new CheckBox
            {
                Text = text,
                Checked = isChecked,
                AutoSize = true,
                BackColor = T.Card,
                ForeColor = T.Text,
                Font = new Font("Segoe UI", 9f),
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(0, 0, 0, 0),
            };
        }

        private static Button MakeButton(string text, bool primary)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(12, 6, 12, 6),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9f, primary ? FontStyle.Bold : FontStyle.Regular),
                BackColor = primary ? T.PrimaryBg : T.ButtonBg,
                ForeColor = primary ? T.PrimaryFg : T.Text,
                UseVisualStyleBackColor = false,
                Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = primary ? T.PrimaryHover : T.ButtonHover;
            b.FlatAppearance.MouseDownBackColor = primary ? T.GoldSoft : T.ButtonBg;
            return b;
        }
    }
}
