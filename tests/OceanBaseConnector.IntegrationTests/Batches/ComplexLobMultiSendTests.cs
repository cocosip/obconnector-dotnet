using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Batches;

public sealed class ComplexLobMultiSendTests
{
    public static IEnumerable<object[]> Rows() =>
        from profile in IntegrationEnvironment.Profiles.Where(p => p.Oracle && p.CreateObjects && p.HasTag("direct"))
        from asynchronous in new[] { false, true }
        from collection in new[] { false, true }
        from character in new[] { false, true }
        select new object[] { profile.Name, asynchronous, collection, character };

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task NamedLobPayloadsReachScalarStorageThroughMultiSend(string profileName, bool asynchronous, bool collection, bool character)
    {
        var profile = IntegrationEnvironment.GetProfile(profileName);
        var builder = profile.CreateBuilder(); builder.UseOraclePrepareExecute = true; builder.UseLobLocators = true;
        builder.UseBatchMultiSend = true; builder.BatchMultiSendNumber = 2;
        await using var db = new DatabaseContext(new IntegrationProfile
        {
            Name = profile.Name, Mode = profile.Mode, ConnectionString = builder.ConnectionString, CreateObjects = true,
        });
        await using var connection = await db.OpenAsync();
        var name = db.NewObject("TYPE");
        var scalar = character ? "CLOB" : "BLOB";
        await db.ExecuteAsync(connection, "CREATE TYPE " + name + (collection ? " AS TABLE OF " + scalar : " AS OBJECT(a " + scalar + ")"), CancellationToken.None);
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY,value VARCHAR2(200)", CancellationToken.None);
        var function = db.NewObject("PROCEDURE", registerCleanup: false); db.RegisterDropCleanup("FUNCTION", function);
        var member = collection ? "p(1)" : "p.a";
        var result = "DBMS_LOB.SUBSTR(" + member + ",32,1)";
        if (!character) result = "RAWTOHEX(" + result + ")";
        await db.ExecuteAsync(connection, "CREATE FUNCTION " + function + "(p IN " + name + ") RETURN VARCHAR2 IS BEGIN " +
            "IF p IS NULL THEN RETURN NULL; END IF; " + (collection ? "IF p.COUNT=0 THEN RETURN 'empty'; END IF; " : "") +
            "RETURN " + result + "; END;", CancellationToken.None);
        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture)!;
        var type = await connection.GetComplexTypeAsync(owner, name);
        const string text = "患者🙂'); --\0tail";
        var bytes = new byte[] { 0, 128, 255, 39, 92 };
        object payload = character ? text : bytes;
        object last = character ? "last" : new byte[] { 42 };
        var values = new object[] { collection ? type.CreateValue(payload, null) : type.CreateValue(payload), DBNull.Value,
            type.CreateValue((object?)null), collection ? type.CreateValue(Array.Empty<object?>()) : type.CreateValue(last) };
        using (var batch = connection.CreateBatch())
        {
            for (var i = 0; i < values.Length; i++)
            {
                var item = batch.CreateBatchCommand();
                item.CommandText = "INSERT INTO " + table + " SELECT :id," + function + "(:p) FROM DUAL";
                item.Parameters.Add(new OceanBaseParameter { ParameterName = "id", DbType = DbType.Int32, Value = i + 1 });
                item.Parameters.Add(new OceanBaseParameter { ParameterName = "p", ComplexType = type, Value = values[i] });
                batch.BatchCommands.Add(item);
            }
            Assert.Equal(4, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
            Assert.All(batch.BatchCommands.Cast<OceanBaseBatchCommand>(), item => Assert.Equal(1, item.RecordsAffected));
        }
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1, "Writer must remain reusable.");
        await using var observer = await db.OpenAsync();
        using var query = db.Command(observer, "SELECT id,value FROM " + table + " ORDER BY id");
        using var reader = await query.ExecuteReaderAsync();
        for (var i = 0; i < 4; i++)
        {
            Assert.True(await reader.ReadAsync()); DatabaseContext.EqualNumber(reader.GetValue(0), i + 1, "Input order must survive.");
            if (i == 1 || i == 2) Assert.True(reader.IsDBNull(1));
            else Assert.Equal(i == 0 ? character ? text : Convert.ToHexString(bytes) : collection ? "empty" : character ? "last" : "2A", reader.GetString(1));
        }
        Assert.False(await reader.ReadAsync());
    }
}
