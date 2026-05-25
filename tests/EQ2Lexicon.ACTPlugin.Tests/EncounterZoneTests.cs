using Xunit;

namespace EQ2Lexicon.ACTPlugin.Tests
{
    /// <summary>
    /// Pins the synthetic-zone set. The auto-upload path AND the
    /// right-click manual path both call this predicate; a silent
    /// change here changes user-visible behaviour in both flows.
    /// </summary>
    public class EncounterZoneTests
    {
        [Theory]
        [InlineData("Import/Merge")]      // exact ACT spelling
        [InlineData("import/merge")]      // case-insensitive
        [InlineData("IMPORT/MERGE")]
        [InlineData("  Import/Merge  ")]  // whitespace tolerant
        public void IsImportOrMerge_TrueForTheSyntheticBucket(string zoneName)
        {
            Assert.True(EncounterZone.IsImportOrMerge(zoneName));
        }

        [Theory]
        [InlineData("Great Divide")]
        [InlineData("The Peat Bog")]
        [InlineData("The Sinking Sands")]
        [InlineData("Import/Merge zone something")] // not a bare match
        [InlineData("Import")]                       // partial isn't enough
        [InlineData("Merge")]
        [InlineData("")]                             // empty zone name → real fight without zone metadata, allow
        [InlineData("   ")]
        [InlineData(null)]
        public void IsImportOrMerge_FalseForRealZonesAndEmpty(string? zoneName)
        {
            Assert.False(EncounterZone.IsImportOrMerge(zoneName));
        }
    }
}
