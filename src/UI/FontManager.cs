using System;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace EQ2Lexicon.ACTPlugin
{
    /// <summary>
    /// Loads the bundled Cinzel variable font from this DLL's embedded
    /// resources and exposes a single <see cref="FontFamily"/> the
    /// settings panel uses for titles and section headings.
    ///
    /// Why this exists:
    ///   - Cinzel is not installed by default on Windows. Without
    ///     embedding it the headings would silently fall back to
    ///     Segoe UI on every user's machine — defeating the whole
    ///     "looks EQ2-themed" point.
    ///   - <see cref="PrivateFontCollection"/> requires unmanaged memory
    ///     for AddMemoryFont, and the FontFamily it returns is only
    ///     valid while the collection itself stays alive. We hold both
    ///     as static lifetime so callers can build <see cref="Font"/>
    ///     instances at any point without re-initialising.
    ///
    /// GDI+ doesn't honour OpenType variable-font axes — it picks the
    /// default static instance from the variable font (weight 400 for
    /// Cinzel). FontStyle.Bold on the resulting Font triggers Windows'
    /// synthesised emboldening, which is good enough for the small
    /// number of heading words we use it for.
    ///
    /// Load failures FAIL OPEN: <see cref="Heading"/> falls back to the
    /// generic serif family so the panel still renders if the resource
    /// goes missing.
    /// </summary>
    internal static class FontManager
    {
        // The collection must stay alive as long as any Font derived
        // from its families is in use — static keeps the GC from
        // ever reclaiming it.
        private static readonly PrivateFontCollection? _collection;
        private static readonly FontFamily? _cinzel;

        // Pinned unmanaged copy of the .ttf bytes — AddMemoryFont
        // requires the buffer to outlive the collection. Tracking
        // it explicitly so a future plugin reload can free + recreate
        // cleanly if we ever need to.
        private static readonly IntPtr _fontBuffer;
        private static readonly int _fontBufferSize;

        static FontManager()
        {
            try
            {
                using (var stream = LoadEmbeddedFontStream())
                {
                    if (stream == null) return;
                    var bytes = ReadAllBytes(stream);
                    _fontBufferSize = bytes.Length;
                    _fontBuffer = Marshal.AllocCoTaskMem(bytes.Length);
                    Marshal.Copy(bytes, 0, _fontBuffer, bytes.Length);

                    _collection = new PrivateFontCollection();
                    _collection.AddMemoryFont(_fontBuffer, bytes.Length);
                    if (_collection.Families.Length > 0)
                    {
                        _cinzel = _collection.Families[0];
                    }
                }
            }
            catch
            {
                // Best-effort. Any failure here (bad TTF, GDI+ refusal,
                // out-of-memory) leaves _cinzel null and the
                // Heading/SectionHeading getters fall back to a serif
                // generic family. We never want a font failure to
                // crash ACT's plugin load.
            }
        }

        /// <summary>
        /// Cinzel family if available; generic serif (Times-equivalent)
        /// otherwise. Always non-null so callers don't need to nil-check.
        /// </summary>
        public static FontFamily HeadingFamily => _cinzel ?? FontFamily.GenericSerif;

        /// <summary>
        /// The big plugin-title font ("EQ2 LEXICON UPLOADER"). Bold
        /// is synthesised when Cinzel is the variable font's default
        /// weight instance.
        /// </summary>
        public static Font Title(float size) =>
            new Font(HeadingFamily, size, FontStyle.Bold, GraphicsUnit.Point);

        /// <summary>
        /// Section heading font ("⚙ CONFIGURATION", etc.). Same family
        /// as Title but smaller; same Bold style for consistent
        /// rendering weight.
        /// </summary>
        public static Font SectionHeading(float size) =>
            new Font(HeadingFamily, size, FontStyle.Bold, GraphicsUnit.Point);

        private static Stream? LoadEmbeddedFontStream()
        {
            var asm = Assembly.GetExecutingAssembly();
            // After ILRepack-merge the embedded resource path is the
            // UI assembly's namespace. Before ILRepack (Debug builds)
            // it's the same — the resource is declared in this csproj.
            // Search by suffix so a future renaming of the resource
            // logical name still resolves.
            var names = asm.GetManifestResourceNames();
            foreach (var n in names)
            {
                if (n.EndsWith("Cinzel-Variable.ttf", StringComparison.Ordinal))
                {
                    return asm.GetManifestResourceStream(n);
                }
            }
            return null;
        }

        private static byte[] ReadAllBytes(Stream stream)
        {
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                return ms.ToArray();
            }
        }
    }
}
