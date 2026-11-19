using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Types;

public sealed class OracleJsonTests
{
    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.OracleWithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public async Task OracleJsonRoundTripsTextAndPreparedRowsAndDistinguishesBothNulls(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "ID INT PRIMARY KEY, DOC JSON", CancellationToken.None);
        var document = "{\"value\":\"" + new string('x', 5000) + "中😀\"}";
        await using (var insert = db.Command(connection, $"INSERT INTO {table} (ID, DOC) VALUES (:id,:doc)"))
        {
            var id = DatabaseContext.Parameter(insert, "id", 0, DbType.Int32);
            var doc = new OceanBaseParameter { ParameterName = "doc", OceanBaseDbType = OceanBaseDbType.Json };
            insert.Parameters.Add(doc);
            await insert.PrepareAsync();
            object[] values = { document, "null", DBNull.Value };
            for (var i = 0; i < values.Length; i++)
            {
                id.Value = i + 1;
                doc.Value = values[i];
                Assert.Equal(1, await insert.ExecuteNonQueryAsync());
            }
        }

        foreach (var prepared in new[] { false, true })
        {
            await using var select = db.Command(connection, $"SELECT DOC FROM {table} ORDER BY ID");
            if (prepared) await select.PrepareAsync();
            await using var reader = await select.ExecuteReaderAsync();
            Assert.Equal("JSON", reader.GetDataTypeName(0));
            Assert.Equal(typeof(string), reader.GetFieldType(0));
            Assert.True(await reader.ReadAsync());
            Assert.Equal(document, reader.GetString(0));
            Assert.Equal(document, reader.GetValue(0));
            Assert.True(await reader.ReadAsync());
            Assert.False(reader.IsDBNull(0));
            Assert.Equal("null", reader.GetFieldValue<string>(0));
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.IsDBNull(0));
            Assert.Equal(DBNull.Value, reader.GetValue(0));
            Assert.False(await reader.ReadAsync());
        }
    }
}
