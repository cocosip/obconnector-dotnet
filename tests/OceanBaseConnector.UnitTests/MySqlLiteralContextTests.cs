using System;
using System.Data;
using System.Text;
using OceanBaseConnector.Internal.Sql;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class MySqlLiteralContextTests
    {
        private static string Build(string sql, string value, string client = "utf8mb4", string connection = "utf8mb4",
            string collation = "utf8mb4_general_ci", string modes = "")
            => ClientPreparedSql.Build(new ParameterBindingPlan(sql, new[] { new OceanBaseParameter { Value = value } }),
                OceanBaseCompatibilityMode.MySql, MySqlLiteralContext.Parse(client, connection, collation, modes));

        [Theory]
        [InlineData("", "('')")]
        [InlineData("'", "('''')")]
        [InlineData("x'); DROP TABLE t; --", "('x''); DROP TABLE t; --')")]
        [InlineData("\\'", "('\\\\''')")]
        [InlineData("\\%\\_", "('\\\\%\\\\_')")]
        [InlineData("\0\b\t\n\r\u001a", "('\\0\\b\\t\\n\\r\\Z')")]
        [InlineData("\ufeff中文😀  ", "('\ufeff中文😀  ')")]
        public void MySqlLiteralEscapesEverySpecialCharacterWithoutChangingCollation(string text, string expected)
            => Assert.Equal("select " + expected, Build("select ?", text));

        [Theory]
        [InlineData("no_backslash_escapes")]
        [InlineData("ANSI_QUOTES,NO_BACKSLASH_ESCAPES,STRICT_TRANS_TABLES")]
        public void MySqlLiteralNoBackslashEscapesRetainsBackslashesAndDoublesQuotes(string modes)
        {
            Assert.Equal("select ('a\\''\b\t\n\r\u001a中😀')", Build("select ?", "a\\'\b\t\n\r\u001a中😀", modes: modes));
            Assert.Throws<NotSupportedException>(() => Build("select ?", "x\0y", modes: modes));
        }

        [Theory]
        [InlineData("utf8mb4", "utf8mb4", "utf8mb4_general_ci")]
        [InlineData("UTF8MB4", "UTF8MB4", "UTF8MB4_BIN")]
        [InlineData("utf8", "utf8mb4", "utf8mb4_unicode_ci")]
        [InlineData("utf8mb4", "utf8", "utf8_general_ci")]
        [InlineData("utf8mb3", "utf8mb3", "utf8mb3_bin")]
        [InlineData("utf8", "utf8mb3", "utf8_bin")]
        public void MySqlLiteralUtf8NamesAndAliasesUseExactCharacters(string client, string connection, string collation)
        {
            Assert.Equal("select ('中é')", Build("select ?", "中é", client, connection, collation));
            if (client != "utf8mb4" && client != "UTF8MB4" || connection != "utf8mb4" && connection != "UTF8MB4")
            {
                Assert.Throws<ArgumentException>(() => Build("select ?", "😀", client, connection, collation));
                Assert.Throws<ArgumentException>(() => Build("select ?, '😀'", "ok", client, connection, collation));
            }
        }

        [Theory]
        [InlineData("latin1", "utf8mb4", "utf8mb4_general_ci", "")]
        [InlineData("utf8mb4", "gbk", "gbk_bin", "")]
        [InlineData("binary", "binary", "binary", "")]
        [InlineData("", "utf8mb4", "utf8mb4_bin", "")]
        [InlineData("utf8mb4", "utf8mb4", "utf8_general_ci", "")]
        [InlineData("utf8mb4", "utf8", "utf8mb4_bin", "")]
        [InlineData("utf8mb4", "utf8mb4", "utf8mb4_'; --", "")]
        [InlineData("utf8mb4", "utf8mb4", "utf8mb4_bin", "NO_BACKSLASH_ESCAPES,")]
        [InlineData("utf8mb4", "utf8mb4", "utf8mb4_bin", "NO_BACKSLASH_ESCAPES; select 1")]
        [InlineData("utf8mb4", "utf8mb4", "utf8mb4_bin", "NO_BACKSLASH_ESCAPE\0S")]
        public void MySqlLiteralUntrustedStateFailsClosed(string client, string connection, string collation, string modes)
            => Assert.Throws<NotSupportedException>(() => MySqlLiteralContext.Parse(client, connection, collation, modes));

        [Theory]
        [InlineData("select ?; select 1")]
        [InlineData("set sql_mode='NO_BACKSLASH_ESCAPES'; select ?")]
        [InlineData("select ?;;")]
        [InlineData("select ?; /*x*/ select 1")]
        [InlineData("select ? /*!; SET NAMES latin1 */")]
        [InlineData("select ? /*M!; SET NAMES latin1 */")]
        [InlineData("select ? # \0")]
        [InlineData("select ? /* \0 */")]
        [InlineData("select ? /* unfinished")]
        public void MySqlLiteralCannotOutliveItsStatementParseState(string sql)
            => Assert.ThrowsAny<ArgumentException>(() => Build(sql, "safe"));

        [Theory]
        [InlineData("select ?;")]
        [InlineData("select ?; /* trailing ; ? */ -- ; ?\n # ?")]
        [InlineData("select ? /* ; ? */ # ; ?\n")]
        [InlineData("select ?, ';', \";\", `;`")]
        public void MySqlLiteralAcceptsOnlyATerminalStatementDelimiter(string sql)
            => Assert.Equal(sql.Replace("select ?", "select ('safe')"), Build(sql, "safe"));

        [Theory]
        [InlineData(0xd800)]
        [InlineData(0xdfff)]
        public void MySqlLiteralNeverReplacesMalformedUnicode(int codeUnit)
            => Assert.Throws<EncoderFallbackException>(() => Build("select ?", new string((char)codeUnit, 1)));

        [Fact]
        public void MySqlLiteralRequestBudgetIncludesEscapingAndRepeatedValues()
        {
            var parameters = new OceanBaseParameter[33];
            for (var i = 0; i < parameters.Length; i++) parameters[i] = new OceanBaseParameter { Value = new string('\'', 1024 * 1024) };
            var sql = "select " + string.Join(",", new string('?', parameters.Length).ToCharArray());
            Assert.Throws<ArgumentException>(() => ClientPreparedSql.Build(new ParameterBindingPlan(sql, parameters), OceanBaseCompatibilityMode.MySql,
                MySqlLiteralContext.Parse("utf8mb4", "utf8mb4", "utf8mb4_bin", "")));
        }
    }
}
