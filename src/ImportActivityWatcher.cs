using System;
using System.Windows.Forms;
using Advanced_Combat_Tracker;

namespace EQ2Lexicon.ACTPlugin
{
    /// <summary>
    /// Watches <see cref="ActGlobals.oFormActMain"/>'s <c>ActiveControl</c>
    /// for any control whose type-name or <c>Name</c> property contains
    /// "Import". This is how we detect that the user is actively in ACT's
    /// import UI — confirmed empirically (the diagnostic-dump session that
    /// led to this code): the moment an import burst landed, ActiveControl
    /// was a custom <c>IO_ImportLog</c> control.
    ///
    /// Polled by <see cref="EncounterCapture"/>'s existing 2-second tick —
    /// no separate timer, no UI marshalling. Stamps the wall-clock at
    /// every sighting; <see cref="WasRecentImportActivity"/> answers
    /// "has the user touched the import UI in the last N seconds?".
    ///
    /// The defining test from the investigation: a fresh live encounter
    /// captures with <c>ActiveControl = TabControlPainted</c>; an import
    /// burst arrives with <c>ActiveControl = IO_ImportLog</c>. The window
    /// of overlap is generous enough (the user holds the import dialog
    /// open while ACT replays the log) that a 30-second threshold catches
    /// the import attempt even if the user clicks away by the time the
    /// encounter settles past the 15-second debounce.
    ///
    /// Why not a continuous timer of its own? EncounterCapture already
    /// polls ACT globals on a worker thread every 2s — coalescing into
    /// that loop keeps the moving parts down and means we can't drift
    /// out of sync with the capture decisions that consume the result.
    /// </summary>
    internal sealed class ImportActivityWatcher
    {
        /// <summary>
        /// Window after the last "import control was focused" sighting
        /// during which a newly-captured encounter is still considered
        /// import-tainted. 30s comfortably covers the longest realistic
        /// gap between "user clicks Import" and "encounter settles + gets
        /// captured by the polling path".
        /// </summary>
        public TimeSpan RecentThreshold { get; set; } = TimeSpan.FromSeconds(30);

        private readonly object _lock = new object();
        private DateTime _lastImportActivityAtUtc = DateTime.MinValue;

        /// <summary>
        /// Called from <see cref="EncounterCapture"/>'s poll tick. Reads
        /// the current ActiveControl and stamps the timestamp if it's an
        /// import-related control. Swallows every exception — diagnostic
        /// instrumentation must never crash ACT or the capture path.
        /// </summary>
        public void Probe()
        {
            try
            {
                var control = ActGlobals.oFormActMain?.ActiveControl;
                if (!IsImportControl(control)) return;
                lock (_lock)
                {
                    _lastImportActivityAtUtc = DateTime.UtcNow;
                }
            }
            catch
            {
                // Swallow. Reading ActiveControl on the wrong thread can
                // sometimes throw on older WinForms; the next tick will
                // simply try again.
            }
        }

        /// <summary>
        /// True if <see cref="Probe"/> has sighted an import-related
        /// control within the last <see cref="RecentThreshold"/>.
        /// Returns false if no sighting has ever happened.
        /// </summary>
        public bool WasRecentImportActivity()
        {
            DateTime lastAt;
            lock (_lock) { lastAt = _lastImportActivityAtUtc; }
            if (lastAt == DateTime.MinValue) return false;
            return (DateTime.UtcNow - lastAt) < RecentThreshold;
        }

        /// <summary>
        /// Wrapper around <see cref="ImportControlClassifier.IsImportControl"/>
        /// — extracts the type name + control name from a real
        /// WinForms <see cref="Control"/> reference and delegates the
        /// rule itself to the Core classifier (which is unit-tested).
        /// Splits the WinForms-coupled ceremony from the pure
        /// predicate so the rule can be pinned without ACT installed.
        /// </summary>
        public static bool IsImportControl(Control? control)
        {
            if (control == null) return false;
            try
            {
                return ImportControlClassifier.IsImportControl(
                    control.GetType().Name,
                    control.Name);
            }
            catch
            {
                // GetType / Name access may throw if the control was
                // disposed between probe ticks — treat as "no signal".
                return false;
            }
        }
    }
}
