using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Protocol;

public sealed partial class MultiStatementTests
{
    public static IEnumerable<object[]> OracleCollectionScriptRows() =>
        from row in OracleRows()
        from shape in new[] { "number", "national", "object", "collection" }
        select row.Concat(new object[] { shape }).ToArray();

    [IntegrationTheory]
    [MemberData(nameof(OracleRows))]
    public async Task OracleCollectionObjectNullElementsRejectBeforePrefixSideEffects(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var child = db.NewObject("TYPE"); var name = db.NewObject("TYPE");
        await db.ExecuteAsync(connection, "CREATE TYPE " + child + " AS OBJECT(n NUMBER,s NVARCHAR2(40))", CancellationToken.None);
        await db.ExecuteAsync(connection, "CREATE TYPE " + name + " AS TABLE OF " + child, CancellationToken.None);
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY,v RAW(8)", CancellationToken.None);
        var procedure = db.NewObject("PROCEDURE");
        await db.ExecuteAsync(connection, "CREATE PROCEDURE " + procedure + "(p IN " + name + ") IS BEGIN INSERT INTO " + table + " VALUES(2,NULL); END;", CancellationToken.None);
        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture)!;
        var builder = db.Profile.CreateBuilder(); builder.Pooling = false; builder.AllowMultiStatements = true;
        await using var writer = new OceanBaseConnection(builder.ConnectionString); await Open(writer, asynchronous);
        var type = await writer.GetComplexTypeAsync(owner, name);
        using var source = new MemoryStream(new byte[] { 42 });
        using var command = db.Command(writer, "INSERT INTO " + table + " VALUES(1,:s); CALL " + procedure + "(:p)");
        DatabaseContext.Parameter(command, "s", source, DbType.Binary);
        var parameter = DatabaseContext.Parameter(command, "p", type.CreateValue(new object?[] { null }));
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
        Assert.Contains("NULL object identity", error.Message);
        Assert.Equal(0, source.Position);
        Assert.Equal(ConnectionState.Open, writer.State);
        await using var verification = await db.OpenAsync(); Assert.Equal(0, await db.CountAsync(verification, table, CancellationToken.None));
        parameter.Value = type.CreateValue(type.Members[0].ComplexType!.CreateValue(null, null));
        if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
        Assert.Equal(1, source.Position);
        Assert.Equal(2, await db.CountAsync(verification, table, CancellationToken.None));
    }

    [IntegrationTheory]
    [MemberData(nameof(OracleCollectionScriptRows))]
    public async Task OracleNamedCollectionScriptParametersPreserveIndependentStorageAndFailedPrefix(string profileName, bool asynchronous, string shape)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true); await Open(connection, asynchronous);
        var element = shape == "national" ? "NVARCHAR2(40)" : "NUMBER";
        if (shape == "object" || shape == "collection")
        {
            element = db.NewObject("TYPE");
            await db.ExecuteAsync(connection, "CREATE TYPE " + element + (shape == "object" ? " AS OBJECT(n NUMBER,s NVARCHAR2(40))" : " AS TABLE OF NUMBER"), CancellationToken.None);
        }
        var name = db.NewObject("TYPE");
        await db.ExecuteAsync(connection, "CREATE TYPE " + name + " AS TABLE OF " + element, CancellationToken.None);
        var table = await db.TableAsync(connection, "id INTEGER,pos INTEGER,kind VARCHAR2(16),n NUMBER,s NVARCHAR2(40),PRIMARY KEY(id,pos)", CancellationToken.None);
        var procedure = db.NewObject("PROCEDURE");
        var body = shape == "collection"
            ? "BEGIN c:=p(i); INSERT INTO " + table + " VALUES(k,i*100,'inner count',c.COUNT,NULL); FOR j IN 1..c.COUNT LOOP INSERT INTO " + table + " VALUES(k,i*100+j,'value',c(j),NULL); END LOOP; EXCEPTION WHEN COLLECTION_IS_NULL THEN INSERT INTO " + table + " VALUES(k,i*100,'null inner',NULL,NULL); END;"
            : shape == "object" ? "IF p(i) IS NULL THEN INSERT INTO " + table + " VALUES(k,i,'null object',NULL,NULL); ELSE INSERT INTO " + table + " VALUES(k,i,'object',p(i).n,p(i).s); END IF;"
            : "INSERT INTO " + table + " VALUES(k,i,'value'," + (shape == "national" ? "NULL,p(i)" : "p(i),NULL") + ");";
        await db.ExecuteAsync(connection, "CREATE PROCEDURE " + procedure + "(k IN NUMBER,p IN " + name + ") IS " + (shape == "collection" ? "c " + element + "; " : "") +
            "BEGIN INSERT INTO " + table + " VALUES(k,0,'count',p.COUNT,NULL); FOR i IN 1..p.COUNT LOOP " + body +
            " END LOOP; EXCEPTION WHEN COLLECTION_IS_NULL THEN INSERT INTO " + table + " VALUES(k,-1,'null',NULL,NULL); END;", CancellationToken.None);
        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture)!;
        var type = await connection.GetComplexTypeAsync(owner, name);
        Assert.True(type.IsCollection);
        var number = new OceanBaseDecimal(BigInteger.Parse("12345678901234567890123456789012345678", CultureInfo.InvariantCulture), 0);
        var text = "国🙂'); --";
        var nested = type.Members[0].ComplexType;
        var value = shape == "object" ? type.CreateValue(nested!.CreateValue(number, text), nested.CreateValue(null, null), nested.CreateValue(-9, "x "))
            : shape == "collection" ? type.CreateValue(nested!.CreateValue(number, null, -9), null, nested.CreateValue(Array.Empty<object?>()))
            : shape == "national" ? type.CreateValue(text, null, "x ") : type.CreateValue(number, null, -9);
        using var command = db.Command(connection, "CALL " + procedure + "(:id,:p); CALL " + procedure + "(:second,:P)");
        var id = DatabaseContext.Parameter(command, "id", 1); var second = DatabaseContext.Parameter(command, "second", 11);
        var parameter = DatabaseContext.Parameter(command, "p", value); parameter.ComplexType = type;
        var inputs = new object[] { value, DBNull.Value, type.CreateValue(Array.Empty<object?>()) };
        for (var i = 0; i < inputs.Length; i++)
        {
            id.Value = i + 1; second.Value = i + 11; parameter.Value = inputs[i];
            if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
            Assert.Same(inputs[i], parameter.Value);
        }
        command.Parameters.Remove(id); command.Parameters.Remove(second); parameter.Value = value;
        command.CommandText = "CALL " + procedure + "(4,:p); CALL " + procedure + "(1,:P); CALL " + procedure + "(5,:p)";
        await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1, "Collection script writer remains reusable.");
        var expected = new List<(int Id, int Position, string Kind, string? Number, string? Text)>();
        foreach (var k in new[] { 1, 4, 11 })
        {
            expected.Add((k, 0, "count", "3", null));
            if (shape == "collection")
            {
                expected.Add((k, 100, "inner count", "3", null)); expected.Add((k, 101, "value", number.ToString(), null));
                expected.Add((k, 102, "value", null, null)); expected.Add((k, 103, "value", "-9", null));
                expected.Add((k, 200, "null inner", null, null)); expected.Add((k, 300, "inner count", "0", null));
            }
            else
            {
                expected.Add((k, 1, shape == "object" ? "object" : "value", shape == "national" ? null : number.ToString(), shape == "number" ? null : text));
                expected.Add((k, 2, shape == "object" ? "object" : "value", null, null));
                expected.Add((k, 3, shape == "object" ? "object" : "value", shape == "national" ? null : "-9", shape == "number" ? null : "x "));
            }
        }
        foreach (var k in new[] { 2, 12 }) expected.Add((k, -1, "null", null, null));
        foreach (var k in new[] { 3, 13 }) expected.Add((k, 0, "count", "0", null));
        await using var verification = await db.OpenAsync();
        foreach (var prepared in new[] { false, true })
        {
            using var query = db.Command(verification, "SELECT id,pos,kind,n,s FROM " + table + " ORDER BY id,pos");
            if (prepared) { if (asynchronous) await query.PrepareAsync(); else query.Prepare(); }
            using var reader = asynchronous ? await query.ExecuteReaderAsync(CommandBehavior.SequentialAccess) : query.ExecuteReader(CommandBehavior.SequentialAccess);
            foreach (var row in expected.OrderBy(row => row.Id).ThenBy(row => row.Position))
            {
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                DatabaseContext.EqualNumber(reader.GetValue(0), row.Id, "Collection script must preserve the successful prefix and reject its suffix.");
                DatabaseContext.EqualNumber(reader.GetValue(1), row.Position, "Collection element order must persist.");
                Assert.Equal(row.Kind, reader.GetString(2));
                if (row.Number == null) Assert.True(reader.IsDBNull(3)); else Assert.Equal(row.Number, reader.GetFieldValue<OceanBaseDecimal>(3).ToString());
                if (row.Text == null) Assert.True(reader.IsDBNull(4)); else Assert.Equal(row.Text, reader.GetString(4));
            }
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        }
    }

    [IntegrationTheory]
    [MemberData(nameof(OracleRows))]
    public async Task OracleNamedCollectionConstructorsInCallScriptsPreserveIndependentStorage(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true); await Open(connection, asynchronous);
        var name = db.NewObject("TYPE");
        await db.ExecuteAsync(connection, "CREATE TYPE " + name + " AS TABLE OF NUMBER", CancellationToken.None);
        var table = await db.TableAsync(connection, "id INTEGER, pos INTEGER, n NUMBER", CancellationToken.None);
        var procedure = db.NewObject("PROCEDURE");
        await db.ExecuteAsync(connection, "CREATE PROCEDURE " + procedure + "(k IN NUMBER, p IN " + name + ") IS BEGIN " +
            "INSERT INTO " + table + " VALUES(k,0,p.COUNT); FOR i IN 1..p.COUNT LOOP INSERT INTO " + table + " VALUES(k,i,p(i)); END LOOP; " +
            "EXCEPTION WHEN COLLECTION_IS_NULL THEN INSERT INTO " + table + " VALUES(k,-1,NULL); END;", CancellationToken.None);
        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture)!;
        var constructor = "\"" + owner.Replace("\"", "\"\"") + "\".\"" + name + "\"";
        using (var call = db.Command(connection, "CALL " + procedure + "(1," + constructor + "(7,NULL,-9)); CALL " + procedure + "(2," + constructor + "()); CALL " + procedure + "(3,NULL)"))
        {
            if (asynchronous) await call.ExecuteNonQueryAsync(); else call.ExecuteNonQuery();
        }
        await using var verification = await db.OpenAsync();
        using var query = db.Command(verification, "SELECT id,pos,n FROM " + table + " ORDER BY id,pos");
        if (asynchronous) await query.PrepareAsync(); else query.Prepare();
        using var reader = asynchronous ? await query.ExecuteReaderAsync(CommandBehavior.SequentialAccess) : query.ExecuteReader(CommandBehavior.SequentialAccess);
        var expected = new (int Id, int Position, int? Number)[] { (1, 0, 3), (1, 1, 7), (1, 2, null), (1, 3, -9), (2, 0, 0), (3, -1, null) };
        foreach (var row in expected)
        {
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            DatabaseContext.EqualNumber(reader.GetValue(0), row.Id, "Collection call ID must persist.");
            DatabaseContext.EqualNumber(reader.GetValue(1), row.Position, "Collection order and NULL/empty shape must persist.");
            if (row.Number.HasValue) DatabaseContext.EqualNumber(reader.GetValue(2), row.Number.Value, "Collection member must persist.");
            else Assert.True(reader.IsDBNull(2));
        }
        Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
        Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
    }
}
