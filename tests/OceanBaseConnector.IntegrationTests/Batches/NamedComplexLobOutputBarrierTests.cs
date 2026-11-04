using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using OceanBaseConnector.IntegrationTests.Oracle;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Batches;

public sealed class NamedComplexLobOutputBarrierTests
{
    public static IEnumerable<object[]> Cases() => NamedComplexLobResourceOutputTests.Cases();

    [IntegrationTheory]
    [MemberData(nameof(Cases))]
    public async Task LocatorInputsKeepIndependentOutputsAndKnownBatchPrefix(string profileName, bool character, string shape, bool asynchronous, string kind)
    {
        var profile = IntegrationEnvironment.GetProfile(profileName);
        var builder = profile.CreateBuilder();
        builder.Pooling = false; builder.UseOraclePrepareExecute = true; builder.UseLobLocators = true;
        builder.UseBatchMultiSend = true; builder.BatchMultiSendNumber = 2;
        await using var db = new DatabaseContext(new IntegrationProfile
        {
            Name = profile.Name,
            Mode = profile.Mode,
            ConnectionString = builder.ConnectionString,
            CreateObjects = true,
        });
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY,n INTEGER", CancellationToken.None);
        for (var id = 1; id <= 5; id++)
            await db.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES(" + id + ",0)", CancellationToken.None);
        var scalar = character ? "CLOB" : "BLOB";
        string? child = null;
        if (shape == "nested-collection")
        {
            child = db.NewObject("TYPE", registerCleanup: false); RegisterTypeCleanup(db, child);
            await db.ExecuteAsync(connection, "CREATE TYPE " + child + " AS OBJECT(a " + scalar + ",n NUMBER)", CancellationToken.None);
        }
        var name = db.NewObject("TYPE", registerCleanup: false); RegisterTypeCleanup(db, name);
        await db.ExecuteAsync(connection, "CREATE TYPE " + name + (shape == "object" ? " AS OBJECT(a " + scalar + ")" : " AS TABLE OF " + (child ?? scalar)), CancellationToken.None);
        var function = kind == "function";
        var routineName = db.NewObject("PROCEDURE", registerCleanup: false);
        db.RegisterDropCleanup(function ? "FUNCTION" : "PROCEDURE", routineName);
        var member = shape == "object" ? "p.a" : shape == "collection" ? "p(1)" : "p(1).a";
        await db.ExecuteAsync(connection, "CREATE " + (function ? "FUNCTION " : "PROCEDURE ") + routineName +
            "(p IN " + name + ",f IN NUMBER,s IN OUT NUMBER" + (function ? "" : ",q OUT " + name) + ",b OUT " + scalar + ",c OUT SYS_REFCURSOR)" +
            (function ? " RETURN " + name : "") + " IS BEGIN s:=s+DBMS_LOB.GETLENGTH(" + member + "); " +
            (function ? "" : "q:=p; ") + "b:=" + member + "; OPEN c FOR SELECT s FROM DUAL UNION ALL SELECT s+1 FROM DUAL ORDER BY 1; " +
            "IF f=1 THEN RAISE_APPLICATION_ERROR(-20001,'Batch output failure'); END IF; " +
            "UPDATE " + table + " SET n=n+1 WHERE id=3; " + (function ? "RETURN p; " : "") + "END;", CancellationToken.None);
        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture)!;
        await using var transaction = (OceanBaseTransaction)await connection.BeginTransactionAsync();
        var type = await connection.GetComplexTypeAsync(owner, name, transaction);
        using var input = asynchronous ? await connection.CreateTemporaryLobAsync(character ? OceanBaseDbType.Clob : OceanBaseDbType.Blob, transaction)
            : connection.CreateTemporaryLob(character ? OceanBaseDbType.Clob : OceanBaseDbType.Blob, transaction);
        const string text = "患者-CLOB";
        var bytes = new byte[] { 0, 1, 92, 39, 128, 255 };
        if (character) { if (asynchronous) await input.WriteTextAsync(0, text); else input.WriteText(0, text); }
        else if (asynchronous) await input.WriteBytesAsync(0, bytes, 0, bytes.Length); else input.WriteBytes(0, bytes, 0, bytes.Length);
        var value = shape == "object" ? type.CreateValue(input) : shape == "collection" ? type.CreateValue(input, null)
            : type.CreateValue(type.Members[0].ComplexType!.CreateValue(input, 7), type.Members[0].ComplexType!.CreateValue(null, 8));
        var session = connection.GetOpenSession();
        using (var batch = connection.CreateBatch())
        {
            batch.Transaction = transaction;
            void Update(int id)
            {
                var item = batch.CreateBatchCommand(); item.CommandText = "UPDATE " + table + " SET n=n+1 WHERE id=:id";
                item.Parameters.Add(new OceanBaseParameter { ParameterName = "id", Value = id, DbType = DbType.Int32 });
                batch.BatchCommands.Add(item);
            }
            Update(1); Update(2);
            var routine = batch.CreateBatchCommand(); routine.CommandType = kind == "text" ? CommandType.Text : CommandType.StoredProcedure;
            routine.CommandText = kind == "text" ? "BEGIN " + routineName + "(:p,:f,:s,:q,:b,:c); END;" : routineName;
            routine.Parameters.Add(new OceanBaseParameter { ParameterName = "p", ComplexType = type, Value = value });
            var fail = new OceanBaseParameter { ParameterName = "f", DbType = DbType.Decimal, Value = 1m }; routine.Parameters.Add(fail);
            var number = new OceanBaseParameter { ParameterName = "s", DbType = DbType.Decimal, Direction = ParameterDirection.InputOutput, Value = 7m }; routine.Parameters.Add(number);
            var complex = new OceanBaseParameter { ParameterName = "q", ComplexType = type, Direction = function ? ParameterDirection.ReturnValue : ParameterDirection.Output, Value = "old complex" }; routine.Parameters.Add(complex);
            var root = new OceanBaseParameter { ParameterName = "b", OceanBaseDbType = character ? OceanBaseDbType.Clob : OceanBaseDbType.Blob, Direction = ParameterDirection.Output, Value = "old lob" }; routine.Parameters.Add(root);
            var cursor = new OceanBaseParameter { ParameterName = "c", OceanBaseDbType = OceanBaseDbType.RefCursor, Direction = ParameterDirection.Output, Value = "old cursor" }; routine.Parameters.Add(cursor);
            batch.BatchCommands.Add(routine); Update(4); Update(5);
            if (asynchronous) await batch.PrepareAsync(); else batch.Prepare();
            var error = await Assert.ThrowsAsync<OceanBaseException>(async () =>
            {
                if (asynchronous) await batch.ExecuteNonQueryAsync(); else batch.ExecuteNonQuery();
            });
            Assert.Equal(20001, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown); Assert.Same(routine, error.BatchCommand);
            Assert.Equal(7m, number.Value); Assert.Equal("old complex", complex.Value); Assert.Equal("old lob", root.Value); Assert.Equal("old cursor", cursor.Value);
            Assert.Equal(new[] { 1, 1, -1, -1, -1 }, batch.BatchCommands.Cast<OceanBaseBatchCommand>().Select(item => item.RecordsAffected));
            await CheckStorage(db, connection, transaction, table, new[] { 1, 1, 0, 0, 0 });
            fail.Value = 0m;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var expected = DatabaseContext.IntegerResult(number.Value) + (character ? text.Length : bytes.Length);
                var affected = asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery();
                Assert.Equal(batch.BatchCommands.Cast<OceanBaseBatchCommand>().Where(item => item.RecordsAffected >= 0).Sum(item => item.RecordsAffected), affected);
                Assert.All(new[] { 0, 1, 3, 4 }, index => Assert.Equal(1, batch.BatchCommands[index].RecordsAffected));
                Assert.Equal(expected, DatabaseContext.IntegerResult(number.Value));
                var returned = Assert.IsType<OceanBaseComplexValue>(complex.Value);
                using var returnedLob = GetMember(returned, shape);
                using var rootLob = Assert.IsType<OceanBaseLob>(root.Value);
                using var refCursor = Assert.IsType<OceanBaseRefCursor>(cursor.Value);
                Assert.NotSame(input, returnedLob); Assert.NotSame(input, rootLob); Assert.NotSame(returnedLob, rootLob);
                if (attempt == 1) batch.Dispose();
                using (var reader = asynchronous ? await refCursor.GetDataReaderAsync(1) : refCursor.GetDataReader(1))
                {
                    for (var row = 0; row < 2; row++)
                    {
                        Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                        Assert.Equal(expected + row, DatabaseContext.IntegerResult(reader.GetValue(0)));
                    }
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                }
                await CheckLob(returnedLob, character, text, bytes, asynchronous); returnedLob.Dispose();
                await CheckLob(rootLob, character, text, bytes, asynchronous); rootLob.Dispose();
                Assert.Same(session, connection.GetOpenSession());
            }
        }
        await CheckLob(input, character, text, bytes, asynchronous);
        await CheckStorage(db, connection, transaction, table, new[] { 3, 3, 2, 2, 2 });
        await transaction.CommitAsync();
        await using var observer = await db.OpenAsync();
        await CheckStorage(db, observer, null, table, new[] { 3, 3, 2, 2, 2 });
    }

    private static OceanBaseLob GetMember(OceanBaseComplexValue value, string shape)
    {
        if (shape == "collection") Assert.Null(value.GetValues()[1]);
        if (shape == "nested-collection")
        {
            var last = Assert.IsType<OceanBaseComplexValue>(value.GetValues()[1]);
            Assert.Null(last.GetValues()[0]); Assert.Equal(8, DatabaseContext.IntegerResult(last.GetValues()[1]));
            value = Assert.IsType<OceanBaseComplexValue>(value.GetValues()[0]);
            Assert.Equal(7, DatabaseContext.IntegerResult(value.GetValues()[1]));
        }
        return Assert.IsType<OceanBaseLob>(value.GetValues()[0]);
    }

    private static async Task CheckLob(OceanBaseLob lob, bool character, string text, byte[] bytes, bool asynchronous)
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

    private static async Task CheckStorage(DatabaseContext db, OceanBaseConnection connection, OceanBaseTransaction? transaction, string table, int[] counts)
    {
        using var query = db.Command(connection, "SELECT id,n FROM " + table + " ORDER BY id", transaction);
        using var reader = await query.ExecuteReaderAsync();
        for (var row = 0; row < counts.Length; row++)
        {
            Assert.True(await reader.ReadAsync()); Assert.Equal(row + 1, DatabaseContext.IntegerResult(reader.GetValue(0)));
            Assert.Equal(counts[row], DatabaseContext.IntegerResult(reader.GetValue(1)));
        }
        Assert.False(await reader.ReadAsync());
    }

    private static void RegisterTypeCleanup(DatabaseContext db, string name)
        => db.RegisterCleanup("TYPE", name, async () =>
        {
            await using var connection = await db.OpenAsync(CancellationToken.None);
            await db.ExecuteAsync(connection, "DROP TYPE " + name + " FORCE", CancellationToken.None);
        });
}
