using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace EQ2Lexicon.ACTPlugin.Tests
{
    /// <summary>
    /// Tests for the ring-buffered in-memory event log used by the
    /// settings panel. Pin the eviction semantics (oldest-out at cap),
    /// the EntryAdded event fan-out, and the basic thread-safety
    /// promise — three places a future refactor could quietly break.
    /// </summary>
    public class EventLogTests
    {
        [Fact]
        public void Log_AddsEntryAndFiresEvent()
        {
            var log = new EventLog(capacity: 10);
            LogEntry? fired = null;
            log.EntryAdded += e => fired = e;

            log.Log(EventSeverity.Info, "capture", "encounter settled");

            var snap = log.Snapshot();
            Assert.Single(snap);
            Assert.Equal("encounter settled", snap[0].Message);
            Assert.Equal("capture", snap[0].Category);
            Assert.Equal(EventSeverity.Info, snap[0].Severity);
            Assert.NotNull(fired);
            Assert.Equal(snap[0].Message, fired!.Message);
        }

        [Fact]
        public void Log_TimestampIsUtc()
        {
            // We store UTC so the UI can render in local time without
            // ambiguity when the user moves time zones.
            var log = new EventLog();
            var before = DateTime.UtcNow.AddSeconds(-1);
            log.Log(EventSeverity.Info, "capture", "x");
            var after = DateTime.UtcNow.AddSeconds(1);
            var entry = log.Snapshot()[0];
            Assert.Equal(DateTimeKind.Utc, entry.At.Kind);
            Assert.InRange(entry.At, before, after);
        }

        [Fact]
        public void Log_EvictsOldestPastCapacity()
        {
            // Cap of 3 — entries 1..3 land, then 4 pushes 1 out.
            var log = new EventLog(capacity: 3);
            log.Log(EventSeverity.Info, "c", "one");
            log.Log(EventSeverity.Info, "c", "two");
            log.Log(EventSeverity.Info, "c", "three");
            log.Log(EventSeverity.Info, "c", "four");

            var snap = log.Snapshot();
            Assert.Equal(3, snap.Count);
            // Oldest dropped → "one" should be gone.
            Assert.Equal("two", snap[0].Message);
            Assert.Equal("three", snap[1].Message);
            Assert.Equal("four", snap[2].Message);
        }

        [Fact]
        public void TotalLogged_CountsEvictedEntries()
        {
            // Cumulative counter keeps growing past the cap — that's
            // how the UI footer shows "showing N of M".
            var log = new EventLog(capacity: 2);
            for (int i = 0; i < 10; i++) log.Log(EventSeverity.Info, "c", i.ToString());
            Assert.Equal(2, log.Snapshot().Count);
            Assert.Equal(10, log.TotalLogged);
        }

        [Fact]
        public void Snapshot_IsCopyNotLive()
        {
            // Snapshot must NOT reflect later additions — the UI takes
            // a stable copy to bind to the ListBox.
            var log = new EventLog();
            log.Log(EventSeverity.Info, "c", "first");
            var snap = log.Snapshot();
            log.Log(EventSeverity.Info, "c", "second");
            Assert.Single(snap);
            Assert.Equal("first", snap[0].Message);
        }

        [Fact]
        public void Clear_EmptiesBufferButKeepsCounter()
        {
            // Clear() is the UI's "Clear" button. We deliberately keep
            // TotalLogged growing across clears so the user can still
            // tell whether the plugin has been active.
            var log = new EventLog();
            log.Log(EventSeverity.Info, "c", "x");
            log.Log(EventSeverity.Info, "c", "y");
            log.Clear();
            Assert.Empty(log.Snapshot());
            Assert.Equal(2, log.TotalLogged);
        }

        [Fact]
        public void EntryAdded_OneSubscriberThrowing_DoesNotBlockOthers()
        {
            // The HTTP path mustn't break because a buggy listener
            // throws — telemetry is best-effort.
            var log = new EventLog();
            var b = 0;
            log.EntryAdded += _ => throw new InvalidOperationException("boom");
            log.EntryAdded += _ => b++;
            log.Log(EventSeverity.Info, "c", "x");
            Assert.Equal(1, b);
        }

        [Fact]
        public async Task ConcurrentWriters_AllEntriesAccountedFor()
        {
            // Smoke test: 8 threads × 100 writes each. Cap larger than
            // total writes so eviction isn't in the way. Just verifying
            // the lock doesn't lose or double-count entries.
            var log = new EventLog(capacity: 2000);
            var tasks = new List<Task>();
            for (int t = 0; t < 8; t++)
            {
                var threadId = t;
                tasks.Add(Task.Run(() =>
                {
                    for (int i = 0; i < 100; i++)
                    {
                        log.Log(EventSeverity.Info, "c", $"{threadId}-{i}");
                    }
                }));
            }
            await Task.WhenAll(tasks);

            var snap = log.Snapshot();
            Assert.Equal(800, snap.Count);
            Assert.Equal(800, log.TotalLogged);
        }

        [Fact]
        public void CapacityFloor_AtLeastOne()
        {
            // Defensive: a 0/negative cap would either dead-loop in the
            // eviction while-loop or accept nothing. Floor to 1.
            var log = new EventLog(capacity: 0);
            Assert.Equal(1, log.Capacity);
            log.Log(EventSeverity.Info, "c", "x");
            log.Log(EventSeverity.Info, "c", "y");
            var snap = log.Snapshot();
            Assert.Single(snap);
            Assert.Equal("y", snap[0].Message);
        }

        [Fact]
        public void Log_NullCategoryAndMessageAreCoercedToEmpty()
        {
            // Defensive — surfaced from the wire, so a malformed call
            // shouldn't put a null inside a DTO the UI later renders.
            var log = new EventLog();
            log.Log(EventSeverity.Warning, null!, null!);
            var entry = log.Snapshot()[0];
            Assert.Equal("", entry.Category);
            Assert.Equal("", entry.Message);
        }
    }
}
