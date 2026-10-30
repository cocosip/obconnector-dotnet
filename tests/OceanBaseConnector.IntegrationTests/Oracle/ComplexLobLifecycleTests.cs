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

namespace OceanBaseConnector.IntegrationTests.Oracle;

public sealed class ComplexLobLifecycleTests
{
    public static IEnumerable<object[]> NamedCases()
    {
        foreach (var row in ComplexLobInputTests.Cases())
            if ((string)row[2] != "array") yield return row;
        foreach (var row in ComplexLobInputTests.Cases())
            if ((string)row[2] == "object") yield return new object[] { row[0], row[1], "nested", row[3] };
    }

    [IntegrationTheory]
    [MemberData(nameof(NamedCases))]
    public async Task NamedOutputLobsPreserveCollectionNullAndExposeObjectNullBoundary(string profileName, bool character, string shape, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var builder = db.Profile.CreateBuilder(); builder.Pooling = false;
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        if (asynchronous) await connection.OpenAsync(); else connection.Open();
        var scalar = character ? "CLOB" : "BLOB";
        var childName = db.NewObject("TYPE");
        await db.ExecuteAsync(connection, "CREATE TYPE " + childName + (shape == "collection" ? " AS TABLE OF " + scalar : " AS OBJECT(a " + scalar + ")"), CancellationToken.None);
        var name = childName;
        if (shape == "nested")
        {
            name = db.NewObject("TYPE");
            await db.ExecuteAsync(connection, "CREATE TYPE " + name + " AS OBJECT(a " + childName + ")", CancellationToken.None);
        }
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY,v " + scalar + ",b RAW(4)", CancellationToken.None);
        var procedure = db.NewObject("PROCEDURE");
        await db.ExecuteAsync(connection, "CREATE PROCEDURE " + procedure + "(p IN " + name + ",q OUT " + name + ",r IN OUT " + name + ",b IN RAW) IS BEGIN " +
            "IF b<>HEXTORAW('2A') THEN RAISE_APPLICATION_ERROR(-20001,'Input mismatch'); END IF; " +
            "IF p IS NULL AND r IS NOT NULL THEN RAISE_APPLICATION_ERROR(-20002,'INOUT NULL mismatch'); END IF; q:=r; r:=p; END;", CancellationToken.None);
        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture)!;
        var type = await connection.GetComplexTypeAsync(owner, name);
        var session = connection.GetOpenSession();
        object payload = character ? new string('x', 90000) + "患者😀\\'" : Enumerable.Range(0, 90007).Select(i => (byte)(i * 7)).ToArray();
        object previous = character ? "旧😀" : new byte[] { 0, 128, 255 };
        using var source = new MemoryStream(new byte[] { 42 });
        OceanBaseComplexValue returned; OceanBaseComplexValue changed;
        using (var command = db.Command(connection, procedure))
        {
            command.CommandType = CommandType.StoredProcedure;
            var input = DatabaseContext.Parameter(command, "p", Value(type, shape, payload));
            input.ComplexType = type;
            var output = DatabaseContext.Parameter(command, "q", "unpublished"); output.Direction = ParameterDirection.Output; output.ComplexType = type;
            var inout = DatabaseContext.Parameter(command, "r", Value(type, shape, previous)); inout.Direction = ParameterDirection.InputOutput; inout.ComplexType = type;
            DatabaseContext.Parameter(command, "b", source, DbType.Binary);
            if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
            Assert.Equal(1, source.Position);
            returned = Assert.IsType<OceanBaseComplexValue>(output.Value); changed = Assert.IsType<OceanBaseComplexValue>(inout.Value);
            foreach (var nullRoot in new[] { false, true })
            {
                input.Value = nullRoot ? DBNull.Value : Value(type, shape, null);
                inout.Value = nullRoot ? DBNull.Value : Value(type, shape, null);
                source.Position = 0;
                if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
                Assert.Equal(1, source.Position);
                foreach (var actual in new[] { output.Value, inout.Value })
                    if (nullRoot && shape == "collection") Assert.IsType<DBNull>(actual);
                    // Current server output normalizes NULL OBJECT roots, including nested objects.
                    else Assert.Null(MemberValue(Assert.IsType<OceanBaseComplexValue>(actual), type, shape));
            }
        }
        using var oldLob = Member(returned, type, shape);
        using var newLob = Member(changed, type, shape);
        Assert.NotSame(oldLob, newLob);
        Assert.Throws<NotSupportedException>(() => Value(type, shape, newLob));
        using (var insert = db.Command(connection, "INSERT INTO " + table + " VALUES(1,:v,:b)"))
        {
            DatabaseContext.Parameter(insert, "v", newLob);
            DatabaseContext.Parameter(insert, "b", new byte[] { 42 }, DbType.Binary);
            Assert.Equal(1, asynchronous ? await insert.ExecuteNonQueryAsync() : insert.ExecuteNonQuery());
        }
        await EqualLob(oldLob, previous, asynchronous);
        await EqualLob(newLob, payload, asynchronous);
        Assert.Same(session, connection.GetOpenSession());
        await using var observer = await db.OpenAsync();
        using (var query = db.Command(observer, "SELECT v,b FROM " + table + " WHERE id=1"))
        {
            if (asynchronous) await query.PrepareAsync(); else query.Prepare();
            using var reader = asynchronous ? await query.ExecuteReaderAsync(CommandBehavior.SequentialAccess) : query.ExecuteReader(CommandBehavior.SequentialAccess);
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            using var stored = reader.GetFieldValue<OceanBaseLob>(0);
            Assert.Equal(new byte[] { 42 }, reader.GetFieldValue<byte[]>(1));
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
            await EqualLob(stored, payload, asynchronous);
        }
        Assert.Equal(1, await db.CountAsync(observer, table, CancellationToken.None));
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1, "The writing session must remain usable.");
    }

    private static OceanBaseComplexValue Value(OceanBaseComplexType type, string shape, object? payload)
        => shape == "nested" ? type.CreateValue(type.Members[0].ComplexType!.CreateValue(payload))
            : shape == "collection" ? type.CreateValue(payload, null) : type.CreateValue(payload);

    public static IEnumerable<object[]> ArrayCases()
    {
        foreach (var row in ComplexLobInputTests.Cases())
            if ((string)row[2] == "array")
                foreach (var ending in new[] { "dispose", "commit", "rollback" })
                    yield return new object[] { row[0], row[1], row[3], ending };
    }

    [IntegrationTheory]
    [MemberData(nameof(ArrayCases))]
    public async Task AnonymousArraysRejectExpiredLobsAndRecoverOnTheSameSession(string profileName, bool character, bool asynchronous, string ending)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var builder = db.Profile.CreateBuilder(); builder.Pooling = false;
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        if (asynchronous) await connection.OpenAsync(); else connection.Open();
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY,v " + (character ? "CLOB" : "BLOB"), CancellationToken.None);
        await db.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES(0," + (character ? "'abc'" : "TO_BLOB(HEXTORAW('616263'))") + ")", CancellationToken.None);
        await using var observer = await db.OpenAsync();
        var session = connection.GetOpenSession();
        await using var transaction = (OceanBaseTransaction)await connection.BeginTransactionAsync();
        using var original = await StoredLob(db, connection, table, transaction, asynchronous);
        var ids = connection.CreateArrayType("NUMBER", transaction);
        var lobs = connection.CreateArrayType(character ? "CLOB" : "BLOB", transaction);
        using var command = db.Command(connection, "INSERT INTO " + table + " VALUES(:k,:v)", transaction);
        var id = DatabaseContext.Parameter(command, "k", ids.CreateValue(1, 2));
        var input = DatabaseContext.Parameter(command, "v", lobs.CreateValue(original, null));
        if (ending == "dispose") original.Dispose();
        else
        {
            if (ending == "commit") { if (asynchronous) await transaction.CommitAsync(); else transaction.Commit(); }
            else { if (asynchronous) await transaction.RollbackAsync(); else transaction.Rollback(); }
        }
        await using var later = ending == "dispose" ? null : (OceanBaseTransaction)await connection.BeginTransactionAsync();
        var active = later ?? transaction; command.Transaction = active;
        await Assert.ThrowsAnyAsync<InvalidOperationException>(async () =>
        {
            if (asynchronous) await command.ExecuteArrayAsync(2); else command.ExecuteArray(2);
        });
        Assert.False(command.HasSentSql);
        Assert.Equal(1, await db.CountAsync(observer, table, CancellationToken.None));
        Assert.Same(session, connection.GetOpenSession());
        ids = connection.CreateArrayType("NUMBER", active); lobs = connection.CreateArrayType(character ? "CLOB" : "BLOB", active);
        Assert.ThrowsAny<InvalidOperationException>(() => lobs.CreateValue(original, null));
        using (var fresh = await StoredLob(db, connection, table, active, asynchronous))
        {
            id.Value = ids.CreateValue(1, 2); input.Value = lobs.CreateValue(fresh, null);
            var result = asynchronous ? await command.ExecuteArrayAsync(2) : command.ExecuteArray(2);
            Assert.True(result.IsComplete); Assert.Equal(2, result.RecordsAffected);
        }
        if (asynchronous) await active.CommitAsync(); else active.Commit();
        Assert.Same(session, connection.GetOpenSession());
        using var query = db.Command(observer, "SELECT id,v FROM " + table + " ORDER BY id");
        if (asynchronous) await query.PrepareAsync(); else query.Prepare();
        var stored = new List<OceanBaseLob>();
        try
        {
            using (var reader = asynchronous ? await query.ExecuteReaderAsync(CommandBehavior.SequentialAccess) : query.ExecuteReader(CommandBehavior.SequentialAccess))
            {
                for (var i = 0; i < 3; i++)
                {
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    DatabaseContext.EqualNumber(reader.GetValue(0), i, "Array storage row order mismatch.");
                    if (i == 2) Assert.True(reader.IsDBNull(1)); else stored.Add(reader.GetFieldValue<OceanBaseLob>(1));
                }
                Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
            }
            foreach (var lob in stored) await EqualLob(lob, character ? "abc" : new byte[] { 97, 98, 99 }, asynchronous);
        }
        finally { foreach (var lob in stored) lob.Dispose(); }
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1, "The array session must remain usable.");
    }

    private static async Task<OceanBaseLob> StoredLob(DatabaseContext db, OceanBaseConnection connection, string table, OceanBaseTransaction? transaction, bool asynchronous)
    {
        using var query = db.Command(connection, "SELECT v FROM " + table + " WHERE id=0", transaction);
        using var reader = asynchronous ? await query.ExecuteReaderAsync() : query.ExecuteReader();
        Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
        var result = reader.GetFieldValue<OceanBaseLob>(0);
        try
        {
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static OceanBaseLob Member(OceanBaseComplexValue value, OceanBaseComplexType type, string shape)
        => Assert.IsType<OceanBaseLob>(MemberValue(value, type, shape));

    private static object? MemberValue(OceanBaseComplexValue value, OceanBaseComplexType type, string shape)
    {
        Assert.Same(type, value.Type);
        var values = value.GetValues();
        if (shape == "collection") { Assert.Equal(2, values.Length); Assert.Null(values[1]); }
        if (shape == "nested")
        {
            var child = Assert.IsType<OceanBaseComplexValue>(values[0]);
            Assert.Same(type.Members[0].ComplexType, child.Type); values = child.GetValues();
        }
        return values[0];
    }

    private static async Task EqualLob(OceanBaseLob actual, object expected, bool asynchronous)
    {
        Assert.Equal(expected is string, actual.IsCharacter);
        if (expected is string text)
        {
            using var reader = actual.OpenTextReader();
            Assert.Equal(text, asynchronous ? await reader.ReadToEndAsync() : reader.ReadToEnd());
        }
        else
        {
            using var source = actual.OpenRead(); using var destination = new MemoryStream();
            if (asynchronous) await source.CopyToAsync(destination); else source.CopyTo(destination);
            Assert.Equal((byte[])expected, destination.ToArray());
        }
    }
}
