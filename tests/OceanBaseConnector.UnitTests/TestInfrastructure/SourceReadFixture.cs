using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests.TestInfrastructure
{
    internal static class SourceReadFixture
    {
        internal const string Instrument = "oceanbase.client.source.read.duration";
        internal static async Task Verify(Func<DbConnection> create, bool oracle, bool local, bool asynchronous,
            string outcome = "success", int size = 20000, bool throwListener = false, bool delayed = false)
        {
            using var cancel = new CancellationTokenSource();
            using var capture = new Capture();
            using var parent = new Activity("application").Start();
            var source = new Source(size);
            var serverWait = new Stopwatch();
            source.BeforeRead = () =>
            {
                Assert.Empty(capture.Samples);
                if (outcome == "source-error") throw new IOException("private-source-content");
                if (outcome == "canceled") { cancel.Cancel(); throw new OperationCanceledException(cancel.Token); }
                if (delayed) Thread.Sleep(5);
            };
            source.Block = outcome == "timeout";
            capture.OnSample = () =>
            {
                if (!throwListener) return;
                Activity.Current = null;
                throw new InvalidOperationException("private-listener-content");
            };
            var server = LoopbackServer.Start(async peer =>
            {
                var channel = new PacketChannel(peer.GetStream());
                var greeting = (byte[])ProtocolFixture.Load("initial-handshake-oceanbase").Payload.Clone();
                greeting[Array.IndexOf(greeting, (byte)0, 1) + 14] |= (byte)CapabilityFlags.ClientLocalFiles;
                await channel.WritePacketAsync(greeting); await channel.ReadPacketAsync();
                await channel.WritePacketAsync(oracle ? SessionState() : Ok());
                await ServerIdentity.ReplyAsync(channel, oracle);
                if (local)
                {
                    Assert.Equal(3, (await channel.ReadPacketAsync())[0]);
                    await channel.WritePacketAsync(new byte[] { 251, 120 });
                }
                else
                {
                    Assert.Equal(22, (await channel.ReadPacketAsync())[0]);
                    await channel.WritePacketAsync(new byte[] { 0, 42, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0 });
                    await channel.WritePacketAsync(Column()); await channel.WritePacketAsync(new byte[] { 254, 0, 0, 2, 0 });
                    channel.BeginCommand();
                }
                if (outcome == "source-error" || outcome == "canceled" || outcome == "timeout")
                {
                    // Initial parameter-source failures may close the prepared statement; LOCAL INFILE breaks.
                    try { await channel.ReadPacketAsync(); } catch (Exception error) when (error is IOException || error.GetType().Name == "ProtocolException") { }
                    return;
                }
                var received = new List<byte>();
                if (local)
                {
                    byte[] packet;
                    while ((packet = await channel.ReadPacketAsync()).Length != 0) received.AddRange(packet);
                }
                else
                {
                    do
                    {
                        var packet = await channel.ReadPacketAsync();
                        Assert.Equal(oracle ? 162 : 24, packet[0]);
                        Assert.Equal(new byte[] { 42, 0, 0, 0, 0, 0 }, packet.Skip(1).Take(6));
                        received.AddRange(oracle ? Convert.FromHexString(Encoding.ASCII.GetString(packet, 17, packet.Length - 17)) : packet.Skip(7));
                        if (oracle) await channel.WritePacketAsync(Ok());
                        channel.BeginCommand();
                    } while (received.Count < size);
                    Assert.Equal(23, (await channel.ReadPacketAsync())[0]);
                }
                Assert.Equal(Enumerable.Range(0, size).Select(i => (byte)(i % 251)), received);
                if (delayed) { serverWait.Start(); await Task.Delay(100); serverWait.Stop(); }
                await channel.WritePacketAsync(outcome == "error" ? new byte[] { 255, 40, 4, 35, 52, 50, 48, 48, 48, 120 } : Ok(1));
                if (!local) { channel.BeginCommand(); Assert.Equal(25, (await channel.ReadPacketAsync())[0]); }
            });
            using var connection = create();
            connection.ConnectionString = $"Server=127.0.0.1;Port={server.Port};User ID=u;TLS Mode=Disabled;Allow Local Infile=true";
            if (local) connection.GetType().GetProperty("LocalInfileStreamFactory")!.SetValue(connection, new Func<string, Stream?>(_ => source));
            try
            {
                if (asynchronous) await connection.OpenAsync(); else connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandTimeout = outcome == "timeout" ? 1 : 10;
                    command.CommandText = local ? "load data local infile 'x'" : oracle ? "insert into t values (:p)" : "insert into t values (?)";
                    if (!local)
                    {
                        var parameter = command.CreateParameter(); parameter.ParameterName = "p"; parameter.DbType = DbType.Binary;
                        parameter.Value = source; command.Parameters.Add(parameter);
                    }
                    var lifetime = Stopwatch.StartNew();
                    var error = await Record.ExceptionAsync(async () =>
                    {
                        Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(cancel.Token) : command.ExecuteNonQuery());
                    });
                    lifetime.Stop();
                    if (outcome == "success") Assert.Null(error);
                    else
                    {
                        Assert.NotNull(error);
                        var cause = local && outcome != "error" ? Assert.IsAssignableFrom<DbException>(error).InnerException : error;
                        if (outcome == "source-error") Assert.IsType<IOException>(cause);
                        else if (outcome == "canceled") Assert.IsType<OperationCanceledException>(cause);
                        else if (outcome == "timeout") Assert.IsType<TimeoutException>(cause);
                        else
                        {
                            Assert.IsAssignableFrom<DbException>(error);
                            Assert.Equal(1064, error.GetType().GetProperty("ServerErrorCode")!.GetValue(error));
                            Assert.Equal(false, error.GetType().GetProperty("IsExecutionOutcomeUnknown")!.GetValue(error));
                        }
                    }
                    var sample = Assert.Single(capture.Samples);
                    Assert.Equal("s", sample.Unit);
                    Assert.True(double.IsFinite(sample.Seconds) && sample.Seconds >= source.Ticks / (double)Stopwatch.Frequency);
                    if (delayed) Assert.True(lifetime.Elapsed.TotalSeconds - sample.Seconds >= serverWait.Elapsed.TotalSeconds);
                    Assert.Equal(3, sample.Tags.Count);
                    Assert.Equal(local ? "local_infile" : "parameter", sample.Tags["oceanbase.source.kind"]);
                    Assert.Equal(oracle ? "oracle" : "mysql", sample.Tags["oceanbase.compatibility_mode"]);
                    Assert.Equal(outcome == "source-error" ? "error" : outcome, sample.Tags["oceanbase.outcome"]);
                    Assert.Same(parent, Activity.Current);
                    Assert.Equal(local, source.Disposed);
                    Assert.Equal(outcome == "success" || outcome == "error" || !local ? ConnectionState.Open : ConnectionState.Broken, connection.State);
                }
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); source.Dispose(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static byte[] Ok(byte affected = 0) => new byte[] { 0, affected, 0, 2, 0, 0, 0 };
        private static byte[] Column() => new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 112, 1, 112, 12, 63, 0, 255, 0, 0, 0, 252, 0, 0, 0, 0, 0 };
        private static byte[] SessionState()
        {
            using var state = new MemoryStream();
            foreach (var (name, value) in new[] { ("time_zone", "+00:00"), ("ncharacter_set_connection", "utf8mb4"), ("character_set_results", "utf8mb4"), ("character_set_connection", "utf8mb4") })
            {
                var key = Encoding.ASCII.GetBytes(name); var bytes = Encoding.ASCII.GetBytes(value);
                state.WriteByte(0); state.WriteByte((byte)(key.Length + bytes.Length + 2));
                state.WriteByte((byte)key.Length); state.Write(key); state.WriteByte((byte)bytes.Length); state.Write(bytes);
            }
            return new byte[] { 0, 0, 0, 6, 64, 0, 0, 0, (byte)state.Length }.Concat(state.ToArray()).ToArray();
        }

        internal sealed class Capture : IDisposable
        {
            private readonly MeterListener _listener = new MeterListener();
            private readonly ConcurrentQueue<(double Seconds, string? Unit, Dictionary<string, object?> Tags)> _samples = new();
            internal (double Seconds, string? Unit, Dictionary<string, object?> Tags)[] Samples => _samples.ToArray();
            internal Action? OnSample;
            internal Capture()
            {
                _listener.InstrumentPublished = (instrument, listener) =>
                { if (instrument.Meter.Name == "OceanBaseConnector" && instrument.Name == Instrument) listener.EnableMeasurementEvents(instrument); };
                _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
                { _samples.Enqueue((value, instrument.Unit, tags.ToArray().ToDictionary(x => x.Key, x => x.Value))); OnSample?.Invoke(); });
                _listener.Start();
            }
            public void Dispose() => _listener.Dispose();
        }

        private sealed class Source(int size) : Stream
        {
            private int _position;
            internal long Ticks;
            internal bool Disposed, Block;
            internal Action? BeforeRead;
            public override bool CanRead => !Disposed;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count)
            {
                var started = Stopwatch.GetTimestamp();
                try { BeforeRead?.Invoke(); return Copy(buffer, offset, count); }
                finally { Ticks += Stopwatch.GetTimestamp() - started; }
            }
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    BeforeRead?.Invoke();
                    if (Block) await Task.Delay(Timeout.Infinite, token);
                    return Copy(buffer, offset, count);
                }
                finally { Ticks += Stopwatch.GetTimestamp() - started; }
            }
            private int Copy(byte[] buffer, int offset, int count)
            {
                var read = Math.Min(Math.Min(count, 3001), size - _position);
                for (var i = 0; i < read; i++) buffer[offset + i] = (byte)((_position + i) % 251);
                _position += read; return read;
            }
            protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
            public override void Flush() => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
