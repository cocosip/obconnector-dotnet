using System;
using System.Data;
using System.Globalization;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Protocol;

public sealed partial class MultiStatementTests
{
    [IntegrationTheory]
    [MemberData(nameof(OracleRows))]
    public async Task OracleObjectScriptParametersPreserveScalarMembersNullsAndFailedPrefix(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true); await Open(connection, asynchronous);
        var name = db.NewObject("TYPE");
        await db.ExecuteAsync(connection, "CREATE TYPE " + name + " AS OBJECT(n NUMBER, s VARCHAR2(80), c CHAR(3), nv NVARCHAR2(40), nc NCHAR(2), r RAW(8), d DATE, ts TIMESTAMP(9))", CancellationToken.None);
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, v " + name, CancellationToken.None);
        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture)!;
        using (var inspect = db.Command(connection, "SELECT ATTR_TYPE_NAME FROM ALL_TYPE_ATTRS WHERE TYPE_NAME=:name ORDER BY ATTR_NO"))
        {
            inspect.Parameters.Add(new OceanBaseParameter { ParameterName = "name", Value = name });
            using var rows = await inspect.ExecuteReaderAsync();
            while (await rows.ReadAsync())
                Assert.Contains(rows.GetString(0), new[] { "NUMBER", "VARCHAR2", "CHAR", "NVARCHAR2", "NCHAR", "RAW", "DATE", "TIMESTAMP", "TIMESTAMP(9)" });
        }
        var type = await connection.GetComplexTypeAsync(owner, name);
        var date = new DateTime(2026, 10, 30, 1, 2, 3);
        var timestamp = new OceanBaseTimestamp(date, 123456789);
        var number = new OceanBaseDecimal(BigInteger.Parse("12345678901234567890123456789012345678", CultureInfo.InvariantCulture), 0);
        var text = "中文'); DELETE FROM t; --";
        var value = type.CreateValue(number, text, "abc", "国🙂", "中文", new byte[] { 0, 39, 92, 255 }, date, timestamp);
        using var insert = db.Command(connection, "INSERT INTO " + table + " VALUES (:id,:p); UPDATE " + table + " SET v=:P WHERE id=:id");
        var id = new OceanBaseParameter { ParameterName = "id", Value = 1 };
        var parameter = new OceanBaseParameter { ParameterName = "p", ComplexType = type, Value = value };
        insert.Parameters.Add(id); insert.Parameters.Add(parameter);
        var inputs = new object[] { value, DBNull.Value, type.CreateValue(new object?[8]) };
        for (var i = 0; i < inputs.Length; i++)
        {
            id.Value = i + 1; parameter.Value = inputs[i];
            Assert.Equal(2, asynchronous ? await insert.ExecuteNonQueryAsync() : insert.ExecuteNonQuery());
        }
        // A later error preserves the successful prefix and prevents the suffix from executing.
        insert.CommandText = "INSERT INTO " + table + " VALUES (4,:p); INSERT INTO " + table + " VALUES (1,:p); INSERT INTO " + table + " VALUES (5,:p)";
        parameter.Value = value; insert.Parameters.Remove(id);
        await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? insert.ExecuteNonQueryAsync() : Task.FromResult(insert.ExecuteNonQuery()));
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, "SELECT 1 FROM DUAL", CancellationToken.None), 1, "The session must remain reusable after a script error.");
        await using var verification = await db.OpenAsync();
        await verification.GetComplexTypeAsync(owner, name);
        using var select = db.Command(verification, "SELECT id,v FROM " + table + " ORDER BY id");
        if (asynchronous) await select.PrepareAsync(); else select.Prepare();
        using var reader = asynchronous ? await select.ExecuteReaderAsync(CommandBehavior.SequentialAccess) : select.ExecuteReader(CommandBehavior.SequentialAccess);
        for (var i = 1; i <= 4; i++)
        {
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            DatabaseContext.EqualNumber(reader.GetValue(0), i, "Stored script row IDs must match the successful prefix.");
            if (i == 2) { Assert.True(reader.IsDBNull(1)); continue; }
            var fields = Assert.IsType<OceanBaseComplexValue>(reader.GetValue(1)).GetValues();
            if (i == 3) { Assert.All(fields, field => Assert.Null(field)); continue; }
            Assert.Equal(number, fields[0]); Assert.Equal(text, fields[1]); Assert.Equal("abc", fields[2]);
            Assert.Equal("国🙂", fields[3]); Assert.Equal("中文", fields[4]);
            Assert.Equal(new byte[] { 0, 39, 92, 255 }, fields[5]); Assert.Equal(date, fields[6]); Assert.Equal(timestamp.ToString(), fields[7]!.ToString());
        }
        Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
    }

    [IntegrationTheory]
    [MemberData(nameof(OracleRows))]
    public async Task OracleObjectConstructorsInScriptsPreserveIndependentStorage(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true); await Open(connection, asynchronous);
        var name = db.NewObject("TYPE");
        await db.ExecuteAsync(connection, "CREATE TYPE " + name + " AS OBJECT(n NUMBER, s VARCHAR2(40))", CancellationToken.None);
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, v " + name, CancellationToken.None);
        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture)!;
        var constructor = "\"" + owner.Replace("\"", "\"\"") + "\".\"" + name + "\"";
        using (var insert = db.Command(connection, "INSERT INTO " + table + " VALUES (1," + constructor + "(7,'中文')); INSERT INTO " + table + " VALUES (2,NULL)"))
            Assert.Equal(2, asynchronous ? await insert.ExecuteNonQueryAsync() : insert.ExecuteNonQuery());
        await using var verification = await db.OpenAsync();
        await verification.GetComplexTypeAsync(owner, name);
        using var select = db.Command(verification, "SELECT v FROM " + table + " ORDER BY id");
        if (asynchronous) await select.PrepareAsync(); else select.Prepare();
        using var reader = asynchronous ? await select.ExecuteReaderAsync(CommandBehavior.SequentialAccess) : select.ExecuteReader(CommandBehavior.SequentialAccess);
        Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
        var values = Assert.IsType<OceanBaseComplexValue>(reader.GetValue(0)).GetValues();
        DatabaseContext.EqualNumber(values[0], 7, "The constructor must preserve the stored NUMBER member.");
        Assert.Equal("中文", values[1]);
        Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.True(reader.IsDBNull(0));
        Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
    }
}
