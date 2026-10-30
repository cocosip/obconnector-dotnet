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
    public async Task OracleNestedObjectScriptParametersPreserveNullShapesAndFailedPrefix(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true); await Open(connection, asynchronous);
        var childName = db.NewObject("TYPE");
        await db.ExecuteAsync(connection, "CREATE TYPE " + childName + " AS OBJECT(n NUMBER, s NVARCHAR2(40), r RAW(8), ts TIMESTAMP(9))", CancellationToken.None);
        var parentName = db.NewObject("TYPE");
        await db.ExecuteAsync(connection, "CREATE TYPE " + parentName + " AS OBJECT(child " + childName + ", tag VARCHAR2(40))", CancellationToken.None);
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, v " + parentName, CancellationToken.None);
        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture)!;
        var type = await connection.GetComplexTypeAsync(owner, parentName); var child = type.Members[0].ComplexType!;
        var number = new OceanBaseDecimal(BigInteger.Parse("12345678901234567890123456789012345678", CultureInfo.InvariantCulture), 0);
        var timestamp = new OceanBaseTimestamp(new DateTime(2026, 10, 30, 1, 2, 3), 123456789);
        var nested = child.CreateValue(number, "国🙂", new byte[] { 0, 39, 92, 255 }, timestamp);
        var value = type.CreateValue(nested, "'); DELETE FROM t; --");
        var inputs = new object[] { value, DBNull.Value, type.CreateValue(null, "null child"),
            type.CreateValue(child.CreateValue(new object?[4]), "all null child"), type.CreateValue(null, null) };
        using var insert = db.Command(connection, "INSERT INTO " + table + " VALUES (:id,:p); UPDATE " + table + " SET v=:P WHERE id=:id");
        var id = DatabaseContext.Parameter(insert, "id", 1);
        var parameter = DatabaseContext.Parameter(insert, "p", value); parameter.ComplexType = type;
        for (var i = 0; i < inputs.Length; i++)
        {
            id.Value = i + 1; parameter.Value = inputs[i];
            Assert.Equal(2, asynchronous ? await insert.ExecuteNonQueryAsync() : insert.ExecuteNonQuery()); Assert.Same(inputs[i], parameter.Value);
        }
        insert.Parameters.Remove(id); parameter.Value = value;
        insert.CommandText = "INSERT INTO " + table + " VALUES (6,:p); INSERT INTO " + table + " VALUES (1,:p); INSERT INTO " + table + " VALUES (7,:p)";
        await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? insert.ExecuteNonQueryAsync() : Task.FromResult(insert.ExecuteNonQuery()));
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1, "Nested script writer remains reusable.");
        await using var verification = await db.OpenAsync();
        var verificationType = await verification.GetComplexTypeAsync(owner, parentName);
        foreach (var sequential in new[] { false, true })
        {
            using var query = db.Command(verification, "SELECT id,v FROM " + table + " ORDER BY id");
            if (asynchronous) await query.PrepareAsync(); else query.Prepare();
            using var reader = asynchronous ? await query.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default)
                : query.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
            Assert.Equal(parentName, reader.GetDataTypeName(1));
            for (var i = 1; i <= 6; i++)
            {
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                DatabaseContext.EqualNumber(reader.GetValue(0), i, "The failed suffix must not persist.");
                if (i == 2) { Assert.True(reader.IsDBNull(1)); continue; }
                var actual = Assert.IsType<OceanBaseComplexValue>(reader.GetValue(1)); Assert.Same(verificationType, actual.Type);
                var fields = actual.GetValues();
                Assert.Equal(i == 3 ? "null child" : i == 4 ? "all null child" : i == 5 ? null : "'); DELETE FROM t; --", fields[1]);
                if (i == 3 || i == 5) { Assert.Null(fields[0]); continue; }
                var actualChild = Assert.IsType<OceanBaseComplexValue>(fields[0]); Assert.Same(verificationType.Members[0].ComplexType, actualChild.Type);
                var members = actualChild.GetValues();
                if (i == 4) { Assert.All(members, member => Assert.Null(member)); continue; }
                Assert.Equal(number, members[0]); Assert.Equal("国🙂", members[1]); Assert.Equal(new byte[] { 0, 39, 92, 255 }, members[2]);
                Assert.Equal(timestamp.ToString(), members[3]!.ToString());
            }
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        }
    }

    [IntegrationTheory]
    [MemberData(nameof(OracleRows))]
    public async Task OracleNestedObjectConstructorsInScriptsPreserveIndependentStorage(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true); await Open(connection, asynchronous);
        var childName = db.NewObject("TYPE");
        await db.ExecuteAsync(connection, "CREATE TYPE " + childName + " AS OBJECT(n NUMBER, s NVARCHAR2(40), r RAW(8), ts TIMESTAMP(9))", CancellationToken.None);
        var parentName = db.NewObject("TYPE");
        await db.ExecuteAsync(connection, "CREATE TYPE " + parentName + " AS OBJECT(child " + childName + ", tag VARCHAR2(40))", CancellationToken.None);
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, v " + parentName, CancellationToken.None);
        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture)!;
        var prefix = "\"" + owner.Replace("\"", "\"\"") + "\".\"";
        var child = prefix + childName + "\""; var parent = prefix + parentName + "\"";
        using (var insert = db.Command(connection, "INSERT INTO " + table + " VALUES (1," + parent + "(" + child + "(7,u'\\4e2d',HEXTORAW('00FF'),TIMESTAMP '2026-10-30 01:02:03.123456789'),'tag')); INSERT INTO " + table + " VALUES (2," + parent + "(NULL,'null child'))"))
            Assert.Equal(2, asynchronous ? await insert.ExecuteNonQueryAsync() : insert.ExecuteNonQuery());
        await using var verification = await db.OpenAsync();
        var type = await verification.GetComplexTypeAsync(owner, parentName);
        using var query = db.Command(verification, "SELECT v FROM " + table + " ORDER BY id");
        if (asynchronous) await query.PrepareAsync(); else query.Prepare();
        using var reader = asynchronous ? await query.ExecuteReaderAsync(CommandBehavior.SequentialAccess) : query.ExecuteReader(CommandBehavior.SequentialAccess);
        Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
        var value = Assert.IsType<OceanBaseComplexValue>(reader.GetValue(0)); Assert.Same(type, value.Type);
        var fields = value.GetValues(); Assert.Equal("tag", fields[1]);
        var nested = Assert.IsType<OceanBaseComplexValue>(fields[0]); Assert.Same(type.Members[0].ComplexType, nested.Type);
        var members = nested.GetValues(); DatabaseContext.EqualNumber(members[0], 7, "Nested constructor NUMBER must persist.");
        Assert.Equal("中", members[1]); Assert.Equal(new byte[] { 0, 255 }, members[2]);
        Assert.Equal(new OceanBaseTimestamp(new DateTime(2026, 10, 30, 1, 2, 3), 123456789).ToString(), members[3]!.ToString());
        Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
        fields = Assert.IsType<OceanBaseComplexValue>(reader.GetValue(0)).GetValues(); Assert.Null(fields[0]); Assert.Equal("null child", fields[1]);
        Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
        Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
    }

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
