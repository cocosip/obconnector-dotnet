using System;
using System.Collections.Generic;
using System.Data;
using System.Numerics;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Sql;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class OracleFloatDerivationMetadataTests
    {
        public static IEnumerable<object?[]> ValidMetadata()
        {
            foreach (var type in new[] { "FLOAT", "NUMBER_FLOAT" })
                foreach (var direction in new[] { "IN", "OUT", "INOUT", "RETURN" })
                    foreach (var precision in new int?[] { null, -1, 1, 24, 53, 126 })
                        yield return new object?[] { type, direction, precision };
        }

        [Theory]
        [MemberData(nameof(ValidMetadata))]
        public void OracleFloatDerivationKeepsBinaryPrecisionOutOfDecimalParameters(string type, string direction, int? precision)
        {
            foreach (var scale in new int?[] { null, -85, -1 })
            {
                using var table = DeriveParametersMetadataTests.Arguments();
                var row = DeriveParametersMetadataTests.Argument(table, type);
                row["NUMERIC_PRECISION"] = precision.HasValue ? (object)precision.Value : DBNull.Value;
                row["NUMERIC_SCALE"] = scale.HasValue ? (object)scale.Value : DBNull.Value;
                row["PARAMETER_MODE"] = direction;
                if (direction == "RETURN") { row["ORDINAL_POSITION"] = 0; row["PARAMETER_NAME"] = DBNull.Value; }
                var parameter = RoutineParameterDerivation.Map(row, false, false);
                Assert.Equal(OceanBaseDbType.Decimal, parameter.OceanBaseDbType);
                Assert.Equal(DbType.Decimal, parameter.DbType);
                Assert.Equal(direction == "IN" ? ParameterDirection.Input : direction == "OUT" ? ParameterDirection.Output
                    : direction == "INOUT" ? ParameterDirection.InputOutput : ParameterDirection.ReturnValue, parameter.Direction);
                Assert.Equal(direction == "RETURN" ? "RETURN_VALUE" : "value", parameter.ParameterName);
                Assert.Equal(0, parameter.Precision); Assert.Null(parameter.DeclaredDecimalScale);
                Assert.Equal(DBNull.Value, parameter.Value);
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
        [InlineData(0, null)]
        [InlineData(-2, null)]
        [InlineData(127, null)]
        [InlineData(255, null)]
        [InlineData(126, 0)]
        [InlineData(126, 2)]
        [InlineData(126, -84)]
        [InlineData(126, -127)]
        public void OracleFloatDerivationRejectsUnknownPrecisionAndFixedScaleShapes(int precision, int? scale)
        {
            foreach (var type in new[] { "FLOAT", "NUMBER_FLOAT" })
            {
                using var table = DeriveParametersMetadataTests.Arguments(); var row = DeriveParametersMetadataTests.Argument(table, type);
                row["NUMERIC_PRECISION"] = precision;
                row["NUMERIC_SCALE"] = scale.HasValue ? (object)scale.Value : DBNull.Value;
                Assert.Throws<NotSupportedException>(() => RoutineParameterDerivation.Map(row, false, false));
            }
        }

        [Theory]
        [InlineData("FLOAT(24)")]
        [InlineData("NUMBER_FLOAT(126)")]
        [InlineData("FLOAT(999)")]
        public void OracleFloatDerivationDoesNotDropDimensionsFromTheDirectoryTypeName(string type)
        {
            using var table = DeriveParametersMetadataTests.Arguments(); var row = DeriveParametersMetadataTests.Argument(table, type);
            row["NUMERIC_PRECISION"] = 24;
            Assert.Throws<NotSupportedException>(() => RoutineParameterDerivation.Map(row, false, false));
        }

        [Fact]
        public void OracleNumberFloatDirectoryAliasDoesNotExpandMySql()
        {
            using var table = DeriveParametersMetadataTests.Arguments(); var row = DeriveParametersMetadataTests.Argument(table, "NUMBER_FLOAT");
            Assert.Throws<NotSupportedException>(() => RoutineParameterDerivation.Map(row, true, false));
            row["DATA_TYPE"] = "FLOAT"; row["TYPE_DECLARATION"] = "float";
            Assert.Equal(OceanBaseDbType.Single, RoutineParameterDerivation.Map(row, true, false).OceanBaseDbType);
        }
    }
}
