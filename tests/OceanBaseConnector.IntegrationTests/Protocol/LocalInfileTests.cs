using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Protocol;

public sealed class LocalInfileTests
{
    public static TheoryData<string, bool, bool> Rows()
    {
        var rows = new TheoryData<string, bool, bool>();
        foreach (var profile in IntegrationEnvironment.Profiles.Where(p => p.CreateObjects && !p.Oracle))
            foreach (var asynchronous in new[] { false, true })
                foreach (var fail in new[] { false, true }) rows.Add(profile.Name, asynchronous, fail);
        return rows;
    }

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task ChunkedUploadOrSourceFailurePreservesOwnershipAndRecovery(string profileName, bool asynchronous, bool fail)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var observer = await db.OpenAsync(deadline.Token);
        var table = await db.TableAsync(observer, "id INTEGER PRIMARY KEY, value TEXT", deadline.Token);
        var value = string.Concat(Enumerable.Repeat("中文abc", 600));
        var bytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(1, 256).Select(id => id + "," + value + "\n")));
        Assert.True(bytes.Length > 1024 * 1024);
        using var upload = new UploadStream(bytes, fail);
        var requests = 0;
        var builder = db.Profile.CreateBuilder();
        builder.Pooling = true; builder.MinPoolSize = 0; builder.MaxPoolSize = 1; builder.AllowLocalInfile = true;
        await using var source = new OceanBaseDataSourceBuilder(builder.ConnectionString).Build();
        await using var connection = (OceanBaseConnection)source.CreateConnection();
        connection.LocalInfileStreamFactory = name =>
        {
            Assert.Equal("obi-upload.csv", name);
            requests++;
            return upload;
        };
        await Open();
        var original = connection.GetOpenSession();
        using (var command = db.Command(connection, "LOAD DATA LOCAL INFILE 'obi-upload.csv' INTO TABLE " + table +
            " FIELDS TERMINATED BY ',' LINES TERMINATED BY '\\n'"))
        {
            if (fail)
            {
                var error = await Assert.ThrowsAsync<OceanBaseException>(async () =>
                {
                    if (asynchronous) await command.ExecuteNonQueryAsync(deadline.Token); else command.ExecuteNonQuery();
                });
                Assert.True(error.IsExecutionOutcomeUnknown);
                Assert.IsType<IOException>(error.InnerException);
            }
            else Assert.Equal(256, asynchronous ? await command.ExecuteNonQueryAsync(deadline.Token) : command.ExecuteNonQuery());
        }
        Assert.Equal(1, requests);
        Assert.True(upload.Disposed);
        Assert.True(upload.Reads >= 2);
        if (fail)
        {
            Assert.Equal(ConnectionState.Broken, connection.State);
            if (asynchronous) await connection.CloseAsync(); else connection.Close();
            await Open();
            Assert.NotSame(original, connection.GetOpenSession());
            Assert.Equal(1, requests);
            // A failed upload has an unknown outcome; recovery never retries its source.
        }
        else
        {
            Assert.Same(original, connection.GetOpenSession());
            Assert.Equal(256, await db.CountAsync(observer, table, deadline.Token));
            using var command = db.Command(observer, "SELECT id, value FROM " + table + " ORDER BY id");
            await using var reader = await command.ExecuteReaderAsync(deadline.Token);
            for (var id = 1; id <= 256; id++)
            {
                Assert.True(await reader.ReadAsync(deadline.Token));
                DatabaseContext.EqualNumber(reader.GetValue(0), id, "Chunked upload row order.");
                Assert.Equal(value, reader.GetString(1));
            }
            Assert.False(await reader.ReadAsync(deadline.Token));
        }
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, deadline.Token), 1,
            "The successful or reopened connection must be usable.");

        async Task Open() { if (asynchronous) await connection.OpenAsync(deadline.Token); else connection.Open(); }
    }

    private sealed class UploadStream(byte[] bytes, bool fail) : MemoryStream(bytes, writable: false)
    {
        internal int Reads { get; private set; }
        internal bool Disposed { get; private set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (++Reads > 1 && fail) throw new IOException("Injected upload source failure.");
            return base.Read(buffer, offset, count);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Read(buffer, offset, count));
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
