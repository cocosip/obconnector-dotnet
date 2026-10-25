using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Types;

public sealed class OracleNumberTests
{
    public static TheoryData<string, bool> Profiles() => Rows(false);
    public static TheoryData<string, bool> ObjectProfiles() => Rows(true);

    private static TheoryData<string, bool> Rows(bool createObjects)
    {
        var rows = new TheoryData<string, bool>();
        foreach (var profile in IntegrationEnvironment.Profiles)
            if (profile.Oracle && (!createObjects || profile.CreateObjects))
            { rows.Add(profile.Name, false); rows.Add(profile.Name, true); }
        return rows;
    }

    [IntegrationTheory]
    [MemberData(nameof(Profiles))]
    public async Task PreparedBindingsPreserveUnknownPrecisionNumbers(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        await using var command = db.Command(connection, "SELECT :value FROM DUAL");
        var parameter = DatabaseContext.Parameter(command, "value", 7, DbType.Int32);
        if (asynchronous) await command.PrepareAsync(); else command.Prepare();
        foreach (var value in new[] { 7m, 7.25m, -7.2500m, 40m })
        {
            parameter.DbType = value == 7m ? DbType.Int32 : DbType.Decimal;
            parameter.Value = value == 7m ? (object)7 : value;
            await using var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader();
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.Equal(value, reader.GetDecimal(0));
            Assert.Equal(value, reader.GetFieldValue<decimal>(0));
            Assert.Equal(value, Assert.IsType<OceanBaseDecimal>(reader.GetProviderSpecificValue(0)).ToDecimal());
            Assert.Equal(value, Assert.IsType<OceanBaseDecimal>(reader.GetValue(0)).ToDecimal());
            Assert.Null(reader.GetColumnSchema()[0].NumericPrecision);
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
        }
    }

    [IntegrationTheory]
    [MemberData(nameof(ObjectProfiles))]
    public async Task DeclaredNegativeScalesStillReturnTheServersRoundedValue(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "a NUMBER(*,-1), b NUMBER(5,-1), c NUMBER", CancellationToken.None);
        await using (var insert = db.Command(connection, "INSERT INTO " + table + " VALUES (42,42,42)"))
        { if (asynchronous) await insert.ExecuteNonQueryAsync(); else insert.ExecuteNonQuery(); }
        foreach (var sql in new[] { "SELECT a,b,c FROM " + table, "SELECT CAST(42 AS NUMBER(*,-1)), CAST(42 AS NUMBER(5,-1)), CAST(42 AS NUMBER) FROM DUAL" })
        {
            await using var command = db.Command(connection, sql);
            if (asynchronous) await command.PrepareAsync(); else command.Prepare();
            await using var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader();
            var columns = reader.GetColumnSchema();
            Assert.Null(columns[0].NumericPrecision); Assert.Equal(-1, columns[0].NumericScale);
            Assert.Equal(5, columns[1].NumericPrecision); Assert.Equal(-1, columns[1].NumericScale);
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            for (var i = 0; i < 3; i++)
            {
                Assert.Equal(i == 2 ? 42m : 40m, reader.GetDecimal(i));
                Assert.Equal(i == 2 ? 42m : 40m, Assert.IsType<OceanBaseDecimal>(reader.GetProviderSpecificValue(i)).ToDecimal());
            }
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
        }
    }
}
