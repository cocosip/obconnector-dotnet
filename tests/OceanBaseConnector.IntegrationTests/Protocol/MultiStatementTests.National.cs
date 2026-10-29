using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Protocol;

public sealed partial class MultiStatementTests
{
    public static IEnumerable<object[]> OracleNationalRows() =>
        from row in OracleRows()
        from charset in new[] { "utf8mb4", "ascii", "latin1", "utf8mb3", "gb18030" }
        from sequential in new[] { false, true }
        select row.Concat(new object[] { charset, sequential }).ToArray();

    public static IEnumerable<object[]> OracleNationalLengthRows() =>
        from row in OracleRows()
        from length in new[] { 0, 1, 2000, 2001, 4000, 4001, 32767 }
        select row.Concat(new object[] { length }).ToArray();

    [IntegrationTheory]
    [MemberData(nameof(OracleNationalRows))]
    public async Task OracleNationalMultiStatementsPreserveStoragePaddingNullAndExpressionMetadata(
        string profileName, bool asynchronous, string charset, bool sequential)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var builder = db.Profile.CreateBuilder(); builder.Pooling = false; builder.AllowMultiStatements = true; builder.CharacterSet = charset;
        await using var connection = new OceanBaseConnection(builder.ConnectionString); await Open(connection, asynchronous);
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, n NCHAR(32), v NVARCHAR2(100)", CancellationToken.None);
        var session = connection.GetOpenSession();
        const string fixedValue = "中🙂'\\\0;:p";
        const string varyingValue = fixedValue + "\\abcd";
        using var command = db.Command(connection, "INSERT INTO " + table + " VALUES (1,:n,:v); INSERT INTO " + table + " VALUES (2,:empty,:null)");
        var n = DatabaseContext.Parameter(command, "n", fixedValue); n.OceanBaseDbType = OceanBaseDbType.NChar; n.Size = fixedValue.Length;
        var v = DatabaseContext.Parameter(command, "v", varyingValue); v.OceanBaseDbType = OceanBaseDbType.NVarchar2; v.Size = varyingValue.Length;
        DatabaseContext.Parameter(command, "empty", "").OceanBaseDbType = OceanBaseDbType.NChar;
        DatabaseContext.Parameter(command, "null", DBNull.Value).OceanBaseDbType = OceanBaseDbType.NVarchar2;
        Assert.Equal(2, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
        Assert.Equal(fixedValue, n.Value); Assert.Equal(varyingValue, v.Value);
        // Verify committed national storage independently of the writer's narrower result-encoding contract.
        builder.CharacterSet = "utf8mb4";
        await using var verification = new OceanBaseConnection(builder.ConnectionString); await Open(verification, asynchronous);
        using var query = db.Command(verification, "SELECT n,v FROM " + table + " ORDER BY id");
        var behavior = sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default;
        using (var reader = asynchronous ? await query.ExecuteReaderAsync(behavior) : query.ExecuteReader(behavior))
        {
            Assert.Equal("NCHAR", reader.GetDataTypeName(0)); Assert.Equal("NVARCHAR2", reader.GetDataTypeName(1));
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            // NCHAR counts the supplementary character once, while string.Length counts two UTF-16 units.
            Assert.Equal(fixedValue + new string(' ', 24), reader.GetString(0)); Assert.Equal(varyingValue, reader.GetString(1));
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.True(reader.IsDBNull(0)); Assert.True(reader.IsDBNull(1));
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        }
        query.CommandText = "SELECT :v,:n FROM DUAL; SELECT COUNT(*) FROM " + table;
        DatabaseContext.Parameter(query, "v", varyingValue).OceanBaseDbType = OceanBaseDbType.NVarchar2;
        DatabaseContext.Parameter(query, "n", fixedValue).OceanBaseDbType = OceanBaseDbType.NChar;
        using (var reader = asynchronous ? await query.ExecuteReaderAsync(behavior) : query.ExecuteReader(behavior))
        {
            Assert.Equal("NCHAR", reader.GetDataTypeName(0)); Assert.Equal("NCHAR", reader.GetDataTypeName(1));
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.Equal(varyingValue, reader.GetString(0)); Assert.Equal(fixedValue, reader.GetString(1));
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            DatabaseContext.EqualNumber(reader.GetValue(0), 2, "Both national rows are committed.");
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        }
        Assert.Same(session, connection.GetOpenSession()); Assert.Equal(2, await db.CountAsync(connection, table, CancellationToken.None));
    }

    [IntegrationTheory]
    [MemberData(nameof(OracleNationalLengthRows))]
    public async Task OracleNationalMultiStatementLongLiteralsUseNationalLengths(string profileName, bool asynchronous, int length)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true); await Open(connection, asynchronous);
        var text = new string('中', length);
        using var command = db.Command(connection, "SELECT :v,LENGTH(:v),LENGTHB(:v) FROM DUAL; SELECT LENGTH(:v),LENGTHB(:v) FROM DUAL");
        var value = DatabaseContext.Parameter(command, "v", text); value.OceanBaseDbType = OceanBaseDbType.NVarchar2; value.Size = text.Length;
        using var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader();
        Assert.Equal("NCHAR", reader.GetDataTypeName(0)); Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
        if (length == 0) { Assert.True(reader.IsDBNull(0)); Assert.True(reader.IsDBNull(1)); Assert.True(reader.IsDBNull(2)); }
        else
        {
            Assert.Equal(text, reader.GetString(0));
            DatabaseContext.EqualNumber(reader.GetValue(1), length, "National character length.");
            DatabaseContext.EqualNumber(reader.GetValue(2), length * 2, "National UTF-16 storage byte length.");
        }
        Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
        if (length == 0) { Assert.True(reader.IsDBNull(0)); Assert.True(reader.IsDBNull(1)); }
        else
        {
            DatabaseContext.EqualNumber(reader.GetValue(0), length, "Repeated national literal length.");
            DatabaseContext.EqualNumber(reader.GetValue(1), length * 2, "Repeated national literal storage bytes.");
        }
        Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
    }

    [IntegrationTheory]
    [MemberData(nameof(OracleRows))]
    public async Task OracleNationalMultiStatementsRejectInvalidValuesBeforeAnyTargetSideEffects(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true); await Open(connection, asynchronous);
        var table = await db.TableAsync(connection, "value NVARCHAR2(100)", CancellationToken.None);
        var session = connection.GetOpenSession();
        using var command = db.Command(connection, "INSERT INTO " + table + " VALUES (:v); INSERT INTO " + table + " VALUES (:v)");
        var parameter = DatabaseContext.Parameter(command, "v", "");
        Func<Task> execute = () => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery());
        foreach (var type in new[] { OceanBaseDbType.NChar, OceanBaseDbType.NVarchar2 })
        {
            parameter.OceanBaseDbType = type; parameter.Value = "\ud800";
            await Assert.ThrowsAsync<EncoderFallbackException>(execute);
            parameter.Value = "🙂"; parameter.Size = 1; await Assert.ThrowsAsync<ArgumentException>(execute);
            parameter.Size = 0; parameter.Value = 42; await Assert.ThrowsAsync<ArgumentException>(execute);
            parameter.Value = DBNull.Value; parameter.Precision = 1; await Assert.ThrowsAsync<NotSupportedException>(execute);
            parameter.Precision = 0; parameter.Scale = 1; await Assert.ThrowsAsync<NotSupportedException>(execute); parameter.Scale = 0;
        }
        Assert.Equal(0, await db.CountAsync(connection, table, CancellationToken.None)); Assert.Same(session, connection.GetOpenSession());
    }

    [IntegrationTheory]
    [MemberData(nameof(OracleRows))]
    public async Task OracleNationalMultiStatementServerErrorKeepsConfirmedPrefixAndSkipsSuffix(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true); await Open(connection, asynchronous);
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, v NVARCHAR2(2)", CancellationToken.None);
        var session = connection.GetOpenSession();
        using var command = db.Command(connection, "INSERT INTO " + table + " VALUES (1,:v); INSERT INTO " + table + " VALUES (2,:tooLong); INSERT INTO " + table + " VALUES (3,:v)");
        DatabaseContext.Parameter(command, "v", "中").OceanBaseDbType = OceanBaseDbType.NChar;
        DatabaseContext.Parameter(command, "tooLong", "中文🙂").OceanBaseDbType = OceanBaseDbType.NVarchar2;
        var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
        Assert.False(error.IsExecutionOutcomeUnknown); Assert.NotEqual(0, error.ServerErrorCode);
        Assert.Equal(1, await db.CountAsync(connection, table, CancellationToken.None));
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, "SELECT id FROM " + table, CancellationToken.None), 1, "Only the confirmed prefix is stored.");
        Assert.Equal("中", await db.ScalarAsync(connection, "SELECT v FROM " + table, CancellationToken.None));
        command.Parameters.Clear(); command.CommandText = "INSERT INTO " + table + " VALUES (4,:v); SELECT :v FROM DUAL";
        DatabaseContext.Parameter(command, "v", "好").OceanBaseDbType = OceanBaseDbType.NVarchar2;
        Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
        Assert.Equal(2, await db.CountAsync(connection, table, CancellationToken.None)); Assert.Same(session, connection.GetOpenSession());
    }
}
