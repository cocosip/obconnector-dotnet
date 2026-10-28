using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Connections;

public sealed partial class PoolResourceIsolationTests
{
    public static IEnumerable<object[]> InterruptedExternalLobRows() =>
        from profile in IntegrationEnvironment.Profiles
        let builder = profile.CreateBuilder()
        where profile.Oracle && profile.CreateObjects && builder.UseOraclePrepareExecute && builder.UseLobLocators &&
            builder.TlsMode == OceanBaseTlsMode.Disabled && !builder.UseCompression && !builder.UseOb20 &&
            OceanBaseEndpoint.Parse(builder.Server, builder.Port).Length == 1
        from character in new[] { false, true }
        from asynchronous in new[] { false, true }
        from partial in new[] { false, true }
        select new object[] { profile.Name, character, asynchronous, partial };

    [IntegrationTheory]
    [MemberData(nameof(InterruptedExternalLobRows))]
    public async Task ExternalLobReadResponseLossProtectsTheNextLease(string profileName, bool character, bool asynchronous, bool partial)
    {
        var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        OceanBaseConnection? setup = null; OceanBaseDataSource? source = null; OceanBaseConnection? connection = null;
        TransactionResponseDropProxy? proxy = null;
        OceanBaseLob? seed = null; OceanBaseLob? oldLob = null; OceanBaseLob? newLob = null;
        Stream? oldStream = null; Stream? newStream = null; TextReader? oldText = null; TextReader? newText = null;
        Exception? failure = null;
        try
        {
            var expectedText = string.Concat(Enumerable.Repeat("患者😀z", 20000));
            var expected = character ? Encoding.UTF8.GetBytes(expectedText) : Enumerable.Range(0, 100000).Select(i => (byte)(i % 251)).ToArray();
            var builder = db.Profile.CreateBuilder(); builder.Pooling = false;
            setup = new OceanBaseConnection(builder.ConnectionString); await setup.OpenAsync(deadline.Token);
            var table = await db.TableAsync(setup, "id INTEGER PRIMARY KEY, value " + (character ? "CLOB" : "BLOB"), deadline.Token);
            seed = await setup.CreateTemporaryLobAsync(character ? OceanBaseDbType.Clob : OceanBaseDbType.Blob);
            if (character) await seed.WriteTextAsync(0, expectedText);
            else await seed.WriteBytesAsync(0, expected, 0, expected.Length);
            using (var insert = db.Command(setup, "INSERT INTO " + table + " VALUES (1, :value)"))
            {
                DatabaseContext.Parameter(insert, "value", seed);
                Assert.Equal(1, await insert.ExecuteNonQueryAsync(deadline.Token));
            }
            await seed.FreeTemporaryAsync(); await seed.DisposeAsync(); seed = null;
            var endpoint = OceanBaseEndpoint.Parse(builder.Server, builder.Port).Single();
            proxy = new TransactionResponseDropProxy(endpoint.Host, endpoint.Port, "BEGIN DBMS_LOB.READ(?, ?, ?, ?); END;",
                partial ? CommandDropPoint.PartialResponse : CommandDropPoint.WholeResponse);
            builder.Server = "127.0.0.1"; builder.Port = proxy.Port;
            builder.Pooling = true; builder.MinPoolSize = 0; builder.MaxPoolSize = 1; builder.PoolWaitTimeout = 2;
            source = new OceanBaseDataSourceBuilder(builder.ConnectionString).Build();
            connection = (OceanBaseConnection)source.CreateConnection(); await Open(connection, asynchronous);
            var original = connection.GetOpenSession();
            var select = "SELECT value FROM " + table + " WHERE id=1";
            oldLob = Assert.IsType<OceanBaseLob>(await Scalar(db, connection, select, asynchronous));
            Assert.False(oldLob.Locator.HasInlineData); Assert.Equal(expected.Length, oldLob.ByteLength);
            if (character) oldText = oldLob.OpenTextReader(); else oldStream = oldLob.OpenRead();
            proxy.Arm(); var buffer = new byte[1024]; var chars = new char[1024];
            var error = await Assert.ThrowsAsync<OceanBaseException>(async () =>
            {
                if (character)
                {
                    if (asynchronous) _ = await oldText!.ReadAsync(chars.AsMemory(), deadline.Token);
                    else _ = oldText!.Read(chars, 0, chars.Length);
                }
                else if (asynchronous) _ = await oldStream!.ReadAsync(buffer, 0, buffer.Length, deadline.Token);
                else _ = oldStream!.Read(buffer, 0, buffer.Length);
            });
            Assert.False(error.IsExecutionOutcomeUnknown);
            Assert.Equal(ConnectionState.Broken, connection.State);
            Assert.Equal(1, proxy.TargetRequests); Assert.Equal((byte)0, proxy.ObservedResponseMarker);
            await Close(connection, asynchronous); await Open(connection, asynchronous);
            Assert.NotSame(original, connection.GetOpenSession()); Assert.Equal(2, proxy.Connections);
            Assert.Equal(1, proxy.TargetRequests);
            if (character)
            {
                Assert.ThrowsAny<InvalidOperationException>(() => oldText!.Read());
                Assert.ThrowsAny<InvalidOperationException>(() => oldLob.OpenTextReader());
            }
            else
            {
                Assert.ThrowsAny<InvalidOperationException>(() => oldStream!.Read(buffer, 0, buffer.Length));
                Assert.ThrowsAny<InvalidOperationException>(() => oldLob.OpenRead());
            }
            newLob = Assert.IsType<OceanBaseLob>(await Scalar(db, connection, select, asynchronous));
            Assert.False(newLob.Locator.HasInlineData); Assert.Equal(expected.Length, newLob.ByteLength);
            if (character) newText = newLob.OpenTextReader(); else newStream = newLob.OpenRead();
            oldText?.Dispose();
            if (asynchronous) { if (oldStream != null) await oldStream.DisposeAsync(); await oldLob.DisposeAsync(); }
            else { oldStream?.Dispose(); oldLob.Dispose(); }
            if (character) Assert.Equal(expectedText, asynchronous ? await newText!.ReadToEndAsync(deadline.Token) : newText!.ReadToEnd());
            else
            {
                using var actual = new MemoryStream();
                if (asynchronous) await newStream!.CopyToAsync(actual, deadline.Token); else newStream!.CopyTo(actual);
                Assert.Equal(expected, actual.ToArray());
            }
            newText?.Dispose();
            if (asynchronous) { if (newStream != null) await newStream.DisposeAsync(); await newLob.DisposeAsync(); }
            else { newStream?.Dispose(); newLob.Dispose(); }
            DatabaseContext.EqualNumber(await Scalar(db, connection, db.SelectOne, asynchronous), 1, "Late old LOB disposal must preserve the new lease and its complete data.");
            Assert.Equal(2, proxy.Connections);
            source.Clear(); await Close(connection, asynchronous); await proxy.Completion.WaitAsync(deadline.Token);
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            await Cleanup(failure, () => { oldText?.Dispose(); return default; }, () => { newText?.Dispose(); return default; },
                () => oldStream?.DisposeAsync() ?? default, () => newStream?.DisposeAsync() ?? default,
                () => oldLob?.DisposeAsync() ?? default, () => newLob?.DisposeAsync() ?? default,
                () => seed?.DisposeAsync() ?? default, () => connection?.DisposeAsync() ?? default,
                () => source?.DisposeAsync() ?? default, () => proxy?.DisposeAsync() ?? default,
                () => setup?.DisposeAsync() ?? default, () => db.DisposeAsync());
        }
    }
}
