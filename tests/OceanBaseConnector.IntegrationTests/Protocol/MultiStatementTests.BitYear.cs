using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Protocol;

public sealed partial class MultiStatementTests
{
    public static IEnumerable<object[]> MySqlBitYearRows() =>
        from row in MySqlRows()
        from sequential in new[] { false, true }
        from sample in Enumerable.Range(0, 6)
        select row.Concat(new object[] { sequential, sample }).ToArray();

    [IntegrationTheory]
    [MemberData(nameof(MySqlBitYearRows))]
    public async Task MySqlBitYearMultiStatementsPreserveIndependentStorageAndResults(string profileName, bool asynchronous, bool sequential, int sample)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true); await Open(connection, asynchronous);
        var bits = new[] { 0UL, 1UL, 511UL, 1UL << 63, 0x8000000000000001UL, ulong.MaxValue }[sample];
        var width = sample < 2 ? 1 : sample == 2 ? 9 : 64;
        var declaredWidth = sample == 5 ? 0 : width;
        var year = new ushort[] { 0, 1901, 2026, 2155, 1901, 2155 }[sample];
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, b BIT(" + width + "), y YEAR", CancellationToken.None);
        using (var command = db.Command(connection, "INSERT INTO " + table + " VALUES (1,?,?); INSERT INTO " + table + " VALUES (2,?,?); INSERT INTO " + table + " VALUES (3,?,?)"))
        {
            for (var row = 0; row < 3; row++)
            {
                var bitParameter = DatabaseContext.Parameter(command, "", row == 2 ? DBNull.Value : bits); bitParameter.OceanBaseDbType = OceanBaseDbType.Bit; bitParameter.Size = declaredWidth;
                var yearParameter = DatabaseContext.Parameter(command, "", row == 2 ? DBNull.Value : year); yearParameter.OceanBaseDbType = OceanBaseDbType.Year; yearParameter.Precision = 4;
            }
            Assert.Equal(3, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            Assert.Equal(bits, command.Parameters[0].Value); Assert.Equal(year, command.Parameters[1].Value);
        }
        await using var verification = await db.OpenAsync();
        foreach (var prepared in new[] { false, true })
        {
            using var query = db.Command(verification, "SELECT id,b,y FROM " + table + (prepared ? " WHERE id>=?" : "") + " ORDER BY id");
            if (prepared) DatabaseContext.Parameter(query, "", 1, DbType.Int32);
            using var reader = asynchronous ? await query.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default)
                : query.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
            Assert.Equal("BIT", reader.GetDataTypeName(1)); Assert.Equal("YEAR", reader.GetDataTypeName(2));
            for (var id = 1; id <= 3; id++)
            {
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); DatabaseContext.EqualNumber(reader.GetValue(0), id, "Independent stored row order.");
                if (id == 3) { Assert.True(reader.IsDBNull(1)); Assert.True(reader.IsDBNull(2)); }
                else { Assert.Equal(bits, reader.GetFieldValue<ulong>(1)); Assert.Equal(year, reader.GetFieldValue<ushort>(2)); }
            }
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
        }
        using (var query = db.Command(connection, "SELECT ?,?,?; SELECT ?,?,?"))
        {
            for (var result = 0; result < 2; result++)
            {
                var bitParameter = DatabaseContext.Parameter(query, "", bits); bitParameter.OceanBaseDbType = OceanBaseDbType.Bit; bitParameter.Size = declaredWidth;
                var yearParameter = DatabaseContext.Parameter(query, "", year); yearParameter.OceanBaseDbType = OceanBaseDbType.Year; yearParameter.Precision = 4;
                DatabaseContext.Parameter(query, "", DBNull.Value).OceanBaseDbType = OceanBaseDbType.Year;
            }
            using var reader = asynchronous ? await query.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default)
                : query.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
            for (var result = 0; result < 2; result++)
            {
                if (result > 0) Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                var number = reader.GetValue(0);
                Assert.Equal((decimal)bits, number is OceanBaseDecimal exact ? exact.ToDecimal() : Convert.ToDecimal(number, CultureInfo.InvariantCulture));
                DatabaseContext.EqualNumber(reader.GetValue(1), year, "Numeric YEAR expression stays exact."); Assert.True(reader.IsDBNull(2));
                Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            }
            Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        }
    }

    [IntegrationTheory]
    [MemberData(nameof(MySqlRows))]
    public async Task MySqlBitYearInvalidInputsRejectBeforeAnyScriptSideEffects(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true); await Open(connection, asynchronous);
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, b BIT(64), y YEAR", CancellationToken.None);
        using var command = db.Command(connection, "INSERT INTO " + table + " VALUES (1,1,2026); INSERT INTO " + table + " VALUES (2,?,?)");
        var bits = DatabaseContext.Parameter(command, "", 2UL); bits.OceanBaseDbType = OceanBaseDbType.Bit; bits.Size = 1;
        var year = DatabaseContext.Parameter(command, "", (ushort)2026); year.OceanBaseDbType = OceanBaseDbType.Year; year.Precision = 4;
        Func<Task> execute = () => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery());
        await Assert.ThrowsAsync<ArgumentException>(execute);
        Assert.Equal(ConnectionState.Open, connection.State); Assert.Equal(0, await db.CountAsync(connection, table, CancellationToken.None));
        bits.Value = ulong.MaxValue; bits.Size = 64; year.Value = (ushort)1900;
        await Assert.ThrowsAsync<ArgumentException>(execute);
        Assert.Equal(ConnectionState.Open, connection.State); Assert.Equal(0, await db.CountAsync(connection, table, CancellationToken.None));
        bits.Value = DBNull.Value; bits.Size = 65; year.Value = DBNull.Value;
        await Assert.ThrowsAsync<NotSupportedException>(execute);
        bits.Size = 64; year.Precision = 2;
        await Assert.ThrowsAsync<NotSupportedException>(execute);
        Assert.Equal(ConnectionState.Open, connection.State); Assert.Equal(0, await db.CountAsync(connection, table, CancellationToken.None));
        await using var verification = await db.OpenAsync(); Assert.Equal(0, await db.CountAsync(verification, table, CancellationToken.None));
    }

    [IntegrationTheory]
    [MemberData(nameof(MySqlRows))]
    public async Task MySqlBitYearTargetLimitKeepsConfirmedTransactionPrefix(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true); await Open(connection, asynchronous);
        await db.ExecuteAsync(connection, "SET sql_mode='STRICT_ALL_TABLES'", CancellationToken.None);
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, b BIT(1), y YEAR", CancellationToken.None);
        await using var transaction = (OceanBaseTransaction)await connection.BeginTransactionAsync();
        using var command = db.Command(connection, "INSERT INTO " + table + " VALUES (1,1,2026); INSERT INTO " + table + " VALUES (2,?,?); INSERT INTO " + table + " VALUES (3,1,2026)", transaction);
        var bits = DatabaseContext.Parameter(command, "", 2UL); bits.OceanBaseDbType = OceanBaseDbType.Bit; bits.Size = 64;
        DatabaseContext.Parameter(command, "", (ushort)2155).OceanBaseDbType = OceanBaseDbType.Year;
        var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
        Assert.Equal(1406, error.ServerErrorCode); Assert.Equal("22001", error.SqlState); Assert.False(error.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(1, await db.CountAsync(connection, table, CancellationToken.None, transaction));
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, "SELECT id FROM " + table, CancellationToken.None, transaction), 1, "Only confirmed prefix executed.");
        await using var verification = await db.OpenAsync(); Assert.Equal(0, await db.CountAsync(verification, table, CancellationToken.None));
        if (asynchronous) await transaction.RollbackAsync(); else transaction.Rollback();
        Assert.Equal(0, await db.CountAsync(connection, table, CancellationToken.None)); Assert.Equal(0, await db.CountAsync(verification, table, CancellationToken.None));
    }
}
