using System;
using System.Collections.Generic;

namespace EQ2Lexicon.ACTPlugin
{
    /// <summary>
    /// Severity tier for an event log entry. Drives the colour the
    /// settings panel paints the row with. Kept separate from the
    /// HTTP status / capture-result distinctions so the UI doesn't
    /// have to translate "success" booleans into colours in five
    /// different call sites.
    /// </summary>
    public enum EventSeverity
    {
        Info,
        Success,
        Warning,
        Error,
    }

    /// <summary>
    /// One row in the in-memory event log. UTC timestamp so the panel
    /// can render in local time without ambiguity when ACT moves
    /// between time zones (rare but possible — laptop user travels).
    /// Plain DTO; no behaviour.
    /// </summary>
    public sealed class LogEntry
    {
        public DateTime At { get; set; }            // UTC
        public EventSeverity Severity { get; set; }
        /// <summary>"capture" | "upload" | "http" — used by the panel's filter checkboxes.</summary>
        public string Category { get; set; } = "";
        public string Message { get; set; } = "";
    }

    /// <summary>
    /// Tiny thread-safe ring-buffered event log surfaced in the settings
    /// panel. One instance per Plugin lifetime; subscribers (the
    /// settings panel) receive each entry via <see cref="EntryAdded"/>
    /// AND can pull the full backlog via <see cref="Snapshot"/> when
    /// they first attach.
    ///
    /// Lives in Core deliberately — no ACT or WinForms refs means it's
    /// unit-testable without spinning up a UI thread, and a future
    /// headless CLI could reuse it. Cap is fixed at construction so a
    /// long-running ACT session doesn't accumulate unbounded entries.
    /// </summary>
    public sealed class EventLog
    {
        private readonly int _cap;
        private readonly LinkedList<LogEntry> _entries = new LinkedList<LogEntry>();
        private readonly object _lock = new object();

        // ID monotonically increments. The UI uses it to detect "I
        // missed entries while the marshal was queued" (entries
        // evicted between subscribe and first paint).
        private long _nextId;

        /// <summary>
        /// Default cap is 500 entries — enough for a multi-hour raid
        /// session at typical capture rates (~1 entry per encounter +
        /// ~2 per upload). Override only in tests where eviction
        /// timing matters.
        /// </summary>
        public EventLog(int capacity = 500)
        {
            if (capacity < 1) capacity = 1;
            _cap = capacity;
        }

        public int Capacity => _cap;

        /// <summary>
        /// Total number of entries ever logged (including evicted).
        /// Surfaced in the UI footer ("showing N of M (cap C)") so the
        /// user knows when older events have rolled off.
        /// </summary>
        public long TotalLogged
        {
            get
            {
                lock (_lock) { return _nextId; }
            }
        }

        /// <summary>
        /// Raised whenever a new entry is added. May fire on any
        /// thread — subscribers MUST marshal if they touch UI.
        /// Handler exceptions are swallowed so a buggy subscriber
        /// can't break the log pipeline for other listeners.
        /// </summary>
        public event Action<LogEntry>? EntryAdded;

        public void Log(EventSeverity severity, string category, string message)
        {
            var entry = new LogEntry
            {
                At = DateTime.UtcNow,
                Severity = severity,
                Category = category ?? "",
                Message = message ?? "",
            };
            lock (_lock)
            {
                _entries.AddLast(entry);
                _nextId++;
                while (_entries.Count > _cap)
                {
                    _entries.RemoveFirst();
                }
            }
            var handler = EntryAdded;
            if (handler == null) return;
            foreach (var subscriber in handler.GetInvocationList())
            {
                try
                {
                    ((Action<LogEntry>)subscriber)(entry);
                }
                catch
                {
                    // One subscriber must not break the rest.
                }
            }
        }

        /// <summary>
        /// Snapshot of the current entries, oldest first. The list is
        /// a copy — safe to enumerate without holding the log lock.
        /// </summary>
        public IReadOnlyList<LogEntry> Snapshot()
        {
            lock (_lock)
            {
                var copy = new List<LogEntry>(_entries.Count);
                copy.AddRange(_entries);
                return copy;
            }
        }

        /// <summary>
        /// Empty the log. The UI's "Clear" button calls this. Does
        /// NOT reset <see cref="TotalLogged"/> — the cumulative
        /// counter keeps growing so we can still tell whether
        /// anything has happened since startup.
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _entries.Clear();
            }
        }
    }
}
