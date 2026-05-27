using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace EQ2Lexicon.ACTPlugin
{
    /// <summary>
    /// Row-based editor for the "Don't upload as" blacklist. Replaces
    /// the old free-text multiline TextBox with an explicit list of
    /// (Character, Server) rows so a player who has the same name on
    /// two EQ2 servers can blacklist just one of them.
    ///
    /// Each row is a small horizontal strip:
    ///     [Character ........]  [Server ......v]  [ ✕ ]
    /// Plus a "+ Add" button at the bottom that appends a new empty
    /// row. The whole control is scrollable when the row count
    /// overflows the visible area.
    ///
    /// Server suggestions come from the ALLOWED SERVERS card via
    /// <see cref="SetSuggestedServers"/> — the ComboBox drops them
    /// in as auto-complete items, with "(any)" as the leading entry
    /// to make the legacy "match any server" semantics explicit.
    /// </summary>
    internal class BlacklistEditor : UserControl
    {
        // Theme constants — kept aligned with SettingsPanel.T so a
        // future light-mode swap there propagates here without
        // touching this file. Named with "Color" suffix because
        // UserControl already exposes a Text property and a same-name
        // field would trigger CS0108 (member hides inherited member).
        private static readonly Color BgColor = Color.FromArgb(38, 42, 51);
        private static readonly Color InputBgColor = Color.FromArgb(24, 27, 33);
        private static readonly Color TextColor = Color.FromArgb(216, 216, 216);
        private static readonly Color ButtonBgColor = Color.FromArgb(54, 60, 72);
        private static readonly Color ButtonHoverColor = Color.FromArgb(72, 80, 96);
        private static readonly Color DangerColor = Color.FromArgb(229, 115, 115);
        private static readonly Color RowInputBg = Color.FromArgb(34, 37, 44);

        private const string AnyServerLabel = "(any)";
        private const int RowCharacterWidth = 200;
        private const int RowServerWidth = 150;
        private const int RemoveButtonWidth = 32;

        private readonly Panel _scrollHost;
        private readonly FlowLayoutPanel _rowsPanel;
        private readonly RoundedButton _addBtn;
        private IReadOnlyList<string> _suggestedServers = Array.Empty<string>();

        public BlacklistEditor(IReadOnlyList<BlacklistedEntry>? initial, int width, int visibleHeight)
        {
            BackColor = BgColor;
            Width = width;
            Height = visibleHeight + 44;   // room for the Add button row below the scroll area
            Margin = new Padding(0, 6, 0, 4);
            Padding = new Padding(0);

            // Scroll host wraps the row panel so we can keep a fixed
            // visible height — when the row count grows past that, a
            // vertical scrollbar appears instead of the parent card
            // ballooning downward.
            _scrollHost = new Panel
            {
                Dock = DockStyle.Top,
                Height = visibleHeight,
                BackColor = InputBgColor,
                BorderStyle = BorderStyle.FixedSingle,
                AutoScroll = true,
                Padding = new Padding(8, 8, 8, 8),
            };

            _rowsPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = InputBgColor,
                Padding = new Padding(0),
                Margin = new Padding(0),
            };
            _scrollHost.Controls.Add(_rowsPanel);

            // "+ Add" button below the scroll area — separate from the
            // scrolling region so it stays put when the user scrolls
            // through a long list.
            _addBtn = new RoundedButton
            {
                Text = "+ Add",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(12, 6, 12, 6),
                Margin = new Padding(0, 8, 0, 0),
                Font = new Font("Segoe UI", 9f),
                BackColor = ButtonBgColor,
                ForeColor = TextColor,
                HoverColor = ButtonHoverColor,
                PressedColor = ButtonBgColor,
                CornerRadius = 6,
            };
            _addBtn.Click += (s, e) => AddRow(new BlacklistedEntry(), focusCharacter: true);
            var addRow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
                BackColor = BgColor,
                Padding = new Padding(0),
                Margin = new Padding(0),
            };
            addRow.Controls.Add(_addBtn);

            // Add scroll host FIRST and addRow SECOND so when both are
            // Dock=Top, the scroll host docks first (taking the top
            // section) and the addRow docks below it. WinForms docks
            // children in z-order from BACK to FRONT, so the last
            // child added shows on the bottom.
            Controls.Add(addRow);
            Controls.Add(_scrollHost);

            if (initial != null)
            {
                foreach (var entry in initial)
                {
                    if (entry == null) continue;
                    AddRow(entry, focusCharacter: false);
                }
            }
            // Always leave at least one empty row visible — clearer
            // affordance than a totally-blank editor.
            if (_rowsPanel.Controls.Count == 0)
            {
                AddRow(new BlacklistedEntry(), focusCharacter: false);
            }
        }

        /// <summary>
        /// Replace the Server combo's suggestion list. Existing rows
        /// pick up the new items immediately; newly-added rows get
        /// them at creation time. "(any)" is always prepended.
        /// </summary>
        public void SetSuggestedServers(IReadOnlyList<string> servers)
        {
            _suggestedServers = servers ?? Array.Empty<string>();
            foreach (RowControls row in EnumerateRows())
            {
                RefreshComboItems(row.ServerCombo);
            }
        }

        /// <summary>
        /// Snapshot of the current rows as a list of entries. Trims
        /// whitespace, drops fully-empty rows, dedupes by
        /// (character, server) case-insensitively, and converts the
        /// "(any)" placeholder back to an empty server string.
        /// </summary>
        public List<BlacklistedEntry> GetEntries()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<BlacklistedEntry>();
            foreach (RowControls row in EnumerateRows())
            {
                var character = (row.CharacterBox.Text ?? "").Trim();
                if (character.Length == 0) continue;
                var server = (row.ServerCombo.Text ?? "").Trim();
                if (string.Equals(server, AnyServerLabel, StringComparison.OrdinalIgnoreCase))
                {
                    server = "";
                }
                var dedupeKey = character.ToLowerInvariant() + "|" + server.ToLowerInvariant();
                if (!seen.Add(dedupeKey)) continue;
                result.Add(new BlacklistedEntry { Character = character, Server = server });
            }
            return result;
        }

        // ──────────────────────────────────────────────────────────────────
        // Row management
        // ──────────────────────────────────────────────────────────────────

        private struct RowControls
        {
            public FlowLayoutPanel Container;
            public TextBox CharacterBox;
            public ComboBox ServerCombo;
            public RoundedButton RemoveBtn;
        }

        private IEnumerable<RowControls> EnumerateRows()
        {
            foreach (Control c in _rowsPanel.Controls)
            {
                if (c is FlowLayoutPanel row && row.Tag is RowControls rc) yield return rc;
            }
        }

        private void AddRow(BlacklistedEntry entry, bool focusCharacter)
        {
            var row = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = InputBgColor,
                Margin = new Padding(0, 0, 0, 6),
                Padding = new Padding(0),
            };

            var characterBox = new TextBox
            {
                Text = entry.Character ?? "",
                Width = RowCharacterWidth,
                Font = new Font("Segoe UI", 9.5f),
                BackColor = RowInputBg,
                ForeColor = TextColor,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(0, 0, 8, 0),
            };

            var serverCombo = new ComboBox
            {
                Width = RowServerWidth,
                Font = new Font("Segoe UI", 9.5f),
                BackColor = RowInputBg,
                ForeColor = TextColor,
                FlatStyle = FlatStyle.Flat,
                DropDownStyle = ComboBoxStyle.DropDown,  // typeable AND dropdown
                Margin = new Padding(0, 0, 8, 0),
            };
            RefreshComboItems(serverCombo);
            // Display "(any)" for an empty saved server so the
            // semantics are explicit; otherwise show the saved name.
            serverCombo.Text = string.IsNullOrWhiteSpace(entry.Server) ? AnyServerLabel : entry.Server;

            var removeBtn = new RoundedButton
            {
                Text = "✕",
                Width = RemoveButtonWidth,
                Height = characterBox.PreferredHeight,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                BackColor = ButtonBgColor,
                ForeColor = DangerColor,
                HoverColor = ButtonHoverColor,
                PressedColor = ButtonBgColor,
                CornerRadius = 5,
                Margin = new Padding(0),
                Padding = new Padding(0),
                AutoSize = false,
            };
            removeBtn.Click += (s, e) =>
            {
                _rowsPanel.Controls.Remove(row);
                row.Dispose();
                // Never leave the editor visibly empty — re-add a
                // blank row so the user sees the affordance.
                if (_rowsPanel.Controls.Count == 0)
                {
                    AddRow(new BlacklistedEntry(), focusCharacter: true);
                }
            };

            row.Controls.Add(characterBox);
            row.Controls.Add(serverCombo);
            row.Controls.Add(removeBtn);
            row.Tag = new RowControls
            {
                Container = row,
                CharacterBox = characterBox,
                ServerCombo = serverCombo,
                RemoveBtn = removeBtn,
            };
            _rowsPanel.Controls.Add(row);

            if (focusCharacter)
            {
                // Defer until WinForms has actually realized the
                // control hierarchy — Focus() before the row is in
                // the visual tree silently no-ops.
                BeginInvoke((Action)(() => characterBox.Focus()));
            }
        }

        private void RefreshComboItems(ComboBox combo)
        {
            var preserved = combo.Text;
            combo.BeginUpdate();
            try
            {
                combo.Items.Clear();
                combo.Items.Add(AnyServerLabel);
                foreach (var s in _suggestedServers)
                {
                    if (string.IsNullOrWhiteSpace(s)) continue;
                    combo.Items.Add(s);
                }
            }
            finally
            {
                combo.EndUpdate();
            }
            // Re-apply the user's typed/selected value — Items.Clear()
            // blanks the Text on a DropDown combo otherwise.
            combo.Text = preserved;
        }
    }
}
