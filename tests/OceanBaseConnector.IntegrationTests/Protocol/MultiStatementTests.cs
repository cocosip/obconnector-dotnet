using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Protocol;

public sealed partial class MultiStatementTests
{
    public static IEnumerable<object[]> Rows() =>
        from profile in IntegrationEnvironment.Profiles
        where profile.CreateObjects
        from asynchronous in new[] { false, true }
        select new object[] { profile.Name, asynchronous };

    public static IEnumerable<object[]> ResultRows() =>
        from row in Rows()
        from disposeEarly in new[] { false, true }
        select row.Concat(new object[] { disposeEarly }).ToArray();

    public static IEnumerable<object[]> OracleRows() =>
        Rows().Where(row => IntegrationEnvironment.GetProfile((string)row[0]).Oracle);

    public static IEnumerable<object[]> MySqlRows() =>
        Rows().Where(row => !IntegrationEnvironment.GetProfile((string)row[0]).Oracle);

    public static IEnumerable<object[]> OracleParameterRows() =>
        from row in OracleRows()
        from sequential in new[] { false, true }
        select row.Concat(new object[] { sequential }).ToArray();

    public static IEnumerable<object[]> OracleIntervalRows() =>
        from row in OracleParameterRows()
        from sample in new[] { 0, 1, 2 }
        select row.Concat(new object[] { sample }).ToArray();

    public static IEnumerable<object[]> OracleZonedRows() =>
        from row in OracleParameterRows()
        from sample in new[] { 0, 1, 2, 3, 4 }
        select row.Concat(new object[] { sample }).ToArray();

    public static IEnumerable<object[]> MySqlParameterRows() =>
        from row in MySqlRows()
        from collation in new[] { "utf8mb4_general_ci", "utf8mb4_bin", "utf8mb4_unicode_ci" }
        from noBackslash in new[] { false, true }
        from sequential in new[] { false, true }
        select row.Concat(new object[] { collation, noBackslash, sequential }).ToArray();

    public static IEnumerable<object[]> MySqlCharsetRows() =>
        from row in MySqlRows()
        from sample in new[]
        {
            new[] { "ascii", "ABC", "414243" },
            new[] { "utf8mb3", "中", "E4B8AD" },
            new[] { "latin1", "é€", "E980" },
            new[] { "dec8", "¤ŒŸ", "A8D7DD" },
            new[] { "tis620", "กฺ฿", "A1DADF" },
            new[] { "gbk", "乗", "815C" },
            new[] { "gb18030", "乗😀", "815C9439FC36" },
            new[] { "gb18030_2022", "乗龴", "815CFE59" },
            new[] { "sjis", "ソ", "835C" },
            new[] { "big5", "功", "A55C" },
            new[] { "hkscs", "功\uf308", "A55C8845" },
            new[] { "hkscs31", "功\U0002010c", "A55C8845" },
        }
        select row.Concat(sample.Cast<object>()).ToArray();

    [IntegrationTheory]
    [MemberData(nameof(OracleZonedRows))]
    public async Task OracleZonedParametersPreserveStorageAcrossScriptTimeZoneChanges(string profileName, bool asynchronous, bool sequential, int sample)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true); await Open(connection, asynchronous);
        await db.ExecuteAsync(connection, "ALTER SESSION SET TIME_ZONE='+05:45'", CancellationToken.None);
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, z TIMESTAMP(9) WITH TIME ZONE, l TIMESTAMP(9) WITH LOCAL TIME ZONE", CancellationToken.None);
        var timestamp = new OceanBaseTimestamp(new DateTime(2026, 1, 2, 3, 4, 5), sample == 4 ? 0 : sample == 1 ? 123000000 : sample == 2 ? 123456700 : 123456789, sample == 4 ? (byte)0 : (byte)9);
        var offset = TimeSpan.FromMinutes(sample == 0 ? 345 : sample == 1 ? -30 : sample == 2 ? 840 : -840);
        object zoned = sample == 2 ? new DateTimeOffset(timestamp.ToDateTime(), offset) :
            new OceanBaseTimestampWithTimeZone(timestamp, offset, sample == 3 ? "+00:00" : null, sample == 3 ? "metadata" : null);
        using var command = db.Command(connection, "INSERT INTO " + table + " VALUES (1,:z,:l); ALTER SESSION SET TIME_ZONE='-03:30'; INSERT INTO " + table + " VALUES (2,:z,:l); INSERT INTO " + table + " VALUES (3,:nullz,:nulll)");
        var zoneParameter = DatabaseContext.Parameter(command, "z", zoned); if (sample == 1) zoneParameter.Scale = 3;
        var local = DatabaseContext.Parameter(command, "l", new OceanBaseTimestampLocalTimeZone(timestamp, "+05:45"));
        local.OceanBaseDbType = OceanBaseDbType.TimestampLocalTimeZone; if (sample == 1) local.Scale = 3;
        DatabaseContext.Parameter(command, "nullz", DBNull.Value).OceanBaseDbType = OceanBaseDbType.TimestampWithTimeZone;
        DatabaseContext.Parameter(command, "nulll", DBNull.Value).OceanBaseDbType = OceanBaseDbType.TimestampLocalTimeZone;
        Assert.Equal(3, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
        Assert.Equal("-03:30", await db.ScalarAsync(connection, "SELECT SESSIONTIMEZONE FROM DUAL", CancellationToken.None));
        await db.ExecuteAsync(connection, "ALTER SESSION SET TIME_ZONE='+05:45'", CancellationToken.None);
        command.Parameters.Clear(); command.CommandText = "SELECT z,l FROM " + table + " WHERE id IN (1,2) ORDER BY id; SELECT z,l FROM " + table + " WHERE id=3";
        using (var reader = asynchronous ? await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default) : command.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default))
        {
            for (var i = 0; i < 2; i++)
            {
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                var actualZoned = reader.GetFieldValue<OceanBaseTimestampWithTimeZone>(0);
                Assert.Equal(timestamp.WholeSeconds, actualZoned.Timestamp.WholeSeconds); Assert.Equal(timestamp.Nanosecond, actualZoned.Timestamp.Nanosecond);
                Assert.Equal(sample == 3 ? TimeSpan.Zero : offset, actualZoned.Offset);
                var actualLocal = reader.GetFieldValue<OceanBaseTimestampLocalTimeZone>(1);
                Assert.Equal(timestamp.WholeSeconds, actualLocal.Timestamp.WholeSeconds); Assert.Equal(timestamp.Nanosecond, actualLocal.Timestamp.Nanosecond);
                Assert.Equal("+05:45", actualLocal.SessionTimeZone);
            }
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.True(reader.IsDBNull(0)); Assert.True(reader.IsDBNull(1));
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        }
        Assert.Equal(3, await db.CountAsync(connection, table, CancellationToken.None));
    }

    [IntegrationTheory]
    [MemberData(nameof(OracleRows))]
    public async Task OracleZonedMultiStatementsRejectMismatchedLocalZoneBeforeSideEffects(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true); await Open(connection, asynchronous);
        await db.ExecuteAsync(connection, "ALTER SESSION SET TIME_ZONE='+05:45'", CancellationToken.None);
        var table = await db.TableAsync(connection, "value TIMESTAMP(9) WITH LOCAL TIME ZONE", CancellationToken.None);
        using var command = db.Command(connection, "INSERT INTO " + table + " VALUES (:p); INSERT INTO " + table + " VALUES (:p)");
        DatabaseContext.Parameter(command, "p", new OceanBaseTimestampLocalTimeZone(new OceanBaseTimestamp(new DateTime(2026, 1, 2), 123456789), "+00:00"));
        if (asynchronous) await Assert.ThrowsAsync<ArgumentException>(() => command.ExecuteNonQueryAsync());
        else Assert.Throws<ArgumentException>(() => command.ExecuteNonQuery());
        Assert.Equal(ConnectionState.Open, connection.State); Assert.Equal(0, await db.CountAsync(connection, table, CancellationToken.None));
    }

    [IntegrationTheory]
    [MemberData(nameof(OracleIntervalRows))]
    public async Task OracleIntervalParametersPreserveStoredValuesAndMultipleResults(string profileName, bool asynchronous, bool sequential, int sample)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true);
        await Open(connection, asynchronous);
        var table = await db.TableAsync(connection,
            "id INTEGER PRIMARY KEY, ym INTERVAL YEAR(9) TO MONTH, ds INTERVAL DAY(9) TO SECOND(9)", CancellationToken.None);
        var session = connection.GetOpenSession();
        using var command = db.Command(connection, "INSERT INTO " + table + " VALUES (1, :ym, :ds); INSERT INTO " + table + " VALUES (2, :empty, :empty)");
        var ymValue = sample == 2 ? default : new OceanBaseIntervalYearToMonth(sample == 0, 12, 3);
        var dsValue = sample == 2 ? default : new OceanBaseIntervalDayToSecond(sample == 1, 4, 5, 6, 7, sample == 0 ? 123456789 : 123000000, 3);
        var ym = DatabaseContext.Parameter(command, "ym", ymValue); if (sample != 2) ym.Precision = 3;
        var ds = DatabaseContext.Parameter(command, "ds", dsValue); if (sample == 1) ds.Scale = 3;
        ds.OceanBaseDbType = OceanBaseDbType.IntervalDayToSecond;
        var empty = DatabaseContext.Parameter(command, "empty", DBNull.Value); empty.OceanBaseDbType = OceanBaseDbType.IntervalYearToMonth;
        Assert.Equal(2, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
        command.Parameters.Clear(); command.CommandText = "SELECT ym, ds FROM " + table + " WHERE id=1; SELECT ym, ds FROM " + table + " WHERE id=2";
        var behavior = sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default;
        using (var reader = asynchronous ? await command.ExecuteReaderAsync(behavior) : command.ExecuteReader(behavior))
        {
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            var yearMonth = reader.GetFieldValue<OceanBaseIntervalYearToMonth>(0);
            Assert.Equal(ymValue.IsNegative, yearMonth.IsNegative); Assert.Equal(ymValue.Years, yearMonth.Years); Assert.Equal(ymValue.Months, yearMonth.Months);
            var daySecond = reader.GetFieldValue<OceanBaseIntervalDayToSecond>(1);
            Assert.Equal(dsValue.IsNegative, daySecond.IsNegative); Assert.Equal(dsValue.Days, daySecond.Days); Assert.Equal(dsValue.Hours, daySecond.Hours);
            Assert.Equal(dsValue.Minutes, daySecond.Minutes); Assert.Equal(dsValue.Seconds, daySecond.Seconds); Assert.Equal(dsValue.Nanosecond, daySecond.Nanosecond);
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.True(reader.IsDBNull(0)); Assert.True(reader.IsDBNull(1));
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        }
        Assert.Same(session, connection.GetOpenSession());
        Assert.Equal(2, await db.CountAsync(connection, table, CancellationToken.None));
    }

    [IntegrationTheory]
    [MemberData(nameof(MySqlCharsetRows))]
    public async Task ConfiguredMySqlCharsetPreservesMultiStatementStringBytes(string profileName, bool asynchronous,
        string charset, string sample, string hex)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var builder = db.Profile.CreateBuilder(); builder.Pooling = false; builder.AllowMultiStatements = true; builder.CharacterSet = charset;
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        await Open(connection, asynchronous);
        await db.ExecuteAsync(connection, "SET SESSION sql_mode='NO_BACKSLASH_ESCAPES'", CancellationToken.None);
        var collation = await db.ScalarAsync(connection, "SELECT @@collation_connection", CancellationToken.None);
        using var command = db.Command(connection, "SELECT ?, HEX(?), COLLATION(?), COERCIBILITY(?); SELECT ?");
        var value = sample + "'\\\0;?";
        for (var i = 0; i < 5; i++) DatabaseContext.Parameter(command, "s" + i, value);
        using var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader();
        Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
        Assert.Equal(value, reader.GetString(0)); Assert.Equal(hex + "275C003B3F", reader.GetString(1));
        Assert.Equal(collation, reader.GetString(2)); DatabaseContext.EqualNumber(reader.GetValue(3), 4, "Configured literal coercibility.");
        Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
        Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.Equal(value, reader.GetString(0));
        Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
        Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
    }

    [IntegrationTheory]
    [MemberData(nameof(MySqlParameterRows))]
    public async Task MySqlScalarParametersPreserveValuesCollationAndCoercibilityAcrossSqlModeChanges(
        string profileName, bool asynchronous, string collation, bool noBackslash, bool sequential)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true);
        await Open(connection, asynchronous);
        await db.ExecuteAsync(connection, "SET NAMES utf8mb4 COLLATE " + collation, CancellationToken.None);
        await db.ExecuteAsync(connection, "SET SESSION sql_mode='" + (noBackslash ? "NO_BACKSLASH_ESCAPES" : "") + "'", CancellationToken.None);
        var session = connection.GetOpenSession();
        using var command = db.Command(connection,
            "SET SESSION sql_mode='" + (noBackslash ? "" : "ANSI_QUOTES,NO_BACKSLASH_ESCAPES") +
            "'; SELECT ?, HEX(?), COLLATION(?), COERCIBILITY(?), ?; SELECT ?, ?");
        const string text = "quote'\\\0中😀; ? --";
        for (var i = 0; i < 4; i++) DatabaseContext.Parameter(command, "s" + i, text);
        DatabaseContext.Parameter(command, "n", 7);
        var bytes = new byte[] { 0, 39, 92, 255 }; DatabaseContext.Parameter(command, "b", bytes);
        DatabaseContext.Parameter(command, "empty", DBNull.Value);
        for (var execution = 0; execution < 2; execution++)
        {
            var value = text + execution;
            for (var i = 0; i < 4; i++) ((OceanBaseParameter)command.Parameters[i]).Value = value;
            var behavior = sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default;
            using var reader = asynchronous ? await command.ExecuteReaderAsync(behavior) : command.ExecuteReader(behavior);
            Assert.Equal(0, reader.FieldCount);
            Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.Equal(value, reader.GetString(0));
            Assert.Equal(Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(value)), reader.GetString(1));
            Assert.Equal(collation, reader.GetString(2));
            DatabaseContext.EqualNumber(reader.GetValue(3), 4, "Ordinary literal coercibility.");
            DatabaseContext.EqualNumber(reader.GetValue(4), 7, "Scalar number.");
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.Equal(bytes, reader.GetFieldValue<byte[]>(0)); Assert.True(reader.IsDBNull(1));
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        }
        Assert.Same(session, connection.GetOpenSession());
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1, "Connection reuse.");
    }

    [IntegrationTheory]
    [MemberData(nameof(MySqlRows))]
    public async Task StringBoundCharsetChangesRejectBeforeScriptSideEffects(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true);
        await Open(connection, asynchronous);
        var table = await db.TableAsync(connection, "v VARCHAR(100)", CancellationToken.None);
        using var command = db.Command(connection, "INSERT INTO " + table + " VALUES (?); SET NAMES latin1");
        DatabaseContext.Parameter(command, "s", "中");
        await Assert.ThrowsAsync<NotSupportedException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
        Assert.Equal(0, await db.CountAsync(connection, table, CancellationToken.None));
    }

    [IntegrationTheory]
    [MemberData(nameof(OracleParameterRows))]
    public async Task OracleScalarParametersPreserveValuesAcrossMultipleResults(string profileName, bool asynchronous, bool sequential)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true);
        await Open(connection, asynchronous);
        var session = connection.GetOpenSession();
        using var command = db.Command(connection,
            "SELECT :n, :s, :b, :empty FROM DUAL; SELECT :s, :N FROM DUAL");
        var number = DatabaseContext.Parameter(command, "n", 7);
        var text = DatabaseContext.Parameter(command, "s", "quote'\\中文; :n --");
        var bytes = new byte[] { 0, 39, 92, 255 };
        DatabaseContext.Parameter(command, "b", bytes);
        DatabaseContext.Parameter(command, "empty", DBNull.Value);
        for (var value = 7; value <= 8; value++)
        {
            number.Value = value;
            text.Value = "quote'\\中文; :n --" + value;
            var behavior = sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default;
            using var reader = asynchronous ? await command.ExecuteReaderAsync(behavior) : command.ExecuteReader(behavior);
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            DatabaseContext.EqualNumber(reader.GetValue(0), value, "First result number.");
            Assert.Equal(text.Value, reader.GetString(1));
            Assert.Equal(bytes, reader.GetFieldValue<byte[]>(2));
            Assert.True(reader.IsDBNull(3));
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.Equal(text.Value, reader.GetString(0));
            DatabaseContext.EqualNumber(reader.GetValue(1), value, "Repeated binding in second result.");
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        }
        Assert.Same(session, connection.GetOpenSession());
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1, "Connection reuse.");
    }

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task ParameterizedMiddleErrorPreservesTransactionAndStopsSuffix(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true);
        await Open(connection, asynchronous);
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY", CancellationToken.None);
        var session = connection.GetOpenSession();
        using var transaction = asynchronous ? await connection.BeginTransactionAsync() : connection.BeginTransaction();
        using var command = db.Command(connection, db.ParameterSql("INSERT INTO " + table + " VALUES (:a); INSERT INTO " + table +
            " VALUES (:b); INSERT INTO " + table + " VALUES (:c)"), (OceanBaseTransaction)transaction);
        DatabaseContext.Parameter(command, "a", 1); DatabaseContext.Parameter(command, "b", 1); DatabaseContext.Parameter(command, "c", 2);
        var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
        Assert.NotEqual(0, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown);
        Assert.Equal(1, await db.CountAsync(connection, table, CancellationToken.None, (OceanBaseTransaction)transaction));
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, "SELECT SUM(id) FROM " + table, CancellationToken.None,
            (OceanBaseTransaction)transaction), 1, "Only the confirmed prefix executes.");
        await using var observer = await db.OpenAsync();
        Assert.Equal(0, await db.CountAsync(observer, table, CancellationToken.None));
        if (asynchronous) await transaction.RollbackAsync(); else transaction.Rollback();
        Assert.Equal(0, await db.CountAsync(connection, table, CancellationToken.None));
        Assert.Same(session, connection.GetOpenSession());
    }

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task ParameterizedMultiStatementsRequireOptInBeforeSideEffects(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: false);
        await Open(connection, asynchronous);
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY", CancellationToken.None);
        using var command = db.Command(connection, db.ParameterSql("INSERT INTO " + table + " VALUES (:id); INSERT INTO " + table + " VALUES (:id)"));
        DatabaseContext.Parameter(command, "id", 1);
        if (!db.Oracle) DatabaseContext.Parameter(command, "id2", 1);
        await Assert.ThrowsAsync<NotSupportedException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
        Assert.Equal(0, await db.CountAsync(connection, table, CancellationToken.None));
    }

    [IntegrationTheory]
    [MemberData(nameof(ResultRows))]
    public async Task ResultsAreConsumedOrDiscardedBeforeConnectionReuse(string profileName, bool asynchronous, bool disposeEarly)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true);
        await Open(connection, asynchronous);
        var session = connection.GetOpenSession();
        using (var command = db.Command(connection, db.SelectOne + "; " + db.ScalarSql("2")))
        {
            var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader();
            try
            {
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                DatabaseContext.EqualNumber(reader.GetValue(0), 1, "First statement result.");
                if (!disposeEarly)
                {
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                    Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    DatabaseContext.EqualNumber(reader.GetValue(0), 2, "Second statement result.");
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                    Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                }
            }
            finally { if (asynchronous) await reader.DisposeAsync(); else reader.Dispose(); }
        }
        if (disposeEarly)
        {
            Assert.Equal(ConnectionState.Broken, connection.State);
            if (asynchronous) await connection.CloseAsync(); else connection.Close();
            await Open(connection, asynchronous);
            Assert.NotSame(session, connection.GetOpenSession());
        }
        else Assert.Same(session, connection.GetOpenSession());
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1,
            "A completed response or fresh connection must support subsequent commands.");
    }

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task MiddleServerErrorStopsLaterStatementsAndKeepsConnectionUsable(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true);
        await Open(connection, asynchronous);
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY", CancellationToken.None);
        var missing = "OBI_" + Guid.NewGuid().ToString("N").Substring(0, 20).ToUpperInvariant();
        var session = connection.GetOpenSession();
        using var command = db.Command(connection, "INSERT INTO " + table + " VALUES (1); SELECT id FROM " + missing +
            "; INSERT INTO " + table + " VALUES (2)");
        var error = await Assert.ThrowsAsync<OceanBaseException>(async () =>
        {
            if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
        });
        Assert.NotEqual(0, error.ServerErrorCode);
        Assert.False(error.IsExecutionOutcomeUnknown);
        Assert.Same(session, connection.GetOpenSession());
        await using var observer = await db.OpenAsync();
        Assert.Equal(1, await db.CountAsync(observer, table, CancellationToken.None));
        DatabaseContext.EqualNumber(await db.ScalarAsync(observer, "SELECT SUM(id) FROM " + table, CancellationToken.None), 1,
            "The statement after the error must not execute.");
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1,
            "A confirmed middle error must leave the connection reusable.");
    }

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task DisabledMultiStatementsRejectBeforeSideEffects(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: false);
        await Open(connection, asynchronous);
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY", CancellationToken.None);
        using var command = db.Command(connection, "INSERT INTO " + table + " VALUES (1); INSERT INTO " + table + " VALUES (2)");
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
        });
        Assert.Equal(0, await db.CountAsync(connection, table, CancellationToken.None));
    }

    private static OceanBaseConnection CreateConnection(DatabaseContext db, bool enabled)
    {
        var builder = db.Profile.CreateBuilder(); builder.Pooling = false; builder.AllowMultiStatements = enabled;
        return new OceanBaseConnection(builder.ConnectionString);
    }

    private static async Task Open(OceanBaseConnection connection, bool asynchronous)
    {
        if (asynchronous) await connection.OpenAsync(); else connection.Open();
    }
}
