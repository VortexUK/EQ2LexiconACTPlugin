using System;

namespace EQ2Lexicon.ACTPlugin
{
    /// <summary>
    /// Pure predicate that decides whether a WinForms control "looks
    /// like" ACT's import UI based on its runtime type name and
    /// <c>Name</c> property. Lives in Core (no WinForms reference) so
    /// the rule is unit-testable without spinning up real ACT controls.
    ///
    /// <see cref="ImportActivityWatcher"/> (UI assembly) calls in here
    /// after extracting the type-name + control-name strings from the
    /// real <c>Control</c> reference.
    ///
    /// The empirical anchor: the diagnostic-dump investigation captured
    /// <c>ActiveControl = &lt;IO_ImportLog&gt;</c> at every import burst
    /// — a custom ACT control type whose name starts with "IO_Import".
    /// The substring rule generalises to any future import-related
    /// control ACT might add. The case where this MUST NOT trip
    /// false-positive is the <c>tvDG</c> TreeView, which sometimes
    /// has an "Import/Merge" node visible in its first row — neither
    /// the type name ("TreeView") nor the control name ("tvDG")
    /// contains "Import", so we stay quiet.
    /// </summary>
    public static class ImportControlClassifier
    {
        /// <summary>
        /// True when either <paramref name="typeName"/> or
        /// <paramref name="controlName"/> contains the substring
        /// "Import" (case-insensitive). Null / empty inputs are
        /// treated as "no signal" — caller treats false as "no
        /// recent import activity".
        /// </summary>
        public static bool IsImportControl(string? typeName, string? controlName)
        {
            if (!string.IsNullOrEmpty(typeName) &&
                typeName.IndexOf("Import", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            if (!string.IsNullOrEmpty(controlName) &&
                controlName.IndexOf("Import", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            return false;
        }
    }
}
