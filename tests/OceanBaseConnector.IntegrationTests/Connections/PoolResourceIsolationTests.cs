using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace OceanBaseConnector.IntegrationTests.Connections;

public sealed class PoolResourceIsolationTests
{
    private readonly ITestOutputHelper _output;
    public PoolResourceIsolationTests(ITestOutputHelper output) => _output = output;

    public static IEnumerable<object[]> LobPhases() =>
        from profile in IntegrationEnvironment.Profiles
        where profile.Oracle && profile.CreateObjects && profile.CreateBuilder().UseOraclePrepareExecute && profile.CreateBuilder().UseLobLocators
        from character in new[] { false, true }
        from asynchronous in new[] { false, true }
        from phase in new[] { "handle", "open", "partial", "complete", "disposed" }
        select new object[] { profile.Name, character, asynchronous, phase };

    public static IEnumerable<object[]> CursorPhases() =>
        from profile in IntegrationEnvironment.Profiles
        where profile.Oracle && profile.CreateBuilder().UseOraclePrepareExecute
        from asynchronous in new[] { false, true }
        from phase in new[] { "unopened", "partial", "complete", "disposed" }
        select new object[] { profile.Name, asynchronous, phase };

    [IntegrationTheory]
    [MemberData(nameof(LobPhases))]
    public async Task PersistentLobResourcesControlSameEndpointPoolReuse(string profileName, bool character, bool asynchronous, string phase)
    {
        var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        OceanBaseConnection? setup = null; OceanBaseDataSource? source = null; OceanBaseConnection? connection = null;
        OceanBaseLob? lob = null; Stream? stream = null; TextReader? text = null;
        Exception? failure = null;
        try
        {
            var builder = db.Profile.CreateBuilder(); builder.Pooling = false;
            setup = new OceanBaseConnection(builder.ConnectionString); await setup.OpenAsync();
            var table = await db.TableAsync(setup, "id INTEGER PRIMARY KEY, value " + (character ? "CLOB" : "BLOB"), CancellationToken.None);
            await db.ExecuteAsync(setup, "INSERT INTO " + table + " VALUES (1, " +
                (character ? "TO_CLOB('patient-data')" : "HEXTORAW('70617469656E742D64617461')") + ")", CancellationToken.None);
            source = Source(db); connection = (OceanBaseConnection)source.CreateConnection();
            await Open(connection, asynchronous);
            var session = connection.GetOpenSession(); var generation = session.Generation;
            lob = Assert.IsType<OceanBaseLob>(await Scalar(db, connection, "SELECT value FROM " + table + " WHERE id=1", asynchronous));
            Assert.Equal(character, lob.IsCharacter); Assert.Equal(Encoding.UTF8.GetByteCount("patient-data"), lob.ByteLength);
            _output.WriteLine("Persistent locator: inline={0}, phase={1}", lob.Locator.HasInlineData, phase);
            if (phase != "handle")
            {
                if (character) text = lob.OpenTextReader(); else stream = lob.OpenRead();
                if (phase == "partial")
                {
                    if (character)
                    {
                        var chars = new char[1];
                        Assert.Equal(1, asynchronous ? await text!.ReadAsync(chars, 0, 1) : text!.Read(chars, 0, 1));
                        Assert.Equal('p', chars[0]);
                    }
                    else
                    {
                        var bytes = new byte[1];
                        Assert.Equal(1, asynchronous ? await stream!.ReadAsync(bytes, 0, 1) : stream!.Read(bytes, 0, 1));
                        Assert.Equal((byte)'p', bytes[0]);
                    }
                }
                if (phase == "complete" || phase == "disposed")
                {
                    if (character) Assert.Equal("patient-data", asynchronous ? await text!.ReadToEndAsync() : text!.ReadToEnd());
                    else
                    {
                        using var contents = new MemoryStream();
                        if (asynchronous) await stream!.CopyToAsync(contents); else stream!.CopyTo(contents);
                        Assert.Equal(Encoding.UTF8.GetBytes("patient-data"), contents.ToArray());
                    }
                }
            }
            var reuse = phase == "disposed";
            if (reuse) { if (asynchronous) await lob.DisposeAsync(); else lob.Dispose(); }
            Assert.Equal(!reuse, session.RequiresDiscardOnReturn);
            await Close(connection, asynchronous); await Open(connection, asynchronous);
            if (reuse) { Assert.Same(session, connection.GetOpenSession()); Assert.True(session.Generation > generation); }
            else Assert.NotSame(session, connection.GetOpenSession());
            Assert.ThrowsAny<InvalidOperationException>(() => { if (character) lob.OpenTextReader(); else lob.OpenRead(); });
            if (text != null) Assert.ThrowsAny<InvalidOperationException>(() => text.Read());
            if (stream != null) Assert.ThrowsAny<InvalidOperationException>(() => stream.Read(new byte[1], 0, 1));
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => lob.TruncateAsync(0));
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => character ? lob.WriteTextAsync(0, "x") : lob.WriteBytesAsync(0, new byte[] { 1 }, 0, 1));
            using (var binding = db.Command(connection, "SELECT :value FROM DUAL"))
            {
                DatabaseContext.Parameter(binding, "value", lob);
                if (asynchronous) await Assert.ThrowsAnyAsync<InvalidOperationException>(() => binding.ExecuteScalarAsync());
                else Assert.ThrowsAny<InvalidOperationException>(() => binding.ExecuteScalar());
            }
            text?.Dispose(); stream?.Dispose();
            if (asynchronous) await lob.DisposeAsync(); else lob.Dispose();
            DatabaseContext.EqualNumber(await Scalar(db, connection, db.SelectOne, asynchronous), 1, "Old LOB operations and disposal must not affect the new lease before clearing.");
            DatabaseContext.EqualNumber(await Scalar(db, setup, "SELECT COUNT(*) FROM " + table + " WHERE id=1 AND DBMS_LOB.GETLENGTH(value)=12", asynchronous), 1, "Rejected stale mutations must preserve the persistent value.");
            var persisted = await Scalar(db, setup, "SELECT DBMS_LOB.SUBSTR(value, 12, 1) FROM " + table + " WHERE id=1", asynchronous);
            if (character) Assert.Equal("patient-data", Assert.IsType<string>(persisted));
            else Assert.Equal(Encoding.UTF8.GetBytes("patient-data"), Assert.IsType<byte[]>(persisted));
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            await Cleanup(failure,
                () => { text?.Dispose(); return default; }, () => { stream?.Dispose(); return default; },
                () => lob?.DisposeAsync() ?? default, () => connection?.DisposeAsync() ?? default,
                () => source?.DisposeAsync() ?? default, () => setup?.DisposeAsync() ?? default, () => db.DisposeAsync());
        }
    }

    [IntegrationTheory]
    [MemberData(nameof(CursorPhases))]
    public async Task OracleOutputCursorRetirementProtectsTheNextSameEndpointBorrower(string profileName, bool asynchronous, string phase)
    {
        var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        OceanBaseDataSource? source = null; OceanBaseConnection? connection = null;
        OceanBaseCommand? command = null; OceanBaseRefCursor? cursor = null; OceanBaseDataReader? reader = null;
        Exception? failure = null;
        try
        {
            source = Source(db); connection = (OceanBaseConnection)source.CreateConnection();
            await Open(connection, asynchronous); var session = connection.GetOpenSession();
            command = db.Command(connection, "BEGIN OPEN :result FOR SELECT 1 AS n FROM DUAL UNION ALL SELECT 2 AS n FROM DUAL ORDER BY n; END;");
            var output = DatabaseContext.Parameter(command, "result", DBNull.Value);
            output.OceanBaseDbType = OceanBaseDbType.RefCursor; output.Direction = ParameterDirection.Output;
            if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
            cursor = Assert.IsType<OceanBaseRefCursor>(output.Value);
            if (asynchronous) await command.DisposeAsync(); else command.Dispose();
            if (phase == "partial" || phase == "complete")
            {
                reader = asynchronous ? await cursor.GetDataReaderAsync(2) : cursor.GetDataReader(2);
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                DatabaseContext.EqualNumber(reader.GetValue(0), 1, "The first cursor row must be real data.");
                if (phase == "complete")
                {
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    DatabaseContext.EqualNumber(reader.GetValue(0), 2, "The second cursor row must be real data.");
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                    Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                    if (asynchronous) await reader.DisposeAsync(); else reader.Dispose();
                }
            }
            if (phase == "disposed") { if (asynchronous) await cursor.DisposeAsync(); else cursor.Dispose(); }
            Assert.True(session.RequiresDiscardOnReturn); // Anonymous PL retires the session even after releasing its cursor.
            await Close(connection, asynchronous); await Open(connection, asynchronous);
            Assert.NotSame(session, connection.GetOpenSession());
            Assert.ThrowsAny<InvalidOperationException>(() => cursor.GetDataReader());
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => cursor.GetDataReaderAsync());
            if (reader != null) { if (asynchronous) await reader.DisposeAsync(); else reader.Dispose(); }
            if (asynchronous) await cursor.DisposeAsync(); else cursor.Dispose();
            DatabaseContext.EqualNumber(await Scalar(db, connection, db.SelectOne, asynchronous), 1, "Old cursor/reader disposal must not affect the new lease before clearing.");
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            await Cleanup(failure, () => reader?.DisposeAsync() ?? default, () => cursor?.DisposeAsync() ?? default,
                () => command?.DisposeAsync() ?? default, () => connection?.DisposeAsync() ?? default,
                () => source?.DisposeAsync() ?? default, () => db.DisposeAsync());
        }
    }

    private static OceanBaseDataSource Source(DatabaseContext db)
    {
        var builder = db.Profile.CreateBuilder();
        builder.Pooling = true; builder.MinPoolSize = 0; builder.MaxPoolSize = 1; builder.PoolWaitTimeout = 5;
        return new OceanBaseDataSourceBuilder(builder.ConnectionString).Build();
    }

    private static async Task Open(OceanBaseConnection connection, bool asynchronous)
    { if (asynchronous) await connection.OpenAsync(); else connection.Open(); }
    private static async Task Close(OceanBaseConnection connection, bool asynchronous)
    { if (asynchronous) await connection.CloseAsync(); else connection.Close(); }
    private static async Task<object?> Scalar(DatabaseContext db, OceanBaseConnection connection, string sql, bool asynchronous)
    {
        await using var command = db.Command(connection, sql);
        return asynchronous ? await command.ExecuteScalarAsync() : command.ExecuteScalar();
    }

    private static async Task Cleanup(Exception? failure, params Func<ValueTask>[] actions)
    {
        var errors = new List<Exception>();
        foreach (var action in actions) { try { await action(); } catch (Exception error) { errors.Add(error); } }
        if (errors.Count != 0) { if (failure != null) errors.Insert(0, failure); throw new AggregateException(errors); }
    }
}
