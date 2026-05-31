using Xunit;

namespace EQ2Lexicon.ACTPlugin.Tests
{
    /// <summary>
    /// Direct coverage for the small helpers that live on
    /// <see cref="EncounterSnapshot"/>. Most snapshot behaviour is
    /// covered indirectly by the PayloadBuilder + UploadClient tests;
    /// this file pins the methods that are called from multiple
    /// assemblies (Core + UI) and would silently break those callers
    /// if their semantics drifted.
    /// </summary>
    public class EncounterSnapshotTests
    {
        // ── EnumerateEnemyNames ─────────────────────────────────────────────
        //
        // Used by PayloadBuilder.ComputeClientWarnings (Core) and was
        // previously a static helper in EncounterCapture (UI). Moved
        // onto the snapshot so the Core caller can reach it without a
        // dependency arrow back into the UI assembly.

        [Fact]
        public void EnumerateEnemyNames_YieldsCombatantsNotInAllyNames()
        {
            var snap = new EncounterSnapshot();
            snap.Combatants.Add(new CombatantSnapshot { Name = "Menludiir" });
            snap.Combatants.Add(new CombatantSnapshot { Name = "Pawbuster" });
            snap.AllyNames.Add("Menludiir");

            var enemies = System.Linq.Enumerable.ToList(snap.EnumerateEnemyNames());

            Assert.Single(enemies);
            Assert.Equal("Pawbuster", enemies[0]);
        }

        [Fact]
        public void EnumerateEnemyNames_AllyMatchIsCaseInsensitive()
        {
            // ACT's GetAllies may return names with different casing
            // than EncounterData.Items keys — AllyNames uses
            // OrdinalIgnoreCase, so this should still classify correctly.
            var snap = new EncounterSnapshot();
            snap.Combatants.Add(new CombatantSnapshot { Name = "Menludiir" });
            snap.AllyNames.Add("MENLUDIIR");

            var enemies = System.Linq.Enumerable.ToList(snap.EnumerateEnemyNames());

            Assert.Empty(enemies);
        }

        [Fact]
        public void EnumerateEnemyNames_SkipsEmptyAndWhitespaceNames()
        {
            // Defensive: a malformed combatant entry shouldn't make it
            // into the enemy list and pollute the rename-detection
            // heuristic with a spurious "" enemy.
            var snap = new EncounterSnapshot();
            snap.Combatants.Add(new CombatantSnapshot { Name = "" });
            snap.Combatants.Add(new CombatantSnapshot { Name = "   " });
            snap.Combatants.Add(new CombatantSnapshot { Name = "Pawbuster" });

            var enemies = System.Linq.Enumerable.ToList(snap.EnumerateEnemyNames());

            Assert.Single(enemies);
            Assert.Equal("Pawbuster", enemies[0]);
        }

        [Fact]
        public void EnumerateEnemyNames_EmptyWhenNoCombatants()
        {
            var snap = new EncounterSnapshot();
            Assert.Empty(snap.EnumerateEnemyNames());
        }

        [Fact]
        public void EnumerateEnemyNames_AllAlliesYieldsEmpty()
        {
            // Six-player raid all in ally list → no enemies. Caller
            // (rename detection) gets nothing to compare against,
            // which is the correct signal: an upload of an all-ally
            // "fight" deserves the title_enemy_mismatch flag.
            var snap = new EncounterSnapshot();
            foreach (var n in new[] { "Menludiir", "Kayleigh", "Sigurd", "Yldris" })
            {
                snap.Combatants.Add(new CombatantSnapshot { Name = n });
                snap.AllyNames.Add(n);
            }

            Assert.Empty(snap.EnumerateEnemyNames());
        }
    }
}
