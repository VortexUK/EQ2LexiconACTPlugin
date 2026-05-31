using Xunit;

namespace EQ2Lexicon.ACTPlugin.Tests
{
    /// <summary>
    /// Tests for <see cref="ImportControlClassifier.IsImportControl"/> —
    /// the pure predicate behind <see cref="ImportActivityWatcher"/>'s
    /// decision of whether ACT's current ActiveControl is an
    /// import-related one.
    ///
    /// The empirical anchor came from the diagnostic-dump investigation
    /// (live encounter vs imported encounter, side-by-side):
    /// <c>ActiveControl = &lt;IO_ImportLog&gt;</c> appeared at every
    /// import burst. A type-name substring match on "Import" catches
    /// IO_ImportLog and any future import-related control ACT might
    /// add, without false-positiving on the tvDG TreeView (which can
    /// have "Import/Merge" zones in its first node).
    /// </summary>
    public class ImportControlClassifierTests
    {
        // ── Type-name signal ────────────────────────────────────────────────

        [Fact]
        public void IsImportControl_TrueWhenTypeNameMatchesIO_ImportLog()
        {
            // The defining case from the dump.
            Assert.True(ImportControlClassifier.IsImportControl("IO_ImportLog", ""));
        }

        [Theory]
        [InlineData("IO_ImportCharacter")]   // a hypothetical sibling
        [InlineData("ImportLogForm")]        // generic "Import"-prefixed type
        [InlineData("LogImporter")]          // "Import" in the middle
        [InlineData("BigImport")]            // "Import" at the end
        public void IsImportControl_TrueForAnyTypeNameContainingImport(string typeName)
        {
            Assert.True(ImportControlClassifier.IsImportControl(typeName, ""));
        }

        [Theory]
        [InlineData("io_importlog")]   // all lowercase
        [InlineData("IO_IMPORTLOG")]   // all uppercase
        [InlineData("iO_iMpOrTlOg")]   // arbitrary case
        public void IsImportControl_TypeNameMatchIsCaseInsensitive(string typeName)
        {
            // ACT's actual type uses mixed case. A future renamer-to-
            // lowercase mustn't silently break the gate.
            Assert.True(ImportControlClassifier.IsImportControl(typeName, ""));
        }

        // ── Control-name signal ─────────────────────────────────────────────

        [Fact]
        public void IsImportControl_TrueWhenControlNameContainsImport()
        {
            // ACT could add a future control whose runtime type name
            // doesn't include "Import" but whose Name property does
            // (e.g. a generic Button with Name="btnImport"). Belt-
            // and-brace coverage.
            Assert.True(ImportControlClassifier.IsImportControl("Button", "btnImportLog"));
        }

        [Fact]
        public void IsImportControl_ControlNameMatchIsCaseInsensitive()
        {
            Assert.True(ImportControlClassifier.IsImportControl("Panel", "io_importlog"));
            Assert.True(ImportControlClassifier.IsImportControl("Panel", "IO_IMPORTLOG"));
        }

        // ── Negative cases ──────────────────────────────────────────────────

        [Fact]
        public void IsImportControl_FalseWhenNeitherStringContainsImport()
        {
            // The user is on the main parse tab — no import activity.
            Assert.False(ImportControlClassifier.IsImportControl(
                "TabControlPainted", "tabControl"));
        }

        [Fact]
        public void IsImportControl_FalseForTreeViewWithImportNodes()
        {
            // The dump showed:
            //   ActiveControl (Control) = <TreeView: Nodes[0]: TreeNode: Import/Merge - [0]>
            // — the user was browsing the encounter tree which happened
            // to have an Import/Merge zone in it. NEITHER the type
            // ("TreeView") nor the control name ("tvDG") contains
            // "Import", so the classifier MUST NOT trip on it.
            // Critical false-positive guard.
            Assert.False(ImportControlClassifier.IsImportControl("TreeView", "tvDG"));
        }

        [Theory]
        [InlineData(null, null)]
        [InlineData("", "")]
        [InlineData("", null)]
        [InlineData(null, "")]
        public void IsImportControl_FalseWhenBothInputsEmpty(string? typeName, string? controlName)
        {
            // Fail-CLOSED on no-information: caller treats false as
            // "no recent import activity", which means no tamper
            // report. Empty strings can arise legitimately (control
            // hasn't been given a Name, or GetType().Name returned
            // null on a weird mocked-out type).
            Assert.False(ImportControlClassifier.IsImportControl(typeName, controlName));
        }

        [Fact]
        public void IsImportControl_OneSideEmptyOtherSideMatching_StillTrue()
        {
            // Either signal alone is sufficient.
            Assert.True(ImportControlClassifier.IsImportControl("IO_ImportLog", null));
            Assert.True(ImportControlClassifier.IsImportControl(null, "btnImport"));
            Assert.True(ImportControlClassifier.IsImportControl("", "ImportLogPanel"));
            Assert.True(ImportControlClassifier.IsImportControl("ImportForm", ""));
        }
    }
}
