using System;
using System.Data;
using System.IO;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task StatementCacheHeldCommandsNeverShareAnIdAndDuplicateReturnClosesOldId(bool asynchronous)
        {
            var server = CacheServer(true, async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 51, 0);
                await StatementCommand(channel, 22); await Prepared(channel, 52, 0);
                Assert.Equal(51, (await StatementCommand(channel, 23))[1]); await channel.WritePacketAsync(Ok());
                Assert.Equal(52, (await StatementCommand(channel, 23))[1]); await channel.WritePacketAsync(Ok());
                Assert.Equal(new byte[] { 25, 51, 0, 0, 0 }, await StatementCommand(channel, 25));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Statement Cache Size=2");
            try
            {
                await connection.OpenAsync();
                using var a = connection.CreateCommand(); a.CommandText = "SELECT n FROM t"; await a.PrepareAsync();
                using var b = connection.CreateCommand(); b.CommandText = a.CommandText; await b.PrepareAsync();
                if (asynchronous) await a.DisposeAsync(); else a.Dispose();
                using (var c = connection.CreateCommand())
                {
                    c.CommandText = a.CommandText; if (asynchronous) await c.PrepareAsync(); else c.Prepare();
                    Assert.Equal(0, await c.ExecuteNonQueryAsync());
                }
                Assert.Equal(0, asynchronous ? await b.ExecuteNonQueryAsync() : b.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task StatementCacheOnlyReusesCursorAfterTerminalFetch(bool oracle, bool asynchronous)
        {
            var server = CacheServer(oracle, async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 61, 0, 1);
                for (var i = 0; i < 2; i++)
                {
                    Assert.Equal(61, (await StatementCommand(channel, 23))[1]); await CursorMetadata(channel, 70);
                    Assert.Equal(new byte[] { 28, 61, 0, 0, 0, 1, 0, 0, 0 }, await StatementCommand(channel, 28));
                    if (oracle) await CursorMetadata(channel, 6);
                    await channel.WritePacketAsync(CursorRow(i)); await channel.WritePacketAsync(Eof(134));
                }
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Statement Cache Size=1");
            try
            {
                await connection.OpenAsync();
                for (var i = 0; i < 2; i++)
                {
                    using var command = connection.CreateCommand(); command.CommandText = "SELECT n FROM t"; SetFetchSize(command, 1);
                    using var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader();
                    using var other = connection.CreateCommand(); other.CommandText = command.CommandText;
                    await Assert.ThrowsAsync<InvalidOperationException>(() => other.PrepareAsync());
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.Equal(i, reader.GetInt64(0));
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                }
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task StatementCacheLongDataUsesFreshParameterBytesOnEachBorrow(bool asynchronous)
        {
            var server = CacheServer(false, async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 61, 1);
                for (var i = 0; i < 2; i++)
                {
                    Assert.Equal(new byte[] { 24, 61, 0, 0, 0, 0, 0, (byte)i }, await StatementCommand(channel, 24));
                    Assert.Equal(61, (await StatementCommand(channel, 23))[1]); await channel.WritePacketAsync(Ok(status: 2));
                }
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Statement Cache Size=1");
            try
            {
                await connection.OpenAsync();
                for (var i = 0; i < 2; i++)
                {
                    using var stream = new MemoryStream(new[] { (byte)i });
                    using var command = connection.CreateCommand(); command.CommandText = "INSERT INTO t VALUES (?)";
                    command.Parameters.Add(new OceanBaseParameter { Value = stream });
                    Assert.Equal(0, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                }
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
