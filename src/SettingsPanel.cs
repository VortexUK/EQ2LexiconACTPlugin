using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;
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
            // Slightly muddier than InputBg — used as the read-only
            // background for the Server URL field when the current
            // user is NOT a site admin. Just dark enough to read as
            // "disabled" without losing the typed URL.
            public static readonly Color InputBgDisabled = Color.FromArgb(34, 37, 44);
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
        private readonly EventLog _eventLog;

        // Configuration card
        private readonly TextBox _serverUrl;
        private readonly Label _serverUrlLock;   // 🔒 glyph next to the URL field; visible only when locked
        private readonly TextBox _apiToken;
        private readonly CheckBox _uploadEnabled;
        private readonly Button _saveBtn;
        private readonly Button _testConnectionBtn;
        private readonly Label _testStatusLabel;

        // Event log card — owner-drawn list of recent capture /
        // upload / HTTP entries. Bound to the EventLog passed in from
        // Plugin; entries arrive via EntryAdded on background threads
        // and get marshaled to the UI thread before touching the
        // ListBox. Not readonly because BuildEventLogCard wires them
        // up after the field-initializer phase.
        private ListBox _eventList = null!;
        private CheckBox _filterCapture = null!;
        private CheckBox _filterUpload = null!;
        private CheckBox _filterHttp = null!;
        private CheckBox _autoScroll = null!;
        private Label _eventCountLabel = null!;

        // Logging-as card
        private readonly Label _currentCharLabel;
        private readonly BlacklistEditor _blacklist;

        // Allowed-servers card — list of EQ2 server names the user is
        // permitted to upload from. Server-supplied (whoami) with
        // Varsoon/Wuoshi as a built-in default until the site ships
        // the field. Body is a FlowLayoutPanel we rebuild on each
        // SetAllowedServers call; the warn label flips visible when
        // the current logging server isn't on the list.
        private readonly FlowLayoutPanel _allowedServersList;
        private readonly Label _allowedServersWarning;

        // Last-captured card
        private readonly Label _captureLabel;
        private readonly Button _showPayloadBtn;
        private readonly Label _uploadStatusLabel;

        // Header status pill
        private readonly Label _headerStatusGlyph;
        private readonly Label _headerStatusText;

        // Version banner — null until SetUpdateStatus marshals a result
        // back from the GitHub check that Plugin fires on init. We add
        // the controls upfront (hidden) and toggle visibility/colour so
        // the layout doesn't reflow when the check completes.
        private readonly FlowLayoutPanel _versionCard;
        private readonly Label _versionGlyph;
        private readonly Label _versionText;
        private readonly Button _versionInstallBtn;
        private readonly Button _versionDownloadBtn;
        private readonly Label _versionInstallStatus;
        private string _latestReleaseUrl = "";

        // Plugin-supplied callback that runs the download + verify +
        // stage flow when the user clicks "Install update". Held as a
        // delegate so SettingsPanel doesn't take a hard reference on
        // the Plugin class — symmetrical with the existing _onSave +
        // GetLastCapturedPayloadJson hooks.
        public Func<Task>? OnInstallUpdateClicked { get; set; }

        // Updated by the Plugin when a new encounter is captured. Held as a
        // delegate so we don't take a hard reference on EncounterCapture from
        // the UI layer.
        public Func<string>? GetLastCapturedPayloadJson { get; set; }

        public SettingsPanel(PluginConfig config, Action<PluginConfig> onSave, UploadClient uploadClient, EventLog eventLog)
        {
            _config = config;
            _onSave = onSave;
            _uploadClient = uploadClient;
            _eventLog = eventLog;

            Dock = DockStyle.Fill;
            // Bumped from a uniform 16 to give the layout some
            // breathing room from the ACT tab edges — the top is
            // smaller (20) so the title sits visually balanced with
            // the side gutters.
            Padding = new Padding(24, 20, 24, 24);
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

            // ── Version banner ─────────────────────────────────────────────
            // Built collapsed (Visible=false). Plugin's update check
            // populates it via SetUpdateStatus once the GitHub fetch
            // returns. Sized like a card but presented inline above
            // CONFIGURATION because it's a status, not a settings group.
            // Card now uses TopDown so the install-status label can
            // appear on its own row below the buttons when present.
            _versionCard = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = T.Card,
                Padding = new Padding(CardPad, 12, CardPad, 12),
                Margin = new Padding(0, 0, 0, 16),
                MinimumSize = new Size(CardWidth, 0),
                MaximumSize = new Size(CardWidth, 99999),
                Visible = false,
            };
            _versionCard.Paint += (s, e) =>
            {
                RoundedButton.PaintRoundedCardBorder(
                    e.Graphics, _versionCard.ClientRectangle, T.CardBorder, 8);
            };

            // Row 1: glyph + text label, side by side.
            var versionRow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = T.Card,
                Margin = new Padding(0),
                Padding = new Padding(0),
            };
            _versionGlyph = new Label
            {
                Text = "●",
                Font = new Font("Segoe UI", 11f),
                ForeColor = T.TextMuted,
                AutoSize = true,
                BackColor = T.Card,
                Margin = new Padding(0, 4, 8, 0),
            };
            _versionText = new Label
            {
                Text = "",
                Font = new Font("Segoe UI", 9f),
                ForeColor = T.Text,
                AutoSize = true,
                BackColor = T.Card,
                MaximumSize = new Size(CardWidth - 60, 0),
                Margin = new Padding(0, 6, 0, 0),
            };
            EnableUnicodeFontFallback(_versionText);
            versionRow.Controls.Add(_versionGlyph);
            versionRow.Controls.Add(_versionText);
            _versionCard.Controls.Add(versionRow);

            // Row 2: button row with Install (primary) + Download (secondary).
            var versionButtonRow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = T.Card,
                Margin = new Padding(0, 10, 0, 0),
                Padding = new Padding(0),
                Visible = false,  // toggled with the buttons via SetUpdateStatus
            };
            _versionInstallBtn = MakeButton("Install update", primary: true);
            _versionInstallBtn.Margin = new Padding(0);
            _versionInstallBtn.Click += OnVersionInstallClicked;
            _versionDownloadBtn = MakeButton("Download in browser", primary: false);
            _versionDownloadBtn.Margin = new Padding(8, 0, 0, 0);
            _versionDownloadBtn.Click += (s, e) =>
            {
                if (string.IsNullOrEmpty(_latestReleaseUrl)) return;
                try
                {
                    System.Diagnostics.Process.Start(_latestReleaseUrl);
                }
                catch
                {
                    // .NET Framework's Process.Start swallows shell errors
                    // sometimes. The URL is in the banner text too, so the
                    // user can copy it if the click no-ops.
                }
            };
            versionButtonRow.Controls.Add(_versionInstallBtn);
            versionButtonRow.Controls.Add(_versionDownloadBtn);
            _versionCard.Controls.Add(versionButtonRow);

            // Row 3: progress / result of an install click. Shown only
            // once the user clicks "Install update".
            _versionInstallStatus = new Label
            {
                Text = "",
                Font = new Font("Segoe UI", 8.75f, FontStyle.Italic),
                ForeColor = T.TextMuted,
                AutoSize = true,
                BackColor = T.Card,
                MaximumSize = new Size(CardWidth - 32, 0),
                Margin = new Padding(0, 8, 0, 0),
                Visible = false,
            };
            EnableUnicodeFontFallback(_versionInstallStatus);
            _versionCard.Controls.Add(_versionInstallStatus);

            stack.Controls.Add(_versionCard);

            // ── Card: Configuration ────────────────────────────────────────
            var cfgCard = MakeCard("⚙ CONFIGURATION");
            stack.Controls.Add(cfgCard);

            // Server URL row — TextBox + lock glyph laid out side-by-side
            // so we can swap the glyph's visibility based on admin state
            // without reflowing the column. Wrapped in a FlowLayoutPanel
            // sized to the card width.
            cfgCard.Controls.Add(MakeFieldLabel("Server URL"));
            var serverUrlRow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = T.Card,
                Margin = new Padding(0),
                Padding = new Padding(0),
            };
            _serverUrl = MakeTextBox(_config.ServerUrl);
            _serverUrl.Width = InputWidth - 28; // leave 28px for the lock glyph
            serverUrlRow.Controls.Add(_serverUrl);
            _serverUrlLock = new Label
            {
                Text = "🔒",
                Font = new Font("Segoe UI Symbol", 10f),
                ForeColor = T.TextMuted,
                AutoSize = true,
                BackColor = T.Card,
                Margin = new Padding(6, 3, 0, 0),
                Visible = true,
            };
            EnableUnicodeFontFallback(_serverUrlLock);
            var lockTooltip = new ToolTip();
            lockTooltip.SetToolTip(_serverUrlLock,
                "Endpoint is fixed for non-admin accounts. " +
                "Log in with an admin token to edit.");
            serverUrlRow.Controls.Add(_serverUrlLock);
            cfgCard.Controls.Add(serverUrlRow);
            // Start locked — Plugin's opportunistic whoami flips it open
            // if the token resolves to an admin account.
            SetAdminState(false);

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
            EnableUnicodeFontFallback(_testStatusLabel);
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
            EnableUnicodeFontFallback(_currentCharLabel);
            logCard.Controls.Add(_currentCharLabel);

            logCard.Controls.Add(MakeFieldLabel("Don't upload as"));
            logCard.Controls.Add(MakeHint(
                "One row per character. Leave Server as \"(any)\" to skip the character on every server, " +
                "or pick a server to skip just that character/server combination."));
            // Doubled visible area vs the old TextBox (was 84px). Editor
            // scrolls vertically when the row count exceeds the
            // visible region.
            _blacklist = new BlacklistEditor(_config.BlacklistedEntries, InputWidth, 180);
            logCard.Controls.Add(_blacklist);
            UpdateCurrentCharLabel();

            // ── Card: Allowed servers ──────────────────────────────────────
            // Read-only list of EQ2 server names the user is permitted
            // to upload parses from. Populated by Plugin from the
            // whoami response; defaults to Varsoon + Wuoshi until the
            // server starts returning the field. Sits between LOGGING
            // AS and LAST CAPTURED because the question it answers is
            // "given the character I'm logging as, is this fight going
            // to be uploaded?" — a natural follow-on from "who am I
            // logging as right now".
            var allowedCard = MakeCard("✦ ALLOWED SERVERS");
            stack.Controls.Add(allowedCard);

            allowedCard.Controls.Add(MakeHint(
                "EQ2 servers the EQ2 Lexicon site permits parses from for your account. " +
                "Set by the site; this list is read-only here."));

            _allowedServersList = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = T.Card,
                Margin = new Padding(0, 4, 0, 0),
                Padding = new Padding(0),
            };
            allowedCard.Controls.Add(_allowedServersList);

            _allowedServersWarning = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(InputWidth, 0),
                Margin = new Padding(0, 10, 0, 0),
                Font = new Font("Segoe UI", 8.75f, FontStyle.Italic),
                ForeColor = T.Warning,
                BackColor = T.Card,
                Text = "",
                Visible = false,
            };
            EnableUnicodeFontFallback(_allowedServersWarning);
            allowedCard.Controls.Add(_allowedServersWarning);

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
            EnableUnicodeFontFallback(_captureLabel);
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
            EnableUnicodeFontFallback(_uploadStatusLabel);
            capCard.Controls.Add(_uploadStatusLabel);

            // ── Root layout: 2-column TableLayoutPanel ─────────────────────
            // Col 0 is an ABSOLUTE pixel width (not AutoSize) — the
            // cards inside use a fixed CardWidth=620, but a child
            // Panel with Dock=Fill doesn't propagate that width back
            // up to the TableLayoutPanel's auto-size measurement, so
            // an AutoSize column collapses to ~200px and the cards
            // visibly bleed under the log column. Pinning column 0
            // at LeftColumnWidth makes the layout deterministic. Col
            // 1 (Percent 100) absorbs whatever horizontal space the
            // ACT tab gives us beyond that and feeds the log card.
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = T.Bg,
                Padding = new Padding(0),
                Margin = new Padding(0),
                AutoSize = false,
            };
            // CardWidth (620) + 12px gutter to the log card + 18px
            // safety margin so the vertical scrollbar that appears
            // when content overflows doesn't eat into the card edge.
            const int LeftColumnWidth = CardWidth + 30;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LeftColumnWidth));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            // The card stack already AutoSize-grows vertically; wrap it
            // in a Panel with AutoScroll so the cards can scroll
            // independently of the log column. Right-padding leaves
            // room for the (possibly hidden) vertical scrollbar so
            // the cards never get clipped by it.
            var stackHost = new Panel
            {
                AutoScroll = true,
                BackColor = T.Bg,
                Dock = DockStyle.Fill,
                Padding = new Padding(0, 0, 18, 0),
                Margin = new Padding(0),
            };
            stackHost.Controls.Add(stack);
            root.Controls.Add(stackHost, 0, 0);

            root.Controls.Add(BuildEventLogCard(), 1, 0);
            // Outer-level scrolling now lives on the per-column
            // panels (stackHost on the left, ListBox on the right).
            // Turn the panel-level autoscroll off so we don't end
            // up with double scrollbars.
            AutoScroll = false;
            Controls.Add(root);
            UpdateHeaderStatus();

            // Replay any backlog into the list, then attach for live
            // updates. Same-thread here — we're still in the
            // constructor on the UI thread.
            HydrateEventLog();
            _eventLog.EntryAdded += OnEventLogEntryAdded;
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
        /// Surface the result of the version-check that Plugin fires on
        /// init. Drives the banner above the Configuration card:
        /// - Current      → muted small "v0.1.8 — up to date" (rarely shown)
        /// - SlightlyStale → yellow banner + Download button
        /// - TooOld       → red banner "uploads blocked" + Download button
        /// - DevBuild     → muted "v0.1.9-dev" line (no nag for maintainers)
        /// - Unknown      → banner stays hidden (failed-open)
        /// Marshals to the UI thread because Plugin invokes it from the
        /// background update-check Task.
        /// </summary>
        public void SetUpdateStatus(UpdateCheckResult result)
        {
            if (InvokeRequired)
            {
                BeginInvoke((Action)(() => SetUpdateStatus(result)));
                return;
            }
            if (result == null || result.Status == UpdateStatus.Unknown)
            {
                _versionCard.Visible = false;
                return;
            }

            _latestReleaseUrl = result.LatestReleaseUrl;
            // Install button needs both a download URL and a digest to
            // verify against. Without a digest we refuse to auto-install
            // (PluginUpdater enforces this) — show only the browser fallback.
            var canAutoInstall =
                !string.IsNullOrWhiteSpace(result.LatestDllUrl) &&
                !string.IsNullOrWhiteSpace(result.LatestDllSha256);

            // The button row container shows/hides the buttons together;
            // Install vs Download visibility within the row is gated by
            // whether auto-install is possible.
            var buttonRow = _versionInstallBtn.Parent;
            switch (result.Status)
            {
                case UpdateStatus.Current:
                    _versionGlyph.ForeColor = T.Success;
                    _versionText.ForeColor = T.TextMuted;
                    _versionText.Text = $"v{result.CurrentVersion} — up to date";
                    if (buttonRow != null) buttonRow.Visible = false;
                    _versionInstallStatus.Visible = false;
                    break;
                case UpdateStatus.SlightlyStale:
                    _versionGlyph.ForeColor = T.Warning;
                    _versionText.ForeColor = T.Text;
                    _versionText.Text =
                        $"Update available: you're on v{result.CurrentVersion}, latest is v{result.LatestVersion}.";
                    if (buttonRow != null) buttonRow.Visible = true;
                    _versionInstallBtn.Visible = canAutoInstall;
                    _versionDownloadBtn.Visible = true;
                    break;
                case UpdateStatus.TooOld:
                    _versionGlyph.ForeColor = T.Danger;
                    _versionText.ForeColor = T.Danger;
                    _versionText.Text =
                        $"v{result.CurrentVersion} is too old (latest v{result.LatestVersion}). " +
                        "Uploads are blocked until you update.";
                    if (buttonRow != null) buttonRow.Visible = true;
                    _versionInstallBtn.Visible = canAutoInstall;
                    _versionDownloadBtn.Visible = true;
                    break;
                case UpdateStatus.DevBuild:
                    _versionGlyph.ForeColor = T.TextMuted;
                    _versionText.ForeColor = T.TextMuted;
                    _versionText.Text = $"v{result.CurrentVersion} (dev build)";
                    if (buttonRow != null) buttonRow.Visible = false;
                    _versionInstallStatus.Visible = false;
                    break;
            }
            _versionCard.Visible = true;
        }

        /// <summary>
        /// Update the small italic status line under the version
        /// banner's button row. Shown after the user clicks "Install
        /// update" — runs through "Downloading…" → "Verifying…" →
        /// either the staged-OK message or a failure reason. Marshals
        /// to the UI thread because Plugin's download orchestration
        /// runs on a worker.
        /// </summary>
        public void SetUpdateInstallStatus(string message, bool success)
        {
            if (InvokeRequired)
            {
                BeginInvoke((Action)(() => SetUpdateInstallStatus(message, success)));
                return;
            }
            _versionInstallStatus.Text = message ?? "";
            _versionInstallStatus.ForeColor = success ? T.Success : T.Danger;
            _versionInstallStatus.Visible = !string.IsNullOrEmpty(message);
            // Disable the buttons after a successful stage so the user
            // can't accidentally re-download. A failed install leaves
            // them clickable so they can retry or fall back to browser.
            if (success)
            {
                _versionInstallBtn.Enabled = false;
                _versionDownloadBtn.Enabled = false;
            }
        }

        private async void OnVersionInstallClicked(object sender, EventArgs e)
        {
            if (OnInstallUpdateClicked == null) return;
            // Gray out immediately so a fast double-click doesn't kick
            // off two concurrent downloads. Plugin's orchestration
            // re-enables only on failure.
            _versionInstallBtn.Enabled = false;
            _versionInstallStatus.ForeColor = T.TextMuted;
            _versionInstallStatus.Text = "Starting…";
            _versionInstallStatus.Visible = true;
            try
            {
                await OnInstallUpdateClicked();
            }
            catch (Exception ex)
            {
                // Belt-and-brace — Plugin should surface errors via
                // SetUpdateInstallStatus, but if something escapes
                // here we still want a visible message rather than a
                // mysteriously-disabled button.
                SetUpdateInstallStatus("Install failed: " + ex.Message, success: false);
            }
            finally
            {
                // Only re-enable on failure (Success path disables
                // both buttons explicitly in SetUpdateInstallStatus).
                if (_versionInstallStatus.ForeColor != T.Success)
                {
                    _versionInstallBtn.Enabled = true;
                }
            }
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
                // Reflect the admin gate immediately — if the user
                // just pasted an admin token, unlock the URL field
                // without making them click Save first. Fail-CLOSED
                // on test failure so a stale unlock can't survive a
                // token swap.
                SetAdminState(result.Success && result.IsAdmin);
            }
            catch (Exception)
            {
                // TestConnectionAsync already wraps the expected exception
                // types (TaskCanceled / HttpRequest) into a Result. If
                // something else escapes, surface a generic message rather
                // than leaking implementation details (stack traces, type
                // names) into the user-visible status label.
                _testStatusLabel.ForeColor = T.Danger;
                _testStatusLabel.Text = "✗ Unexpected error during test.";
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
            // Editor returns trimmed, deduped (Character, Server) rows
            // — no string splitting / line parsing needed any more.
            // Clear the legacy character list too: any historical
            // entries were folded into BlacklistedEntries by Load() →
            // MigrateLegacyBlacklist, so writing them out again would
            // re-introduce stale data on subsequent loads.
            _config.BlacklistedEntries = _blacklist.GetEntries();
            _config.BlacklistedCharacters = new List<string>();

            try
            {
                // Plugin's OnConfigSaved callback owns the actual Save(path)
                // — keeps SettingsPanel free of any persistence path concern.
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
            var serverName = ActHelpers.GetLoggingServerName();
            if (!string.IsNullOrWhiteSpace(charName) && _config.IsBlacklisted(charName, serverName))
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
        ///
        /// Appends the detected EQ2 server in parens when known
        /// (e.g. "Kayleigh (Varsoon)") — derived from the log file's
        /// parent directory; empty/unknown is silently omitted rather
        /// than drawing attention to the missing data point.
        /// </summary>
        private void UpdateCurrentCharLabel()
        {
            var currentChar = ActHelpers.GetLoggingCharacterName();
            var server = ActHelpers.GetLoggingServerName();

            if (string.IsNullOrWhiteSpace(currentChar))
            {
                _currentCharLabel.Text = "Currently logging as: (none detected yet)";
                _currentCharLabel.ForeColor = T.TextMuted;
                return;
            }

            var nameWithServer = string.IsNullOrWhiteSpace(server)
                ? currentChar
                : $"{currentChar} ({server})";

            if (_config.IsBlacklisted(currentChar, server))
            {
                _currentCharLabel.Text = $"Currently logging as: {nameWithServer}  —  blacklisted";
                _currentCharLabel.ForeColor = T.Warning;
            }
            else
            {
                _currentCharLabel.Text = $"Currently logging as: {nameWithServer}";
                _currentCharLabel.ForeColor = T.Text;
            }
        }

        /// <summary>
        /// Unhook the EventLog subscription so the EventLog doesn't
        /// keep a dead delegate alive across the plugin unload /
        /// re-enable cycle ACT triggers when it auto-updates a
        /// plugin's DLL. Without this, a re-enabled SettingsPanel
        /// would receive entries on TWO instances of itself.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing && _eventLog != null)
            {
                _eventLog.EntryAdded -= OnEventLogEntryAdded;
            }
            base.Dispose(disposing);
        }

        // ──────────────────────────────────────────────────────────────────
        // Admin-state gate (Plugin pushes this after whoami)
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Toggle the Server URL field between editable (admin) and
        /// read-only (everyone else). Plugin calls this from the
        /// startup whoami and from each Test Connection / Save
        /// click. Fails CLOSED — Plugin can only flip this to TRUE
        /// after a successful whoami that reported is_admin:true.
        /// Marshals to the UI thread because Plugin may invoke from
        /// a background task.
        /// </summary>
        public void SetAdminState(bool isAdmin)
        {
            if (InvokeRequired)
            {
                BeginInvoke((Action)(() => SetAdminState(isAdmin)));
                return;
            }
            _serverUrl.ReadOnly = !isAdmin;
            _serverUrl.BackColor = isAdmin ? T.InputBg : T.InputBgDisabled;
            _serverUrl.ForeColor = isAdmin ? T.Text : T.TextMuted;
            _serverUrlLock.Visible = !isAdmin;
        }

        /// <summary>
        /// Replace the ALLOWED SERVERS card body with the given list.
        /// Plugin calls this with the defaults at startup, then again
        /// after each whoami if the server returned a list. Surfaces
        /// a warning when the current logging server isn't on the
        /// list so the user knows uploads from that character will be
        /// rejected. Marshals to the UI thread because Plugin invokes
        /// from a background task.
        /// </summary>
        public void SetAllowedServers(IReadOnlyList<string> servers)
        {
            if (InvokeRequired)
            {
                BeginInvoke((Action)(() => SetAllowedServers(servers)));
                return;
            }
            // Also feed the list into the blacklist editor's server
            // suggestions — saves the user typing server names twice
            // and aligns the typeable values with what the site
            // actually permits.
            _blacklist?.SetSuggestedServers(servers ?? Array.Empty<string>());
            _allowedServersList.SuspendLayout();
            try
            {
                _allowedServersList.Controls.Clear();
                if (servers == null || servers.Count == 0)
                {
                    _allowedServersList.Controls.Add(new Label
                    {
                        Text = "(none — uploads disabled until the site grants access)",
                        AutoSize = true,
                        Font = new Font("Segoe UI", 9f, FontStyle.Italic),
                        ForeColor = T.TextMuted,
                        BackColor = T.Card,
                        Margin = new Padding(0, 0, 0, 2),
                    });
                }
                else
                {
                    foreach (var name in servers)
                    {
                        _allowedServersList.Controls.Add(MakeAllowedServerRow(name));
                    }
                }
            }
            finally
            {
                _allowedServersList.ResumeLayout();
            }
            UpdateAllowedServersWarning(servers);
        }

        /// <summary>
        /// Build one server row: a gold bullet plus the server name in
        /// body-text colour. Kept as a horizontal FlowLayoutPanel so
        /// the bullet and label stay aligned without me hand-rolling
        /// pixel-perfect padding for each row.
        /// </summary>
        private static FlowLayoutPanel MakeAllowedServerRow(string name)
        {
            var row = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = T.Card,
                Margin = new Padding(0, 0, 0, 2),
                Padding = new Padding(0),
            };
            row.Controls.Add(new Label
            {
                Text = "●",
                Font = new Font("Segoe UI", 9f),
                ForeColor = T.Gold,
                AutoSize = true,
                BackColor = T.Card,
                Margin = new Padding(0, 3, 8, 0),
            });
            var label = new Label
            {
                Text = name,
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = T.Text,
                AutoSize = true,
                BackColor = T.Card,
                Margin = new Padding(0, 2, 0, 0),
            };
            EnableUnicodeFontFallback(label);
            row.Controls.Add(label);
            return row;
        }

        /// <summary>
        /// If the current logging server doesn't appear in the allowed
        /// list, show a warning under the list so the user knows their
        /// fights will be rejected server-side. Matches case-
        /// insensitively because EQ2's server casing varies between
        /// log paths and display ("Varsoon" vs "varsoon").
        /// </summary>
        private void UpdateAllowedServersWarning(IReadOnlyList<string> servers)
        {
            var current = ActHelpers.GetLoggingServerName();
            if (servers == null || servers.Count == 0 || string.IsNullOrWhiteSpace(current))
            {
                _allowedServersWarning.Visible = false;
                return;
            }
            bool matched = false;
            foreach (var s in servers)
            {
                if (string.Equals(s, current, StringComparison.OrdinalIgnoreCase))
                {
                    matched = true;
                    break;
                }
            }
            if (matched)
            {
                _allowedServersWarning.Visible = false;
            }
            else
            {
                _allowedServersWarning.Text =
                    $"Currently logging from \"{current}\" — not on the allowed list. " +
                    "Uploads from this server will be rejected.";
                _allowedServersWarning.Visible = true;
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // Event log — hydrate + receive + render
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Pull the current backlog out of EventLog and populate the
        /// ListBox in one go at panel-construction time. Anything
        /// logged after this attaches via EntryAdded for incremental
        /// updates.
        /// </summary>
        private void HydrateEventLog()
        {
            var backlog = _eventLog.Snapshot();
            _eventList.BeginUpdate();
            try
            {
                foreach (var entry in backlog)
                {
                    if (PassesFilter(entry)) _eventList.Items.Add(entry);
                }
            }
            finally
            {
                _eventList.EndUpdate();
            }
            UpdateEventCounter();
        }

        private void OnEventLogEntryAdded(LogEntry entry)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke((Action)(() => OnEventLogEntryAdded(entry)));
                }
                catch (ObjectDisposedException)
                {
                    // Plugin unload race — handle is gone. Ignore.
                }
                return;
            }
            if (!PassesFilter(entry)) { UpdateEventCounter(); return; }
            _eventList.Items.Add(entry);
            // Soft cap — ListBox can hold the full EventLog cap (500),
            // but enforce here too in case a user re-filters and the
            // visible count grows unexpectedly.
            while (_eventList.Items.Count > _eventLog.Capacity)
            {
                _eventList.Items.RemoveAt(0);
            }
            if (_autoScroll.Checked && _eventList.Items.Count > 0)
            {
                _eventList.TopIndex = _eventList.Items.Count - 1;
            }
            UpdateEventCounter();
        }

        private bool PassesFilter(LogEntry entry)
        {
            switch (entry.Category)
            {
                case "capture": return _filterCapture.Checked;
                case "upload": return _filterUpload.Checked;
                case "http": return _filterHttp.Checked;
                default: return true;  // plugin/config/auth/etc — always visible
            }
        }

        private void RebuildEventList()
        {
            // User toggled a filter — redo the visible list from the
            // full backlog. ListBox doesn't have a built-in filter
            // view so we rebuild Items directly.
            _eventList.BeginUpdate();
            try
            {
                _eventList.Items.Clear();
                foreach (var entry in _eventLog.Snapshot())
                {
                    if (PassesFilter(entry)) _eventList.Items.Add(entry);
                }
            }
            finally
            {
                _eventList.EndUpdate();
            }
            if (_autoScroll.Checked && _eventList.Items.Count > 0)
            {
                _eventList.TopIndex = _eventList.Items.Count - 1;
            }
            UpdateEventCounter();
        }

        private void UpdateEventCounter()
        {
            _eventCountLabel.Text =
                $"showing {_eventList.Items.Count} of {_eventLog.Snapshot().Count}  •  " +
                $"{_eventLog.TotalLogged} since start (cap {_eventLog.Capacity})";
        }

        private void OnEventListDrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _eventList.Items.Count)
            {
                e.DrawBackground();
                e.DrawFocusRectangle();
                return;
            }
            var entry = (LogEntry)_eventList.Items[e.Index];
            Color fg;
            switch (entry.Severity)
            {
                case EventSeverity.Success: fg = T.Success; break;
                case EventSeverity.Warning: fg = T.Warning; break;
                case EventSeverity.Error: fg = T.Danger; break;
                default: fg = T.Text; break;
            }

            // Selection highlight uses the system colour the list
            // already has — fight it less by drawing our own bg too.
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (var bg = new SolidBrush(selected ? T.ButtonHover : T.InputBg))
            {
                e.Graphics.FillRectangle(bg, e.Bounds);
            }

            var localTime = entry.At.ToLocalTime().ToString("HH:mm:ss");
            var line = $"{localTime}  [{entry.Category,-7}]  {entry.Message}";
            using (var brush = new SolidBrush(fg))
            {
                TextRenderer.DrawText(
                    e.Graphics, line, e.Font, e.Bounds, fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
        }

        private void OnEventClearClicked(object sender, EventArgs e)
        {
            _eventLog.Clear();
            _eventList.Items.Clear();
            UpdateEventCounter();
        }

        private void OnEventCopyClicked(object sender, EventArgs e)
        {
            // Build plain-text dump from the FULL log (not just the
            // filtered view) so a bug-reporter who copies gets
            // everything the plugin saw, not just what they happened
            // to have filtered to.
            var lines = new List<string>();
            foreach (var entry in _eventLog.Snapshot())
            {
                lines.Add($"{entry.At:yyyy-MM-dd HH:mm:ss}Z  [{entry.Severity,-7}]  [{entry.Category,-7}]  {entry.Message}");
            }
            try
            {
                Clipboard.SetText(string.Join(Environment.NewLine, lines));
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                // Clipboard contention is a known WinForms flakiness —
                // another app holding it briefly causes this. Swallow;
                // user will retry.
            }
        }

        /// <summary>
        /// Build the event log card that occupies the right-hand column.
        /// Layout (top→bottom): toolbar row (clear/copy/auto-scroll +
        /// category filters), owner-drawn ListBox (Dock=Fill), footer
        /// counter. The card itself docks to the parent cell so it
        /// grows/shrinks with the column width ACT gives us.
        /// </summary>
        private TableLayoutPanel BuildEventLogCard()
        {
            var card = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                BackColor = T.Card,
                Padding = new Padding(CardPad, 12, CardPad, CardPad),
                Margin = new Padding(0),
                // Don't let the column be squeezed below readable —
                // the AutoSize on column 0 wins under tight widths and
                // would otherwise leave us a few pixels.
                MinimumSize = new Size(280, 0),
            };
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            card.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // title
            card.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // toolbar
            card.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // list grows
            card.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // footer
            card.Paint += (s, e) =>
            {
                RoundedButton.PaintRoundedCardBorder(
                    e.Graphics, card.ClientRectangle, T.CardBorder, 8);
            };

            // Title — Cinzel to match the rest of the panel's headings.
            card.Controls.Add(new Label
            {
                Text = "▤ EVENT LOG",
                Font = FontManager.SectionHeading(9.5f),
                ForeColor = T.Gold,
                AutoSize = true,
                BackColor = T.Card,
                UseCompatibleTextRendering = false,
                Margin = new Padding(0, 0, 0, 12),
            }, 0, 0);

            // Toolbar — Clear + Copy + Auto-scroll + filters
            var toolbar = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = T.Card,
                Margin = new Padding(0, 0, 0, 8),
                Padding = new Padding(0),
                Dock = DockStyle.Fill,
            };
            var clearBtn = MakeButton("Clear", primary: false);
            clearBtn.Margin = new Padding(0, 0, 8, 0);
            clearBtn.Click += OnEventClearClicked;
            toolbar.Controls.Add(clearBtn);

            var copyBtn = MakeButton("Copy", primary: false);
            copyBtn.Margin = new Padding(0, 0, 16, 0);
            copyBtn.Click += OnEventCopyClicked;
            toolbar.Controls.Add(copyBtn);

            var autoScroll = MakeCheckBox("Auto-scroll", true);
            autoScroll.Margin = new Padding(0, 6, 12, 0);
            toolbar.Controls.Add(autoScroll);

            var filterCapture = MakeCheckBox("Captures", true);
            filterCapture.Margin = new Padding(0, 6, 8, 0);
            filterCapture.CheckedChanged += (s, e) => RebuildEventList();
            toolbar.Controls.Add(filterCapture);

            var filterUpload = MakeCheckBox("Uploads", true);
            filterUpload.Margin = new Padding(0, 6, 8, 0);
            filterUpload.CheckedChanged += (s, e) => RebuildEventList();
            toolbar.Controls.Add(filterUpload);

            var filterHttp = MakeCheckBox("HTTP", true);
            filterHttp.Margin = new Padding(0, 6, 0, 0);
            filterHttp.CheckedChanged += (s, e) => RebuildEventList();
            toolbar.Controls.Add(filterHttp);
            card.Controls.Add(toolbar, 0, 1);

            // List body
            var list = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = T.InputBg,
                ForeColor = T.Text,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font(FontFamily.GenericMonospace, 9f),
                DrawMode = DrawMode.OwnerDrawFixed,
                IntegralHeight = false,
                ItemHeight = 18,
                SelectionMode = SelectionMode.MultiExtended,
                Margin = new Padding(0, 0, 0, 8),
            };
            list.DrawItem += OnEventListDrawItem;
            // Ctrl+C on a selection copies the chosen rows. Convenience
            // for users who only want a few entries (the "Copy" button
            // dumps everything).
            list.KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.C)
                {
                    var lines = new List<string>();
                    foreach (LogEntry entry in list.SelectedItems)
                    {
                        lines.Add($"{entry.At:yyyy-MM-dd HH:mm:ss}Z  [{entry.Severity,-7}]  [{entry.Category,-7}]  {entry.Message}");
                    }
                    if (lines.Count > 0)
                    {
                        try { Clipboard.SetText(string.Join(Environment.NewLine, lines)); }
                        catch (System.Runtime.InteropServices.ExternalException) { }
                    }
                    e.Handled = true;
                }
            };
            card.Controls.Add(list, 0, 2);

            // Footer counter
            var footer = new Label
            {
                Text = "showing 0 of 0",
                Font = new Font("Segoe UI", 8f),
                ForeColor = T.TextMuted,
                AutoSize = true,
                BackColor = T.Card,
                Margin = new Padding(0),
            };
            card.Controls.Add(footer, 0, 3);

            // Stash the controls we need later. Not readonly because
            // BuildEventLogCard is the most readable single place to
            // wire all six up.
            _eventList = list;
            _filterCapture = filterCapture;
            _filterUpload = filterUpload;
            _filterHttp = filterHttp;
            _autoScroll = autoScroll;
            _eventCountLabel = footer;

            return card;
        }

        /// <summary>
        /// Force the label to render via GDI (TextRenderer / Uniscribe)
        /// instead of GDI+ (Graphics.DrawString). GDI does proper Unicode
        /// font fallback through Uniscribe — when a glyph isn't in the
        /// declared font (Segoe UI), Windows automatically substitutes
        /// from Microsoft YaHei (CJK), Segoe UI Symbol, Segoe UI Emoji,
        /// etc. GDI+ doesn't fall back and renders missing glyphs as
        /// tofu boxes (□). Apply to any label that displays user-supplied
        /// strings (Discord names, encounter titles, character names).
        ///
        /// Application.SetCompatibleTextRenderingDefault is the
        /// process-wide knob but ACT owns that — we can only set this
        /// per-label.
        /// </summary>
        private static void EnableUnicodeFontFallback(Label label)
        {
            label.UseCompatibleTextRendering = false;
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
                // 18 below = a hair more space between the title and
                // the first card, matching the new larger inter-card
                // gap while still feeling like the title belongs
                // visually to the panel rather than to its first card.
                Margin = new Padding(0, 4, 0, 18),
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
                // Cinzel display serif — embedded as a font resource so
                // users don't need it installed. Larger than the old
                // Segoe UI size because Cinzel reads visually smaller
                // at the same point size due to its narrower stems.
                Font = FontManager.Title(15f),
                ForeColor = T.Gold,
                AutoSize = true,
                BackColor = T.Bg,
                UseCompatibleTextRendering = false,
                Margin = new Padding(0, 0, 0, 4),
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
                Padding = new Padding(CardPad, 14, CardPad, CardPad),
                Margin = new Padding(0, 0, 0, 16),
                MinimumSize = new Size(CardWidth, 0),
                MaximumSize = new Size(CardWidth, 99999),
            };
            // Rounded border drawn manually — FixedSingle would draw a
            // system-coloured rectangle that fights our theme AND has
            // no rounding. RoundedButton.PaintRoundedCardBorder
            // centralises the corner-arc geometry so the radius stays
            // consistent across every card.
            card.Paint += (s, e) =>
            {
                RoundedButton.PaintRoundedCardBorder(
                    e.Graphics, card.ClientRectangle, T.CardBorder, 8);
            };

            card.Controls.Add(new Label
            {
                Text = title,
                // Cinzel section heading at a smaller point size; the
                // wider stem of Cinzel's bold weight reads as heavier
                // than Segoe UI at the same size, so we drop a hair.
                Font = FontManager.SectionHeading(9.5f),
                ForeColor = T.Gold,
                AutoSize = true,
                BackColor = T.Card,
                UseCompatibleTextRendering = false,
                Margin = new Padding(0, 0, 0, 12),
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
            // RoundedButton owns its own state-driven background paint
            // so we feed it three colours (normal / hover / pressed)
            // instead of relying on FlatAppearance.
            //
            // Explicit Margin=0 so all callers start from the same
            // baseline. The default Button margin is (3,3,3,3), which
            // caused subtle vertical misalignment when one button in a
            // row had its margin overridden and an adjacent one didn't.
            var b = new RoundedButton
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(14, 8, 14, 8),
                Margin = new Padding(0),
                Font = new Font("Segoe UI", 9f, primary ? FontStyle.Bold : FontStyle.Regular),
                BackColor = primary ? T.PrimaryBg : T.ButtonBg,
                ForeColor = primary ? T.PrimaryFg : T.Text,
                HoverColor = primary ? T.PrimaryHover : T.ButtonHover,
                PressedColor = primary ? T.GoldSoft : T.ButtonBg,
                CornerRadius = 6,
            };
            return b;
        }
    }
}
