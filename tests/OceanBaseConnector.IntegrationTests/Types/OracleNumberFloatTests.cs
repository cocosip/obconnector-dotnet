using System;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Types;

public sealed class OracleNumberFloatTests
{
    public static TheoryData<string, bool> Profiles()
    {
        var rows = new TheoryData<string, bool>();
        foreach (var profile in IntegrationEnvironment.Profiles)
            if (profile.Oracle && profile.CreateObjects)
            { rows.Add(profile.Name, false); rows.Add(profile.Name, true); }
        return rows;
    }

    [IntegrationTheory]
    [MemberData(nameof(Profiles))]
    public async Task FloatColumnsAndCastsPreserveExactNumberValues(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, a FLOAT(126), b FLOAT(24)", CancellationToken.None);
        var values = new OceanBaseDecimal?[] { new OceanBaseDecimal(7.25m),
            new OceanBaseDecimal(BigInteger.Parse("12345678901234567890123456789012345678", CultureInfo.InvariantCulture), 0),
            new OceanBaseDecimal(BigInteger.One, 130), new OceanBaseDecimal(BigInteger.One, -125), null };
        await using (var insert = db.Command(connection, "INSERT INTO " + table + " VALUES (:id,:a,:b)"))
        {
            var id = DatabaseContext.Parameter(insert, "id", 0, DbType.Int32);
            var a = DatabaseContext.Parameter(insert, "a", null); a.OceanBaseDbType = OceanBaseDbType.Decimal;
            DatabaseContext.Parameter(insert, "b", new OceanBaseDecimal(1.5m)).OceanBaseDbType = OceanBaseDbType.Decimal;
            if (asynchronous) await insert.PrepareAsync(); else insert.Prepare();
            for (var i = 0; i < values.Length; i++)
            {
                id.Value = i; a.Value = values[i].HasValue ? (object)values[i]!.Value : DBNull.Value;
                Assert.Equal(1, asynchronous ? await insert.ExecuteNonQueryAsync() : insert.ExecuteNonQuery());
            }
        }
        foreach (var binary in new[] { false, true })
            foreach (var sequential in new[] { false, true })
            {
                await using var command = db.Command(connection, "SELECT a,b FROM " + table + " ORDER BY id");
                if (binary) { if (asynchronous) await command.PrepareAsync(); else command.Prepare(); }
                await using var reader = asynchronous ? await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default)
                    : command.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                var schema = reader.GetColumnSchema();
                for (var j = 0; j < 2; j++)
                {
                    Assert.Equal("FLOAT", reader.GetDataTypeName(j)); Assert.Equal(typeof(OceanBaseDecimal), reader.GetFieldType(j));
                    Assert.Equal(j == 0 ? 126 : 24, schema[j].NumericPrecision); Assert.Null(schema[j].NumericScale);
                }
                foreach (var expected in values)
                {
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    if (expected.HasValue)
                    {
                        var actual = reader.GetFieldValue<OceanBaseDecimal>(0);
                        // Server formatting may normalize coefficient and scale without changing the value.
                        var scale = Math.Max(expected.Value.Scale, actual.Scale);
                        Assert.Equal(expected.Value.Coefficient * BigInteger.Pow(10, scale - expected.Value.Scale), actual.Coefficient * BigInteger.Pow(10, scale - actual.Scale));
                        Assert.Equal(actual, reader.GetProviderSpecificValue(0));
                        if (expected.Value.Scale == 2) Assert.Equal(7.25m, reader.GetDecimal(0));
                        else Assert.Throws<OverflowException>(() => reader.GetDecimal(0));
                    }
                    else Assert.Equal(DBNull.Value, reader.GetValue(0));
                    Assert.Equal(1.5m, reader.GetDecimal(1));
                }
                Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            }
        await using var cast = db.Command(connection, "SELECT CAST(7.25 AS FLOAT(126)) FROM DUAL");
        Assert.Equal(7.25m, Assert.IsType<OceanBaseDecimal>(asynchronous ? await cast.ExecuteScalarAsync() : cast.ExecuteScalar()).ToDecimal());
    }
}
