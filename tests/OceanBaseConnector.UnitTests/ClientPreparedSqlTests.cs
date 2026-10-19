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

        [Theory]
        [InlineData("select :p, ':hidden ?', q'[it''s ? :hidden]', :P", "select (CAST('it''s \\ 中文' AS VARCHAR2(4000))), ':hidden ?', q'[it''s ? :hidden]', (CAST('it''s \\ 中文' AS VARCHAR2(4000)))")]
        [InlineData("select /* ? :hidden */ :p -- ?\n", "select /* ? :hidden */ (CAST('it''s \\ 中文' AS VARCHAR2(4000))) -- ?\n")]
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
            Assert.Throws<NotSupportedException>(() => Build("select :p", OceanBaseCompatibilityMode.Oracle, new OceanBaseParameter { ParameterName = "p", Value = new DateTime(2026, 1, 1) }));
            Assert.Throws<NotSupportedException>(() => Build("select :p", OceanBaseCompatibilityMode.Oracle, new OceanBaseParameter { ParameterName = "p", OceanBaseDbType = OceanBaseDbType.NVarchar2, Value = DBNull.Value }));
            Assert.Throws<ArgumentException>(() => Build("select :p", OceanBaseCompatibilityMode.Oracle, new OceanBaseParameter { ParameterName = "p", Value = "x\0y" }));
            Assert.ThrowsAny<ArgumentException>(() => Build("select :p", OceanBaseCompatibilityMode.Oracle, new OceanBaseParameter { ParameterName = "p", Value = "\ud800" }));
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
        public void ConservativeOracleLiteralLimitsAreChecked()
        {
            Assert.Throws<ArgumentException>(() => Build("select :p", OceanBaseCompatibilityMode.Oracle, new OceanBaseParameter { ParameterName = "p", Value = new string('x', 4001) }));
            Assert.Throws<ArgumentException>(() => Build("select :p", OceanBaseCompatibilityMode.Oracle, new OceanBaseParameter { ParameterName = "p", Value = new byte[2001] }));
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
    }
}
