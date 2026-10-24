using System;
using System.Data;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private static async Task TemporaryLobCreateRequest(PacketChannel channel, bool character)
        {
            var request = new ByteCursor(await StatementCommand(channel, 0xa1)); request.Skip(10);
            Assert.Equal("DECLARE lob_value " + (character ? "CLOB" : "BLOB") + "; BEGIN DBMS_LOB.CREATETEMPORARY(lob_value, FALSE, DBMS_LOB.SESSION); ? := lob_value; END;",
                Encoding.UTF8.GetString(request.ReadLengthEncodedBytes()));
            Assert.Equal(1u, request.ReadUInt32()); Assert.Equal(1, request.ReadUInt8()); Assert.Equal(1, request.ReadUInt8());
            Assert.Equal((ushort)6, request.ReadUInt16()); request.Skip(16); Assert.False(request.HasRemaining);
        }
        private static async Task TemporaryLobFreeRequest(PacketChannel channel, bool character, byte[] locator)
        {
            var request = new ByteCursor(await StatementCommand(channel, 0xa1)); request.Skip(10);
            Assert.Equal("DECLARE lob_value " + (character ? "CLOB" : "BLOB") + " := ?; BEGIN DBMS_LOB.FREETEMPORARY(lob_value); ? := '1'; END;",
                Encoding.UTF8.GetString(request.ReadLengthEncodedBytes()));
            Assert.Equal(2u, request.ReadUInt32()); Assert.Equal(2, request.ReadUInt8()); Assert.Equal(1, request.ReadUInt8());
            Assert.Equal(character ? (ushort)211 : (ushort)210, request.ReadUInt16()); Assert.Equal((ushort)6, request.ReadUInt16());
            Assert.Equal(locator, request.ReadLengthEncodedBytes()); request.Skip(16); Assert.False(request.HasRemaining);
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
        public async Task TemporaryLobCreationAndExplicitFreeUseOriginalSession(bool async, bool character)
        {
            var locator = InlineLob(Array.Empty<byte>()); var type = character ? (byte)211 : (byte)210;
            var server = Server(async channel =>
            {
                await TemporaryLobCreateRequest(channel, character);
                await LobMutationReply(channel, type, locator, parameters: 1); await StatementCommand(channel, 25); channel.BeginCommand();
                await TemporaryLobFreeRequest(channel, character, locator);
                await LobMutationReply(channel, 253, Encoding.UTF8.GetBytes("1"), parameters: 2); await StatementCommand(channel, 25); channel.BeginCommand();
                await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            }, lobLocators: true, serverVersion: "4.3.5.0");
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync(); var kind = character ? OceanBaseDbType.Clob : OceanBaseDbType.Blob;
                Assert.Throws<ArgumentOutOfRangeException>(() => connection.CreateTemporaryLob(OceanBaseDbType.String));
                using var canceled = new CancellationTokenSource(); canceled.Cancel();
                var canceledCreate = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.CreateTemporaryLobAsync(kind, cancellationToken: canceled.Token));
                Assert.Equal(canceled.Token, canceledCreate.CancellationToken);
                using var lob = async ? await connection.CreateTemporaryLobAsync(kind) : connection.CreateTemporaryLob(kind);
                Assert.Equal(character, lob.IsCharacter); Assert.Equal(0, lob.ByteLength);
                var canceledFree = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lob.FreeTemporaryAsync(canceled.Token)); Assert.Equal(canceled.Token, canceledFree.CancellationToken);
                using (var source = character ? (IDisposable)lob.OpenTextReader() : lob.OpenRead())
                    Assert.Throws<InvalidOperationException>(() => lob.FreeTemporary());
                if (async) await lob.FreeTemporaryAsync(); else lob.FreeTemporary();
                Assert.Throws<ObjectDisposedException>(() => lob.Truncate(0)); Assert.Throws<ObjectDisposedException>(() => lob.FreeTemporary());
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            catch { if (server.Completion.IsFaulted) await server.Completion; throw; }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
        public async Task TemporaryLobDisposeKeepsExplicitDatabaseOwnershipContract(bool async, bool character)
        {
            var server = Server(async channel =>
            {
                await TemporaryLobCreateRequest(channel, character);
                await LobMutationReply(channel, character ? (byte)211 : (byte)210, InlineLob(Array.Empty<byte>()), parameters: 1);
                await StatementCommand(channel, 25); channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            }, lobLocators: true, serverVersion: "4.3.5.0");
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync(); var lob = await connection.CreateTemporaryLobAsync(character ? OceanBaseDbType.Clob : OceanBaseDbType.Blob);
                if (async) await lob.DisposeAsync(); else lob.Dispose();
                Assert.Throws<ObjectDisposedException>(() => lob.FreeTemporary());
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            catch { if (server.Completion.IsFaulted) await server.Completion; throw; }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task TemporaryLobFreeRejectsBorrowedHandlesWithoutSendingSql(bool character)
        {
            var server = Server(async channel =>
            {
                await LobQueryReply(channel, character ? (byte)211 : (byte)210, InlineLob(Array.Empty<byte>()));
                await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "select lob_value";
                using var lob = (OceanBaseLob)(await command.ExecuteScalarAsync())!;
                Assert.Throws<InvalidOperationException>(() => lob.FreeTemporary());
                await Assert.ThrowsAsync<InvalidOperationException>(() => lob.FreeTemporaryAsync());
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task UnfreedTemporaryLobDiscardsPooledSession(bool async)
        {
            var oldServer = Server(async channel =>
            {
                await TemporaryLobCreateRequest(channel, false);
                await LobMutationReply(channel, 210, InlineLob(Array.Empty<byte>()), parameters: 1);
                await StatementCommand(channel, 25);
                Assert.Equal(0, await channelStreamRead(channel));
            }, lobLocators: true, serverVersion: "4.3.5.0");
            var newServer = Server(async channel => { await Query(channel, "after"); await channel.WritePacketAsync(Ok(1)); }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(oldServer.Port) + ";Pooling=true");
            OceanBaseLob? lob = null;
            try
            {
                if (async) await connection.OpenAsync(); else connection.Open();
                lob = async ? await connection.CreateTemporaryLobAsync(OceanBaseDbType.Blob) : connection.CreateTemporaryLob(OceanBaseDbType.Blob);
                connection.Close();
                await oldServer.Completion.WaitAsync(TimeSpan.FromSeconds(10));
                connection.ConnectionString = LobConfiguration(newServer.Port) + ";Pooling=true";
                if (async) await connection.OpenAsync(); else connection.Open();
                Assert.Throws<InvalidOperationException>(() => lob!.FreeTemporary());
                using var after = connection.CreateCommand(); after.CommandText = "after";
                Assert.Equal(1, async ? await after.ExecuteNonQueryAsync() : after.ExecuteNonQuery());
            }
            finally
            {
                lob?.Dispose(); connection.Close(); oldServer.Stop(); newServer.Stop();
            }
            await newServer.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
