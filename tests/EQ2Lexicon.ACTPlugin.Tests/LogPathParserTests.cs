using Xunit;

namespace EQ2Lexicon.ACTPlugin.Tests
{
    /// <summary>
    /// Pins the EQ2 log-path → server-name parser. Tests use forward
    /// slashes in path inputs because Path.GetDirectoryName /
    /// Path.GetFileName accept both separators on Windows (the only
    /// platform ACT runs on); keeps test strings readable.
    /// </summary>
    public class LogPathParserTests
    {
        [Theory]
        [InlineData(@"C:\Program Files\Sony\EverQuest II\logs\Varsoon\eq2log_Kayleigh.txt", "Varsoon")]
        [InlineData(@"C:\Program Files\Sony\EverQuest II\logs\Kaladim\eq2log_Kayleigh.txt", "Kaladim")]
        [InlineData(@"C:\Program Files\Sony\EverQuest II\logs\Butcherblock\eq2log_SomeAlt.txt", "Butcherblock")]
        [InlineData(@"D:\Games\EQ2\logs\Nagafen\eq2log_PvP.txt", "Nagafen")]
        public void ParseServerName_HappyPath(string path, string expected)
        {
            Assert.Equal(expected, LogPathParser.ParseServerName(path));
        }

        [Fact]
        public void ParseServerName_PassesMultiWordServersThrough()
        {
            // EQ2 wiki / fan sites don't conclusively document whether
            // "Antonia Bayle" appears with the space in the directory
            // name or stripped. We pass the directory name through
            // unmodified — Daybreak's Census API accepts world names
            // with spaces, so either form will work if the directory
            // matches. Pinning the behaviour here means a future user
            // report ("AB doesn't work") points the finger at the
            // game's filesystem choice, not at our parser.
            Assert.Equal("Antonia Bayle",
                LogPathParser.ParseServerName(@"C:\EQ2\logs\Antonia Bayle\eq2log_Char.txt"));
        }

        [Fact]
        public void ParseServerName_ReturnsEmptyForLegacyGenericLog()
        {
            // User never ran /log in-game — EQ2 wrote to the generic
            // logs/eq2log.txt path. Parent directory is literally
            // "logs", which is not a server name. Server has to fall
            // back to its EQ2_WORLD default.
            Assert.Equal("",
                LogPathParser.ParseServerName(@"C:\Program Files\Sony\EverQuest II\logs\eq2log.txt"));
        }

        [Fact]
        public void ParseServerName_CaseInsensitiveLogsDirectoryCheck()
        {
            // Some EQ2 installs may use lowercase, some uppercase.
            // The "logs" sentinel comparison must be case-insensitive.
            Assert.Equal("",
                LogPathParser.ParseServerName(@"C:\EQ2\Logs\eq2log.txt"));
            Assert.Equal("",
                LogPathParser.ParseServerName(@"C:\EQ2\LOGS\eq2log.txt"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ParseServerName_ReturnsEmptyForMissingPath(string? path)
        {
            Assert.Equal("", LogPathParser.ParseServerName(path));
        }

        [Fact]
        public void ParseServerName_ReturnsEmptyForBareFilename()
        {
            // No directory component at all → nothing to extract.
            Assert.Equal("", LogPathParser.ParseServerName("eq2log_Kayleigh.txt"));
        }
    }
}
