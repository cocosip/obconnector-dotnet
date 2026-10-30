using System;
using System.Data;
using System.Numerics;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Sql;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class OracleNumberDerivationMetadataTests
    {
        [Theory]
        [InlineData("IN")]
        [InlineData("OUT")]
        [InlineData("INOUT")]
        [InlineData("RETURN")]
        public void UnconstrainedNumberSentinelDoesNotConstrainBoundValues(string direction)
        {
            foreach (var type in new[] { "NUMBER", "DECIMAL", "NUMERIC", "INTEGER" })
            {
                using var table = DeriveParametersMetadataTests.Arguments();
                var row = DeriveParametersMetadataTests.Argument(table, type);
                row["NUMERIC_SCALE"] = -85; row["PARAMETER_MODE"] = direction;
                if (direction == "RETURN") { row["ORDINAL_POSITION"] = 0; row["PARAMETER_NAME"] = DBNull.Value; }
                var parameter = RoutineParameterDerivation.Map(row, false, false);
                Assert.Equal(DbType.Decimal, parameter.DbType); Assert.Equal(0, parameter.Precision);
                Assert.Null(parameter.DecimalScale); Assert.Equal(DBNull.Value, parameter.Value);
                Assert.Equal(direction == "IN" ? ParameterDirection.Input : direction == "OUT" ? ParameterDirection.Output
                    : direction == "INOUT" ? ParameterDirection.InputOutput : ParameterDirection.ReturnValue, parameter.Direction);
                foreach (var value in new[] { new OceanBaseDecimal(7.2500m), new OceanBaseDecimal(700m),
                    new OceanBaseDecimal(BigInteger.One, 130), new OceanBaseDecimal(BigInteger.One, -125) })
                {
                    parameter.Value = value;
                    Assert.Equal(PreparedParameters.Encode(new[] { new OceanBaseParameter { Value = value, OceanBaseDbType = OceanBaseDbType.Decimal, Direction = parameter.Direction } }, OceanBaseCompatibilityMode.Oracle),
                        PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
                }
            }
        }

        [Theory]
        [InlineData(-84)]
        [InlineData(-1)]
        [InlineData(0)]
        [InlineData(127)]
        public void RealSignedScalesStillConstrainDerivedNumberBindings(int scale)
        {
            using var table = DeriveParametersMetadataTests.Arguments(); var row = DeriveParametersMetadataTests.Argument(table, "NUMBER");
            row["NUMERIC_PRECISION"] = 3; row["NUMERIC_SCALE"] = scale;
            var parameter = RoutineParameterDerivation.Map(row, false, false);
            Assert.Equal(3, parameter.Precision); Assert.Equal(scale, parameter.DecimalScale);
            parameter.Value = new OceanBaseDecimal(BigInteger.One, scale);
            Assert.NotEmpty(PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
            parameter.Value = new OceanBaseDecimal(new BigInteger(1001), scale);
            Assert.Throws<OverflowException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
        }

        [Theory]
        [InlineData(null, -86, false)]
        [InlineData(null, -127, false)]
        [InlineData(1, -85, false)]
        [InlineData(0, -85, false)]
        [InlineData(null, -85, true)]
        [InlineData(null, null, true)]
        public void UnconstrainedNumberHandlingDoesNotAcceptInvalidOrMySqlMetadata(int? precision, int? scale, bool mysql)
        {
            using var table = DeriveParametersMetadataTests.Arguments();
            var row = DeriveParametersMetadataTests.Argument(table, mysql ? "DECIMAL" : "NUMBER", mysql ? "decimal" : null);
            row["NUMERIC_PRECISION"] = precision.HasValue ? (object)precision.Value : DBNull.Value;
            row["NUMERIC_SCALE"] = scale.HasValue ? (object)scale.Value : DBNull.Value;
            Assert.Throws<NotSupportedException>(() => RoutineParameterDerivation.Map(row, mysql, false));
        }
    }
}
