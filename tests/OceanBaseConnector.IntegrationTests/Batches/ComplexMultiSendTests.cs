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

public sealed class ComplexMultiSendTests
{
    public static IEnumerable<object[]> Rows() =>
        from profile in IntegrationEnvironment.Profiles.Where(p => p.Oracle && p.CreateObjects && p.HasTag("direct"))
        from asynchronous in new[] { false, true }
        from collection in new[] { false, true }
        select new object[] { profile.Name, asynchronous, collection };

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task NamedComplexMultiSendReadsStoredResults(string profileName, bool asynchronous, bool nested)
    {
        var profile = IntegrationEnvironment.GetProfile(profileName);
        var builder = profile.CreateBuilder(); builder.UseBatchMultiSend = true; builder.BatchMultiSendNumber = 2;
        builder.UseLobLocators = false;
        await using var db = new DatabaseContext(new IntegrationProfile
        {
            Name = profile.Name, Mode = profile.Mode, ConnectionString = builder.ConnectionString, CreateObjects = true,
        });
        await using var connection = await db.OpenAsync();
        var leaf = db.NewObject("TYPE");
        await db.ExecuteAsync(connection, "CREATE TYPE " + leaf + " AS OBJECT(n NUMBER,s NVARCHAR2(40))", CancellationToken.None);
        var name = leaf;
        if (nested)
        {
            name = db.NewObject("TYPE");
            await db.ExecuteAsync(connection, "CREATE TYPE " + name + " AS OBJECT(v " + leaf + ",r RAW(8))", CancellationToken.None);
        }
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY,value " + name, CancellationToken.None);
        string first = leaf + "(7,N'国🙂 ')", last = leaf + "(-9,N'last')", empty = leaf + "(NULL,NULL)";
        if (nested)
        {
            first = name + "(" + first + ",HEXTORAW('00FF'))";
            last = name + "(" + last + ",HEXTORAW('00FF'))";
            empty = name + "(" + empty + ",NULL)";
        }
        foreach (var (id, value) in new[] { (1, first), (2, empty), (3, last) })
            await db.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES(" + id + "," + value + ")", CancellationToken.None);
        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture)!;
        var type = await connection.GetComplexTypeAsync(owner, name);
        var zone = Convert.ToString(await db.ScalarAsync(connection, "SELECT SESSIONTIMEZONE FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture);
        await using var batch = connection.CreateBatch();
        for (var id = 1; id <= 3; id++)
        {
            var item = batch.CreateBatchCommand();
            item.CommandText = "SELECT value," + (nested ? "t.value.v.s" : "t.value.s") +
                ",CAST(TIMESTAMP '2026-11-02 08:09:10.123456789' AS TIMESTAMP WITH LOCAL TIME ZONE) FROM " + table + " t WHERE id=:id";
            item.Parameters.Add(new OceanBaseParameter { ParameterName = "id", DbType = DbType.Int32, Value = id });
            batch.BatchCommands.Add(item);
        }
        OceanBaseComplexValue? retained = null;
        using (var reader = asynchronous ? await batch.ExecuteReaderAsync() : batch.ExecuteReader())
        {
            for (var i = 0; i < 3; i++)
            {
                Assert.Equal(typeof(OceanBaseComplexValue), reader.GetFieldType(0)); Assert.Equal(name, reader.GetDataTypeName(0));
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                var value = reader.GetFieldValue<OceanBaseComplexValue>(0);
                Assert.Same(type, value.Type); var members = value.GetValues();
                if (nested)
                {
                    if (i == 1) Assert.Null(members[1]); else Assert.Equal(new byte[] { 0, 255 }, members[1]);
                    members = Assert.IsType<OceanBaseComplexValue>(members[0]).GetValues();
                }
                if (i == 1) { Assert.Null(members[0]); Assert.Null(members[1]); }
                else
                {
                    DatabaseContext.EqualNumber(members[0], i == 0 ? 7 : -9, "Stored complex member mismatch.");
                    Assert.Equal(i == 0 ? "国🙂 " : "last", members[1]);
                }
                if (i == 1) Assert.True(reader.IsDBNull(1)); else Assert.Equal(i == 0 ? "国🙂 " : "last", reader.GetString(1));
                Assert.Equal(zone, reader.GetFieldValue<OceanBaseTimestampLocalTimeZone>(2).SessionTimeZone);
                if (i == 0) retained = value;
                Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                Assert.Equal(i != 2, asynchronous ? await reader.NextResultAsync() : reader.NextResult());
            }
        }
        var scalar = Assert.IsType<OceanBaseComplexValue>(asynchronous ? await batch.ExecuteScalarAsync() : batch.ExecuteScalar());
        Assert.Same(type, scalar.Type);
        Assert.Equal(-1, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
        connection.ClearComplexTypeCache(); Assert.NotEmpty(retained!.GetValues());
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1, "Reader must release the writer.");
        await using var observer = await db.OpenAsync(); Assert.Equal(3, await db.CountAsync(observer, table, CancellationToken.None));
    }

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task NamedComplexMultiSendPreservesIndependentStorage(string profileName, bool asynchronous, bool collection)
    {
        var profile = IntegrationEnvironment.GetProfile(profileName);
        var builder = profile.CreateBuilder();
        builder.UseBatchMultiSend = true;
        builder.BatchMultiSendNumber = 2;
        builder.UseLobLocators = false;
        await using var db = new DatabaseContext(new IntegrationProfile
        {
            Name = profile.Name, Mode = profile.Mode, ConnectionString = builder.ConnectionString, CreateObjects = true,
        });
        await using var connection = await db.OpenAsync();
        var name = db.NewObject("TYPE");
        await db.ExecuteAsync(connection, "CREATE TYPE " + name + (collection
            ? " AS TABLE OF NVARCHAR2(40)" : " AS OBJECT(n NUMBER,s NVARCHAR2(40))"), CancellationToken.None);
        var table = await db.TableAsync(connection, "id INTEGER" + (collection ? "" : " PRIMARY KEY") +
            ", value " + (collection ? "NVARCHAR2(40)" : name), CancellationToken.None);
        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture)!;
        var type = await connection.GetComplexTypeAsync(owner, name);
        const string text = "国🙂'); --";
        var values = collection
            ? new object[] { type.CreateValue(text, null, "tail"), DBNull.Value, type.CreateValue(Array.Empty<object?>()), type.CreateValue("last") }
            : new object[] { type.CreateValue(7, text), DBNull.Value, type.CreateValue(null, null), type.CreateValue(-9, "last") };
        await using (var batch = connection.CreateBatch())
        {
            for (var i = 0; i < values.Length; i++)
            {
                var item = batch.CreateBatchCommand();
                item.CommandText = collection
                    ? "INSERT INTO " + table + " SELECT :id,c.COLUMN_VALUE FROM TABLE(CAST(:p AS " + name + ")) c"
                    : "INSERT INTO " + table + " VALUES(:id,:p)";
                item.Parameters.Add(new OceanBaseParameter { ParameterName = "id", DbType = DbType.Int32, Value = i + 1 });
                item.Parameters.Add(new OceanBaseParameter { ParameterName = "p", ComplexType = type, Value = values[i] });
                batch.BatchCommands.Add(item);
            }
            Assert.Equal(4, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
            Assert.Equal(collection ? new[] { 3, 0, 0, 1 } : new[] { 1, 1, 1, 1 },
                batch.BatchCommands.Cast<OceanBaseBatchCommand>().Select(c => c.RecordsAffected));
        }
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1, "Writer must remain reusable.");

        await using var observer = await db.OpenAsync();
        if (collection)
        {
            Assert.Equal(4, await db.CountAsync(observer, table, CancellationToken.None));
            foreach (var (id, expected) in new[] { (1, text), (1, "tail"), (4, "last") })
            {
                using var query = db.Command(observer, "SELECT COUNT(*) FROM " + table + " WHERE id=:id AND value=:s");
                DatabaseContext.Parameter(query, "id", id);
                DatabaseContext.Parameter(query, "s", expected, DbType.String);
                DatabaseContext.EqualNumber(await query.ExecuteScalarAsync(), 1, "Each collection element must retain its input identity.");
            }
            DatabaseContext.EqualNumber(await db.ScalarAsync(observer,
                "SELECT COUNT(*) FROM " + table + " WHERE id=1 AND value IS NULL", CancellationToken.None), 1, "NULL element must survive.");
        }
        else
        {
            using var query = db.Command(observer, "SELECT id,CASE WHEN t.value IS NULL THEN 1 ELSE 0 END,t.value.n,t.value.s FROM " + table + " t ORDER BY id");
            using var reader = await query.ExecuteReaderAsync();
            for (var i = 0; i < 4; i++)
            {
                Assert.True(await reader.ReadAsync());
                DatabaseContext.EqualNumber(reader.GetValue(0), i + 1, "Object row identity mismatch.");
                DatabaseContext.EqualNumber(reader.GetValue(1), i == 1 ? 1 : 0, "Root NULL must differ from an all-NULL object.");
                if (i == 1 || i == 2) { Assert.True(reader.IsDBNull(2)); Assert.True(reader.IsDBNull(3)); }
                else
                {
                    DatabaseContext.EqualNumber(reader.GetValue(2), i == 0 ? 7 : -9, "Object numeric member mismatch.");
                    Assert.Equal(i == 0 ? text : "last", reader.GetString(3));
                }
            }
            Assert.False(await reader.ReadAsync());
        }
    }
}
