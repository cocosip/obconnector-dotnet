using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        public static IEnumerable<object[]> OwnedLobDrainCases()
        {
            foreach (var asynchronous in new[] { false, true })
                foreach (var character in new[] { false, true })
                    foreach (var anonymous in new[] { false, true })
                        yield return new object[] { asynchronous, character, anonymous };
        }

        private static async Task OwnedLobDrainPrepared(PacketChannel channel, byte id, byte code, bool anonymous)
        {
            await StatementCommand(channel, 22);
            await channel.WritePacketAsync(new byte[] { 0, id, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
            await channel.WritePacketAsync(anonymous ? ComplexColumn("", "", element: code) : Column("lob", code));
            await channel.WritePacketAsync(Eof());
        }

        private static async Task OwnedLobDrainReply(PacketChannel channel, byte code, bool anonymous, bool binary, bool isNull)
        {
            channel.BeginResponse(); await channel.WritePacketAsync(new byte[] { 1 });
            await channel.WritePacketAsync(anonymous ? ComplexColumn("", "", element: code) : Column("lob", code));
            await channel.WritePacketAsync(Eof());
            var field = WithLength(InlineLob(new byte[] { 97 }));
            if (anonymous) field = new byte[] { 1, 0 }.Concat(field).ToArray();
            await channel.WritePacketAsync(isNull ? binary ? new byte[] { 0, 4 } : new byte[] { 251 }
                : binary ? new byte[] { 0, 0 }.Concat(field).ToArray() : field);
            await channel.WritePacketAsync(Eof());
        }

        [Theory]
        [MemberData(nameof(OwnedLobDrainCases))]
        public async Task OwnedMultiSendNonQueryDrainsLobsWithoutAllocatingHandles(bool asynchronous, bool character, bool anonymous)
        {
            var code = character ? (byte)211 : (byte)210;
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 90, 0);
                await OwnedLobDrainPrepared(channel, 91, code, anonymous); await OwnedLobDrainPrepared(channel, 92, code, anonymous);
                await StatementCommand(channel, 22); await Prepared(channel, 93, 0);
                Assert.Equal(90, (await StatementCommand(channel, 23))[1]); Assert.Equal(91, (await StatementCommand(channel, 23))[1]);
                channel.BeginResponse(); await channel.WritePacketAsync(Ok(1)); await OwnedLobDrainReply(channel, code, anonymous, true, false);
                Assert.Equal(92, (await StatementCommand(channel, 23))[1]); Assert.Equal(93, (await StatementCommand(channel, 23))[1]);
                await OwnedLobDrainReply(channel, code, anonymous, true, true); channel.BeginResponse(); await channel.WritePacketAsync(Ok(2));
                Assert.Equal(0, await channelStreamRead(channel));
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Batch Multi Send=true;Batch Multi Send Number=2");
            PhysicalSession? session = null;
            connection.StateChange += (_, change) => { if (change.CurrentState == ConnectionState.Open) session = connection.GetOpenSession(); };
            try
            {
                using var batch = new OceanBaseBatch(connection);
                BatchItem(batch, "insert into t values (1)"); BatchItem(batch, "select lob from t");
                BatchItem(batch, "select lob from other_t"); BatchItem(batch, "update t set id=2");
                Assert.Equal(3, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                Assert.Equal(new[] { 1, -1, -1, 2 }, batch.BatchCommands.Cast<OceanBaseBatchCommand>().Select(item => item.RecordsAffected));
                Assert.Equal(ConnectionState.Closed, connection.State); Assert.NotNull(session); Assert.False(session.RequiresDiscardOnReturn);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task OwnedMultiSendNonQueryDrainsTextAndBinaryLobResults(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await channel.WritePacketAsync(UnsupportedPrepare());
                await OwnedLobDrainPrepared(channel, 90, 210, false);
                channel.BeginCommand(); await Query(channel, "select first_lob"); await StatementCommand(channel, 23);
                await OwnedLobDrainReply(channel, 210, false, false, false); await OwnedLobDrainReply(channel, 210, false, true, false);
                Assert.Equal(0, await channelStreamRead(channel));
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Batch Multi Send=true");
            try
            {
                using var batch = new OceanBaseBatch(connection); BatchItem(batch, "select first_lob"); BatchItem(batch, "select second_lob");
                Assert.Equal(-1, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                Assert.Equal(ConnectionState.Closed, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task OwnedMultiSendNonQueryLobErrorKeepsSuccessfulPrefix(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 90, 0);
                await OwnedLobDrainPrepared(channel, 91, 210, false);
                await StatementCommand(channel, 22); await Prepared(channel, 92, 0);
                for (var i = 0; i < 3; i++) await StatementCommand(channel, 23);
                channel.BeginResponse(); await channel.WritePacketAsync(Ok(1)); channel.BeginResponse(); await channel.WritePacketAsync(Error());
                try { Assert.Equal(0, await channelStreamRead(channel)); } catch (IOException) { }
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Batch Multi Send=true");
            try
            {
                using var batch = new OceanBaseBatch(connection); BatchItem(batch, "insert into t values (1)");
                BatchItem(batch, "select lob from t"); BatchItem(batch, "update t set id=2");
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? batch.ExecuteNonQueryAsync() : Task.FromResult(batch.ExecuteNonQuery()));
                Assert.Same(batch.BatchCommands[1], error.BatchCommand); Assert.False(error.IsExecutionOutcomeUnknown);
                Assert.Equal(new[] { 1, -1, -1 }, batch.BatchCommands.Cast<OceanBaseBatchCommand>().Select(item => item.RecordsAffected));
                Assert.Equal(ConnectionState.Closed, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)]
        [InlineData(false, true)] [InlineData(true, true)]
        public async Task OwnedMultiSendNonQueryCannotBypassLobCapabilityOrCharset(bool asynchronous, bool badCharset)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22);
                await channel.WritePacketAsync(new byte[] { 0, 90, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
                await channel.WritePacketAsync(Column("lob", 211, badCharset ? (ushort)63 : (ushort)45)); await channel.WritePacketAsync(Eof());
                Assert.Equal(0, await channelStreamRead(channel));
            }, lobLocators: badCharset);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Batch Multi Send=true");
            try
            {
                using var batch = new OceanBaseBatch(connection); BatchItem(batch, "select lob from t");
                await Assert.ThrowsAsync<NotSupportedException>(() => asynchronous ? batch.ExecuteNonQueryAsync() : Task.FromResult(batch.ExecuteNonQuery()));
                Assert.Equal(ConnectionState.Closed, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
