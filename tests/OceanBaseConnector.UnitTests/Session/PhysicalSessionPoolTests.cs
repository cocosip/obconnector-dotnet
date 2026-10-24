using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests.Session
{
    public sealed class PhysicalSessionPoolTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ReturnResetsAndRentReusesPhysicalSession(bool asynchronous)
        {
            var resetSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
                await channel.ReadPacketAsync();
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 2, 0, 0, 0 });
                await ServerIdentity.ReplyAsync(channel, oracle: false);
                var reset = await channel.ReadPacketAsync();
                Assert.Equal(new byte[] { 0x1f }, reset);
                resetSeen.SetResult();
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 });
                channel.BeginCommand();
                var initDb = await channel.ReadPacketAsync();
                Assert.Equal((byte)0x02, initDb[0]);
                Assert.Equal("app", System.Text.Encoding.UTF8.GetString(initDb, 1, initDb.Length - 1));
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 });
                channel.BeginCommand();
                var ping = await channel.ReadPacketAsync();
                Assert.Equal(new byte[] { 0x0e }, ping);
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 });
            });

            var options = new OceanBaseConnectionStringBuilder
            {
                Server = "127.0.0.1",
                Port = server.Port,
                UserId = "u",
                Password = "p",
                Database = "app",
                TlsMode = OceanBaseTlsMode.Disabled,
                ConnectionTimeout = 5,
                Pooling = true,
            }.Build();
            using var pool = new PhysicalSessionPool();
            var first = pool.Rent(options, null, new EndpointSelector(), out var reused);
            Assert.False(reused);
            await first.OpenAsync();
            var returned = asynchronous
                ? await pool.ReturnAsync(first, new ConnectionLog(null))
                : pool.Return(first, new ConnectionLog(null));
            Assert.True(returned);
            await resetSeen.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var second = pool.Rent(options, null, new EndpointSelector(), out reused);
            Assert.True(reused);
            Assert.Same(first, second);
            second.Close();
            server.Stop();
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public void BrokenSessionIsDestroyedInsteadOfReturned()
        {
            var options = new OceanBaseConnectionStringBuilder
            {
                Server = "127.0.0.1",
                UserId = "u",
                Password = "p",
                Pooling = true,
            }.Build();
            using var pool = new PhysicalSessionPool();
            var session = pool.Rent(options, null, new EndpointSelector(), out _);
            session.Break();
            Assert.False(pool.Return(session, new ConnectionLog(null)));
            Assert.False(pool.TryRent(out _));
        }

        [Fact]
        public async Task PoolWaitsAndTimesOutWhenMaximumIsBorrowed()
        {
            var options = new OceanBaseConnectionStringBuilder
            {
                Server = "127.0.0.1",
                UserId = "u",
                Password = "p",
                Pooling = true,
                MaxPoolSize = 1,
                PoolWaitTimeout = 1,
            }.Build();
            using var pool = new PhysicalSessionPool();
            var first = pool.Rent(options, null, new EndpointSelector(), out _);

            await Assert.ThrowsAsync<TimeoutException>(() =>
                pool.RentAsync(options, null, new EndpointSelector(), cancellationToken: default));

            first.Close();
        }

        [Fact]
        public async Task PoolWaitCanBeCanceledWhenMaximumIsBorrowed()
        {
            var options = new OceanBaseConnectionStringBuilder
            {
                Server = "127.0.0.1", UserId = "u", Password = "p", Pooling = true,
                MaxPoolSize = 1, PoolWaitTimeout = 30,
            }.Build();
            using var pool = new PhysicalSessionPool();
            var first = pool.Rent(options, null, new EndpointSelector(), out _);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                pool.RentAsync(options, null, new EndpointSelector(), cancellationToken: cancellation.Token));

            first.Close();
        }

        [Fact]
        public async Task ExpiredIdleSessionIsDiscardedBeforeReuse()
        {
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
                await channel.ReadPacketAsync();
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 2, 0, 0, 0 });
                await ServerIdentity.ReplyAsync(channel, oracle: false);
                Assert.Equal(new byte[] { 0x1f }, await channel.ReadPacketAsync());
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 });
            });
            var options = new OceanBaseConnectionStringBuilder
            {
                Server = "127.0.0.1",
                Port = server.Port,
                UserId = "u",
                Password = "p",
                TlsMode = OceanBaseTlsMode.Disabled,
                ConnectionTimeout = 5,
                Pooling = true,
                ConnectionIdleLifetime = 1,
            }.Build();
            using var pool = new PhysicalSessionPool();
            var first = pool.Rent(options, null, new EndpointSelector(), out var reused);
            await first.OpenAsync();
            Assert.True(pool.Return(first, new ConnectionLog(null)));
            await Task.Delay(TimeSpan.FromSeconds(1.1));

            var second = pool.Rent(options, null, new EndpointSelector(), out reused);
            Assert.False(reused);
            Assert.NotSame(first, second);
            second.Close();
            server.Stop();
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task IdleLifetimeDoesNotExpireWhileSessionIsBorrowed()
        {
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
                await channel.ReadPacketAsync();
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 2, 0, 0, 0 });
                await ServerIdentity.ReplyAsync(channel, oracle: false);
                Assert.Equal(new byte[] { 0x1f }, await channel.ReadPacketAsync());
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 });
                channel.BeginCommand();
                var initDb = await channel.ReadPacketAsync();
                Assert.Equal((byte)0x02, initDb[0]);
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 });
                channel.BeginCommand();
                Assert.Equal(new byte[] { 0x0e }, await channel.ReadPacketAsync());
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 });
            });
            var options = new OceanBaseConnectionStringBuilder
            {
                Server = "127.0.0.1", Port = server.Port, UserId = "u", Password = "p",
                Database = "app", TlsMode = OceanBaseTlsMode.Disabled, ConnectionTimeout = 5,
                Pooling = true, ConnectionIdleLifetime = 1,
            }.Build();
            using var pool = new PhysicalSessionPool();
            var first = pool.Rent(options, null, new EndpointSelector(), out var reused);
            await first.OpenAsync();
            await Task.Delay(TimeSpan.FromSeconds(1.1));

            Assert.True(pool.Return(first, new ConnectionLog(null)));
            var second = pool.Rent(options, null, new EndpointSelector(), out reused);
            Assert.True(reused);
            Assert.Same(first, second);
            second.Close();
            server.Stop();
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task ResetFailureDestroysSessionInsteadOfReturningIt()
        {
            var resetFailures = 0;
            using var listener = new MeterListener
            {
                InstrumentPublished = (instrument, owner) =>
                {
                    if (instrument.Meter.Name == "OceanBaseConnector") owner.EnableMeasurementEvents(instrument);
                },
                MeasurementsCompleted = (_, _) => { },
            };
            listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
            {
                if (instrument.Name == "oceanbase.client.pool.reset.failures") Interlocked.Add(ref resetFailures, (int)value);
            });
            listener.Start();
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
                await channel.ReadPacketAsync();
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 2, 0, 0, 0 });
                await ServerIdentity.ReplyAsync(channel, oracle: false);
                Assert.Equal(new byte[] { 0x1f }, await channel.ReadPacketAsync());
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 1, 0, 0, 0 });
            });
            var options = new OceanBaseConnectionStringBuilder
            {
                Server = "127.0.0.1", Port = server.Port, UserId = "u", Password = "p",
                TlsMode = OceanBaseTlsMode.Disabled, ConnectionTimeout = 5, Pooling = true,
            }.Build();
            using var pool = new PhysicalSessionPool();
            var session = pool.Rent(options, null, new EndpointSelector(), out _);
            await session.OpenAsync();
            Assert.False(await pool.ReturnAsync(session, new ConnectionLog(null)));
            Assert.False(pool.TryRent(out _));
            Assert.Equal(1, Volatile.Read(ref resetFailures));
            server.Stop();
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task FailedOpenRemovesEmptyRegistryPool()
        {
            var server = LoopbackServer.Start(async client =>
            {
                client.Dispose();
                await Task.CompletedTask;
            });
            var connectionString = new OceanBaseConnectionStringBuilder
            {
                Server = "127.0.0.1",
                Port = server.Port,
                UserId = "registry-test-user",
                Password = "p",
                TlsMode = OceanBaseTlsMode.Disabled,
                ConnectionTimeout = 1,
                Pooling = true,
            }.ConnectionString;
            var original = PhysicalSessionPoolRegistry.Get(connectionString);
            try
            {
                using var connection = new OceanBaseConnection(connectionString);
                await Assert.ThrowsAsync<OceanBaseConnector.OceanBaseException>(() => connection.OpenAsync().WaitAsync(TimeSpan.FromSeconds(10)));

                var replacement = PhysicalSessionPoolRegistry.Get(connectionString);
                Assert.NotSame(original, replacement);
                PhysicalSessionPoolRegistry.ReleaseIfEmpty(connectionString, replacement);
            }
            finally
            {
                PhysicalSessionPoolRegistry.ReleaseIfEmpty(connectionString, original);
                server.Stop();
            }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MinimumPoolSizePrewarmsPhysicalSessionsBeforeRent(bool asynchronous)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var opened = 0;
            async Task Serve(TcpClient client)
            {
                using (client)
                {
                    var channel = new PacketChannel(client.GetStream());
                    await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
                    await channel.ReadPacketAsync();
                    await channel.WritePacketAsync(new byte[] { 0, 0, 0, 2, 0, 0, 0 });
                    await ServerIdentity.ReplyAsync(channel, oracle: false);
                    Interlocked.Increment(ref opened);
                    try
                    {
                        var packet = await channel.ReadPacketAsync().WaitAsync(TimeSpan.FromSeconds(10));
                        Assert.Equal((byte)0x0e, packet[0]);
                        await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 });
                    }
                    catch (EndOfStreamException) { }
                    catch (IOException) { }
                    catch (ProtocolException) { }
                }
            }
            var serving = Task.Run(async () =>
            {
                var clients = new List<TcpClient>();
                try
                {
                    var conversations = new List<Task>();
                    for (var i = 0; i < 2; i++)
                    {
                        var client = await listener.AcceptTcpClientAsync();
                        clients.Add(client);
                        conversations.Add(Serve(client));
                    }
                    await Task.WhenAll(conversations);
                }
                finally
                {
                    foreach (var client in clients) client.Dispose();
                }
            });

            var options = new OceanBaseConnectionStringBuilder
            {
                Server = "127.0.0.1",
                Port = ((IPEndPoint)listener.LocalEndpoint).Port,
                UserId = "u",
                Password = "p",
                TlsMode = OceanBaseTlsMode.Disabled,
                ConnectionTimeout = 5,
                Pooling = true,
                MinPoolSize = 2,
                MaxPoolSize = 2,
            }.Build();
            using var pool = new PhysicalSessionPool();
            var borrowed = asynchronous
                ? await pool.RentAsync(options, null, new EndpointSelector())
                : pool.Rent(options, null, new EndpointSelector(), out _);
            Assert.Equal(2, Volatile.Read(ref opened));
            Assert.True(pool.TryRent(out var prewarmed));
            Assert.NotSame(borrowed, prewarmed);
            pool.Discard(borrowed);
            pool.Discard(prewarmed!);
            listener.Stop();
            await serving.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task MinimumPoolWarmupFailureRollsBackSessionsCreatedByTheAttempt()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var authenticated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var serving = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
                await channel.ReadPacketAsync();
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 2, 0, 0, 0 });
                await ServerIdentity.ReplyAsync(channel, oracle: false);
                authenticated.TrySetResult();
                await client.GetStream().ReadUpToAsync(new byte[1], 1);
            });
            var options = new OceanBaseConnectionStringBuilder
            {
                Server = "127.0.0.1",
                Port = ((IPEndPoint)listener.LocalEndpoint).Port,
                UserId = "u",
                Password = "p",
                TlsMode = OceanBaseTlsMode.Disabled,
                ConnectionTimeout = 1,
                Pooling = true,
                MinPoolSize = 2,
                MaxPoolSize = 2,
            }.Build();
            using var pool = new PhysicalSessionPool();
            var opening = pool.RentAsync(options, null, new EndpointSelector());
            await authenticated.Task.WaitAsync(TimeSpan.FromSeconds(10));
            listener.Stop();
            await Assert.ThrowsAsync<OceanBaseConnector.OceanBaseException>(() => opening);
            Assert.False(pool.TryRent(out _));
            await serving.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task PoolMetricsReportSessionsBorrowsWaitersAndDiscards()
        {
            var longs = new List<(string Name, long Value)>();
            var doubles = new List<(string Name, double Value)>();
            using var listener = new MeterListener
            {
                InstrumentPublished = (instrument, owner) =>
                {
                    if (instrument.Meter.Name == "OceanBaseConnector") owner.EnableMeasurementEvents(instrument);
                },
                MeasurementsCompleted = (_, _) => { },
            };
            listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => longs.Add((instrument.Name, value)));
            listener.SetMeasurementEventCallback<double>((instrument, value, _, _) => doubles.Add((instrument.Name, value)));
            listener.Start();
            var options = new OceanBaseConnectionStringBuilder
            {
                Server = "127.0.0.1",
                UserId = "u",
                Password = "p",
                Pooling = true,
                MaxPoolSize = 1,
                PoolWaitTimeout = 1,
            }.Build();
            using var pool = new PhysicalSessionPool();
            var first = pool.Rent(options, null, new EndpointSelector(), out _);
            await Assert.ThrowsAsync<TimeoutException>(() => pool.RentAsync(options, null, new EndpointSelector()));
            pool.Discard(first);
            Assert.Contains(longs, item => item == ("oceanbase.client.pool.borrowed", 1));
            Assert.Contains(longs, item => item == ("oceanbase.client.pool.sessions", 1));
            Assert.Contains(longs, item => item == ("oceanbase.client.pool.sessions", -1));
            Assert.Contains(longs, item => item == ("oceanbase.client.pool.waiters", 1));
            Assert.Contains(longs, item => item == ("oceanbase.client.pool.waiters", -1));
            Assert.Contains(longs, item => item == ("oceanbase.client.pool.discarded", 1));
            Assert.Contains(doubles, item => item.Name == "oceanbase.client.pool.wait.duration" && item.Value >= 0.5);
        }
    }
}
