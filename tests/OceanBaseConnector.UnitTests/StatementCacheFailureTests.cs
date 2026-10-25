using System;
using System.Data;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Sql;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData("/* trace */ SELECT 'ALTER TABLE t'", true)]
        [InlineData("WITH c AS (SELECT 1) SELECT * FROM c", true)]
        [InlineData("INSERT INTO t VALUES ('SET x=1')", true)]
        [InlineData("MERGE INTO t USING s ON (t.n=s.n) WHEN MATCHED THEN UPDATE SET t.a=s.a", true)]
        [InlineData("SELECT 1; ALTER TABLE t ADD n INT", false)]
        [InlineData("/*! SET sql_mode='' */ SELECT 1", false)]
        [InlineData("SELECT @v", false)]
        [InlineData("ALTER\nTABLE t ADD n INT", false)]
        [InlineData("CALL p()", false)]
        public void StatementCacheCandidateDoesNotConfuseLiteralsCommentsAndContextChanges(string sql, bool expected)
            => Assert.Equal(expected, SqlStatementBoundary.CanCacheStatement(sql, OceanBaseCompatibilityMode.MySql));

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public async Task StatementCacheNeverRetainsPartialMetadataOrParameterCountMismatch(int fault)
        {
            var server = CacheServer(false, async channel =>
            {
                await StatementCommand(channel, 22);
                if (fault == 0)
                {
                    await channel.WritePacketAsync(new byte[] { 0, 81, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0 });
                    await channel.WritePacketAsync(Error());
                }
                else await Prepared(channel, 81, (ushort)(fault == 1 ? 2 : 1));
                Assert.Equal(81, (await StatementCommand(channel, 25))[1]);
                await StatementCommand(channel, 22); await Prepared(channel, 82, 1);
                Assert.Equal(82, (await StatementCommand(channel, 23))[1]); await channel.WritePacketAsync(Ok(status: 2));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Statement Cache Size=1");
            try
            {
                await connection.OpenAsync();
                if (fault == 2)
                {
                    using (var first = connection.CreateCommand())
                    {
                        first.CommandText = "SELECT ?"; first.Parameters.Add(new OceanBaseParameter { Value = 1 });
                        await first.PrepareAsync();
                    }
                    // A matching SQL cache entry still requires the exact server parameter count.
                    using var wrong = connection.CreateCommand(); wrong.CommandText = "SELECT ?";
                    wrong.Parameters.Add(new OceanBaseParameter { Value = 1 }); wrong.Parameters.Add(new OceanBaseParameter { Value = 2 });
                    await Assert.ThrowsAsync<ArgumentException>(() => wrong.PrepareAsync());
                }
                else
                {
                    using var rejected = connection.CreateCommand(); rejected.CommandText = "SELECT ?";
                    rejected.Parameters.Add(new OceanBaseParameter { Value = 1 });
                    if (fault == 0) await Assert.ThrowsAsync<OceanBaseException>(() => rejected.PrepareAsync());
                    else await Assert.ThrowsAsync<ArgumentException>(() => rejected.PrepareAsync());
                }
                using var next = connection.CreateCommand(); next.CommandText = "SELECT ?";
                next.Parameters.Add(new OceanBaseParameter { Value = 3 });
                await next.PrepareAsync(); Assert.Equal(0, await next.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task StatementCacheUnreadResponseBreaksSessionAndForgetsIdleEntries(bool disposeCommand)
        {
            var server = CacheServer(true, async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 81, 0);
                await StatementCommand(channel, 22); await Prepared(channel, 82, 0, 1);
                await StatementCommand(channel, 23);
                await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(Column("n", 8));
                await channel.WritePacketAsync(Eof()); await channel.WritePacketAsync(CursorRow(1));
                await Assert.ThrowsAsync<Internal.Protocol.ProtocolException>(() => channel.ReadPacketAsync());
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Statement Cache Size=2");
            try
            {
                await connection.OpenAsync();
                using (var idle = connection.CreateCommand()) { idle.CommandText = "SELECT m FROM t"; await idle.PrepareAsync(); }
                using var command = connection.CreateCommand(); command.CommandText = "SELECT n FROM t"; await command.PrepareAsync();
                using var reader = await command.ExecuteReaderAsync();
                if (disposeCommand) command.Dispose(); else reader.Dispose();
                Assert.Equal(ConnectionState.Broken, connection.State);
                using var next = connection.CreateCommand(); next.CommandText = "SELECT m FROM t";
                await Assert.ThrowsAsync<InvalidOperationException>(() => next.PrepareAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
