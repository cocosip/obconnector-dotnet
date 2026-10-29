using System;
using System.Data;
using System.Globalization;
using System.Numerics;
using OceanBaseConnector.Internal.Sql;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class ClientPreparedSqlTests
    {
        private static string Build(string sql, OceanBaseCompatibilityMode mode, params OceanBaseParameter[] parameters)
        {
            var collection = new OceanBaseParameterCollection();
            foreach (var parameter in parameters) collection.Add(parameter);
            return ClientPreparedSql.Build(ParameterBindingPlan.Create(sql, mode, collection), mode);
        }

        private static string BuildMulti(string sql, OceanBaseCompatibilityMode mode, params OceanBaseParameter[] parameters)
        {
            var collection = new OceanBaseParameterCollection();
            foreach (var parameter in parameters) collection.Add(parameter);
            return ClientPreparedSql.Build(ParameterBindingPlan.Create(sql, mode, collection), mode, multiStatement: true);
        }

        [Theory]
        [InlineData("select :p, ':hidden ?', q'[it''s ? :hidden]', :P", "select (REPLACE( 'it''s \\ 中文',NULL,NULL)), ':hidden ?', q'[it''s ? :hidden]', (REPLACE( 'it''s \\ 中文',NULL,NULL))")]
        [InlineData("select /* ? :hidden */ :p -- ?\n", "select /* ? :hidden */ (REPLACE( 'it''s \\ 中文',NULL,NULL)) -- ?\n")]
        public void OracleUsesOriginalBindingPositions(string sql, string expected)
            => Assert.Equal(expected, Build(sql, OceanBaseCompatibilityMode.Oracle, new OceanBaseParameter { ParameterName = "p", Value = "it's \\ 中文" }));

        [Theory]
        [InlineData("select ?, '?', \"?\", `?` # ?\n", "select (1), '?', \"?\", `?` # ?\n")]
        [InlineData("select /* ? */ ? -- ?\n", "select /* ? */ (1) -- ?\n")]
        public void MySqlSkipsQuotedAndCommentedQuestionMarks(string sql, string expected)
            => Assert.Equal(expected, Build(sql, OceanBaseCompatibilityMode.MySql, new OceanBaseParameter { Value = 1 }));

        [Fact]
        public void MySqlStringsRequireTrustedCollationButNullDoesNot()
        {
            Assert.Throws<NotSupportedException>(() => Build("select ?", OceanBaseCompatibilityMode.MySql, new OceanBaseParameter { Value = "secret" }));
            Assert.Equal("select (NULL)", Build("select ?", OceanBaseCompatibilityMode.MySql, new OceanBaseParameter { Value = DBNull.Value }));
        }

        [Theory]
        [InlineData("select 'back\\' ? '")]
        [InlineData("select 'back\\' ? ' ? '")]
        [InlineData("select /*!50000 ? */ ?")]
        [InlineData("select /*M! ? */ ?")]
        [InlineData("select 'unfinished ?")]
        [InlineData("select /* unfinished ?")]
        [InlineData("select ??")]
        public void MySqlAmbiguityAndExecutableCommentsAreRejected(string sql)
            => Assert.ThrowsAny<ArgumentException>(() => Build(sql, OceanBaseCompatibilityMode.MySql, new OceanBaseParameter { Value = 1 }));

        [Fact]
        public void MySqlAnsiQuotesIsCheckedIndependentlyOfSingleQuoteEscapes()
        {
            var sql = new string(Array.ConvertAll(new[] { 34, 92, 34, 39, 92, 39, 92, 34, 34, 34, 34, 63, 92, 39, 39 }, x => (char)x));
            Assert.Throws<ArgumentException>(() => Build(sql, OceanBaseCompatibilityMode.MySql, new OceanBaseParameter { Value = 1 }));
        }

        [Fact]
        public void MySqlFloatLiteralPreservesTheExactSingleValue()
            => Assert.Equal("select (0.10000000149011612E0)", Build("select ?", OceanBaseCompatibilityMode.MySql, new OceanBaseParameter { Value = 0.1f }));

        [Fact]
        public void CountsAndUnsupportedTypesAreNotGuessed()
        {
            Assert.Throws<ArgumentException>(() => Build("select ?, ?", OceanBaseCompatibilityMode.MySql, new OceanBaseParameter { Value = 1 }));
            Assert.ThrowsAny<ArgumentException>(() => Build("select :p", OceanBaseCompatibilityMode.Oracle, new OceanBaseParameter { ParameterName = "p", Value = "\ud800" }));
        }

        [Fact]
        public void OracleClientFallbackSupportsExistingTemporalIntervalAndNationalLiterals()
        {
            Assert.Equal("select (TO_DATE('2026-01-02 03:04:05','YYYY-MM-DD HH24:MI:SS'))",
                Build("select :p", OceanBaseCompatibilityMode.Oracle,
                    new OceanBaseParameter { ParameterName = "p", DbType = DbType.Date, Value = new DateTime(2026, 1, 2, 3, 4, 5) }));
            Assert.Equal("select (TIMESTAMP '2026-01-02 03:04:05.123456789')",
                Build("select :p", OceanBaseCompatibilityMode.Oracle,
                    new OceanBaseParameter { ParameterName = "p", OceanBaseDbType = OceanBaseDbType.Timestamp,
                        Value = new OceanBaseTimestamp(new DateTime(2026, 1, 2, 3, 4, 5), 123456789) }));
            Assert.Equal("select (INTERVAL '-012-03' YEAR(3) TO MONTH)",
                Build("select :p", OceanBaseCompatibilityMode.Oracle,
                    new OceanBaseParameter { ParameterName = "p", Precision = 3,
                        Value = new OceanBaseIntervalYearToMonth(true, 12, 3) }));
            Assert.Equal("select (u'\\4e2d')",
                Build("select :p", OceanBaseCompatibilityMode.Oracle,
                    new OceanBaseParameter { ParameterName = "p", OceanBaseDbType = OceanBaseDbType.NChar, Value = "中" }));
        }

        [Fact]
        public void MultiStatementJsonUsesTrustedStringLiteralsAndKeepsNullDistinct()
        {
            var parameters = new OceanBaseParameterCollection();
            parameters.Add(new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Json, Value = "{\"x\":null}" });
            parameters.Add(new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Json, Value = "null" });
            parameters.Add(new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Json, Value = DBNull.Value });
            var bindings = ParameterBindingPlan.Create("select ?; select ?, ?", OceanBaseCompatibilityMode.MySql, parameters);
            var context = MySqlLiteralContext.Parse("utf8mb4", "utf8mb4", "utf8mb4_bin", "NO_BACKSLASH_ESCAPES");
            Assert.Equal("select (REPLACE('x','x',_utf8mb4 X'7B2278223A6E756C6C7D')); select (REPLACE('x','x',_utf8mb4 X'6E756C6C')), (NULL)",
                ClientPreparedSql.Build(bindings, OceanBaseCompatibilityMode.MySql, context, multiStatement: true));
        }

        [Fact]
        public void MultiStatementNationalStringsUseUnicodeCodeUnitsWithoutOrdinaryLiteralLimits()
        {
            Assert.Equal("select (u'\\4e2d\\d83d\\de42\\0027\\005c\\0000\\003b\\003a\\0070'); select (NULL), (u'')",
                BuildMulti("select :n; select :v, :e", OceanBaseCompatibilityMode.Oracle,
                    new OceanBaseParameter { ParameterName = "n", OceanBaseDbType = OceanBaseDbType.NChar, Value = "中🙂'\\\0;:p", Size = 9 },
                    new OceanBaseParameter { ParameterName = "v", OceanBaseDbType = OceanBaseDbType.NVarchar2, Value = DBNull.Value },
                    new OceanBaseParameter { ParameterName = "e", OceanBaseDbType = OceanBaseDbType.NVarchar2, Value = "" }));
            var large = new string('中', 4001);
            var sql = BuildMulti("select :n; select :n", OceanBaseCompatibilityMode.Oracle,
                new OceanBaseParameter { ParameterName = "n", OceanBaseDbType = OceanBaseDbType.NVarchar2, Value = large });
            Assert.StartsWith("select (u'\\4e2d\\4e2d", sql);
            Assert.EndsWith("\\4e2d'); select (u'" + string.Concat(System.Linq.Enumerable.Repeat("\\4e2d", 4001)) + "')", sql);
        }

        [Fact]
        public void MultiStatementNationalStringsRetainModeDeclarationAndFallbackValidation()
        {
            foreach (var provider in new[] { OceanBaseDbType.NChar, OceanBaseDbType.NVarchar2 })
            {
                Assert.Throws<NotSupportedException>(() => BuildMulti("select ?; select 1", OceanBaseCompatibilityMode.MySql,
                    new OceanBaseParameter { OceanBaseDbType = provider, Value = DBNull.Value }));
                Assert.Throws<System.Text.EncoderFallbackException>(() => BuildMulti("select :p; select 1", OceanBaseCompatibilityMode.Oracle,
                    new OceanBaseParameter { ParameterName = "p", OceanBaseDbType = provider, Value = "\ud800" }));
                Assert.Throws<ArgumentException>(() => BuildMulti("select :p; select 1", OceanBaseCompatibilityMode.Oracle,
                    new OceanBaseParameter { ParameterName = "p", OceanBaseDbType = provider, Value = "🙂", Size = 1 }));
                Assert.Throws<NotSupportedException>(() => BuildMulti("select :p; select 1", OceanBaseCompatibilityMode.Oracle,
                    new OceanBaseParameter { ParameterName = "p", OceanBaseDbType = provider, Value = DBNull.Value, Precision = 1 }));
                Assert.Throws<NotSupportedException>(() => BuildMulti("select :p; select 1", OceanBaseCompatibilityMode.Oracle,
                    new OceanBaseParameter { ParameterName = "p", OceanBaseDbType = provider, Value = DBNull.Value, Scale = 1 }));
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void MultiStatementNationalLiteralExpansionAndRepeatedNamesRespectPacketBudget(bool repeated)
        {
            var text = new string('x', checked((int)(OceanBaseConnector.Internal.Protocol.PacketChannel.DefaultMaxPacketSize / (repeated ? 10 : 5) + 1)));
            Assert.Throws<ArgumentException>(() => BuildMulti(repeated ? "select :p; select :p" : "select :p; select 1", OceanBaseCompatibilityMode.Oracle,
                new OceanBaseParameter { ParameterName = "p", OceanBaseDbType = OceanBaseDbType.NVarchar2, Value = text }));
        }

        [Fact]
        public void MultiStatementJsonRetainsModeMetadataAndFallbackRestrictions()
        {
            Assert.Throws<NotSupportedException>(() => Build("select :p", OceanBaseCompatibilityMode.Oracle,
                new OceanBaseParameter { ParameterName = "p", OceanBaseDbType = OceanBaseDbType.Json, Value = DBNull.Value }));
            Assert.Throws<NotSupportedException>(() => BuildMulti("select :p; select 1 from dual", OceanBaseCompatibilityMode.Oracle,
                new OceanBaseParameter { ParameterName = "p", OceanBaseDbType = OceanBaseDbType.Json, Value = DBNull.Value }));
            foreach (var parameter in new[]
            {
                new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Json, Precision = 1, Value = DBNull.Value },
                new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Json, Scale = 1, Value = DBNull.Value },
                new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Json, Direction = ParameterDirection.Output, Value = DBNull.Value }
            }) Assert.Throws<NotSupportedException>(() => BuildMulti("select ?; select 1", OceanBaseCompatibilityMode.MySql, parameter));
        }

        [Fact]
        public void NumbersAreInvariantExactAndParenthesized()
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                var number = new OceanBaseDecimal(BigInteger.Parse("12345678901234567890123456789012345678"), 10);
                Assert.Equal("select (-1), (1.25E0), (1234567890123456789012345678.9012345678), (NULL), (1)",
                    Build("select ?, ?, ?, ?, ?", OceanBaseCompatibilityMode.MySql,
                        new OceanBaseParameter { Value = -1 }, new OceanBaseParameter { Value = 1.25 },
                        new OceanBaseParameter { Value = number }, new OceanBaseParameter { Value = DBNull.Value }, new OceanBaseParameter { Value = true }));
                Assert.Equal("select (1.2500)", Build("select :p", OceanBaseCompatibilityMode.Oracle,
                    new OceanBaseParameter { ParameterName = "p", Value = 1.25m, Scale = 4 }));
                Assert.Throws<ArgumentException>(() => Build("select ?", OceanBaseCompatibilityMode.MySql, new OceanBaseParameter { Value = double.NaN }));
                Assert.Throws<ArgumentException>(() => Build("select ?", OceanBaseCompatibilityMode.MySql, new OceanBaseParameter { DbType = DbType.Int32, Value = "secret" }));
            }
            finally { CultureInfo.CurrentCulture = previous; }
        }

        [Theory]
        [InlineData(OceanBaseCompatibilityMode.Oracle, "select (HEXTORAW('0027FF'))")]
        [InlineData(OceanBaseCompatibilityMode.MySql, "select (X'0027FF')")]
        public void BinaryHasNoTextEscapePath(OceanBaseCompatibilityMode mode, string expected)
            => Assert.Equal(expected, Build(mode == OceanBaseCompatibilityMode.Oracle ? "select :p" : "select ?", mode,
                new OceanBaseParameter { ParameterName = "p", Value = new byte[] { 0, 39, 255 } }));

        [Fact]
        public void OracleTextLiteralQuotesAreDoubledWithoutChangingItsValue()
        {
            Assert.Equal("select (REPLACE( 'a''b',NULL,NULL))", Build("select :p", OceanBaseCompatibilityMode.Oracle, new OceanBaseParameter { ParameterName = "p", Value = "a'b" }));
        }

        [Theory]
        [InlineData(false, 2001)]
        [InlineData(true, 2001)]
        [InlineData(false, 65536)]
        [InlineData(true, 65536)]
        [InlineData(false, 1048576)]
        [InlineData(true, 1048576)]
        public void OracleLargeBinaryLiteralsKeepRawExpressionIdentity(bool multiStatement, int size)
        {
            var bytes = new byte[size]; for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)i;
            var parameter = new OceanBaseParameter { ParameterName = "p", Value = bytes };
            var literal = "(HEXTORAW('" + Convert.ToHexString(bytes) + "'))";
            Assert.Equal("SELECT " + literal + (multiStatement ? " FROM DUAL; SELECT " + literal : "") + " FROM DUAL",
                multiStatement ? BuildMulti("SELECT :p FROM DUAL; SELECT :P FROM DUAL", OceanBaseCompatibilityMode.Oracle, parameter)
                    : Build("SELECT :p FROM DUAL", OceanBaseCompatibilityMode.Oracle, parameter));
            Assert.Same(bytes, parameter.Value);
        }

        [Fact]
        public void OracleLargeBinaryLiteralsRetainIndividualAndRepeatedSqlBudgets()
        {
            Assert.Throws<ArgumentException>(() => Build("SELECT :p FROM DUAL", OceanBaseCompatibilityMode.Oracle,
                new OceanBaseParameter { ParameterName = "p", Value = new byte[32 * 1024 * 1024] }));
            var sql = string.Join("; ", System.Linq.Enumerable.Repeat("SELECT :p FROM DUAL", 33));
            Assert.Throws<ArgumentException>(() => BuildMulti(sql, OceanBaseCompatibilityMode.Oracle,
                new OceanBaseParameter { ParameterName = "p", Value = new byte[1024 * 1024] }));
        }

        [Theory]
        [InlineData(OceanBaseCompatibilityMode.Oracle, "select (1E+300d), (1.25f)")]
        [InlineData(OceanBaseCompatibilityMode.MySql, "select (1E+300), (1.25E0)")]
        public void FloatingPointLiteralsKeepTheirFloatingType(OceanBaseCompatibilityMode mode, string expected)
            => Assert.Equal(expected, Build(mode == OceanBaseCompatibilityMode.Oracle ? "select :d, :f" : "select ?, ?", mode,
                new OceanBaseParameter { ParameterName = "d", Value = 1e300 }, new OceanBaseParameter { ParameterName = "f", Value = 1.25f }));

        [Fact]
        public void FinalSqlBudgetIncludesRepeatedEscapedValues()
        {
            var text = new byte[1024 * 1024];
            var parameters = new OceanBaseParameter[33];
            for (var i = 0; i < parameters.Length; i++) parameters[i] = new OceanBaseParameter { Value = text };
            Assert.Throws<ArgumentException>(() => Build("select " + string.Join(",", new string('?', parameters.Length).ToCharArray()), OceanBaseCompatibilityMode.MySql, parameters));
        }

        [Fact]
        public void IntegerLiteralsKeepWorkingAndCustomFormattablesAreNeverInlined()
        {
            Assert.Equal("select (7)", Build("select :p", OceanBaseCompatibilityMode.Oracle, new OceanBaseParameter { ParameterName = "p", Value = 7L }));
            Assert.Equal("select (42)", Build("select ?", OceanBaseCompatibilityMode.MySql, new OceanBaseParameter { Value = 42 }));
            Assert.ThrowsAny<ArgumentException>(() => Build("select :p", OceanBaseCompatibilityMode.Oracle,
                new OceanBaseParameter { ParameterName = "p", DbType = DbType.String, Value = new LiteralProbe("x') OR ('1'='1") }));
            Assert.ThrowsAny<ArgumentException>(() => Build("select :p", OceanBaseCompatibilityMode.Oracle,
                new OceanBaseParameter { ParameterName = "p", DbType = DbType.String, Value = 'x' }));
            Assert.ThrowsAny<ArgumentException>(() => Build("select ?", OceanBaseCompatibilityMode.MySql,
                new OceanBaseParameter { DbType = DbType.Int32, Value = new LiteralProbe("1; DROP TABLE t") }));
        }

        [Fact]
        public void MultiStatementTemporalLiteralsUseModeSpecificSql()
        {
            Assert.Equal("select ('2026-01-02'); select ('2026-01-02 03:04:05.123'); select ('-27:08:09.456000')",
                BuildMulti("select ?; select ?; select ?", OceanBaseCompatibilityMode.MySql,
                    new OceanBaseParameter { DbType = DbType.Date, Value = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Unspecified) },
                    new OceanBaseParameter { DbType = DbType.DateTime, Scale = 3, Value = new DateTime(2026, 1, 2, 3, 4, 5, 123, DateTimeKind.Unspecified) },
                    new OceanBaseParameter { DbType = DbType.Time, Value = new TimeSpan(-1, -3, -8, -9, -456) }));
            Assert.Equal("select (TO_DATE('2026-01-02 03:04:05','YYYY-MM-DD HH24:MI:SS')); select (TIMESTAMP '2026-01-02 03:04:05.123456789')",
                BuildMulti("select :d; select :t", OceanBaseCompatibilityMode.Oracle,
                    new OceanBaseParameter { ParameterName = "d", DbType = DbType.Date, Value = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified) },
                    new OceanBaseParameter { ParameterName = "t", OceanBaseDbType = OceanBaseDbType.Timestamp,
                        Value = new OceanBaseTimestamp(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified), 123456789) }));
        }

        [Fact]
        public void MultiStatementIntervalsKeepExactValuesAndDeclaredPrecisions()
        {
            Assert.Equal("select (INTERVAL '-012-03' YEAR(3) TO MONTH); select (INTERVAL '+004 05:06:07.123456789' DAY(3) TO SECOND(9)); select (NULL)",
                BuildMulti("select :ym; select :ds; select :empty", OceanBaseCompatibilityMode.Oracle,
                    new OceanBaseParameter { ParameterName = "ym", Precision = 3,
                        Value = new OceanBaseIntervalYearToMonth(true, 12, 3) },
                    new OceanBaseParameter { ParameterName = "ds", OceanBaseDbType = OceanBaseDbType.IntervalDayToSecond,
                        Value = new OceanBaseIntervalDayToSecond(false, 4, 5, 6, 7, 123456789, 3) },
                    new OceanBaseParameter { ParameterName = "empty", OceanBaseDbType = OceanBaseDbType.IntervalYearToMonth, Value = DBNull.Value }));
            Assert.Equal("select (INTERVAL '+000000000-00' YEAR(9) TO MONTH); select (INTERVAL '-02 03:04:05.123' DAY(2) TO SECOND(3))",
                BuildMulti("select :ym; select :ds", OceanBaseCompatibilityMode.Oracle,
                    new OceanBaseParameter { ParameterName = "ym", OceanBaseDbType = OceanBaseDbType.IntervalYearToMonth,
                        Value = new OceanBaseIntervalYearToMonth(false, 0, 0) },
                    new OceanBaseParameter { ParameterName = "ds", Precision = 2, Scale = 3,
                        Value = new OceanBaseIntervalDayToSecond(true, 2, 3, 4, 5, 123000000) }));
        }

        [Fact]
        public void MultiStatementIntervalsRetainValidationAndFallbackRestrictions()
        {
            var ym = new OceanBaseParameter { ParameterName = "p", Value = new OceanBaseIntervalYearToMonth(false, 12, 3) };
            var ds = new OceanBaseParameter { ParameterName = "p", Value = new OceanBaseIntervalDayToSecond(false, 4, 5, 6, 7, 123456789) };
            Assert.Throws<NotSupportedException>(() => BuildMulti("select ?; select 1", OceanBaseCompatibilityMode.MySql, ym.Snapshot()));
            ym.Precision = 1;
            Assert.ThrowsAny<ArgumentException>(() => BuildMulti("select :p; select 1", OceanBaseCompatibilityMode.Oracle, ym.Snapshot()));
            ds.Scale = 6;
            Assert.ThrowsAny<ArgumentException>(() => BuildMulti("select :p; select 1", OceanBaseCompatibilityMode.Oracle, ds));
            ym.OceanBaseDbType = OceanBaseDbType.IntervalYearToMonth; ym.Precision = 10; ym.Value = DBNull.Value;
            Assert.Throws<NotSupportedException>(() => BuildMulti("select :p; select 1", OceanBaseCompatibilityMode.Oracle, ym));
        }

        [Fact]
        public void MultiStatementZonedTimestampsKeepOffsetsSourceZonesAndExactScale()
        {
            var timestamp = new OceanBaseTimestamp(new DateTime(2026, 1, 2, 3, 4, 5), 123000000);
            Assert.Equal("select (TIMESTAMP '2026-01-02 03:04:05.123 -00:30'); select (TIMESTAMP '2026-01-02 03:04:05.123 +06:00'); select (CAST(FROM_TZ(TIMESTAMP '2026-01-02 03:04:05.123', '+05:45') AS TIMESTAMP(3) WITH LOCAL TIME ZONE)); select (NULL); select (NULL)",
                BuildMulti("select :offset; select :named; select :local; select :nullz; select :nulll", OceanBaseCompatibilityMode.Oracle,
                    new OceanBaseParameter { ParameterName = "offset", Value = new DateTimeOffset(timestamp.WholeSeconds.AddTicks(1230000), TimeSpan.FromMinutes(-30)), Scale = 3 },
                    new OceanBaseParameter { ParameterName = "named", Value = new OceanBaseTimestampWithTimeZone(timestamp, TimeSpan.FromMinutes(345), "+06:00", "metadata"), Scale = 3 },
                    new OceanBaseParameter { ParameterName = "local", OceanBaseDbType = OceanBaseDbType.TimestampLocalTimeZone,
                        Value = new OceanBaseTimestampLocalTimeZone(timestamp, "+05:45"), Scale = 3 },
                    new OceanBaseParameter { ParameterName = "nullz", OceanBaseDbType = OceanBaseDbType.TimestampWithTimeZone, Value = DBNull.Value },
                    new OceanBaseParameter { ParameterName = "nulll", OceanBaseDbType = OceanBaseDbType.TimestampLocalTimeZone, Value = DBNull.Value }));
            Assert.Equal("select (TIMESTAMP '2026-01-02 03:04:05.123456789 +05:45'); select (CAST(FROM_TZ(TIMESTAMP '2026-01-02 03:04:05.123456789', '+00:00') AS TIMESTAMP(9) WITH LOCAL TIME ZONE))",
                BuildMulti("select :z; select :l", OceanBaseCompatibilityMode.Oracle,
                    new OceanBaseParameter { ParameterName = "z", OceanBaseDbType = OceanBaseDbType.TimestampWithTimeZone,
                        Value = new OceanBaseTimestampWithTimeZone(new OceanBaseTimestamp(timestamp.WholeSeconds, 123456789), TimeSpan.FromMinutes(345)) },
                    new OceanBaseParameter { ParameterName = "l", Value = new OceanBaseTimestampLocalTimeZone(new OceanBaseTimestamp(timestamp.WholeSeconds, 123456789), "+00:00") }));
        }

        [Fact]
        public void MultiStatementZonedTimestampsQuoteZoneNamesAsContent()
        {
            var timestamp = new OceanBaseTimestamp(new DateTime(2026, 1, 2, 3, 4, 5), 0, 0);
            const string zone = "x');DELETE/**/FROM/**/t;--";
            Assert.Equal("select (TIMESTAMP '2026-01-02 03:04:05 x'');DELETE/**/FROM/**/t;--'); select (CAST(FROM_TZ(TIMESTAMP '2026-01-02 03:04:05', 'x'');DELETE/**/FROM/**/t;--') AS TIMESTAMP(0) WITH LOCAL TIME ZONE))",
                BuildMulti("select :z; select :l", OceanBaseCompatibilityMode.Oracle,
                    new OceanBaseParameter { ParameterName = "z", Value = new OceanBaseTimestampWithTimeZone(timestamp, TimeSpan.Zero, zone) },
                    new OceanBaseParameter { ParameterName = "l", Value = new OceanBaseTimestampLocalTimeZone(timestamp, zone) }));
        }

        [Fact]
        public void MultiStatementZonedTimestampsRetainModeTypePrecisionAndFallbackValidation()
        {
            var value = new OceanBaseTimestampWithTimeZone(new OceanBaseTimestamp(new DateTime(2026, 1, 2), 123456789), TimeSpan.Zero);
            Assert.Throws<NotSupportedException>(() => BuildMulti("select ?; select 1", OceanBaseCompatibilityMode.MySql, new OceanBaseParameter { Value = value }));
            Assert.ThrowsAny<ArgumentException>(() => BuildMulti("select :p; select 1", OceanBaseCompatibilityMode.Oracle, new OceanBaseParameter { ParameterName = "p", Value = value, Scale = 6 }));
            Assert.ThrowsAny<ArgumentException>(() => BuildMulti("select :p; select 1", OceanBaseCompatibilityMode.Oracle, new OceanBaseParameter { ParameterName = "p", Value = value, DbType = DbType.DateTime2 }));
            Assert.Throws<NotSupportedException>(() => BuildMulti("select :p; select 1", OceanBaseCompatibilityMode.Oracle, new OceanBaseParameter { ParameterName = "p", Value = DBNull.Value, OceanBaseDbType = OceanBaseDbType.TimestampWithTimeZone, Scale = 10 }));
        }

        private sealed record LiteralProbe(string Text) : IFormattable
        {
            public override string ToString() => Text;
            public string ToString(string? format, IFormatProvider? provider) => Text;
        }
    }
}
