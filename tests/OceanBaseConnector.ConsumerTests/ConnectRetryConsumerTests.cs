using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed partial class Ob20ConsumerTests
    {
        public static IEnumerable<object[]> RetryAssets()
        {
            foreach (var target in ConsumerTargetMatrix.Representative)
                foreach (var mysql in new[] { false, true })
                    foreach (var asynchronous in new[] { false, true })
                        foreach (var path in target.StartsWith("netstandard", StringComparison.Ordinal) ? new[] { 0, 1 } : new[] { 0, 1, 2 })
                            yield return new object[] { target, mysql, asynchronous, path };
        }

        [Theory]
        [MemberData(nameof(RetryAssets))]
        public async Task EveryAssetRetriesOnlyTcpAndExecutesOnTheRecoveredEndpoint(string target, bool mysql, bool asynchronous, int path)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(18));
            using var endpoint = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            endpoint.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var port = ((IPEndPoint)endpoint.LocalEndPoint!).Port;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var capture = new RetryTcpAttempts(() => { endpoint.Listen(1); ready.TrySetResult(); });
            var serving = Task.Run(async () =>
            {
                await ready.Task.WaitAsync(stop.Token);
                using var peer = new TcpClient { Client = await endpoint.AcceptAsync(stop.Token), NoDelay = true };
                var channel = new PacketChannel(peer.GetStream());
                await Handshake(channel, peer.GetStream(), "unrequested", stop.Token, mysql: mysql);
                var request = await SessionTimeZoneFixture.ReadCommand(channel, stop.Token, expectProbe: false);
                Assert.Equal(3, request[0]); Assert.Equal("after bounded retry", Encoding.UTF8.GetString(request, 1, request.Length - 1));
                await channel.WritePacketAsync(Report(false, 1), stop.Token);
                if (path != 0)
                {
                    channel.BeginCommand(); Assert.Equal(31, (await channel.ReadPacketAsync(stop.Token))[0]);
                    await channel.WritePacketAsync(Report(false, 0), stop.Token);
                    if (!mysql) await OceanBaseConnector.UnitTests.TestInfrastructure.ServerIdentity.ReplyOracleAutocommitAsync(channel, stop.Token);
                }
                Assert.Equal(0, await peer.GetStream().ReadAsync(new byte[1], stop.Token));
            });
            var context = new AssemblyLoadContext("bounded-retry-" + target, isCollectible: true);
            DbConnection? connection = null; DbDataSource? source = null;
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var type = assembly.GetType("OceanBaseConnector.OceanBaseConnection")!;
                var configuration = $"Server=127.0.0.1;Port={port};User ID=u;TLS Mode=Disabled;Connection Timeout=8;Connect Retry Count=1;Connect Retry Interval=1;Pooling={path != 0};Max Pool Size=1";
                var builderType = assembly.GetType("OceanBaseConnector.OceanBaseConnectionStringBuilder")!;
                var builder = (DbConnectionStringBuilder)Activator.CreateInstance(builderType, configuration)!;
                Assert.Equal(1, builderType.GetProperty("ConnectRetryCount")!.GetValue(builder));
                Assert.Equal(1, builderType.GetProperty("ConnectRetryInterval")!.GetValue(builder));
                if (path == 2)
                {
                    var sourceBuilderType = assembly.GetType("OceanBaseConnector.OceanBaseDataSourceBuilder")!;
                    var sourceBuilder = Activator.CreateInstance(sourceBuilderType, builder.ConnectionString)!;
                    source = (DbDataSource)sourceBuilderType.GetMethod("Build")!.Invoke(sourceBuilder, null)!;
                    connection = source.CreateConnection();
                }
                else { connection = (DbConnection)Activator.CreateInstance(type)!; connection.ConnectionString = builder.ConnectionString; }
                if (asynchronous) await connection.OpenAsync(stop.Token); else connection.Open();
                Assert.Equal(ConnectionState.Open, connection.State); Assert.Equal(new[] { "error", "success" }, capture.Outcomes);
                using var command = connection.CreateCommand(); command.CommandText = "after bounded retry";
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(stop.Token) : command.ExecuteNonQuery());
                if (asynchronous) await connection.CloseAsync(); else connection.Close();
                if (source != null) source.Dispose(); else type.GetMethod("ClearPool")!.Invoke(null, new object[] { connection });
                await serving.WaitAsync(stop.Token);
            }
            finally
            {
                connection?.Close(); source?.Dispose();
                if (connection != null && source == null) connection.GetType().GetMethod("ClearPool")!.Invoke(null, new object[] { connection });
                connection?.Dispose(); stop.Cancel(); endpoint.Dispose();
                try { await serving; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                context.Unload();
            }
        }

        private sealed class RetryTcpAttempts : IDisposable
        {
            private readonly MeterListener _listener = new();
            private readonly Activity _scope = new Activity("consumer-retry").Start();
            private readonly string _id = Guid.NewGuid().ToString("N");
            private readonly List<string> _outcomes = new();
            internal string[] Outcomes => _outcomes.ToArray();
            internal RetryTcpAttempts(Action onFirstFailure)
            {
                _scope.SetBaggage("consumer-retry", _id);
                _listener.InstrumentPublished = (instrument, listener) =>
                { if (instrument.Meter.Name == "OceanBaseConnector" && instrument.Name == "oceanbase.client.connection.phase.duration") listener.EnableMeasurementEvents(instrument); };
                _listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
                {
                    if (Activity.Current?.GetBaggageItem("consumer-retry") != _id) return;
                    var values = tags.ToArray();
                    if (!values.Any(tag => tag.Key == "oceanbase.connection.phase" && Equals(tag.Value, "tcp"))) return;
                    var outcome = (string)values.Single(tag => tag.Key == "oceanbase.outcome").Value!;
                    _outcomes.Add(outcome); if (_outcomes.Count == 1 && outcome == "error") onFirstFailure();
                });
                _listener.Start();
            }
            public void Dispose() { _listener.Dispose(); _scope.Dispose(); }
        }
    }
}
