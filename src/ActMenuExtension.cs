using System;
using System.ComponentModel;
using System.Windows.Forms;
using Advanced_Combat_Tracker;

namespace EQ2Lexicon.ACTPlugin
{
    /// <summary>
    /// Adds an "Upload to EQ2 Lexicon" item to the right-click menu on
    /// ACT's encounter tree. Click → invokes the callback the Plugin
    /// supplies, which runs the manual-upload path (bypasses blacklist
    /// + upload-enabled toggle, still enforces token + version + HMAC +
    /// placeholder-title check).
    ///
    /// ── ACT integration notes ──────────────────────────────────────────
    ///
    /// ACT does NOT expose a documented extension point for its
    /// context menu. Plugins reach into the WinForms control tree by
    /// name and mutate the existing ContextMenuStrip. Reference
    /// implementation: ActStatter
    /// (https://github.com/eq2reapp/ActStatter/blob/main/StatterMain.cs).
    ///
    ///   * The encounter view is a TreeView named "tvDG" — NOT
    ///     "lvEncounters" or anything ListView-shaped. The MainTreeView
    ///     property exists but is documented as "do not enumerate"
    ///     (nodes are populated lazily on expand).
    ///
    ///   * The controls do NOT exist at IActPluginV1.InitPlugin() time.
    ///     A delayed WinForms Timer (5s) waits for ACT's UI to finish
    ///     loading before walking the tree. One-shot — if tvDG still
    ///     isn't there at 5s, the menu just won't appear (rare; the
    ///     user can disable+reenable the plugin to retry).
    ///
    ///   * Each encounter TreeNode has its Tag set to the literal string
    ///     "EncounterData". Zone nodes have a different tag. Walk up
    ///     parents until we find one — handles right-clicking on a
    ///     child element under an encounter node.
    ///
    ///   * Resolution: ActGlobals.oFormActMain.ZoneList[parent.Index]
    ///     .Items[node.Index] returns the live EncounterData. ACT's
    ///     "All" pseudo-encounter is not in the tree as a tagged node,
    ///     so the Tag check filters it out for free.
    ///
    ///   * DeInitPlugin MUST unsubscribe the handlers and Remove the
    ///     menu item. ACT's auto-updater swaps plugin DLLs at runtime by
    ///     disabling + re-enabling — leftover dead delegates pile up
    ///     otherwise (and your menu item appears twice after a reload).
    ///
    /// Threading: WinForms Timer Tick fires on the UI thread, as do
    /// the ContextMenuStrip Opening + Click handlers. No marshalling
    /// needed for anything touched here. The callback to Plugin may
    /// kick off async upload work — that's Plugin's concern.
    /// </summary>
    internal sealed class ActMenuExtension : IDisposable
    {
        // Walked by name from ActGlobals.oFormActMain. ActStatter and
        // other EQ2 plugins use the same string — change only if a
        // future ACT release renames the control.
        private const string EncounterTreeName = "tvDG";

        // 5s lines up with ActStatter's empirically-derived wait. Long
        // enough for ACT's UI to construct the encounter tree on cold
        // start; short enough that a user opening the plugin tab right
        // after launch doesn't feel a noticeable lag.
        private const int AttachDelayMs = 5000;

        private readonly Action<EncounterData> _onUploadClicked;
        private readonly Timer _delayedAttach;

        // Populated on successful attach. Null means we never found
        // the tree (rare) or we've been Disposed.
        private TreeView? _tvDG;
        private ContextMenuStrip? _menu;
        private ToolStripMenuItem? _menuItem;
        private CancelEventHandler? _openingHandler;

        /// <summary>True once the menu item has been added to ACT's
        /// context menu. Useful for diagnostics if the menu doesn't
        /// appear (e.g. ACT changed the tvDG control name).</summary>
        public bool IsAttached => _menuItem != null;

        public ActMenuExtension(Action<EncounterData> onUploadClicked)
        {
            _onUploadClicked = onUploadClicked ?? throw new ArgumentNullException(nameof(onUploadClicked));

            _delayedAttach = new Timer { Interval = AttachDelayMs };
            _delayedAttach.Tick += (s, e) =>
            {
                _delayedAttach.Stop();
                TryAttach();
            };
            _delayedAttach.Start();
        }

        private void TryAttach()
        {
            try
            {
                _tvDG = FindControl(ActGlobals.oFormActMain, EncounterTreeName) as TreeView;
                if (_tvDG?.ContextMenuStrip == null)
                {
                    // Either tvDG doesn't exist (ACT changed?) or its
                    // ContextMenuStrip hasn't been wired up. Don't
                    // create our own — appending to ACT's keeps the
                    // existing items (Clear, Properties, etc.) intact.
                    return;
                }
                _menu = _tvDG.ContextMenuStrip;

                _menuItem = new ToolStripMenuItem("Upload to EQ2 Lexicon");
                _menuItem.Click += OnMenuClick;
                _menu.Items.Add(_menuItem);

                _openingHandler = OnMenuOpening;
                _menu.Opening += _openingHandler;
            }
            catch
            {
                // Menu-extension failures must never crash ACT. The
                // worst-case is "the right-click menu doesn't have our
                // item" — the auto-upload path still works regardless.
            }
        }

        private void OnMenuOpening(object sender, CancelEventArgs e)
        {
            if (_menuItem == null) return;
            // Gray out when there's no real encounter selected (zone
            // node, empty area, etc.) so the user gets the right
            // affordance instead of clicking a no-op item.
            //
            // Also greyed out for the Import/Merge zone — those are
            // user-customised parses (merged fights, imported logs)
            // that we never want uploaded. The click handler re-checks
            // defensively in case a fast click somehow races the
            // Opening event's enable computation.
            var enc = GetSelectedEncounter();
            _menuItem.Enabled = enc != null && !EncounterZone.IsImportOrMerge(enc.ZoneName);
        }

        private void OnMenuClick(object sender, EventArgs e)
        {
            var enc = GetSelectedEncounter();
            if (enc == null) return;
            try
            {
                _onUploadClicked(enc);
            }
            catch
            {
                // Plugin's callback is expected to surface failures via
                // SettingsPanel.SetUploadStatus — this is a final
                // safety net so a bug in the upload path never throws
                // an unhandled exception into ACT's UI thread.
            }
        }

        /// <summary>
        /// Resolve the current TreeView selection to a real
        /// EncounterData, or null if the selection isn't on an
        /// encounter node (zone, empty area, control selected
        /// programmatically with a non-Encounter tag, etc.).
        /// </summary>
        private EncounterData? GetSelectedEncounter()
        {
            var tn = _tvDG?.SelectedNode;
            if (tn == null) return null;
            // Walk up to the EncounterData-tagged node. The user might
            // have right-clicked on a child element (some ACT plugins
            // nest more under each encounter).
            while (tn != null && !"EncounterData".Equals(tn.Tag))
            {
                tn = tn.Parent;
            }
            if (tn?.Parent == null) return null;
            try
            {
                return ActGlobals.oFormActMain?.ZoneList[tn.Parent.Index].Items[tn.Index];
            }
            catch
            {
                // Indices can briefly point at stale state if ACT
                // mutates ZoneList during our walk. Treat as "no
                // selection" rather than throwing — the user just
                // right-clicks again.
                return null;
            }
        }

        /// <summary>
        /// Recursive depth-first walk of the WinForms control tree to
        /// find a child by Name. ACT's encounter tree is several
        /// nested SplitContainers deep so a single Controls[name]
        /// lookup won't find it.
        /// </summary>
        private static Control? FindControl(Control? parent, string name)
        {
            if (parent == null) return null;
            if (parent.Name == name) return parent;
            foreach (Control c in parent.Controls)
            {
                var found = FindControl(c, name);
                if (found != null) return found;
            }
            return null;
        }

        public void Dispose()
        {
            try
            {
                _delayedAttach.Stop();
                _delayedAttach.Dispose();
            }
            catch { /* swallow */ }

            try
            {
                if (_menuItem != null && _menu != null)
                {
                    _menuItem.Click -= OnMenuClick;
                    _menu.Items.Remove(_menuItem);
                    _menuItem.Dispose();
                }
                if (_openingHandler != null && _menu != null)
                {
                    _menu.Opening -= _openingHandler;
                }
            }
            catch
            {
                // Plugin disable/reenable cycle (or ACT shutdown) must
                // not throw. Dead delegates would be ugly but the
                // process is going down anyway in the shutdown case.
            }
            finally
            {
                _menuItem = null;
                _menu = null;
                _openingHandler = null;
                _tvDG = null;
            }
        }
    }
}
