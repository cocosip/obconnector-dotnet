using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Oracle;

public sealed class UrowidTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var profile in ProfileTheoryData.OraclePrepareExecuteObjects())
            foreach (var prepareExecute in new[] { false, true })
                foreach (var asynchronous in new[] { false, true })
                    yield return new object[] { profile, prepareExecute, asynchronous };
    }

    [IntegrationTheory]
    [MemberData(nameof(Cases))]
    public async Task UrowidResultCanBeBoundBackWithType209(string profileName, bool prepareExecute, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var builder = db.Profile.CreateBuilder(); builder.Pooling = false; builder.UseOraclePrepareExecute = prepareExecute;
        if (!prepareExecute) builder.UseLobLocators = false;
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        if (asynchronous) await connection.OpenAsync(); else connection.Open();
        var source = await db.TableAsync(connection, "id VARCHAR2(1000) PRIMARY KEY", CancellationToken.None);
        var target = await db.TableAsync(connection, "id INTEGER PRIMARY KEY,u UROWID(4000)", CancellationToken.None);
        await db.ExecuteAsync(connection, "INSERT INTO " + source + " VALUES ('a')", CancellationToken.None);
        await db.ExecuteAsync(connection, "INSERT INTO " + source + " VALUES (RPAD('z',900,'z'))", CancellationToken.None);

        var rowids = new string[2];
        using (var query = db.Command(connection, "SELECT ROWID FROM " + source + " ORDER BY id"))
        {
            if (asynchronous) await query.PrepareAsync(); else query.Prepare();
            using var reader = asynchronous ? await query.ExecuteReaderAsync() : query.ExecuteReader();
            Assert.Equal("ROWID", reader.GetDataTypeName(0));
            Assert.Equal(typeof(string), reader.GetFieldType(0));
            for (var i = 0; i < rowids.Length; i++)
            {
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                rowids[i] = Assert.IsType<string>(reader.GetValue(0));
            }
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        }
        Assert.NotEqual(rowids[0], rowids[1]); Assert.True(rowids[1].Length > 251);

        var session = connection.GetOpenSession();
        using (var insert = db.Command(connection, "INSERT INTO " + target + " VALUES (:id,:p)"))
        {
            var id = DatabaseContext.Parameter(insert, "id", 1, DbType.Int32);
            var parameter = new OceanBaseParameter { ParameterName = "p", OceanBaseDbType = OceanBaseDbType.Urowid, Value = null, Size = 1 };
            insert.Parameters.Add(parameter);
            Assert.Equal(DbType.String, parameter.DbType);
            if (asynchronous) await insert.PrepareAsync(); else insert.Prepare();
            for (var i = 0; i < 4; i++)
            {
                id.Value = i + 1;
                parameter.Value = i == 0 ? null : i == 2 ? DBNull.Value : rowids[i / 2];
                Assert.Equal(1, asynchronous ? await insert.ExecuteNonQueryAsync() : insert.ExecuteNonQuery());
                Assert.Same(session, connection.GetOpenSession());
            }
            id.Value = 5; parameter.Value = "invalid!";
            var error = asynchronous
                ? await Assert.ThrowsAsync<OceanBaseException>(() => insert.ExecuteNonQueryAsync())
                : Assert.Throws<OceanBaseException>(() => insert.ExecuteNonQuery());
            Assert.NotEqual(0, error.ServerErrorCode);
            Assert.False(error.IsExecutionOutcomeUnknown);
            Assert.Same(session, connection.GetOpenSession());
            Assert.Equal(4L, await db.CountAsync(connection, target, CancellationToken.None));
            parameter.Value = rowids[0];
            Assert.Equal(1, asynchronous ? await insert.ExecuteNonQueryAsync() : insert.ExecuteNonQuery());
            Assert.Same(session, connection.GetOpenSession());
        }

        await using var observer = await db.OpenAsync();
        foreach (var prepared in new[] { false, true })
        {
            using var query = db.Command(observer, "SELECT id,u FROM " + target + " ORDER BY id");
            if (prepared) { if (asynchronous) await query.PrepareAsync(); else query.Prepare(); }
            using var reader = asynchronous ? await query.ExecuteReaderAsync(CommandBehavior.SequentialAccess) : query.ExecuteReader(CommandBehavior.SequentialAccess);
            Assert.Equal("ROWID", reader.GetDataTypeName(1));
            for (var i = 0; i < 5; i++)
            {
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                DatabaseContext.EqualNumber(reader.GetValue(0), i + 1, "UROWID execution order mismatch.");
                if (i == 0 || i == 2) Assert.True(reader.IsDBNull(1));
                else Assert.Equal(rowids[i == 3 ? 1 : 0], reader.GetString(1));
            }
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        }
    }

    public static IEnumerable<object[]> OutputCases()
    {
        foreach (var profile in ProfileTheoryData.OraclePrepareExecuteObjects())
            foreach (var asynchronous in new[] { false, true })
                foreach (var shape in new[] { "procedure", "function", "returning" })
                    yield return new object[] { profile, asynchronous, shape };
    }

    [IntegrationTheory]
    [MemberData(nameof(OutputCases))]
    public async Task UrowidOutputsRetainValuesAndRecoverAfterServerErrors(string profileName, bool asynchronous, string shape)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var builder = db.Profile.CreateBuilder(); builder.Pooling = false; builder.UseOraclePrepareExecute = true;
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        if (asynchronous) await connection.OpenAsync(); else connection.Open();
        var source = await db.TableAsync(connection, "id VARCHAR2(1000) PRIMARY KEY", CancellationToken.None);
        var target = await db.TableAsync(connection, "id INTEGER PRIMARY KEY,u UROWID(4000)", CancellationToken.None);
        await db.ExecuteAsync(connection, "INSERT INTO " + source + " VALUES (RPAD('z',900,'z'))", CancellationToken.None);
        var rowid = Assert.IsType<string>(await db.ScalarAsync(connection, "SELECT ROWID FROM " + source, CancellationToken.None));
        Assert.True(rowid.Length > 251);
        var sql = "INSERT INTO " + target + " VALUES (:id,:p) RETURNING u INTO :q";
        if (shape != "returning")
        {
            var kind = shape == "function" ? "FUNCTION" : "PROCEDURE";
            var name = "OBI_" + Guid.NewGuid().ToString("N").Substring(0, 20).ToUpperInvariant();
            db.RegisterCleanup(kind, name, async () =>
            {
                await using var cleanup = await db.OpenAsync();
                await db.ExecuteAsync(cleanup, "DROP " + kind + " " + name, CancellationToken.None);
            });
            await db.ExecuteAsync(connection, "CREATE " + kind + " " + name +
                " (p_id IN FLOAT,p_input IN UROWID,p_output OUT UROWID,p_inout IN OUT UROWID) " +
                (shape == "function" ? "RETURN UROWID " : "") + "AS BEGIN p_output:=p_inout; p_inout:=p_input; INSERT INTO " +
                target + " VALUES (p_id,p_input); " + (shape == "function" ? "RETURN p_input; " : "") + "END;", CancellationToken.None);
            sql = name;
        }
        using var command = db.Command(connection, sql);
        OceanBaseParameter id, input, output;
        OceanBaseParameter? inout = null, returned = null;
        if (shape == "returning")
        {
            id = DatabaseContext.Parameter(command, "id", 1, DbType.Int32);
            input = DatabaseContext.Parameter(command, "p", null);
            output = DatabaseContext.Parameter(command, "q", "old-output");
            input.OceanBaseDbType = output.OceanBaseDbType = OceanBaseDbType.Urowid;
            output.Direction = ParameterDirection.Output;
        }
        else
        {
            command.CommandType = CommandType.StoredProcedure;
            if (asynchronous) await command.DeriveParametersAsync(); else command.DeriveParameters();
            OceanBaseParameter Parameter(string name) => Assert.Single(command.Parameters.Cast<OceanBaseParameter>(), p => string.Equals(p.ParameterName, name, StringComparison.OrdinalIgnoreCase));
            id = Parameter("P_ID"); input = Parameter("P_INPUT"); output = Parameter("P_OUTPUT"); inout = Parameter("P_INOUT");
            if (shape == "function") { returned = Parameter("RETURN_VALUE"); Assert.Equal(ParameterDirection.ReturnValue, returned.Direction); }
            Assert.Equal(ParameterDirection.Input, input.Direction); Assert.Equal(ParameterDirection.Output, output.Direction); Assert.Equal(ParameterDirection.InputOutput, inout.Direction);
            foreach (var p in new[] { input, output, inout, returned }.Where(p => p != null))
            {
                Assert.Equal(OceanBaseDbType.Urowid, p!.OceanBaseDbType);
                Assert.Equal(DbType.String, p.DbType);
            }
        }
        output.Value = "old-output";
        var session = connection.GetOpenSession();
        if (asynchronous) await command.PrepareAsync(); else command.Prepare();
        Assert.Equal("old-output", output.Value);
        Assert.Equal(0L, await db.CountAsync(connection, target, CancellationToken.None));
        for (var i = 0; i < 4; i++)
        {
            id.Value = shape == "returning" ? (object)(i + 1) : (decimal)(i + 1); input.Value = i % 2 == 0 ? (object)DBNull.Value : rowid;
            if (inout != null) inout.Value = i % 2 == 0 ? rowid : DBNull.Value;
            if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
            Assert.Equal(shape == "returning" ? input.Value : i % 2 == 0 ? rowid : (object)DBNull.Value, output.Value);
            if (inout != null) Assert.Equal(input.Value, inout.Value);
            if (returned != null) Assert.Equal(input.Value, returned.Value);
            Assert.Same(session, connection.GetOpenSession());
        }
        // Fail after PL assignments (duplicate key), so outputs must retain their previous values.
        id.Value = shape == "returning" ? (object)4 : 4m; input.Value = DBNull.Value;
        if (inout != null) inout.Value = rowid;
        var oldOutput = output.Value; var oldReturn = returned?.Value;
        var error = asynchronous
            ? await Assert.ThrowsAsync<OceanBaseException>(() => command.ExecuteNonQueryAsync())
            : Assert.Throws<OceanBaseException>(() => command.ExecuteNonQuery());
        Assert.Equal(1, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown);
        Assert.Equal(oldOutput, output.Value);
        if (inout != null) Assert.Equal(rowid, inout.Value);
        if (returned != null) Assert.Equal(oldReturn, returned.Value);
        Assert.Same(session, connection.GetOpenSession());
        id.Value = shape == "returning" ? (object)5 : 5m;
        if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
        Assert.Equal(shape == "returning" ? DBNull.Value : rowid, output.Value);
        if (inout != null) Assert.Equal(DBNull.Value, inout.Value);
        if (returned != null) Assert.Equal(DBNull.Value, returned.Value);
        Assert.Same(session, connection.GetOpenSession());

        await using var observer = await db.OpenAsync();
        using var query = db.Command(observer, "SELECT id,u FROM " + target + " ORDER BY id");
        if (asynchronous) await query.PrepareAsync(); else query.Prepare();
        using var reader = asynchronous ? await query.ExecuteReaderAsync(CommandBehavior.SequentialAccess) : query.ExecuteReader(CommandBehavior.SequentialAccess);
        for (var i = 1; i <= 5; i++)
        {
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            DatabaseContext.EqualNumber(reader.GetValue(0), i, "UROWID output storage order mismatch.");
            Assert.Equal(i % 2 == 0 ? rowid : (object)DBNull.Value, reader.GetValue(1));
        }
        Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
        Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
    }
}
