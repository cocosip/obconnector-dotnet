using System;
using System.Data;
using System.Text;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false, 0)]
        [InlineData(false, 1)]
        [InlineData(true, 0)]
        [InlineData(true, 1)]
        public async Task StatementCacheRemovesRepeatedPrepareRoundTripsWithSyntheticDelay(bool oracle, int cacheSize)
        {
            var prepares = 0;
            var server = CacheServer(oracle, async channel =>
            {
                for (var i = 0; i < 8; i++)
                {
                    var id = (byte)(100 + (cacheSize == 0 ? i : 0));
                    if (i == 0 || cacheSize == 0)
                    {
                        await StatementCommand(channel, 22); prepares++;
                        await Task.Delay(15); // Synthetic prepare latency, not database performance evidence.
                        await Prepared(channel, id, 0);
                    }
                    Assert.Equal(id, (await StatementCommand(channel, 23))[1]); await channel.WritePacketAsync(Ok(status: (byte)(oracle ? 6 : 2)));
                    if (cacheSize == 0) Assert.Equal(id, (await StatementCommand(channel, 25))[1]);
                }
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + $";Statement Cache Size={cacheSize}");
            try
            {
                await connection.OpenAsync();
                for (var i = 0; i < 8; i++)
                {
                    using var command = connection.CreateCommand(); command.CommandText = "UPDATE t SET n=1";
                    await command.PrepareAsync(); Assert.Equal(0, await command.ExecuteNonQueryAsync());
                }
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(cacheSize == 0 ? 8 : 1, prepares);
        }

        [Fact]
        public void StatementCacheOptionsAreBoundedAndSurviveEndpointAndCredentialCopies()
        {
            var builder = new OceanBaseConnectionStringBuilder("Server=ob;User ID=u");
            Assert.Equal(0, builder.StatementCacheSize);
            builder.StatementCacheSize = 17;
            var options = builder.Build();
            Assert.Equal(17, new OceanBaseConnectionStringBuilder(builder.ConnectionString).StatementCacheSize);
            Assert.Equal(17, options.ForEndpoint(Internal.OceanBaseEndpoint.Parse("other", 2881)[0]).StatementCacheSize);
            Assert.Equal(17, options.WithCredential(new OceanBaseCredential("u", "p", 1)).StatementCacheSize);
            foreach (var value in new[] { "-1", "1025", "2147483648", "invalid" })
                Assert.Throws<ArgumentException>(() => builder["Statement Cache Size"] = value);
            builder.StatementCacheSize = 1024;
            Assert.Equal(1024, builder.Build().StatementCacheSize);
        }

        private static LoopbackServer CacheServer(bool oracle, Func<PacketChannel, Task> script) => LoopbackServer.Start(async client =>
        {
            var channel = new PacketChannel(client.GetStream());
            await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
            await channel.ReadPacketAsync();
            await channel.WritePacketAsync(TimeZoneOk("+00:00", status: (byte)(oracle ? 6 : 2)));
            await ServerIdentity.ReplyAsync(channel, oracle: oracle);
            await script(channel);
        });

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task StatementCacheReusesPreparedIdAcrossDisposedCommands(bool oracle, bool asynchronous)
        {
            var server = CacheServer(oracle, async channel =>
            {
                var prepare = await StatementCommand(channel, 22);
                Assert.Equal("UPDATE t SET n=?", Encoding.UTF8.GetString(prepare, 1, prepare.Length - 1));
                await Prepared(channel, 41, 1);
                for (var i = 0; i < 3; i++)
                {
                    var execute = await StatementCommand(channel, 23);
                    Assert.Equal(41, execute[1]);
                    Assert.Equal(i, execute[^8]);
                    await channel.WritePacketAsync(Ok((byte)(i + 1), status: (byte)(oracle ? 6 : 2)));
                }
                await StatementCommand(channel, 3); await channel.WritePacketAsync(Ok());
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Statement Cache Size=2");
            try
            {
                await connection.OpenAsync();
                for (var i = 0; i < 3; i++)
                {
                    using var command = connection.CreateCommand(); command.CommandText = oracle ? "UPDATE t SET n=:n" : "UPDATE t SET n=?";
                    command.Parameters.Add(new OceanBaseParameter { ParameterName = "n", Value = (long)i });
                    Assert.Equal(i + 1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                }
                using var plain = connection.CreateCommand(); plain.CommandText = "SELECT 1";
                Assert.Equal(0, await plain.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task StatementCacheEvictsLeastRecentlyReturnedStatement(bool asynchronous)
        {
            var server = CacheServer(true, async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 11, 0);
                await StatementCommand(channel, 22); await Prepared(channel, 12, 0);
                // A hit transfers ID11 out of the cache and makes it the most recently returned entry.
                await StatementCommand(channel, 23); await channel.WritePacketAsync(Ok());
                await StatementCommand(channel, 22); await Prepared(channel, 13, 0);
                Assert.Equal(new byte[] { 25, 12, 0, 0, 0 }, await StatementCommand(channel, 25));
                await StatementCommand(channel, 22); await Prepared(channel, 14, 0);
                Assert.Equal(new byte[] { 25, 11, 0, 0, 0 }, await StatementCommand(channel, 25));
                await StatementCommand(channel, 3); await channel.WritePacketAsync(Ok());
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Statement Cache Size=2");
            try
            {
                await connection.OpenAsync();
                var statements = new[] { "SELECT 11", "SELECT 12", "SELECT 11", "SELECT 13", "SELECT 12" };
                for (var i = 0; i < statements.Length; i++)
                {
                    using var command = connection.CreateCommand(); command.CommandText = statements[i];
                    if (asynchronous) await command.PrepareAsync(); else command.Prepare();
                    // Explicit preparation of the third command should be a cache hit; execute it once.
                    if (i == 2) Assert.Equal(0, await command.ExecuteNonQueryAsync());
                }
                using var plain = connection.CreateCommand(); plain.CommandText = "SELECT 1"; await plain.ExecuteNonQueryAsync();
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("/* ddl */ ALTER TABLE t ADD n INT")]
        [InlineData("SET SESSION sql_mode = ''")]
        [InlineData("CALL p()")]
        public async Task StatementCacheContextChangeInvalidatesIdleAndHeldStatements(string sql)
        {
            var server = CacheServer(true, async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 21, 0);
                await StatementCommand(channel, 22); await Prepared(channel, 22, 0);
                await StatementCommand(channel, 3); await channel.WritePacketAsync(Ok());
                Assert.Equal(22, (await StatementCommand(channel, 25))[1]);
                Assert.Equal(21, (await StatementCommand(channel, 25))[1]);
                await StatementCommand(channel, 22); await Prepared(channel, 23, 0);
                Assert.Equal(23, (await StatementCommand(channel, 23))[1]); await channel.WritePacketAsync(Ok());
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Statement Cache Size=2");
            try
            {
                await connection.OpenAsync();
                using var held = connection.CreateCommand(); held.CommandText = "SELECT 21"; await held.PrepareAsync();
                using (var idle = connection.CreateCommand()) { idle.CommandText = "SELECT 22"; await idle.PrepareAsync(); }
                using (var change = connection.CreateCommand()) { change.CommandText = sql; await change.ExecuteNonQueryAsync(); }
                Assert.Equal(0, await held.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
