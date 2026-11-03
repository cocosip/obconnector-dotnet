using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
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

    public static IEnumerable<object[]> LocatorRows() =>
        from profile in IntegrationEnvironment.Profiles.Where(p => p.Oracle && p.CreateObjects && p.HasTag("direct"))
        from asynchronous in new[] { false, true }
        from shape in new[] { "object", "collection", "nested-collection" }
        from character in new[] { false, true }
        select new object[] { profile.Name, asynchronous, shape, character };

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task NamedComplexLobResultsRetainCallerOwnedHandles(string profileName, bool asynchronous, bool nested, bool character)
    {
        var profile = IntegrationEnvironment.GetProfile(profileName);
        var builder = profile.CreateBuilder(); builder.UseOraclePrepareExecute = true; builder.UseLobLocators = true;
        builder.UseBatchMultiSend = true; builder.BatchMultiSendNumber = 2;
        await using var db = new DatabaseContext(new IntegrationProfile
        {
            Name = profile.Name, Mode = profile.Mode, ConnectionString = builder.ConnectionString, CreateObjects = true,
        });
        await using var connection = await db.OpenAsync();
        var leaf = db.NewObject("TYPE");
        var scalar = character ? "CLOB" : "BLOB";
        await db.ExecuteAsync(connection, "CREATE TYPE " + leaf + " AS OBJECT(a " + scalar + ",n NUMBER)", CancellationToken.None);
        var name = leaf;
        if (nested)
        {
            name = db.NewObject("TYPE");
            await db.ExecuteAsync(connection, "CREATE TYPE " + name + " AS OBJECT(v " + leaf + ",b " + scalar + ")", CancellationToken.None);
        }
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY,value " + name, CancellationToken.None);
        string first = character ? "TO_CLOB('患者🙂')" : "TO_BLOB(HEXTORAW('0080FF'))";
        string last = character ? "TO_CLOB('last')" : "TO_BLOB(HEXTORAW('2A'))";
        string other = character ? "TO_CLOB('other')" : "TO_BLOB(HEXTORAW('FF00'))";
        var values = new[] { leaf + "(" + first + ",7)", leaf + "(NULL,7)", leaf + "(" + last + ",7)" };
        for (var i = 0; i < 3; i++)
        {
            var value = nested ? name + "(" + values[i] + "," + (i == 1 ? "NULL" : other) + ")" : values[i];
            await db.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES(" + (i + 1) + "," + value + ")", CancellationToken.None);
        }
        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture)!;
        var type = await connection.GetComplexTypeAsync(owner, name);
        using var batch = connection.CreateBatch();
        for (var i = 1; i <= 3; i++)
        {
            var item = batch.CreateBatchCommand(); item.CommandText = "SELECT value FROM " + table + " WHERE id=:id";
            item.Parameters.Add(new OceanBaseParameter { ParameterName = "id", DbType = DbType.Int32, Value = i });
            batch.BatchCommands.Add(item);
        }
        var handles = new List<(OceanBaseLob Lob, string Expected)>();
        void Retain(OceanBaseComplexValue value, int row)
        {
            Assert.Same(type, value.Type); var members = value.GetValues();
            if (nested)
            {
                if (row == 1) Assert.Null(members[1]);
                else handles.Add((Assert.IsType<OceanBaseLob>(members[1]), character ? "other" : "FF00"));
                members = Assert.IsType<OceanBaseComplexValue>(members[0]).GetValues();
            }
            DatabaseContext.EqualNumber(members[1], 7, "Ordinary members must retain their value.");
            if (row == 1) Assert.Null(members[0]);
            else handles.Add((Assert.IsType<OceanBaseLob>(members[0]), character ? row == 0 ? "患者🙂" : "last" : row == 0 ? "0080FF" : "2A"));
        }
        try
        {
            using (var reader = asynchronous ? await batch.ExecuteReaderAsync() : batch.ExecuteReader())
            {
                for (var i = 0; i < 3; i++)
                {
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    Retain(reader.GetFieldValue<OceanBaseComplexValue>(0), i);
                    if (i == 0)
                        foreach (var (lob, _) in handles)
                            Assert.Throws<InvalidOperationException>(() => { if (character) lob.OpenTextReader(); else lob.OpenRead(); });
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                    Assert.Equal(i < 2, asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                }
            }
            Retain(Assert.IsType<OceanBaseComplexValue>(asynchronous ? await batch.ExecuteScalarAsync() : batch.ExecuteScalar()), 0);
            Assert.Equal(-1, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
            batch.Dispose(); connection.ClearComplexTypeCache();
            foreach (var (lob, expected) in handles)
            {
                if (character) { using var source = lob.OpenTextReader(); Assert.Equal(expected, await source.ReadToEndAsync()); }
                else
                {
                    using var source = lob.OpenRead(); using var target = new MemoryStream();
                    await source.CopyToAsync(target); Assert.Equal(Convert.FromHexString(expected), target.ToArray());
                }
            }
            DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1, "Writer must remain reusable.");
            await using var observer = await db.OpenAsync(); Assert.Equal(3, await db.CountAsync(observer, table, CancellationToken.None));
        }
        finally { foreach (var (lob, _) in handles) lob.Dispose(); }
    }

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

    [IntegrationTheory]
    [MemberData(nameof(LocatorRows))]
    public async Task NamedLobLocatorsReachScalarStorageThroughMultiSend(string profileName, bool asynchronous, string shape, bool character)
    {
        var profile = IntegrationEnvironment.GetProfile(profileName);
        var builder = profile.CreateBuilder(); builder.UseOraclePrepareExecute = true; builder.UseLobLocators = true;
        builder.UseBatchMultiSend = true; builder.BatchMultiSendNumber = 2;
        await using var db = new DatabaseContext(new IntegrationProfile
        {
            Name = profile.Name, Mode = profile.Mode, ConnectionString = builder.ConnectionString, CreateObjects = true,
        });
        await using var connection = await db.OpenAsync();
        var scalar = character ? "CLOB" : "BLOB";
        var source = await db.TableAsync(connection, "id NUMBER PRIMARY KEY,value " + scalar, CancellationToken.None);
        await db.ExecuteAsync(connection, "INSERT INTO " + source + " VALUES(1," +
            (character ? "TO_CLOB('患者🙂')" : "TO_BLOB(HEXTORAW('0080FF'))") + ")", CancellationToken.None);
        string? leafName = null;
        string typeName;
        if (shape == "nested-collection")
        {
            leafName = db.NewObject("TYPE");
            typeName = db.NewObject("TYPE");
            await db.ExecuteAsync(connection, "CREATE TYPE " + leafName + " AS OBJECT(a " + scalar + ")", CancellationToken.None);
            await db.ExecuteAsync(connection, "CREATE TYPE " + typeName + " AS TABLE OF " + leafName, CancellationToken.None);
        }
        else
        {
            typeName = db.NewObject("TYPE");
            await db.ExecuteAsync(connection, "CREATE TYPE " + typeName + (shape == "collection" ? " AS TABLE OF " + scalar : " AS OBJECT(a " + scalar + ")"), CancellationToken.None);
        }
        var target = await db.TableAsync(connection, "id NUMBER PRIMARY KEY,value VARCHAR2(200)", CancellationToken.None);
        var function = db.NewObject("PROCEDURE", registerCleanup: false); db.RegisterDropCleanup("FUNCTION", function);
        var member = shape == "object" ? "p.a" : shape == "collection" ? "p(1)" : "p(1).a";
        var expression = character ? "DBMS_LOB.SUBSTR(" + member + ",32,1)" : "RAWTOHEX(DBMS_LOB.SUBSTR(" + member + ",32,1))";
        await db.ExecuteAsync(connection, "CREATE FUNCTION " + function + "(p IN " + typeName + ") RETURN VARCHAR2 IS BEGIN " +
            "IF p IS NULL THEN RETURN NULL; END IF; RETURN " + expression + "; END;", CancellationToken.None);
        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture)!;
        var type = await connection.GetComplexTypeAsync(owner, typeName);
        using var sourceCommand = db.Command(connection, "SELECT value FROM " + source);
        using var lob = Assert.IsType<OceanBaseLob>(asynchronous ? await sourceCommand.ExecuteScalarAsync() : sourceCommand.ExecuteScalar());
        var value = shape == "object" ? type.CreateValue(lob)
            : shape == "collection" ? type.CreateValue(lob, null)
            : type.CreateValue(type.Members[0].ComplexType!.CreateValue(lob));
        using (var batch = connection.CreateBatch())
        {
            for (var i = 1; i <= 3; i++)
            {
                var item = batch.CreateBatchCommand(); item.CommandText = "INSERT INTO " + target + " SELECT :id," + function + "(:p) FROM DUAL";
                item.Parameters.Add(new OceanBaseParameter { ParameterName = "id", DbType = DbType.Int32, Value = i });
                item.Parameters.Add(new OceanBaseParameter { ParameterName = "p", ComplexType = type, Value = value });
                batch.BatchCommands.Add(item);
            }
            Assert.Equal(3, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
            Assert.All(batch.BatchCommands.Cast<OceanBaseBatchCommand>(), item => Assert.Equal(1, item.RecordsAffected));
        }
        var expected = character ? "患者🙂" : "0080FF";
        await using var observer = await db.OpenAsync();
        using var query = db.Command(observer, "SELECT id,value FROM " + target + " ORDER BY id");
        using var reader = await query.ExecuteReaderAsync();
        for (var i = 1; i <= 3; i++)
        {
            Assert.True(await reader.ReadAsync()); DatabaseContext.EqualNumber(reader.GetValue(0), i, "Input order must survive.");
            Assert.Equal(expected, reader.GetString(1));
        }
        Assert.False(await reader.ReadAsync());
    }
}
