using System;
using System.Data;
using System.Numerics;
using System.Text;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Sql;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class DecimalScaleTests
    {
        private static void SetScale(OceanBaseParameter parameter, int? scale)
            => parameter.DecimalScale = scale;

        [Theory]
        [InlineData(3, -2, 12300, "12300")]
        [InlineData(3, -2, -12300, "-12300")]
        [InlineData(0, -1, 120, "120")]
        public void OracleDecimalScaleConstrainsActualParameterPayload(byte precision, int scale, int value, string expected)
        {
            var parameter = new OceanBaseParameter { DbType = DbType.Decimal, Precision = precision, Value = new OceanBaseDecimal(new BigInteger(value), 0) };
            SetScale(parameter, scale);
            var encoded = PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle);
            Assert.Equal(new byte[] { 0, 1, 246, 0 }, encoded.AsSpan(0, 4).ToArray());
            Assert.Equal(expected, Encoding.ASCII.GetString(new ByteCursor(encoded.AsSpan(4).ToArray()).ReadLengthEncodedBytes()));
        }

        [Fact]
        public void OracleDecimalScaleRejectsRoundingAndPrecisionOverflow()
        {
            var parameter = new OceanBaseParameter { DbType = DbType.Decimal, Precision = 3, Value = 12345m };
            SetScale(parameter, -2);
            Assert.Throws<OverflowException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
            parameter.Value = 12300m; parameter.Precision = 2;
            Assert.Throws<OverflowException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
        }

        [Theory]
        [InlineData(-84)]
        [InlineData(127)]
        public void OracleDecimalScaleTypedNullSupportsMetadataBounds(int scale)
        {
            var parameter = new OceanBaseParameter { DbType = DbType.Decimal, Value = DBNull.Value };
            SetScale(parameter, scale);
            Assert.Equal(new byte[] { 1, 1, 6, 0 }, PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
        }

        [Theory]
        [InlineData(-84)]
        [InlineData(127)]
        public void OracleDecimalScaleEncodesNonzeroBoundaryValues(int scale)
        {
            var parameter = new OceanBaseParameter { Value = new OceanBaseDecimal(BigInteger.One, scale), Precision = 1, DecimalScale = scale };
            var wire = PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle);
            var expected = scale < 0 ? "1" + new string('0', -scale) : "0." + new string('0', scale - 1) + "1";
            Assert.Equal(expected, Encoding.ASCII.GetString(new ByteCursor(wire.AsSpan(4).ToArray()).ReadLengthEncodedBytes()));
        }

        [Theory]
        [InlineData(-85)]
        [InlineData(128)]
        [InlineData(int.MinValue)]
        [InlineData(int.MaxValue)]
        public void DecimalScaleSetterRejectsOutOfRangeWithoutChangingTheParameter(int scale)
        {
            var parameter = new OceanBaseParameter { DecimalScale = -2 };
            Assert.Throws<ArgumentOutOfRangeException>(() => parameter.DecimalScale = scale);
            Assert.Equal(-2, parameter.DecimalScale);
        }

        [Fact]
        public void DecimalScaleLegacyPrecedenceAndSnapshotsPreserveDeclaredOrInferredScale()
        {
            var parameter = new OceanBaseParameter { Value = 12300m, Precision = 3, Scale = 2 };
            Assert.Null(parameter.DecimalScale);
            parameter.DecimalScale = -2;
            Assert.Equal(0, parameter.Scale);
            var copy = parameter.Snapshot();
            Assert.Equal(-2, copy.DecimalScale);
            parameter.Scale = 3;
            Assert.Null(parameter.DecimalScale); Assert.Equal(3, parameter.DeclaredDecimalScale);
            Assert.Equal(-2, copy.DeclaredDecimalScale);
            parameter.DecimalScale = 1;
            Assert.Equal(1, parameter.Scale); Assert.Null(parameter.DeclaredScale);
            parameter.DecimalScale = null;
            Assert.Equal(0, parameter.Scale); Assert.Null(parameter.DeclaredDecimalScale);
            parameter.Value = new OceanBaseDecimal(new BigInteger(123), -2); parameter.Precision = 0;
            var wire = PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle);
            Assert.Equal("12300", Encoding.ASCII.GetString(new ByteCursor(wire.AsSpan(4).ToArray()).ReadLengthEncodedBytes()));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(31)]
        public void MySqlDecimalScaleValidatesEvenNullAndPrevalidation(int scale)
        {
            foreach (var value in new object[] { DBNull.Value, 1m })
                foreach (var prevalidate in new[] { false, true })
                {
                    var parameter = new OceanBaseParameter { DbType = DbType.Decimal, DecimalScale = scale, Value = value };
                    Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.MySql, validateOnly: prevalidate));
                }
        }

        [Theory]
        [InlineData(DbType.String)]
        [InlineData(DbType.Binary)]
        [InlineData(DbType.DateTime2)]
        [InlineData(DbType.Object)]
        [InlineData(DbType.Int32)]
        public void DecimalScaleCannotBeIgnoredByNonDecimalOrNullParameters(DbType type)
        {
            var parameter = new OceanBaseParameter { DbType = type, DecimalScale = 0, Value = DBNull.Value };
            var error = Assert.Throws<ArgumentException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
            Assert.Equal(nameof(OceanBaseParameter.DecimalScale), error.ParamName);
        }

        [Theory]
        [InlineData(OceanBaseDbType.Array)]
        [InlineData(OceanBaseDbType.Struct)]
        [InlineData(OceanBaseDbType.RefCursor)]
        [InlineData(OceanBaseDbType.Blob)]
        public void DecimalScaleCannotBypassComplexOrLocatorFastPaths(OceanBaseDbType type)
        {
            var parameter = new OceanBaseParameter { OceanBaseDbType = type, DecimalScale = 0, Value = DBNull.Value };
            var error = Assert.Throws<ArgumentException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
            Assert.Equal(nameof(OceanBaseParameter.DecimalScale), error.ParamName);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void DecimalScaleAlsoControlsClientPreparedNumericLiterals(bool mysql)
        {
            var parameter = new OceanBaseParameter { Value = mysql ? 12m : 12300m, Precision = 3, DecimalScale = mysql ? 1 : -2 };
            var mode = mysql ? OceanBaseCompatibilityMode.MySql : OceanBaseCompatibilityMode.Oracle;
            var bindings = new ParameterBindingPlan("select ?", new[] { parameter }, new[] { 7 });
            Assert.Equal(mysql ? "select (12.0)" : "select (12300)", ClientPreparedSql.Build(bindings, mode));
            parameter.Value = mysql ? 12.34m : 12345m;
            Assert.Throws<OverflowException>(() => ClientPreparedSql.Build(bindings, mode));
            Assert.Throws<OverflowException>(() => PreparedParameters.Encode(new[] { parameter }, mode, validateOnly: true));
        }

        [Fact]
        public void DecimalScalePreservesMySqlPrecisionAndLegacyScaleGuards()
        {
            var parameter = new OceanBaseParameter { DbType = DbType.Decimal, Value = DBNull.Value, Precision = 2, DecimalScale = 3 };
            Assert.Throws<ArgumentException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.MySql));
            parameter.Scale = 128;
            Assert.Null(parameter.DecimalScale);
            Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
            parameter.Scale = 0; parameter.DecimalScale = 30; parameter.Precision = 65;
            Assert.Equal(new byte[] { 1, 1, 6, 0 }, PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.MySql));
        }
    }
}
