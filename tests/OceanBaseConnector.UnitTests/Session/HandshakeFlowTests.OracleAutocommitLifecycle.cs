using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests.Session
{
    public sealed partial class HandshakeFlowTests
    {
        public static IEnumerable<object[]> OracleAutocommitInterruptions()
        {
            foreach (var asynchronous in new[] { false, true })
                foreach (var reset in new[] { false, true })
                    foreach (var interruption in new[] { "cancel", "close", "timeout" })
                        yield return new object[] { asynchronous, reset, interruption };
        }

        [Theory]
        [MemberData(nameof(OracleAutocommitInterruptions))]
        public async Task PendingOracleAutocommitCannotPublishOrRetainAPoolSlot(bool asynchronous, bool reset, string interruption)
        {
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(BuildInitialHandshake(), stop.Token);
                await channel.ReadPacketAsync(stop.Token);
                await channel.WritePacketAsync(BuildAuthOk(oracleMode: true), stop.Token);
                await IdentityResponse(channel, oracle: true, initializeAutocommit: reset);
                if (reset)
                {
                    channel.BeginCommand();
                    Assert.Equal(new byte[] { 31 }, await channel.ReadPacketAsync(stop.Token));
                    await channel.WritePacketAsync(new byte[] { 0, 0, 0, 4, 0, 0, 0 }, stop.Token);
                }
                channel.BeginCommand();
                Assert.Equal(OceanBaseCommand.SqlPacket("set autocommit = 1", 3), await channel.ReadPacketAsync(stop.Token));
                received.SetResult();
                try { Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1], stop.Token)); }
                catch (IOException) { }
            });
            var options = new OceanBaseConnectionStringBuilder
            {
                Server = "127.0.0.1",
                Port = server.Port,
                UserId = "u",
                TlsMode = OceanBaseTlsMode.Disabled,
                Pooling = true,
                MaxPoolSize = 1,
                PoolWaitTimeout = 1,
                ConnectionTimeout = !reset && interruption == "timeout" ? 1 : 5,
            }.Build();
            using var pool = new PhysicalSessionPool();
            using var caller = new CancellationTokenSource();
            var session = pool.Rent(options, null, new EndpointSelector(), out _);
            try
            {
                if (reset) await session.OpenAsync(stop.Token);
                var generation = session.Generation;
                var operation = Task.Run(async () =>
                {
                    if (reset)
                    {
                        if (asynchronous) await session.ResetAsync(new ConnectionLog(null), 1, caller.Token);
                        else session.Reset(new ConnectionLog(null), 1, caller.Token);
                    }
                    else if (asynchronous) await session.OpenAsync(caller.Token);
                    else session.Open(caller.Token);
                });
                await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(operation.IsCompleted);
                Assert.Equal(reset ? SessionState.Busy : SessionState.Connecting, session.State);
                Assert.Equal(generation, session.Generation);
                if (interruption == "cancel") caller.Cancel();
                else if (interruption == "close") session.Close();
                var error = await Record.ExceptionAsync(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.NotNull(error);
                if (!reset && interruption == "cancel")
                    Assert.Equal(caller.Token, Assert.IsAssignableFrom<OperationCanceledException>(error).CancellationToken);
                if (!reset && interruption == "timeout") Assert.IsType<TimeoutException>(error);
                Assert.NotEqual(SessionState.Open, session.State);
                Assert.Equal(generation, session.Generation);
                Assert.False(pool.Return(session, new ConnectionLog(null), timeout: 1));
                Assert.Equal(SessionState.Closed, session.State);
                var replacement = pool.Rent(options, null, new EndpointSelector(), out var reused);
                Assert.False(reused);
                Assert.NotSame(session, replacement);
                replacement.Close();
                await server.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            }
            finally { session.Close(); server.Stop(); }
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(false, false, true)]
        [InlineData(false, true, false)]
        [InlineData(false, true, true)]
        [InlineData(true, false, false)]
        [InlineData(true, false, true)]
        [InlineData(true, true, false)]
        [InlineData(true, true, true)]
        public async Task OracleAutocommitObservesFreshMetadataAndClearsResetMetadata(bool asynchronous, bool reset, bool report)
        {
            byte[] State(string zone, string national) => TextCommandTests.SessionVariablesOk(
                new NationalCharacterSet(national, "utf8mb4", "utf8mb4").Variables(zone));
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(BuildInitialHandshake());
                await channel.ReadPacketAsync();
                await channel.WritePacketAsync(State("+01:00", "utf8mb4"));
                await IdentityResponse(channel, oracle: true, initializeAutocommit: false);
                channel.BeginCommand();
                Assert.Equal(OceanBaseCommand.SqlPacket("set autocommit = 1", 3), await channel.ReadPacketAsync());
                await channel.WritePacketAsync(report || reset ? State("+02:00", "utf16") : AutocommitResponse("ok"));
                if (reset)
                {
                    channel.BeginCommand();
                    Assert.Equal(new byte[] { 31 }, await channel.ReadPacketAsync());
                    await channel.WritePacketAsync(State("+08:00", "gbk"));
                    channel.BeginCommand();
                    Assert.Equal(OceanBaseCommand.SqlPacket("set autocommit = 1", 3), await channel.ReadPacketAsync());
                    await channel.WritePacketAsync(report ? State("+05:00", "gb18030") : AutocommitResponse("ok"));
                }
                Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1]));
            });
            using var session = new PhysicalSession(new OceanBaseConnectionStringBuilder
            {
                Server = "127.0.0.1",
                Port = server.Port,
                UserId = "u",
                TlsMode = OceanBaseTlsMode.Disabled,
                ConnectionTimeout = 5,
            }.Build());
            try
            {
                if (asynchronous) await session.OpenAsync(); else session.Open();
                Assert.Equal(report || reset ? "+02:00" : "+01:00", session.SessionTimeZone);
                Assert.Equal(report || reset ? "utf16" : "utf8mb4", session.NationalCharacterSet!.National);
                if (reset)
                {
                    if (asynchronous) await session.ResetAsync(new ConnectionLog(null), 2); else session.Reset(new ConnectionLog(null), 2);
                    Assert.Equal(report ? "+05:00" : null, session.SessionTimeZone);
                    Assert.Equal(report ? "gb18030" : null, session.NationalCharacterSet?.National);
                }
                session.Close();
                await server.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            }
            finally { session.Close(); server.Stop(); }
        }
    }
}
