using System;
using System.Data;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
        public async Task TemporaryLobVersionFailureBeforeCreationKeepsSessionUsable(bool async, bool rejected)
        {
            var server = Server(async channel =>
            {
                if (rejected)
                { await Query(channel, "select ob_version() from dual"); await channel.WritePacketAsync(Error()); channel.BeginCommand(); }
                else await OracleVersionReply(channel, "2.2.75");
                await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync();
                var error = await Record.ExceptionAsync(async () =>
                { using var lob = async ? await connection.CreateTemporaryLobAsync(OceanBaseDbType.Blob) : connection.CreateTemporaryLob(OceanBaseDbType.Blob); });
                if (rejected) Assert.False(Assert.IsType<OceanBaseException>(error).IsExecutionOutcomeUnknown);
                else Assert.IsType<NotSupportedException>(error);
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            catch { if (server.Completion.IsFaulted) await server.Completion; throw; }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("option")] [InlineData("capability")]
        public async Task TemporaryLobFactoryRequiresNegotiatedOptInBeforeSendingSql(string missing)
        {
            var server = Server(async channel => { await Query(channel, "after"); await channel.WritePacketAsync(Ok(1)); }, lobLocators: missing != "capability");
            using var connection = new OceanBaseConnection(missing == "option" ? Configuration(server.Port) + ";Use Oracle Prepare Execute=true" : LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync(); Assert.Throws<NotSupportedException>(() => connection.CreateTemporaryLob(OceanBaseDbType.Blob));
                await Assert.ThrowsAsync<NotSupportedException>(() => connection.CreateTemporaryLobAsync(OceanBaseDbType.Clob));
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
        public async Task TemporaryLobRequiresExplicitTransactionAndFreeBeforeItsEnd(bool character, bool freeBeforeEnd)
        {
            var locator = InlineLob(Array.Empty<byte>());
            var server = Server(async channel =>
            {
                await TransactionBegin(channel); await OracleVersionReply(channel, "4.3.5.0"); await TemporaryLobCreateRequest(channel, character);
                await LobMutationReply(channel, character ? (byte)211 : (byte)210, locator, parameters: 1); await StatementCommand(channel, 25); channel.BeginCommand();
                if (freeBeforeEnd)
                {
                    await TemporaryLobFreeRequest(channel, character, locator); await LobMutationReply(channel, 253, Encoding.UTF8.GetBytes("1"), parameters: 2);
                    await StatementCommand(channel, 25); channel.BeginCommand();
                }
                await TransactionEnd(channel, false); channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync(); using var transaction = (OceanBaseTransaction)await connection.BeginTransactionAsync(); var kind = character ? OceanBaseDbType.Clob : OceanBaseDbType.Blob;
                Assert.Throws<InvalidOperationException>(() => connection.CreateTemporaryLob(kind));
                using var lob = await connection.CreateTemporaryLobAsync(kind, transaction);
                if (freeBeforeEnd) await lob.FreeTemporaryAsync();
                await transaction.RollbackAsync();
                if (!freeBeforeEnd) await Assert.ThrowsAsync<InvalidOperationException>(() => lob.FreeTemporaryAsync());
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            catch { if (server.Completion.IsFaulted) await server.Completion; throw; }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task TemporaryLobCannotFreeThroughAReopenedConnection(bool character)
        {
            var first = Server(async channel =>
            {
                await OracleVersionReply(channel, "4.3.5.0"); await TemporaryLobCreateRequest(channel, character);
                await LobMutationReply(channel, character ? (byte)211 : (byte)210, InlineLob(Array.Empty<byte>()), parameters: 1); await StatementCommand(channel, 25);
            }, lobLocators: true);
            var second = Server(async channel => { await Query(channel, "after"); await channel.WritePacketAsync(Ok(1)); }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(first.Port));
            try
            {
                await connection.OpenAsync(); using var lob = await connection.CreateTemporaryLobAsync(character ? OceanBaseDbType.Clob : OceanBaseDbType.Blob);
                connection.Close(); connection.ConnectionString = LobConfiguration(second.Port); await connection.OpenAsync();
                Assert.Throws<InvalidOperationException>(() => lob.FreeTemporary()); await Assert.ThrowsAsync<InvalidOperationException>(() => lob.FreeTemporaryAsync());
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); first.Stop(); second.Stop(); }
            await Task.WhenAll(first.Completion, second.Completion).WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
        public async Task TemporaryLobCompletedButUndeliverableCreationReclaimsOnlyOriginalSession(bool async, bool reopen)
        {
            var first = Server(async channel =>
            {
                await OracleVersionReply(channel, "4.3.5.0"); await TemporaryLobCreateRequest(channel, false);
                await LobMutationReply(channel, 210, InlineLob(Array.Empty<byte>()), parameters: 1);
                if (!reopen) await StatementCommand(channel, 25);
                await Assert.ThrowsAsync<OceanBaseConnector.Internal.Protocol.ProtocolException>(() => channel.ReadPacketAsync());
            }, lobLocators: true);
            var second = reopen ? Server(async channel => { await Query(channel, "after"); await channel.WritePacketAsync(Ok(1)); }, lobLocators: true) : null;
            using var caller = new CancellationTokenSource(); OceanBaseConnection? connection = null; var notified = 0;
            var log = new RecordingLoggerFactory { OnLog = id =>
            {
                if (id.Id != 1100 || Interlocked.Exchange(ref notified, 1) != 0) return;
                if (reopen) { connection!.Close(); connection.ConnectionString = LobConfiguration(second!.Port); connection.Open(); }
                else caller.Cancel();
            } };
            using (connection = new OceanBaseConnection(LobConfiguration(first.Port), log))
            {
                try
                {
                    await connection.OpenAsync();
                    async Task Create()
                    {
                        using var lob = async ? await connection.CreateTemporaryLobAsync(OceanBaseDbType.Blob, cancellationToken: caller.Token)
                            : connection.CreateTemporaryLob(OceanBaseDbType.Blob);
                    }
                    if (reopen) await Assert.ThrowsAsync<InvalidOperationException>(Create);
                    else
                    {
                        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(Create); Assert.Equal(caller.Token, error.CancellationToken);
                    }
                    if (reopen)
                    {
                        Assert.Equal(ConnectionState.Open, connection.State); using var after = connection.CreateCommand(); after.CommandText = "after";
                        Assert.Equal(1, await after.ExecuteNonQueryAsync());
                    }
                    else Assert.Equal(ConnectionState.Broken, connection.State);
                }
                finally { connection.Close(); first.Stop(); second?.Stop(); }
            }
            await first.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            if (second != null) await second.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
