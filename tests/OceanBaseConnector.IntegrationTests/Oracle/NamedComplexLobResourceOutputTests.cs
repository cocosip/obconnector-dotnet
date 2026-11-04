using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Oracle;

public sealed class NamedComplexLobResourceOutputTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var row in NamedComplexLobBindingTests.PreparedCases())
            foreach (var kind in new[] { "text", "procedure", "function" })
                yield return new object[] { row[0], row[1], row[2], row[3], kind };
    }

    [IntegrationTheory]
    [MemberData(nameof(Cases))]
    public async Task LocatorConstructorsPreserveIndependentResourceOutputs(string profileName, bool character, string shape, bool asynchronous, string kind)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var scalar = character ? "CLOB" : "BLOB";
        var table = await db.TableAsync(connection, "v " + (character ? "VARCHAR2(128)" : "RAW(32)"), CancellationToken.None);
        var typeName = db.NewObject("TYPE", registerCleanup: false);
        string? childName = null;
        if (shape == "nested-collection")
        {
            childName = db.NewObject("TYPE", registerCleanup: false);
            RegisterTypeCleanup(db, childName);
            await db.ExecuteAsync(connection, "CREATE TYPE " + childName + " AS OBJECT(a " + scalar + ", n NUMBER)", CancellationToken.None);
        }
        RegisterTypeCleanup(db, typeName);
        await db.ExecuteAsync(connection, "CREATE TYPE " + typeName + (shape == "object" ? " AS OBJECT(a " + scalar + ")" : " AS TABLE OF " + (childName ?? scalar)), CancellationToken.None);
        var function = kind == "function";
        var routine = db.NewObject("PROCEDURE", registerCleanup: false);
        db.RegisterDropCleanup(function ? "FUNCTION" : "PROCEDURE", routine);
        var member = shape == "object" ? "p.a" : shape == "collection" ? "p(1)" : "p(1).a";
        await db.ExecuteAsync(connection, "CREATE " + (function ? "FUNCTION " : "PROCEDURE ") + routine +
            "(p IN " + typeName + ", f IN NUMBER" + (function ? "" : ", q OUT " + typeName) + ", b OUT " + scalar + ", c OUT SYS_REFCURSOR)" +
            (function ? " RETURN " + typeName : "") + " IS n NUMBER; BEGIN n:=DBMS_LOB.GETLENGTH(" + member + "); " +
            (function ? "" : "q:=p; ") + "b:=" + member + "; OPEN c FOR SELECT n FROM DUAL UNION ALL SELECT n+1 FROM DUAL ORDER BY 1; " +
            "INSERT INTO " + table + " VALUES(DBMS_LOB.SUBSTR(" + member + ",32,1)); " +
            "IF f=1 THEN RAISE_APPLICATION_ERROR(-20001,'Resource output failure'); END IF; " + (function ? "RETURN p; " : "") + "END;", CancellationToken.None);
        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture)!;
        await using var transaction = (OceanBaseTransaction)await connection.BeginTransactionAsync();
        var type = await connection.GetComplexTypeAsync(owner, typeName, transaction);
        await using var input = asynchronous ? await connection.CreateTemporaryLobAsync(character ? OceanBaseDbType.Clob : OceanBaseDbType.Blob, transaction)
            : connection.CreateTemporaryLob(character ? OceanBaseDbType.Clob : OceanBaseDbType.Blob, transaction);
        const string text = "患者-CLOB";
        var bytes = new byte[] { 0, 1, 92, 39, 128, 255 };
        if (character) { if (asynchronous) await input.WriteTextAsync(0, text); else input.WriteText(0, text); }
        else if (asynchronous) await input.WriteBytesAsync(0, bytes, 0, bytes.Length); else input.WriteBytes(0, bytes, 0, bytes.Length);
        var value = shape == "object" ? type.CreateValue(input) : shape == "collection" ? type.CreateValue(input, null)
            : type.CreateValue(type.Members[0].ComplexType!.CreateValue(input, 7), type.Members[0].ComplexType!.CreateValue(null, 8));
        var session = connection.GetOpenSession();
        using (var command = db.Command(connection, kind == "text" ? "BEGIN " + routine + "(:p,:f,:q,:b,:c); END;" : routine, transaction))
        {
            command.CommandType = kind == "text" ? CommandType.Text : CommandType.StoredProcedure;
            DatabaseContext.Parameter(command, "p", value);
            var fail = DatabaseContext.Parameter(command, "f", 1m, DbType.Decimal);
            var complex = DatabaseContext.Parameter(command, "q", "old complex"); complex.ComplexType = type;
            complex.Direction = function ? ParameterDirection.ReturnValue : ParameterDirection.Output;
            var root = DatabaseContext.Parameter(command, "b", "old lob"); root.Direction = ParameterDirection.Output;
            root.OceanBaseDbType = character ? OceanBaseDbType.Clob : OceanBaseDbType.Blob;
            var cursor = DatabaseContext.Parameter(command, "c", "old cursor"); cursor.Direction = ParameterDirection.Output; cursor.OceanBaseDbType = OceanBaseDbType.RefCursor;
            if (asynchronous) await command.PrepareAsync(); else command.Prepare();
            var error = await Assert.ThrowsAsync<OceanBaseException>(async () =>
            {
                if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
            });
            Assert.Equal(20001, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown);
            Assert.Equal("old complex", complex.Value); Assert.Equal("old lob", root.Value); Assert.Equal("old cursor", cursor.Value);
            Assert.Same(session, connection.GetOpenSession());
            fail.Value = 0m;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
                var returned = Assert.IsType<OceanBaseComplexValue>(complex.Value);
                using var returnedLob = Member(returned, shape);
                using var rootLob = Assert.IsType<OceanBaseLob>(root.Value);
                using var refCursor = Assert.IsType<OceanBaseRefCursor>(cursor.Value);
                Assert.NotSame(input, returnedLob); Assert.NotSame(input, rootLob); Assert.NotSame(returnedLob, rootLob);
                if (attempt == 1) command.Dispose();
                using (var reader = asynchronous ? await refCursor.GetDataReaderAsync(1, CommandBehavior.SequentialAccess) : refCursor.GetDataReader(1, CommandBehavior.SequentialAccess))
                {
                    for (var row = 0; row < 2; row++)
                    {
                        Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                        Assert.Equal((character ? text.Length : bytes.Length) + row, DatabaseContext.IntegerResult(reader.GetValue(0)));
                    }
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                }
                await EqualLob(returnedLob, character, text, bytes, asynchronous);
                returnedLob.Dispose();
                await EqualLob(rootLob, character, text, bytes, asynchronous);
                rootLob.Dispose();
                if (attempt == 1) await EqualLob(input, character, text, bytes, asynchronous);
                Assert.Same(session, connection.GetOpenSession());
            }
        }
        await transaction.CommitAsync();
        await using var observer = await db.OpenAsync();
        using var query = db.Command(observer, "SELECT v FROM " + table);
        using var stored = asynchronous ? await query.ExecuteReaderAsync() : query.ExecuteReader();
        for (var row = 0; row < 2; row++)
        {
            Assert.True(asynchronous ? await stored.ReadAsync() : stored.Read());
            if (character) Assert.Equal(text, stored.GetFieldValue<string>(0));
            else Assert.Equal(bytes, stored.GetFieldValue<byte[]>(0));
        }
        Assert.False(asynchronous ? await stored.ReadAsync() : stored.Read());
    }

    private static OceanBaseLob Member(OceanBaseComplexValue value, string shape)
    {
        if (shape == "collection") Assert.Null(value.GetValues()[1]);
        if (shape == "nested-collection")
        {
            var second = Assert.IsType<OceanBaseComplexValue>(value.GetValues()[1]);
            Assert.Null(second.GetValues()[0]);
            Assert.Equal(8, DatabaseContext.IntegerResult(second.GetValues()[1]));
            var first = Assert.IsType<OceanBaseComplexValue>(value.GetValues()[0]);
            Assert.Equal(7, DatabaseContext.IntegerResult(first.GetValues()[1]));
            value = first;
        }
        return Assert.IsType<OceanBaseLob>(value.GetValues()[0]);
    }

    private static async Task EqualLob(OceanBaseLob lob, bool character, string text, byte[] bytes, bool asynchronous)
    {
        Assert.Equal(character, lob.IsCharacter);
        if (character)
        {
            using var reader = lob.OpenTextReader();
            Assert.Equal(text, asynchronous ? await reader.ReadToEndAsync() : reader.ReadToEnd());
        }
        else
        {
            using var source = lob.OpenRead(); using var target = new MemoryStream();
            if (asynchronous) await source.CopyToAsync(target); else source.CopyTo(target);
            Assert.Equal(bytes, target.ToArray());
        }
    }

    private static void RegisterTypeCleanup(DatabaseContext db, string name)
        => db.RegisterCleanup("TYPE", name, async () =>
        {
            await using var connection = await db.OpenAsync(CancellationToken.None);
            await db.ExecuteAsync(connection, "DROP TYPE " + name + " FORCE", CancellationToken.None);
        });
}
