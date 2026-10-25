using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task StatementCacheForgetsIdsAfterPoolReset(bool oracle, bool asynchronous)
        {
            var server = CacheServer(oracle, async channel =>
            {
                for (byte id = 71; id < 73; id++)
                {
                    if (id == 72) { await StatementCommand(channel, 14); await channel.WritePacketAsync(TimeZoneOk("+00:00", status: (byte)(oracle ? 6 : 2))); }
                    await StatementCommand(channel, 22); await Prepared(channel, id, 0);
                    Assert.Equal(id, (await StatementCommand(channel, 23))[1]);
                    await channel.WritePacketAsync(Ok(status: (byte)(oracle ? 6 : 2)));
                    await StatementCommand(channel, 31);
                    await channel.WritePacketAsync(TimeZoneOk("+00:00", status: (byte)(oracle ? 6 : 2)));
                }
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Pooling=true;Max Pool Size=1;Statement Cache Size=2");
            try
            {
                for (var i = 0; i < 2; i++)
                {
                    if (asynchronous) await connection.OpenAsync(); else connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = "UPDATE t SET n=1";
                        if (asynchronous) await command.PrepareAsync(); else command.Prepare();
                        Assert.Equal(0, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                    }
                    if (asynchronous) await connection.CloseAsync(); else connection.Close();
                }
            }
            finally { connection.Close(); OceanBaseConnection.ClearPool(connection); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task StatementCacheNeverRetainsExecutionErrors(bool asynchronous)
        {
            var server = CacheServer(true, async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 31, 0);
                await StatementCommand(channel, 23); await channel.WritePacketAsync(Error());
                Assert.Equal(31, (await StatementCommand(channel, 25))[1]);
                await StatementCommand(channel, 22); await Prepared(channel, 32, 0);
                Assert.Equal(32, (await StatementCommand(channel, 23))[1]); await channel.WritePacketAsync(Ok());
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Statement Cache Size=2");
            try
            {
                await connection.OpenAsync();
                using (var rejected = connection.CreateCommand())
                {
                    rejected.CommandText = "UPDATE t SET n=1"; await rejected.PrepareAsync();
                    var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? rejected.ExecuteNonQueryAsync() : Task.FromResult(rejected.ExecuteNonQuery()));
                    Assert.False(error.IsExecutionOutcomeUnknown);
                }
                using var next = connection.CreateCommand(); next.CommandText = "UPDATE t SET n=1";
                if (asynchronous) await next.PrepareAsync(); else next.Prepare();
                Assert.Equal(0, await next.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task StatementCacheSchemaOnlyDoesNotConsumeIdleStatement(bool asynchronous)
        {
            var server = CacheServer(true, async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 31, 0);
                await StatementCommand(channel, 22); await Prepared(channel, 32, 0, 1);
                Assert.Equal(32, (await StatementCommand(channel, 25))[1]);
                Assert.Equal(31, (await StatementCommand(channel, 23))[1]); await channel.WritePacketAsync(Ok());
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Statement Cache Size=2");
            try
            {
                await connection.OpenAsync();
                using (var first = connection.CreateCommand()) { first.CommandText = "SELECT n FROM t"; await first.PrepareAsync(); }
                using (var schema = connection.CreateCommand())
                {
                    schema.CommandText = "SELECT n FROM t";
                    using var reader = asynchronous ? await schema.ExecuteReaderAsync(CommandBehavior.SchemaOnly) : schema.ExecuteReader(CommandBehavior.SchemaOnly);
                    Assert.Equal(1, reader.FieldCount); Assert.False(reader.Read());
                }
                using var next = connection.CreateCommand(); next.CommandText = "SELECT n FROM t"; await next.PrepareAsync();
                Assert.Equal(0, await next.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task StatementCacheDatabaseChangeClosesIdleAndRepreparesHeldStatement(bool asynchronous)
        {
            var server = CacheServer(false, async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 31, 0);
                await StatementCommand(channel, 22); await Prepared(channel, 32, 0);
                Assert.Equal(OceanBaseCommand.SqlPacket("other", 2), await StatementCommand(channel, 2));
                await channel.WritePacketAsync(Ok(status: 2));
                var first = (await StatementCommand(channel, 25))[1]; var second = (await StatementCommand(channel, 25))[1];
                Assert.Contains((byte)31, new[] { first, second }); Assert.Contains((byte)32, new[] { first, second });
                await StatementCommand(channel, 22); await Prepared(channel, 33, 0);
                Assert.Equal(33, (await StatementCommand(channel, 23))[1]); await channel.WritePacketAsync(Ok(status: 2));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Statement Cache Size=2");
            try
            {
                await connection.OpenAsync();
                using var held = connection.CreateCommand(); held.CommandText = "SELECT n FROM t"; await held.PrepareAsync();
                using (var idle = connection.CreateCommand()) { idle.CommandText = "SELECT m FROM t"; await idle.PrepareAsync(); }
                if (asynchronous) await connection.ChangeDatabaseAsync("other"); else connection.ChangeDatabase("other");
                Assert.Equal(0, await held.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task StatementCachePreCanceledDdlDoesNotInvalidateCachedStatement()
        {
            var server = CacheServer(true, async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 31, 0);
                Assert.Equal(31, (await StatementCommand(channel, 23))[1]); await channel.WritePacketAsync(Ok());
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Statement Cache Size=1");
            try
            {
                await connection.OpenAsync();
                using (var first = connection.CreateCommand()) { first.CommandText = "SELECT n FROM t"; await first.PrepareAsync(); }
                using (var ddl = connection.CreateCommand())
                {
                    ddl.CommandText = "ALTER TABLE t ADD m INT";
                    using var canceled = new CancellationTokenSource(); canceled.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ddl.ExecuteNonQueryAsync(canceled.Token));
                }
                using var next = connection.CreateCommand(); next.CommandText = "SELECT n FROM t"; await next.PrepareAsync();
                Assert.Equal(0, await next.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
