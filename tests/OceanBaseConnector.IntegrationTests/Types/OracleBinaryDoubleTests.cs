using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Types;

public sealed class OracleBinaryDoubleTests
{
    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.OracleWithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public async Task PreparedDoubleBindsRoundTripThroughBothOracleRowProtocols(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "ID INT PRIMARY KEY, V BINARY_DOUBLE", CancellationToken.None);
        double[] values = { double.MinValue, double.MaxValue, double.Epsilon, -double.Epsilon, 1e200, Math.BitIncrement(1d) };
        await using (var insert = db.Command(connection, $"INSERT INTO {table} (ID,V) VALUES (:id,:v)"))
        {
            var id = DatabaseContext.Parameter(insert, "id", 0, DbType.Int32);
            var value = DatabaseContext.Parameter(insert, "v", 0d, DbType.Double);
            await insert.PrepareAsync();
            for (var i = 0; i < values.Length; i++)
            {
                id.Value = i;
                value.Value = values[i];
                Assert.Equal(1, await insert.ExecuteNonQueryAsync());
            }
            id.Value = values.Length;
            value.Value = -0.0d;
            Assert.Equal(1, await insert.ExecuteNonQueryAsync());
        }
        Assert.Equal(1, await db.ExecuteAsync(connection, $"INSERT INTO {table} (ID,V) VALUES ({values.Length + 1},-0.0D)", CancellationToken.None));

        foreach (var prepared in new[] { false, true })
        {
            await using var select = db.Command(connection, $"SELECT V FROM {table} ORDER BY ID");
            if (prepared) await select.PrepareAsync();
            await using var reader = await select.ExecuteReaderAsync();
            Assert.Equal("BINARY_DOUBLE", reader.GetDataTypeName(0));
            Assert.Equal(typeof(double), reader.GetFieldType(0));
            foreach (var expected in values)
            {
                Assert.True(await reader.ReadAsync());
                var bits = BitConverter.DoubleToInt64Bits(expected);
                Assert.Equal(bits, BitConverter.DoubleToInt64Bits(Assert.IsType<double>(reader.GetValue(0))));
                Assert.Equal(bits, BitConverter.DoubleToInt64Bits(reader.GetDouble(0)));
            }
            // OceanBase may canonicalize negative zero in both stored values.
            Assert.True(await reader.ReadAsync());
            var boundZero = reader.GetDouble(0);
            Assert.Equal(0d, boundZero);
            Assert.True(await reader.ReadAsync());
            Assert.Equal(BitConverter.DoubleToInt64Bits(boundZero), BitConverter.DoubleToInt64Bits(reader.GetDouble(0)));
            Assert.False(await reader.ReadAsync());
        }
    }
}
