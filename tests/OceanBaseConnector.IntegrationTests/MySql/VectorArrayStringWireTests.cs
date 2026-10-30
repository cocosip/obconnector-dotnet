using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.MySql;

public sealed class VectorArrayStringWireTests
{
    public static TheoryData<string, bool, bool> Cases()
    {
        var data = new TheoryData<string, bool, bool>();
        foreach (var profile in IntegrationEnvironment.Profiles)
            if (!profile.Oracle && profile.CreateObjects && profile.HasTag("direct"))
                foreach (var asynchronous in new[] { false, true })
                    foreach (var prepared in new[] { false, true }) data.Add(profile.Name, asynchronous, prepared);
        return data;
    }

    [IntegrationTheory]
    [MemberData(nameof(Cases))]
    public async Task VectorAndArrayColumnsUseExistingStringWirePath(string profileName, bool asynchronous, bool prepared)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var builder = db.Profile.CreateBuilder(); builder.Pooling = false;
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        if (asynchronous) await connection.OpenAsync(); else connection.Open();

        var table = await db.TableAsync(connection, "id INT PRIMARY KEY,v VECTOR(3),a ARRAY(INT)", CancellationToken.None);
        await using (var insert = db.Command(connection, "INSERT INTO " + table + " VALUES (?,?,?)"))
        {
            var id = DatabaseContext.Parameter(insert, "", 0, DbType.Int32);
            var vector = DatabaseContext.Parameter(insert, "", DBNull.Value, DbType.String);
            var array = DatabaseContext.Parameter(insert, "", DBNull.Value, DbType.String);
            if (prepared) { if (asynchronous) await insert.PrepareAsync(); else insert.Prepare(); }
            foreach (var row in new[] { (1, "[1,2,3]", "[1,2,3]"), (2, (string?)null, (string?)null), (3, "[3,2,1]", "[3,2,1]") })
            {
                id.Value = row.Item1; vector.Value = row.Item2 ?? (object)DBNull.Value; array.Value = row.Item3 ?? (object)DBNull.Value;
                Assert.Equal(1, asynchronous ? await insert.ExecuteNonQueryAsync() : insert.ExecuteNonQuery());
            }
        }

        await using var observer = await db.OpenAsync();
        await using var query = db.Command(observer, "SELECT id,v,a FROM " + table + " ORDER BY id");
        if (prepared) { if (asynchronous) await query.PrepareAsync(); else query.Prepare(); }
        await using var reader = asynchronous ? await query.ExecuteReaderAsync(CommandBehavior.SequentialAccess) : query.ExecuteReader(CommandBehavior.SequentialAccess);
        Assert.Equal("CHAR", reader.GetDataTypeName(1));
        Assert.Equal("CHAR", reader.GetDataTypeName(2));
        Assert.Equal(typeof(string), reader.GetFieldType(1));
        Assert.Equal(typeof(string), reader.GetFieldType(2));
        var schema = Assert.IsType<OceanBaseDataReader>(reader).GetColumnSchema();
        Assert.Equal((int)OceanBaseDbType.String, schema[1]["ProviderType"]);
        Assert.Equal((int)OceanBaseDbType.String, schema[2]["ProviderType"]);

        var expected = new (string? Vector, string? Array)[] { ("[1,2,3]", "[1,2,3]"), (null, null), ("[3,2,1]", "[3,2,1]") };
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            DatabaseContext.EqualNumber(reader.GetValue(0), i + 1, "Vector/ARRAY row order mismatch.");
            if (expected[i].Vector == null)
            {
                Assert.True(reader.IsDBNull(1)); Assert.True(reader.IsDBNull(2));
            }
            else
            {
                Assert.Equal(expected[i].Vector, reader.GetString(1)); Assert.Equal(expected[i].Array, reader.GetString(2));
            }
        }
        Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
        Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
    }
}
