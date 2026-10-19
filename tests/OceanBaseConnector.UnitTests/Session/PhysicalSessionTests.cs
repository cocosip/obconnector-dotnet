using System;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests.Session
{
    public sealed class PhysicalSessionTests
    {
        private static LoopbackServer StartServer(bool truncate = false)
        {
            return LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
                await channel.ReadPacketAsync();
                if (truncate) return;
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 });
                _ = await client.GetStream().ReadAsync(new byte[1]);
            });
        }

        private static PhysicalSession Create(int port, int timeout = 5)
        {
            return new PhysicalSession(new OceanBaseConnectionStringBuilder
            {
                Server = "127.0.0.1",
                Port = port,
                UserId = "u",
                Password = "hidden-secret",
                TlsMode = OceanBaseTlsMode.Disabled,
                ConnectionTimeout = timeout,
            }.Build());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OpenLeaseCompleteAndClose(bool asynchronous)
        {
            var server = StartServer();
            using var session = Create(server.Port);
            try
            {
                Assert.Equal(SessionState.Closed, session.State);
                if (asynchronous) await session.OpenAsync(); else session.Open();
                Assert.Equal(OceanBaseCompatibilityMode.Oracle, session.Info!.Mode);
                using (var lease = session.Acquire())
                {
                    Assert.Equal(SessionState.Busy, session.State);
                    Assert.Throws<InvalidOperationException>(() => session.Acquire());
                    lease.Complete();
                }
                Assert.Equal(SessionState.Open, session.State);
                await session.CloseAsync();
                session.Close();
                Assert.Equal(SessionState.Closed, session.State);
                Assert.Throws<InvalidOperationException>(() => session.Acquire());
                Assert.Throws<InvalidOperationException>(() => session.Open());
            }
            finally { session.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task ExpiredLeaseCancellationCannotBreakSubsequentCommand()
        {
            var server = StartServer();
            using var session = Create(server.Port);
            try
            {
                await session.OpenAsync();
                using var budget = new CancellationTokenSource();
                using var cleanup = session.Acquire();
                using var cancellation = budget.Token.Register(cleanup.Dispose);
                cleanup.CompleteIfActive();
                using var next = session.Acquire();
                budget.Cancel();
                Assert.Equal(SessionState.Busy, session.State);
                Assert.NotNull(next.Channel);
                next.Complete();
                Assert.Equal(SessionState.Open, session.State);
            }
            finally { session.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task UnconsumedLeaseBreaksSessionAndClosesSocket()
        {
            var server = StartServer();
            using var session = Create(server.Port);
            try
            {
                await session.OpenAsync();
                var lease = session.Acquire();
                lease.Dispose();
                lease.Dispose();
                Assert.Equal(SessionState.Broken, session.State);
                Assert.Throws<InvalidOperationException>(() => session.Acquire());
                Assert.Throws<InvalidOperationException>(() => lease.Complete());
            }
            finally { session.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task TruncatedAuthenticationBreaksSessionWithSafeException()
        {
            var server = StartServer(truncate: true);
            using var session = Create(server.Port);
            try
            {
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => session.OpenAsync());
                Assert.NotNull(error.InnerException);
                Assert.False(error.IsExecutionOutcomeUnknown);
                Assert.DoesNotContain("hidden-secret", error.ToString());
                Assert.Equal(SessionState.Broken, session.State);
            }
            finally { session.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ConnectionBudgetIncludesSilentHandshake(bool asynchronous)
        {
            var server = LoopbackServer.Start(async client =>
            {
                _ = await client.GetStream().ReadAsync(new byte[1]);
            });
            using var session = Create(server.Port, timeout: 1);
            try
            {
                if (asynchronous)
                    await Assert.ThrowsAsync<TimeoutException>(() => session.OpenAsync().WaitAsync(TimeSpan.FromSeconds(10)));
                else
                    Assert.Throws<TimeoutException>(() => session.Open());
                Assert.Equal(SessionState.Broken, session.State);
            }
            finally { session.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "timeout")]
        [InlineData(true, "timeout")]
        [InlineData(false, "close")]
        [InlineData(true, "close")]
        [InlineData(true, "cancel")]
        public async Task SilentTlsHandshakeIsInterruptedAndClosesTransport(bool asynchronous, string interruption)
        {
            var tlsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = LoopbackServer.Start(async client =>
            {
                var peer = client.GetStream();
                var channel = new PacketChannel(peer);
                await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
                Assert.Equal(32, (await channel.ReadPacketAsync()).Length);
                var first = new byte[1];
                Assert.Equal(1, await peer.ReadUpToAsync(first, 1));
                Assert.Equal(22, first[0]); // TLS handshake record, before any authentication response.
                tlsStarted.SetResult();
                var buffer = new byte[1024];
                while (await peer.ReadUpToAsync(buffer, buffer.Length) != 0) { }
            });
            using var session = new PhysicalSession(new OceanBaseConnectionStringBuilder
            {
                Server = "127.0.0.1", Port = server.Port, UserId = "u", Password = "hidden-secret",
                TlsMode = OceanBaseTlsMode.VerifyFull, ConnectionTimeout = interruption == "timeout" ? 2 : 10,
            }.Build());
            using var caller = new CancellationTokenSource();
            try
            {
                var opening = asynchronous ? session.OpenAsync(caller.Token) : Task.Run(session.Open);
                await tlsStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(SessionState.Connecting, session.State);
                if (interruption == "close") session.Close();
                if (interruption == "cancel") caller.Cancel();
                if (interruption == "timeout")
                    await Assert.ThrowsAsync<TimeoutException>(() => opening.WaitAsync(TimeSpan.FromSeconds(10)));
                else
                {
                    var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TimeSpan.FromSeconds(10)));
                    if (interruption == "cancel") Assert.Equal(caller.Token, error.CancellationToken);
                }
                Assert.Equal(interruption == "close" ? SessionState.Closed : SessionState.Broken, session.State);
                Assert.Null(session.Info);
                Assert.Throws<InvalidOperationException>(() => session.Acquire());
                await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { session.Close(); server.Stop(); }
        }

        [Fact]
        public async Task CloseDuringHandshakeCancelsAndCannotResurrectSession()
        {
            var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = LoopbackServer.Start(async client =>
            {
                accepted.SetResult();
                _ = await client.GetStream().ReadAsync(new byte[1]);
            });
            using var session = Create(server.Port);
            try
            {
                var opening = session.OpenAsync();
                await accepted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(SessionState.Connecting, session.State);
                session.Close();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
                Assert.Equal(SessionState.Closed, session.State);
            }
            finally { session.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task CallerCancellationAfterOpenDoesNotCloseSession()
        {
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
                await channel.ReadPacketAsync();
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 });
                channel.BeginCommand();
                Assert.Equal(new byte[] { 0x0E }, await channel.ReadPacketAsync());
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 });
            });
            using var session = Create(server.Port);
            using var cts = new CancellationTokenSource();
            try
            {
                await session.OpenAsync(cts.Token);
                cts.Cancel();
                using var lease = session.Acquire();
                await lease.Channel.WritePacketAsync(new byte[] { 0x0E });
                var ok = OkPacket.Parse(await lease.Channel.ReadPacketAsync(), CapabilityFlags.ClientProtocol41);
                Assert.Equal(ServerStatusFlags.Autocommit | ServerStatusFlags.ObServerUseOracleMode, ok.StatusFlags);
                lease.Complete();
                Assert.Equal(SessionState.Open, session.State);
            }
            finally { session.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task PreCanceledOpenSendsNoCredentials()
        {
            using var session = Create(1);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.OpenAsync(cts.Token));
            Assert.Equal(SessionState.Broken, session.State);
        }
    }
}
