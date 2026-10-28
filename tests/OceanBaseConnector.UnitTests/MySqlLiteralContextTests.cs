using System;
using System.Data;
using System.Text;
using OceanBaseConnector.Internal.Protocol;
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
        [InlineData("latin1", "latin1", "latin1_bin", "")]
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

        [Theory]
        [InlineData("ascii", "ABC", "414243")]
        [InlineData("latin1", "é€", "E980")]
        [InlineData("dec8", "¤ŒŸ", "A8D7DD")]
        [InlineData("tis620", "กฺ฿", "A1DADF")]
        public void ConfiguredSingleByteLiteralPreservesBytesAndEscaping(string name, string text, string hex)
        {
            var encoding = ClientCharacterSet.Parse(name);
            foreach (var noBackslash in new[] { false, true })
            {
                var context = MySqlLiteralContext.Parse(name, name, name + "_bin", noBackslash ? "NO_BACKSLASH_ESCAPES" : "", encoding);
                var bindings = new ParameterBindingPlan("select ?", new[] { new OceanBaseParameter { Value = text + "'\\\n" } });
                var sql = ClientPreparedSql.Build(bindings, OceanBaseCompatibilityMode.MySql, context, encoding);
                var expected = "73656C656374202827" + hex + (noBackslash ? "27275C0A2729" : "27275C5C5C6E2729");
                Assert.Equal(Convert.FromHexString(expected), encoding.GetBytes(sql));
                Assert.Equal("''", context.Literal(""));
                Assert.Throws<EncoderFallbackException>(() => context.Literal("\ud800"));
                Assert.Throws<EncoderFallbackException>(() => context.Literal("😀"));
                Assert.Throws<EncoderFallbackException>(() => context.ValidateText("select '中'"));
                if (noBackslash) Assert.Throws<NotSupportedException>(() => context.Literal("\0"));
                else Assert.Equal("'\\0\\b\\t\\n\\r\\Z'", context.Literal("\0\b\t\n\r\u001a"));
            }
        }

        [Theory]
        [InlineData("gbk", "乗", "815C")]
        [InlineData("gb18030", "乗😀", "815C9439FC36")]
        [InlineData("gb18030_2022", "乗龴", "815CFE59")]
        [InlineData("sjis", "ソ", "835C")]
        [InlineData("big5", "功", "A55C")]
        [InlineData("hkscs", "功\uf308", "A55C8845")]
        [InlineData("hkscs31", "功\U0002010c", "A55C8845")]
        public void ConfiguredDbcsLiteralKeepsTrailBackslashesInsideCharacters(string name, string text, string hex)
        {
            var encoding = ClientCharacterSet.Parse(name);
            foreach (var noBackslash in new[] { false, true })
            {
                var context = MySqlLiteralContext.Parse(name, name, name + "_bin", noBackslash ? "NO_BACKSLASH_ESCAPES" : "", encoding);
                var value = text + "'\\'); SELECT 999;--";
                var bindings = new ParameterBindingPlan("SELECT ? AS v", new[] { new OceanBaseParameter { Value = value } });
                var sql = ClientPreparedSql.Build(bindings, OceanBaseCompatibilityMode.MySql, context, encoding);
                var escaped = noBackslash ? "27275C2727293B2053454C454354203939393B2D2D" : "27275C5C2727293B2053454C454354203939393B2D2D";
                Assert.Equal(Convert.FromHexString("53454C454354202827" + hex + escaped + "27292041532076"), encoding.GetBytes(sql));
                Assert.Throws<EncoderFallbackException>(() => context.Literal("\ud800"));
                if (noBackslash) Assert.Throws<NotSupportedException>(() => context.Literal("\0"));
                else Assert.Equal("'\\0\\b\\t\\n\\r\\Z'", context.Literal("\0\b\t\n\r\u001a"));
            }
        }

        [Theory]
        [InlineData("ascii", "latin1", "ascii", "ascii_bin", "")]
        [InlineData("latin1", "latin1", "ascii", "ascii_bin", "")]
        [InlineData("dec8", "dec8", "dec8", "latin1_bin", "")]
        [InlineData("tis620", "tis620", "tis620", "tis620_bin';--", "")]
        [InlineData("latin1", "latin1", "latin1", "latin1_bin", "NO_BACKSLASH_ESCAPES,")]
        [InlineData("utf8mb4", "latin1", "latin1", "latin1_bin", "")]
        [InlineData("gbk", "latin1", "gbk", "gbk_bin", "")]
        [InlineData("gbk", "gbk", "gb18030", "gb18030_bin", "")]
        [InlineData("sjis", "sjis", "sjis", "big5_bin", "")]
        [InlineData("gb18030", "gb18030", "gb18030", "gb18030_2022_bin", "NO_BACKSLASH_ESCAPES;")]
        public void SingleByteLiteralRequiresConfiguredMatchingTrustedState(string configured, string client, string connection, string collation, string modes)
            => Assert.Throws<NotSupportedException>(() => MySqlLiteralContext.Parse(client, connection, collation, modes, ClientCharacterSet.Parse(configured)));

        [Fact]
        public void MySqlLiteralRequestBudgetIncludesEscapingAndRepeatedValues()
        {
            var parameters = new OceanBaseParameter[33];
            for (var i = 0; i < parameters.Length; i++) parameters[i] = new OceanBaseParameter { Value = new string('\'', 1024 * 1024) };
            var sql = "select " + string.Join(",", new string('?', parameters.Length).ToCharArray());
            Assert.Throws<ArgumentException>(() => ClientPreparedSql.Build(new ParameterBindingPlan(sql, parameters), OceanBaseCompatibilityMode.MySql,
                MySqlLiteralContext.Parse("utf8mb4", "utf8mb4", "utf8mb4_bin", "")));
        }

        [Theory]
        [InlineData("utf8mb4", "a'\\\0中😀", "61275C00E4B8ADF09F9880")]
        [InlineData("utf8mb3", "中", "E4B8AD")]
        [InlineData("latin1", "é€", "E980")]
        [InlineData("gbk", "乗", "815C")]
        [InlineData("sjis", "ソ", "835C")]
        [InlineData("big5", "功", "A55C")]
        [InlineData("utf8mb4", "", "")]
        public void MySqlMultiStatementLiteralsUseStrictHexContentIndependentOfSqlMode(string name, string value, string hex)
        {
            var encoding = ClientCharacterSet.Parse(name);
            foreach (var modes in new[] { "", "NO_BACKSLASH_ESCAPES", "ANSI_QUOTES,NO_BACKSLASH_ESCAPES" })
            {
                var context = MySqlLiteralContext.Parse(name, name, name + "_bin", modes, encoding);
                var sql = ClientPreparedSql.Build(new ParameterBindingPlan("SELECT ?; SELECT 1", new[] { new OceanBaseParameter { Value = value } }),
                    OceanBaseCompatibilityMode.MySql, context, encoding, multiStatement: true);
                Assert.Equal("SELECT (REPLACE('x','x',_" + (name == "utf8mb3" ? "utf8" : name) + " X'" + hex + "')); SELECT 1", sql);
            }
        }

        [Theory]
        [InlineData("SET NAMES latin1; SELECT ?")]
        [InlineData("SELECT ?; SET CHARACTER SET latin1")]
        [InlineData("SELECT ?; SET CHARSET latin1")]
        [InlineData("SELECT ?; SET @@session.character_set_client='latin1'")]
        [InlineData("SELECT ?; SET SESSION character_set_connection='latin1'")]
        [InlineData("SELECT ?; SET collation_connection='latin1_bin'")]
        [InlineData("SELECT ?; SET /* hi */ @@session . `character_set_client`='latin1'")]
        [InlineData("SELECT ?; SET \"character_set_connection\"='latin1'")]
        [InlineData("SELECT ?; CALL p()")]
        [InlineData("SELECT ?; DO f()")]
        [InlineData("SELECT ?; EXECUTE prepared_stmt")]
        [InlineData("SELECT ?; PREPARE stmt FROM @sql")]
        [InlineData("SELECT ?; DEALLOCATE PREPARE stmt")]
        [InlineData("SELECT ?; RESET CONNECTION")]
        [InlineData("SET @v=1, @@character_set_connection='latin1'; SELECT ?")]
        public void MySqlStringMultiStatementsRejectObservableCharsetAndDynamicExecutionChanges(string sql)
        {
            var bindings = new ParameterBindingPlan(sql, new[] { new OceanBaseParameter { Value = "secret" } });
            var context = MySqlLiteralContext.Parse("utf8mb4", "utf8mb4", "utf8mb4_general_ci", "");
            var error = Assert.Throws<NotSupportedException>(() => ClientPreparedSql.Build(bindings, OceanBaseCompatibilityMode.MySql, context, multiStatement: true));
            Assert.DoesNotContain("secret", error.Message);
        }

        [Theory]
        [InlineData("SELECT /*!50000 ? */ ?; SELECT 1")]
        [InlineData("SELECT /*m! ? */ ?; SELECT 1")]
        [InlineData("SELECT 'back\\' ? '; SELECT ?")]
        [InlineData("SELECT ?; SELECT 'unfinished")]
        public void MySqlMultiStatementQuotingAndExecutableCommentsStayFailClosed(string sql)
        {
            var bindings = new ParameterBindingPlan(sql, new[] { new OceanBaseParameter { Value = "secret" } });
            Assert.Throws<ArgumentException>(() => ClientPreparedSql.Build(bindings, OceanBaseCompatibilityMode.MySql,
                MySqlLiteralContext.Parse("utf8mb4", "utf8mb4", "utf8mb4_general_ci", ""), multiStatement: true));
        }

        [Theory]
        [InlineData("my ordinary comment")]
        [InlineData("My ordinary comment")]
        [InlineData("MISSING is only comment text")]
        public void OrdinaryMySqlCommentsDoNotBecomeExecutableBecauseOfTheirInitialLetter(string comment)
        {
            var sql = "SELECT ?; /*" + comment + "*/ SELECT 1";
            Assert.Equal("SELECT (REPLACE('x','x',_utf8mb4 X'78')); /*" + comment + "*/ SELECT 1",
                ClientPreparedSql.Build(new ParameterBindingPlan(sql, new[] { new OceanBaseParameter { Value = "x" } }),
                    OceanBaseCompatibilityMode.MySql, MySqlLiteralContext.Parse("utf8mb4", "utf8mb4", "utf8mb4_general_ci", ""), multiStatement: true));
        }

        [Fact]
        public void MySqlHexMultiStatementStringsStillRespectTheSharedPacketBudget()
        {
            var parameters = new OceanBaseParameter[9];
            var value = new string('a', 4 * 1024 * 1024);
            for (var i = 0; i < parameters.Length; i++) parameters[i] = new OceanBaseParameter { Value = value };
            Assert.Throws<ArgumentException>(() => ClientPreparedSql.Build(new ParameterBindingPlan("SELECT ?; SELECT ?,?,?,?,?,?,?,?", parameters),
                OceanBaseCompatibilityMode.MySql, MySqlLiteralContext.Parse("utf8mb4", "utf8mb4", "utf8mb4_general_ci", ""), multiStatement: true));
        }
    }
}
