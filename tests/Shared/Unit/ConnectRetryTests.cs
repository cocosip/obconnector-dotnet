using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class ConnectRetryTests
    {

        [Theory]
#if SECURITY_ACCEPTANCE
        [InlineData(false, "tls")]
        [InlineData(true, "tls")]
#else
        [InlineData(false, "greeting")]
        [InlineData(true, "greeting")]
        [InlineData(false, "authentication")]
        [InlineData(true, "authentication")]
        [InlineData(false, "identity")]
        [InlineData(true, "identity")]
        [InlineData(false, "command")]
        [InlineData(true, "command")]
#endif
        public async Task RetryPolicyNeverRetriesAfterTcpSucceeded(bool asynchronous, string stage)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var connection = new OceanBaseConnection(Configuration(((IPEndPoint)listener.LocalEndpoint).Port) + ";Connect Retry Count=10" + (stage == "tls" ? ";TLS Mode=Required" : ""));
            using var capture = new TcpAttempts();
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(stop.Token);
                if (stage == "greeting") return;
                var channel = new PacketChannel(peer.GetStream());
                await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, stop.Token);
                await channel.ReadPacketAsync(stop.Token);
                if (stage == "tls") return;
                if (stage != "authentication")
                {
                    await channel.WritePacketAsync(new byte[] { 0, 0, 0, 2, 0, 0, 0 }, stop.Token);
                    if (stage == "command")
                    {
                        await ServerIdentity.ReplyAsync(channel, false, token: stop.Token);
                        Assert.Equal(3, (await TextCommandTests.ReadClientCommand(channel))[0]);
                    }
                    else { channel.BeginCommand(); Assert.Equal(3, (await channel.ReadPacketAsync(stop.Token))[0]); }
                }
                await channel.WritePacketAsync(new byte[] { 255, 21, 4 }.Concat(Encoding.ASCII.GetBytes("#08S01 private-detail")).ToArray(), stop.Token);
                Assert.Equal(0, await peer.GetStream().ReadAsync(new byte[1], stop.Token));
            });
            try
            {
                if (stage == "command")
                {
                    await Open(connection, asynchronous, stop.Token);
                    using var command = connection.CreateCommand(); command.CommandText = "fail once";
                    var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? command.ExecuteNonQueryAsync(stop.Token) : Task.Run(command.ExecuteNonQuery));
                    Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Broken, connection.State);
                }
                else await Assert.ThrowsAsync<OceanBaseException>(() => Open(connection, asynchronous, stop.Token));
                connection.Close(); await serving.WaitAsync(stop.Token);
                Assert.Equal(new[] { "success" }, capture.Outcomes); Assert.False(listener.Pending());
            }
            finally { connection.Close(); stop.Cancel(); listener.Stop(); await ObserveStopped(serving, stop.Token); }
        }
        private static string Configuration(int port) => $"Server=127.0.0.1;Port={port};User ID=u;TLS Mode=Disabled;Connection Timeout=8;Connect Retry Count=1;Connect Retry Interval=1";
        private static Task Open(OceanBaseConnection connection, bool asynchronous, CancellationToken token = default) => asynchronous ? connection.OpenAsync(token) : Task.Run(connection.Open, token);
        private static async Task ObserveStopped(Task serving, CancellationToken stopped)
        {
            try { await serving; }
            catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
        }

        private sealed class TcpAttempts : IDisposable
        {
            private readonly MeterListener _listener = new();
            private readonly Activity _scope = new Activity("retry-test").Start();
            private readonly string _id = Guid.NewGuid().ToString("N");
            private readonly List<string> _outcomes = new();
            private readonly List<long> _timestamps = new();
            internal double SecondsBetweenAttempts => (_timestamps[1] - _timestamps[0]) / (double)Stopwatch.Frequency;
            internal string[] Outcomes => _outcomes.ToArray();
            internal TcpAttempts(Action? onFirstFailure = null, Action<int>? onAttempt = null)
            {
                _scope.SetBaggage("retry-test", _id);
                _listener.InstrumentPublished = (instrument, listener) =>
                { if (instrument.Meter.Name == "OceanBaseConnector" && instrument.Name == "oceanbase.client.connection.phase.duration") listener.EnableMeasurementEvents(instrument); };
                _listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
                {
                    if (Activity.Current?.GetBaggageItem("retry-test") != _id) return;
                    var values = tags.ToArray();
                    if (!values.Any(tag => tag.Key == "oceanbase.connection.phase" && Equals(tag.Value, "tcp"))) return;
                    var outcome = (string)values.Single(tag => tag.Key == "oceanbase.outcome").Value!;
                    _timestamps.Add(Stopwatch.GetTimestamp());
                    _outcomes.Add(outcome); onAttempt?.Invoke(_outcomes.Count);
                    if (_outcomes.Count == 1 && outcome == "error") onFirstFailure?.Invoke();
                });
                _listener.Start();
            }
            public void Dispose() { _listener.Dispose(); _scope.Dispose(); }
        }
    }
}
