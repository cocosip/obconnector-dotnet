using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using OceanBaseConnector.Internal;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Oracle;

public sealed class UrowidLiteralTests
{
    public static IEnumerable<object[]> Cases() =>
        from profile in IntegrationEnvironment.Profiles.Where(p => p.Oracle && p.CreateObjects && p.HasTag("direct"))
        from asynchronous in new[] { false, true }
        select new object[] { profile.Name, asynchronous };

    [IntegrationTheory]
    [MemberData(nameof(Cases))]
    public async Task UrowidTextConversionPreservesValuesAndIdentifiesEmptyBoundary(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var source = await db.TableAsync(connection, "id VARCHAR2(1000) PRIMARY KEY", CancellationToken.None);
        await db.ExecuteAsync(connection, "INSERT INTO " + source + " VALUES('a')", CancellationToken.None);
        await db.ExecuteAsync(connection, "INSERT INTO " + source + " VALUES(RPAD('z',900,'z'))", CancellationToken.None);
        var rowids = new List<object?> { null, DBNull.Value };
        using (var query = db.Command(connection, "SELECT ROWID FROM " + source + " ORDER BY id"))
        using (var reader = await query.ExecuteReaderAsync())
            while (await reader.ReadAsync()) rowids.Add(reader.GetString(0));
        Assert.True(((string)rowids[3]!).Length > 251);
        using var native = db.Command(connection, "SELECT :p FROM DUAL");
        using var cast = db.Command(connection, "SELECT CHARTOROWID(:p) FROM DUAL");
        var nativeValue = DatabaseContext.Parameter(native, "p", null); nativeValue.OceanBaseDbType = OceanBaseDbType.Urowid;
        var textValue = DatabaseContext.Parameter(cast, "p", null, DbType.String);
        foreach (var value in rowids)
        {
            nativeValue.Value = textValue.Value = value;
            var expected = asynchronous ? await native.ExecuteScalarAsync() : native.ExecuteScalar();
            try { Assert.Equal(expected, asynchronous ? await cast.ExecuteScalarAsync() : cast.ExecuteScalar()); }
            catch (OceanBaseException error) { Assert.Fail("CHARTOROWID rejected case " + rowids.IndexOf(value) + ": " + error.ServerErrorCode + "/" + error.SqlState); }
        }
        foreach (var value in new[] { "invalid!", "A'); DELETE FROM " + source + "; --" })
        {
            nativeValue.Value = textValue.Value = value;
            var expected = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? native.ExecuteScalarAsync() : Task.FromResult(native.ExecuteScalar()));
            var actual = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? cast.ExecuteScalarAsync() : Task.FromResult(cast.ExecuteScalar()));
            Assert.Equal(expected.ServerErrorCode, actual.ServerErrorCode); Assert.False(actual.IsExecutionOutcomeUnknown);
        }
        nativeValue.Value = textValue.Value = "";
        var emptyError = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? native.ExecuteScalarAsync() : Task.FromResult(native.ExecuteScalar()));
        Assert.False(emptyError.IsExecutionOutcomeUnknown);
        Assert.Equal(DBNull.Value, asynchronous ? await cast.ExecuteScalarAsync() : cast.ExecuteScalar());
        Assert.Equal(2, await db.CountAsync(connection, source, CancellationToken.None));
    }

    public static IEnumerable<object[]> TextInputCases() =>
        from item in Cases()
        from fallback in new[] { false, true }
        select new object[] { item[0], item[1], fallback };

    [IntegrationTheory]
    [MemberData(nameof(TextInputCases))]
    public async Task UrowidClientTextStoresValuesAndRecoversAfterBadInput(string profileName, bool asynchronous, bool fallback)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var observer = await db.OpenAsync();
        var source = await db.TableAsync(observer, "id VARCHAR2(1000) PRIMARY KEY", CancellationToken.None);
        var table = await db.TableAsync(observer, "id INTEGER PRIMARY KEY,u UROWID(4000)", CancellationToken.None);
        await db.ExecuteAsync(observer, "INSERT INTO " + source + " VALUES('a')", CancellationToken.None);
        await db.ExecuteAsync(observer, "INSERT INTO " + source + " VALUES(RPAD('z',900,'z'))", CancellationToken.None);
        var rowids = new List<string>();
        using (var query = db.Command(observer, "SELECT ROWID FROM " + source + " ORDER BY id"))
        using (var rows = await query.ExecuteReaderAsync())
            while (await rows.ReadAsync()) rowids.Add(rows.GetString(0));
        Assert.True(rowids[1].Length > 251);
        var sql = "INSERT INTO " + table + " VALUES (:id,:p)";
        var builder = db.Profile.CreateBuilder(); builder.Pooling = false; builder.AllowMultiStatements = !fallback;
        var endpoint = OceanBaseEndpoint.Parse(builder.Server, builder.Port).Single();
        await using var relay = fallback ? new PrepareRefusalRelay(endpoint.Host, endpoint.Port, "INSERT INTO " + table + " VALUES (?,?)") : null;
        if (fallback)
        {
            Assert.Equal(OceanBaseTlsMode.Disabled, builder.TlsMode); Assert.False(builder.UseCompression); Assert.False(builder.UseOb20);
            builder.Server = "127.0.0.1"; builder.Port = relay!.Port;
        }
        else sql += "; INSERT INTO " + table + " VALUES (:id+100,:p)";
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        if (asynchronous) await connection.OpenAsync(); else connection.Open();
        using var insert = db.Command(connection, sql); insert.AllowClientSidePrepareFallback = fallback;
        var id = DatabaseContext.Parameter(insert, "id", 0, DbType.Int32);
        var input = DatabaseContext.Parameter(insert, "p", null); input.OceanBaseDbType = OceanBaseDbType.Urowid; input.Size = 1;
        var session = connection.GetOpenSession();
        var values = new object?[] { null, rowids[0], DBNull.Value, rowids[1] };
        for (var i = 0; i < values.Length; i++)
        {
            id.Value = i; input.Value = values[i]; relay?.Arm();
            Assert.Equal(fallback ? 1 : 2, asynchronous ? await insert.ExecuteNonQueryAsync() : insert.ExecuteNonQuery());
            Assert.Same(session, connection.GetOpenSession());
        }
        id.Value = values.Length;
        foreach (var invalid in new[] { "invalid!", "A'); DELETE FROM " + table + "; --" })
        {
            input.Value = invalid; relay?.Arm();
            var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? insert.ExecuteNonQueryAsync() : Task.FromResult(insert.ExecuteNonQuery()));
            Assert.NotEqual(0, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown);
            Assert.Equal(fallback ? values.Length : 2 * values.Length, await db.CountAsync(observer, table, CancellationToken.None));
            Assert.Same(session, connection.GetOpenSession());
        }
        input.Value = ""; relay?.Arm();
        await Assert.ThrowsAsync<ArgumentException>(() => asynchronous ? insert.ExecuteNonQueryAsync() : Task.FromResult(insert.ExecuteNonQuery()));
        Assert.Equal(fallback ? values.Length : 2 * values.Length, await db.CountAsync(observer, table, CancellationToken.None));
        input.Value = rowids[0]; relay?.Arm();
        Assert.Equal(fallback ? 1 : 2, asynchronous ? await insert.ExecuteNonQueryAsync() : insert.ExecuteNonQuery());
        Assert.Same(session, connection.GetOpenSession());
        if (relay != null) { Assert.Equal(8, relay.Refusals); Assert.Equal(8, relay.PrepareRequests); Assert.Equal(0, relay.ForwardedPrepares); }
        using var verify = db.Command(observer, "SELECT id,u FROM " + table + " ORDER BY id");
        if (asynchronous) await verify.PrepareAsync(); else verify.Prepare();
        using var reader = asynchronous ? await verify.ExecuteReaderAsync(CommandBehavior.SequentialAccess) : verify.ExecuteReader(CommandBehavior.SequentialAccess);
        Assert.Equal("ROWID", reader.GetDataTypeName(1));
        for (var copy = 0; copy < (fallback ? 1 : 2); copy++)
            for (var i = 0; i <= values.Length; i++)
            {
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                DatabaseContext.EqualNumber(reader.GetValue(0), copy * 100 + i, "Text input must execute once per statement.");
                if (i == 0 || i == 2) Assert.True(reader.IsDBNull(1));
                else Assert.Equal(i == 3 ? rowids[1] : rowids[0], reader.GetString(1));
            }
        Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
        Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
    }
}
