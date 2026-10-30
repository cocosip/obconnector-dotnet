using System;
using System.Data;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Sql;
using Xunit;

namespace OceanBaseConnector.UnitTests;

public sealed class GeometryParameterTests
{
    [Theory]
    [InlineData("GEOMETRY")]
    [InlineData("POINT")]
    [InlineData("LINESTRING")]
    [InlineData("POLYGON")]
    [InlineData("MULTIPOINT")]
    [InlineData("MULTILINESTRING")]
    [InlineData("MULTIPOLYGON")]
    [InlineData("GEOMETRYCOLLECTION")]
    [InlineData("GEOMCOLLECTION")]
    public void MySqlSpatialRoutineArgumentsBindUnmodifiedGeometryBytes(string type)
    {
        using var table = DeriveParametersMetadataTests.Arguments();
        var row = DeriveParametersMetadataTests.Argument(table, type, type.ToLowerInvariant());
        var parameter = RoutineParameterDerivation.Map(row, true, false);
        Assert.Equal(OceanBaseDbType.Binary, parameter.OceanBaseDbType);
        Assert.Equal(DbType.Binary, parameter.DbType);
        Assert.Equal(0, parameter.Size);
        var bytes = Convert.FromHexString("000000000101000000000000000000F03F0000000000000040");
        parameter.Value = bytes;
        Assert.Equal(Convert.FromHexString("0001FC0019000000000101000000000000000000F03F0000000000000040"), PreparedParameters.Encode(new[] { parameter }));
        var parameters = new OceanBaseParameterCollection(); parameter.ParameterName = "p"; parameters.Add(parameter);
        Assert.Equal("INSERT INTO t VALUES ((X'000000000101000000000000000000F03F0000000000000040'))",
            ClientPreparedSql.Build(ParameterBindingPlan.Create("INSERT INTO t VALUES (?)", OceanBaseCompatibilityMode.MySql, parameters), OceanBaseCompatibilityMode.MySql));
        parameter.Value = DBNull.Value;
        Assert.Equal(new byte[] { 1, 1, 6, 0 }, PreparedParameters.Encode(new[] { parameter }));
        Assert.Throws<NotSupportedException>(() => RoutineParameterDerivation.Map(row, false, false));
    }
}
