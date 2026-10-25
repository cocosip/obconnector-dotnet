using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Connections;

public sealed partial class PoolResourceIsolationTests
{
    public static IEnumerable<object[]> LargeLobRows() =>
        from profile in IntegrationEnvironment.Profiles
        where profile.Oracle && profile.CreateObjects && profile.CreateBuilder().UseOraclePrepareExecute && profile.CreateBuilder().UseLobLocators
        from character in new[] { false, true }
        from asynchronous in new[] { false, true }
        select new object[] { profile.Name, character, asynchronous };

    [IntegrationTheory]
    [MemberData(nameof(LargeLobRows))]
    public async Task ExternalPersistentLobStreamsExactDataAndAllowsPoolReuse(string profileName, bool character, bool asynchronous)
    {
        var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        OceanBaseConnection? setup = null; OceanBaseDataSource? source = null; OceanBaseConnection? connection = null;
        OceanBaseLob? seed = null; OceanBaseLob? lob = null; Stream? stream = null; TextReader? text = null;
        Exception? failure = null;
        try
        {
            var expectedText = string.Concat(Enumerable.Repeat("患者😀z", 20000));
            var expectedBytes = character ? Encoding.UTF8.GetBytes(expectedText) : Enumerable.Range(0, 100000).Select(i => (byte)(i % 251)).ToArray();
            var builder = db.Profile.CreateBuilder(); builder.Pooling = false;
            setup = new OceanBaseConnection(builder.ConnectionString); await setup.OpenAsync();
            var table = await db.TableAsync(setup, "id INTEGER PRIMARY KEY, value " + (character ? "CLOB" : "BLOB"), CancellationToken.None);
            seed = await setup.CreateTemporaryLobAsync(character ? OceanBaseDbType.Clob : OceanBaseDbType.Blob);
            if (character) await seed.WriteTextAsync(0, expectedText);
            else await seed.WriteBytesAsync(0, expectedBytes, 0, expectedBytes.Length);
            using (var insert = db.Command(setup, "INSERT INTO " + table + " VALUES (1, :value)"))
            {
                DatabaseContext.Parameter(insert, "value", seed);
                Assert.Equal(1, await insert.ExecuteNonQueryAsync());
            }
            await seed.FreeTemporaryAsync(); await seed.DisposeAsync(); seed = null;
            source = Source(db); connection = (OceanBaseConnection)source.CreateConnection();
            await Open(connection, asynchronous);
            var session = connection.GetOpenSession(); var generation = session.Generation;
            lob = Assert.IsType<OceanBaseLob>(await Scalar(db, connection, "SELECT value FROM " + table + " WHERE id=1", asynchronous));
            _output.WriteLine("Large persistent locator: inline={0}, bytes={1}, server={2}", lob.Locator.HasInlineData, lob.ByteLength, connection.ServerVersion);
            Assert.Equal(expectedBytes.Length, lob.ByteLength);
            Assert.False(lob.Locator.HasInlineData, "External LOB acceptance requires an actual external locator, not merely a large inline result.");
            if (character)
            {
                text = lob.OpenTextReader();
                Assert.Equal(expectedText, asynchronous ? await text.ReadToEndAsync() : text.ReadToEnd());
            }
            else
            {
                stream = lob.OpenRead(); using var contents = new MemoryStream();
                if (asynchronous) await stream.CopyToAsync(contents); else stream.CopyTo(contents);
                Assert.Equal(expectedBytes, contents.ToArray());
            }
            text?.Dispose(); stream?.Dispose();
            if (asynchronous) await lob.DisposeAsync(); else lob.Dispose();
            Assert.False(session.RequiresDiscardOnReturn);
            await Close(connection, asynchronous); await Open(connection, asynchronous);
            Assert.Same(session, connection.GetOpenSession()); Assert.True(session.Generation > generation);
            DatabaseContext.EqualNumber(await Scalar(db, connection, db.SelectOne, asynchronous), 1, "External read completion must release its reservation and permit healthy reset/reuse.");
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            await Cleanup(failure, () => { text?.Dispose(); return default; }, () => { stream?.Dispose(); return default; },
                () => lob?.DisposeAsync() ?? default, () => seed?.DisposeAsync() ?? default,
                () => connection?.DisposeAsync() ?? default, () => source?.DisposeAsync() ?? default,
                () => setup?.DisposeAsync() ?? default, () => db.DisposeAsync());
        }
    }
}
