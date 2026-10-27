using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.Acceptance;

internal static partial class DatabaseCases
{
    private static void AddOracle(List<AcceptanceCase> cases)
    {
        cases.Add(new("oracle.scalar-out-inout", "oracle", Outputs, Mode: "Oracle", RequiresPrepareExecute: true));
        cases.Add(new("oracle.returning", "oracle", Returning, Mode: "Oracle", RequiresObjects: true, RequiresPrepareExecute: true));
        cases.Add(new("oracle.stored-procedure", "oracle", Procedure, Mode: "Oracle", RequiresObjects: true, RequiresPrepareExecute: true));
        cases.Add(new("oracle.ref-cursor", "oracle", RefCursor, Mode: "Oracle", RequiresPrepareExecute: true));
        cases.Add(new("oracle.stream-out", "oracle", StreamOutput, Mode: "Oracle", RequiresPrepareExecute: true));
        cases.Add(new("oracle.array-binding", "oracle", ArrayBinding, Mode: "Oracle", RequiresObjects: true, RequiresPrepareExecute: true));
        foreach (var character in new[] { false, true })
            cases.Add(new("lob.temporary-" + (character ? "clob" : "blob"), "lob", (c, t) => TemporaryLob(c, character, t), Mode: "Oracle", RequiresLob: true));
        foreach (var collection in new[] { false, true })
            cases.Add(new("complex." + (collection ? "collection" : "object"), "complex", (c, t) => Complex(c, collection, t), Mode: "Oracle", RequiresObjects: true));
        cases.Add(new("xa.prepare-recover-rollback", "xa", (c, t) => Xa(c, false, t), RequiresObjects: true));
        cases.Add(new("xa.prepare-recover-commit", "xa", (c, t) => Xa(c, true, t), RequiresObjects: true));
    }
    private static async Task Outputs(CaseContext c, CancellationToken token)
    {
        await using var connection = await c.OpenAsync(token);
        await using var command = c.Command(connection, "BEGIN :result := :input + 1; :inout := 10; END;");
        CaseContext.Parameter(command, "input", 41, DbType.Int32);
        var output = CaseContext.Parameter(command, "result", DBNull.Value, DbType.Int32); output.Direction = ParameterDirection.Output;
        var inout = CaseContext.Parameter(command, "inout", 8, DbType.Int32); inout.Direction = ParameterDirection.InputOutput;
        await command.ExecuteNonQueryAsync(token);
        c.EqualNumber(output.Value, 42, "Scalar OUT mismatch."); c.EqualNumber(inout.Value, 10, "Scalar InputOutput mismatch.");
    }
    private static async Task Returning(CaseContext c, CancellationToken token)
    {
        await using var connection = await c.OpenAsync(token);
        var table = await c.TableAsync(connection, "id INTEGER PRIMARY KEY", token);
        await using var command = c.Command(connection, "INSERT INTO " + table + " VALUES (:id) RETURNING id INTO :result");
        CaseContext.Parameter(command, "id", 7, DbType.Int32);
        var output = CaseContext.Parameter(command, "result", DBNull.Value, DbType.Int32); output.Direction = ParameterDirection.Output;
        c.Check(await command.ExecuteNonQueryAsync(token) == 1, "RETURNING insert must affect one row.");
        c.EqualNumber(output.Value, 7, "RETURNING value mismatch.");
    }
    private static async Task Procedure(CaseContext c, CancellationToken token)
    {
        await using var connection = await c.OpenAsync(token);
        var name = c.NewObject("PROCEDURE");
        await c.ExecuteAsync(connection, "CREATE PROCEDURE " + name + " (p_input IN NUMBER, p_output OUT NUMBER) AS BEGIN p_output := p_input + 1; END;", token);
        await using var command = c.Command(connection, name); command.CommandType = CommandType.StoredProcedure;
        CaseContext.Parameter(command, "p_input", 6, DbType.Int32);
        var output = CaseContext.Parameter(command, "p_output", DBNull.Value, DbType.Int32); output.Direction = ParameterDirection.Output;
        await command.ExecuteNonQueryAsync(token); c.EqualNumber(output.Value, 7, "Stored procedure OUT mismatch.");
        var owner = Convert.ToString(await c.ScalarAsync(connection, "SELECT USER FROM DUAL", token), CultureInfo.InvariantCulture);
        using var procedures = await connection.GetSchemaAsync("Procedures", new string?[] { null, owner, name }, token);
        using var parameters = await connection.GetSchemaAsync("ProcedureParameters", new string?[] { null, owner, name }, token);
        c.Check(procedures.Rows.Count == 1 && parameters.Rows.Count == 2, "Created procedure and its parameters must be discoverable.");
    }
    private static async Task RefCursor(CaseContext c, CancellationToken token)
    {
        await using var connection = await c.OpenAsync(token);
        OceanBaseRefCursor cursor;
        await using (var command = c.Command(connection, "BEGIN OPEN :result FOR SELECT 7 AS value FROM DUAL; END;"))
        {
            var output = CaseContext.Parameter(command, "result", DBNull.Value); output.OceanBaseDbType = OceanBaseDbType.RefCursor; output.Direction = ParameterDirection.Output;
            await command.ExecuteNonQueryAsync(token);
            c.Check(output.Value is OceanBaseRefCursor, "REF CURSOR output must return a cursor handle.");
            cursor = output.Value as OceanBaseRefCursor ?? throw new AssertionFailure("Missing cursor handle.");
        }
        using (cursor)
        await using (var reader = await cursor.GetDataReaderAsync(1, cancellationToken: token))
        {
            c.Check(await reader.ReadAsync(token), "REF CURSOR must return its row after parent command disposal.");
            c.EqualNumber(reader.GetValue(0), 7, "REF CURSOR value mismatch.");
            c.Check(!await reader.ReadAsync(token) && !await reader.NextResultAsync(token), "REF CURSOR must finish its complete result.");
        }
    }
    private static async Task StreamOutput(CaseContext c, CancellationToken token)
    {
        await using var connection = await c.OpenAsync(token);
        var bytes = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();
        using var stream = new MemoryStream(bytes, false);
        await using var command = c.Command(connection, "BEGIN :result := UTL_RAW.LENGTH(:value); END;");
        CaseContext.Parameter(command, "value", stream, DbType.Binary);
        var output = CaseContext.Parameter(command, "result", DBNull.Value, DbType.Int32); output.Direction = ParameterDirection.Output;
        await command.ExecuteNonQueryAsync(token);
        c.EqualNumber(output.Value, bytes.Length, "PIECE_DATA with PrepareExecute OUT must preserve the uploaded byte count.");
    }
    private static async Task ArrayBinding(CaseContext c, CancellationToken token)
    {
        await using var connection = await c.OpenAsync(token);
        var table = await c.TableAsync(connection, "id INTEGER PRIMARY KEY", token);
        await using var transaction = (OceanBaseTransaction)await connection.BeginTransactionAsync(token);
        var type = connection.CreateArrayType("NUMBER", transaction);
        await using var command = c.Command(connection, "INSERT INTO " + table + " VALUES (:id)", transaction);
        CaseContext.Parameter(command, "id", type.CreateValue(1, 2, 3));
        var result = await command.ExecuteArrayAsync(3, token);
        c.Check(result.IsComplete && result.Items.Count == 3 && result.Items.All(i => i.Status == OceanBaseArrayBindingStatus.Succeeded), "All array binding positions must succeed.");
        c.Check(result.RecordsAffected == 3 && await c.CountAsync(connection, table, token, transaction) == 3, "Array binding must insert three rows.");
        await transaction.RollbackAsync(token);
        c.Check(await c.CountAsync(connection, table, token) == 0, "Array binding writes must belong to the explicit transaction.");
    }
    private static async Task TemporaryLob(CaseContext c, bool character, CancellationToken token)
    {
        var connection = await c.OpenAsync(token);
        c.RegisterCleanup("SESSION", "temporary-lob", () => connection.DisposeAsync().AsTask());
        var lob = await connection.CreateTemporaryLobAsync(character ? OceanBaseDbType.Clob : OceanBaseDbType.Blob, cancellationToken: token);
        c.RegisterCleanup("TEMPORARY_LOB", character ? "CLOB" : "BLOB", async () =>
        {
            try { await lob.FreeTemporaryAsync(CancellationToken.None); }
            finally { await lob.DisposeAsync(); }
        });
        if (character)
        {
            await lob.WriteTextAsync(0, "中文🙂XYZ", token); await lob.TruncateAsync(3, token);
            using var reader = lob.OpenTextReader();
            c.Check(await reader.ReadToEndAsync(token) == "中文🙂", "CLOB write/truncate/read must use Unicode scalar offsets.");
        }
        else
        {
            var bytes = new byte[] { 0, 1, 128, 255 };
            await lob.WriteBytesAsync(0, bytes, 0, bytes.Length, token); await lob.TruncateAsync(3, token);
            using var source = lob.OpenRead(); using var destination = new MemoryStream(); await source.CopyToAsync(destination, token);
            c.Check(destination.ToArray().SequenceEqual(bytes.Take(3)), "BLOB write/truncate/read mismatch.");
        }
    }

    private static async Task Complex(CaseContext c, bool collection, CancellationToken token)
    {
        await using var connection = await c.OpenAsync(token);
        var name = c.NewObject("TYPE");
        await c.ExecuteAsync(connection, "CREATE TYPE " + name + (collection ? " AS TABLE OF NUMBER" : " AS OBJECT (n NUMBER, s VARCHAR2(40))"), token);
        var owner = Convert.ToString(await c.ScalarAsync(connection, "SELECT USER FROM DUAL", token), CultureInfo.InvariantCulture)!;
        var table = await c.TableAsync(connection, "id INTEGER PRIMARY KEY, value " + name, token);
        var type = await connection.GetComplexTypeAsync(owner, name, cancellationToken: token);
        c.Check(type.IsCollection == collection, "Dictionary type kind must match created type.");
        var value = collection ? type.CreateValue(1, 2, 3) : type.CreateValue(7, "中文");
        await using (var insert = c.Command(connection, "INSERT INTO " + table + " VALUES (1, :value)"))
        { CaseContext.Parameter(insert, "value", value); await insert.ExecuteNonQueryAsync(token); }
        foreach (var prepared in new[] { false, true })
        {
            await using var select = c.Command(connection, "SELECT value FROM " + table + " WHERE id=" + (prepared ? ":id" : "1"));
            if (prepared) CaseContext.Parameter(select, "id", 1, DbType.Int32);
            else await select.PrepareAsync(token); // Named complex results require binary protocol.
            await using var reader = await select.ExecuteReaderAsync(token);
            c.Check(await reader.ReadAsync(token) && reader.GetValue(0) is OceanBaseComplexValue, "Complex query must return a provider value.");
            var actual = ((OceanBaseComplexValue)reader.GetValue(0)).GetValues();
            c.Check(actual.Length == (collection ? 3 : 2), "Complex member count mismatch.");
            for (var i = 0; i < (collection ? 3 : 1); i++) c.Check(Number(actual[i]) == (collection ? i + 1 : 7), "Complex numeric member mismatch.");
            if (!collection) c.Check(actual[1] is string s && s == "中文", "Complex text member mismatch.");
            c.Check(!await reader.ReadAsync(token) && !await reader.NextResultAsync(token), "Complex query must consume its complete result.");
        }
        connection.ClearComplexTypeCache();
    }
    internal static async Task CleanupXaAsync(Func<Task> end, Func<Task> close, Func<Task> rollback)
    {
        var errors = new List<Exception>();
        foreach (var step in new[] { end, close, rollback })
        {
            try { await step(); } catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count > 0) throw new AggregateException(errors);
    }

    private static async Task Xa(CaseContext c, bool commit, CancellationToken token)
    {
        var connection = await c.OpenAsync(token);
        c.RegisterCleanup("SESSION", "xa", () => connection.DisposeAsync().AsTask());
        var table = await c.TableAsync(connection, "id INTEGER PRIMARY KEY", token);
        var resource = connection.GetXaResource();
        var global = Guid.NewGuid().ToByteArray(); var xid = new OceanBaseXid(0x4F4241, global, new byte[] { 1 });
        var attempted = false; var active = false; var completed = false;
        c.RegisterCleanup("XA", Convert.ToHexString(global), () => CleanupXaAsync(
            () => active ? resource.EndAsync(xid) : Task.CompletedTask,
            () => connection.DisposeAsync().AsTask(),
            async () =>
            {
                if (!attempted || completed) return;
                await using var cleanup = await c.OpenAsync(CancellationToken.None);
                await cleanup.GetXaResource().RollbackAsync(xid);
            }));
        attempted = true;
        await resource.StartAsync(xid, cancellationToken: token); active = true;
        await c.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES (1)", token);
        await resource.EndAsync(xid, cancellationToken: token); active = false;
        c.Check(await resource.PrepareAsync(xid, token) == OceanBaseXaVote.Prepared, "Written XA branch must vote prepared.");
        await using (var recovery = await c.OpenAsync(token))
        {
            var recovered = await recovery.GetXaResource().RecoverAsync(token);
            c.Check(recovered.Any(item => item.Equals(xid)), "Recovery must include this test's prepared branch.");
        }
        if (commit) await resource.CommitAsync(xid, cancellationToken: token); else await resource.RollbackAsync(xid, token);
        completed = true;
        await using var observer = await c.OpenAsync(token);
        c.Check(await c.CountAsync(observer, table, token) == (commit ? 1 : 0), "XA completion must produce the selected durable outcome.");
    }
}
