using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using OceanBaseConnector.Internal.Sql;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Protocol;

public sealed partial class MultiStatementTests
{
    public static IEnumerable<object[]> OracleOrdinaryTextRows() =>
        from row in OracleParameterRows()
        from sample in Enumerable.Range(0, 8)
        select row.Concat(new object[] { sample }).ToArray();

    public static IEnumerable<object[]> OracleOrdinaryTextCharsetRows() =>
        from row in OracleRows()
        from charset in new[] { "ascii", "latin1", "utf8mb3", "utf8mb4", "gb18030" }
        select row.Concat(new object[] { charset }).ToArray();

    [IntegrationTheory]
    [MemberData(nameof(OracleOrdinaryTextRows))]
    public async Task OracleOrdinaryTextPreservesVarcharResultsAndIndependentClobStorage(string profileName, bool asynchronous, bool sequential, int sample)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true); await Open(connection, asynchronous);
        var text = sample switch
        {
            0 => "",
            1 => "'\\\0;:p -- 中🙂 ",
            2 => new string('\'', 3000),
            3 => new string('中', 2000),
            4 => new string('x', 4001),
            5 => new string('中', 32767),
            7 => string.Concat(Enumerable.Repeat("a\0b", 4001)),
            _ => new string('x', 1048576),
        };
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, v CLOB", CancellationToken.None);
        using (var command = db.Command(connection, "INSERT INTO " + table + " VALUES (1,:p); INSERT INTO " + table + " VALUES (2,:P); INSERT INTO " + table + " VALUES (3,:n)"))
        {
            var value = DatabaseContext.Parameter(command, "p", text); value.OceanBaseDbType = OceanBaseDbType.String;
            DatabaseContext.Parameter(command, "n", DBNull.Value, DbType.String);
            Assert.Equal(3, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery()); Assert.Same(text, value.Value);
        }
        await using var verification = await db.OpenAsync();
        Assert.Equal(3, await db.CountAsync(verification, table, CancellationToken.None));
        for (var id = 1; id <= 3; id++)
            Assert.Equal(id == 3 || text.Length == 0 ? null : text, await ReadOracleStoredText(db, verification, table, id, asynchronous, sequential));

        using var query = db.Command(connection,
            "SELECT :p v,:n n,CASE WHEN :space='x' THEN 1 ELSE 0 END eq FROM DUAL; SELECT :P v,:N n,CASE WHEN :space='x' THEN 1 ELSE 0 END eq FROM DUAL");
        DatabaseContext.Parameter(query, "p", text, DbType.String); DatabaseContext.Parameter(query, "n", DBNull.Value, DbType.String);
        DatabaseContext.Parameter(query, "space", "x ", DbType.String);
        using var reader = asynchronous ? await query.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default)
            : query.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
        for (var result = 0; result < 2; result++)
        {
            if (result > 0) Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
            Assert.Equal("VARCHAR2", reader.GetDataTypeName(0)); Assert.Equal(typeof(string), reader.GetFieldType(0));
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            if (text.Length == 0) Assert.True(reader.IsDBNull(0)); else Assert.Equal(text, reader.GetString(0));
            Assert.True(reader.IsDBNull(1)); DatabaseContext.EqualNumber(reader.GetValue(2), 0, "Varying strings do not use CHAR blank padding.");
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
        }
        Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
    }

    [IntegrationTheory]
    [MemberData(nameof(OracleOrdinaryTextCharsetRows))]
    public async Task OracleOrdinaryTextUsesStrictClientEncodingWithoutCastTruncation(string profileName, bool asynchronous, string charset)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var builder = db.Profile.CreateBuilder(); builder.Pooling = false; builder.AllowMultiStatements = true; builder.CharacterSet = charset;
        await using var connection = new OceanBaseConnection(builder.ConnectionString); await Open(connection, asynchronous);
        var text = (charset == "ascii" ? new string('A', 6000) : charset == "latin1" ? new string('é', 3000)
            : charset == "gb18030" ? string.Concat(Enumerable.Repeat("乗😀", 1000)) : new string('中', 2000)) + "'\\\0;:p -- ";
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, v CLOB", CancellationToken.None);
        using var command = db.Command(connection, "INSERT INTO " + table + " VALUES (1,:p); INSERT INTO " + table + " VALUES (2,:P)");
        DatabaseContext.Parameter(command, "p", text, DbType.String);
        Assert.Equal(2, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
        await using var verification = await db.OpenAsync();
        for (var id = 1; id <= 2; id++) Assert.Equal(text, await ReadOracleStoredText(db, verification, table, id, asynchronous, sequential: false));
    }

    [IntegrationTheory]
    [MemberData(nameof(OracleParameterRows))]
    public async Task OracleLargeNulSingleTextExpressionsPreserveEveryCharacter(string profileName, bool asynchronous, bool sequential)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true); await Open(connection, asynchronous);
        var text = string.Concat(Enumerable.Repeat("a\0b", 65536));
        using var command = db.Command(connection, "SELECT :p v FROM DUAL");
        DatabaseContext.Parameter(command, "p", text, DbType.String);
        command.CommandText = ClientPreparedSql.Build(ParameterBindingPlan.Create(command.CommandText, OceanBaseCompatibilityMode.Oracle, (OceanBaseParameterCollection)command.Parameters), OceanBaseCompatibilityMode.Oracle);
        command.Parameters.Clear();
        // The same long-NUL script times out with Connector/J; verify the supported single-expression shape.
        for (var result = 0; result < 2; result++)
        {
            using var reader = asynchronous ? await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default)
                : command.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
            Assert.Equal("VARCHAR2", reader.GetDataTypeName(0)); Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.Equal(text, reader.GetString(0)); Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        }
    }

    [IntegrationTheory]
    [MemberData(nameof(OracleRows))]
    public async Task OracleOrdinaryTextValidationRejectsBeforeAnyScriptSideEffects(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var builder = db.Profile.CreateBuilder(); builder.Pooling = false; builder.AllowMultiStatements = true; builder.CharacterSet = "ascii";
        await using var connection = new OceanBaseConnection(builder.ConnectionString); await Open(connection, asynchronous);
        var table = await db.TableAsync(connection, "id INTEGER, v CLOB", CancellationToken.None);
        using var command = db.Command(connection, "INSERT INTO " + table + " VALUES (1,:p); INSERT INTO " + table + " VALUES (2,:P)");
        var value = DatabaseContext.Parameter(command, "p", "", DbType.String);
        Func<Task> execute = () => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery());
        foreach (var invalid in new[] { "\ud800", "中" })
        {
            value.Value = invalid; await Assert.ThrowsAnyAsync<EncoderFallbackException>(execute);
            Assert.Equal(ConnectionState.Open, connection.State); Assert.Equal(0, await db.CountAsync(connection, table, CancellationToken.None));
        }
        value.Value = new string('\'', 1048576);
        command.CommandText = string.Join("; ", Enumerable.Repeat("INSERT INTO " + table + " VALUES (1,:p)", 33));
        await Assert.ThrowsAsync<ArgumentException>(execute);
        Assert.Equal(ConnectionState.Open, connection.State); Assert.Equal(0, await db.CountAsync(connection, table, CancellationToken.None));
        await using var verification = await db.OpenAsync(); Assert.Equal(0, await db.CountAsync(verification, table, CancellationToken.None));
    }

    [IntegrationTheory]
    [MemberData(nameof(OracleRows))]
    public async Task OracleOrdinaryTextKeepsServerColumnLimitAndKnownTransactionPrefix(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true); await Open(connection, asynchronous);
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, v VARCHAR2(4000)", CancellationToken.None);
        await using var transaction = (OceanBaseTransaction)await connection.BeginTransactionAsync();
        using var command = db.Command(connection,
            "INSERT INTO " + table + " VALUES (1,:good); INSERT INTO " + table + " VALUES (2,:large); INSERT INTO " + table + " VALUES (3,:good)", transaction);
        DatabaseContext.Parameter(command, "good", "safe", DbType.String); DatabaseContext.Parameter(command, "large", new string('x', 4001), DbType.String);
        var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
        Assert.Equal(12899, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(1, await db.CountAsync(connection, table, CancellationToken.None, transaction));
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, "SELECT id FROM " + table, CancellationToken.None, transaction), 1, "Only the confirmed prefix executed.");
        await using var verification = await db.OpenAsync(); Assert.Equal(0, await db.CountAsync(verification, table, CancellationToken.None));
        if (asynchronous) await transaction.RollbackAsync(); else transaction.Rollback();
        Assert.Equal(0, await db.CountAsync(connection, table, CancellationToken.None)); Assert.Equal(0, await db.CountAsync(verification, table, CancellationToken.None));
    }

    private static async Task<string?> ReadOracleStoredText(DatabaseContext db, OceanBaseConnection connection, string table, int id, bool asynchronous, bool sequential)
    {
        string? actual = null; OceanBaseLob? lob = null;
        using (var command = db.Command(connection, "SELECT v FROM " + table + " WHERE id=" + id))
        using (var reader = asynchronous ? await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default)
            : command.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default))
        {
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            if (!reader.IsDBNull(0))
            {
                if (reader.GetFieldType(0) == typeof(OceanBaseLob)) lob = (OceanBaseLob)reader.GetValue(0);
                else { using var source = reader.GetTextReader(0); actual = asynchronous ? await source.ReadToEndAsync() : source.ReadToEnd(); }
            }
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
        }
        if (lob != null)
        {
            using (lob)
            using (var source = lob.OpenTextReader()) actual = asynchronous ? await source.ReadToEndAsync() : source.ReadToEnd();
        }
        return actual;
    }
}
