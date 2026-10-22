using System;
using System.Data;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false, "error")] [InlineData(true, "error")]
        [InlineData(false, "null")] [InlineData(true, "null")]
        [InlineData(false, "wrong-type")] [InlineData(true, "wrong-type")]
        [InlineData(false, "malformed")] [InlineData(true, "malformed")]
        [InlineData(false, "disconnect")] [InlineData(true, "disconnect")]
        public async Task TemporaryLobCreationDoesNotPublishOwnershipAfterFailure(bool async, string fault)
        {
            var server = Server(async channel =>
            {
                await TemporaryLobCreateRequest(channel, false);
                if (fault == "disconnect") return;
                await LobMutationReply(channel, fault == "wrong-type" ? (byte)211 : (byte)210,
                    fault == "null" ? null : fault == "malformed" ? new byte[] { 1, 2, 3 } : InlineLob(Array.Empty<byte>()), fault == "error", parameters: 1,
                    rejectOutput: fault == "wrong-type" || fault == "malformed");
                if (fault == "error" || fault == "null") await StatementCommand(channel, 25);
            }, lobLocators: true, serverVersion: "4.3.5.0");
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync();
                var error = await Assert.ThrowsAsync<OceanBaseException>(async () =>
                { using var lob = async ? await connection.CreateTemporaryLobAsync(OceanBaseDbType.Blob) : connection.CreateTemporaryLob(OceanBaseDbType.Blob); });
                Assert.Equal(fault != "error" && fault != "null", error.IsExecutionOutcomeUnknown);
                if (fault == "wrong-type" || fault == "malformed") Assert.IsType<ProtocolException>(error.InnerException);
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            catch { if (server.Completion.IsFaulted) await server.Completion; throw; }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("error")] [InlineData("null")] [InlineData("marker")] [InlineData("lob")] [InlineData("disconnect")]
        public async Task TemporaryLobFreeInvalidatesItsOwnerWithoutReplayingOnFailure(string fault)
        {
            var locator = InlineLob(Array.Empty<byte>());
            var server = Server(async channel =>
            {
                await TemporaryLobCreateRequest(channel, false);
                await LobMutationReply(channel, 210, locator, parameters: 1); await StatementCommand(channel, 25); channel.BeginCommand();
                await TemporaryLobFreeRequest(channel, false, locator);
                if (fault == "disconnect") return;
                await LobMutationReply(channel, fault == "lob" ? (byte)210 : (byte)253,
                    fault == "null" ? null : fault == "lob" ? locator : Encoding.UTF8.GetBytes(fault == "marker" ? "0" : "1"), fault == "error", parameters: 2, rejectOutput: fault == "lob");
                if (fault == "error")
                { await StatementCommand(channel, 25); channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1)); }
            }, lobLocators: true, serverVersion: "4.3.5.0");
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync(); using var lob = await connection.CreateTemporaryLobAsync(OceanBaseDbType.Blob);
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => lob.FreeTemporaryAsync());
                Assert.Equal(fault == "lob" || fault == "disconnect", error.IsExecutionOutcomeUnknown);
                if (fault == "lob") Assert.IsType<ProtocolException>(error.InnerException);
                Assert.Throws<ObjectDisposedException>(() => lob.FreeTemporary()); Assert.Throws<ObjectDisposedException>(() => lob.OpenRead());
                if (fault == "error") { using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync()); }
                else Assert.Equal(ConnectionState.Broken, connection.State);
            }
            catch { if (server.Completion.IsFaulted) await server.Completion; throw; }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "cancel")] [InlineData(false, "timeout")]
        [InlineData(true, "cancel")] [InlineData(true, "timeout")]
        [InlineData(true, "dispose")] [InlineData(true, "dispose-async")]
        public async Task TemporaryLobUnconfirmedOperationsKeepUnknownOutcomeAndCloseTheSession(bool freeing, string cause)
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var locator = InlineLob(Array.Empty<byte>());
            var server = Server(async channel =>
            {
                await TemporaryLobCreateRequest(channel, false);
                if (freeing)
                {
                    await LobMutationReply(channel, 210, locator, parameters: 1); await StatementCommand(channel, 25); channel.BeginCommand();
                    await TemporaryLobFreeRequest(channel, false, locator);
                }
                started.TrySetResult(); await Assert.ThrowsAsync<ProtocolException>(() => channel.ReadPacketAsync());
            }, lobLocators: true, serverVersion: "4.3.5.0");
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + (cause == "timeout" ? ";Command Timeout=1" : ""));
            OceanBaseLob? lob = null;
            try
            {
                await connection.OpenAsync(); using var caller = new CancellationTokenSource();
                if (freeing) lob = await connection.CreateTemporaryLobAsync(OceanBaseDbType.Blob);
                var pending = freeing ? lob!.FreeTemporaryAsync(caller.Token) : (Task)connection.CreateTemporaryLobAsync(OceanBaseDbType.Blob, cancellationToken: caller.Token);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (cause == "cancel") caller.Cancel(); else if (cause == "dispose") lob!.Dispose(); else if (cause == "dispose-async") await lob!.DisposeAsync();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5))); Assert.True(error.IsExecutionOutcomeUnknown);
                if (cause == "timeout") Assert.IsType<TimeoutException>(error.InnerException);
                else
                {
                    var canceled = Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
                    if (cause == "cancel") Assert.Equal(caller.Token, canceled.CancellationToken);
                }
                Assert.Equal(ConnectionState.Broken, connection.State);
                if (lob != null) Assert.Throws<ObjectDisposedException>(() => lob.FreeTemporary());
            }
            finally { lob?.Dispose(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
