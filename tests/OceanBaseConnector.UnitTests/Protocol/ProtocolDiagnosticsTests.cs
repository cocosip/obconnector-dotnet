using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests.Protocol
{
    [Collection("Driver diagnostics")]
    public sealed partial class ProtocolDiagnosticsTests
    {
        private const string ListenerName = "OceanBaseConnector.Protocol";

        [Theory]
        [InlineData(false, "mysql")]
        [InlineData(true, "mysql")]
        [InlineData(false, "compressed")]
        [InlineData(true, "compressed")]
        [InlineData(false, "ob20")]
        [InlineData(true, "ob20")]
        public async Task EachFramingLayerReportsMetadataWithoutPayloads(bool asynchronous, string layer)
        {
            using var capture = new Capture();
            var response = Encoding.UTF8.GetBytes("private-result-and-password");
            var frame = MySqlCompressionTests.Frame(1, response);
            var input = layer == "compressed" ? MySqlCompressionTests.Block(1, frame)
                : layer == "ob20" ? Ob20Packet.Build(7, 0, 1, frame) : frame;
            using var wire = new Ob20StreamTests.Wire(input);
            var channel = new PacketChannel(wire);
            if (layer == "compressed") channel.EnableCompression();
            if (layer == "ob20") channel.EnableOb20(7);
            channel.BeginCommand();
            var request = Encoding.UTF8.GetBytes("secret-query-parameter");
            if (asynchronous) await channel.WritePacketAsync(request); else channel.WritePacket(request);
            Assert.Equal(response, asynchronous ? await channel.ReadPacketAsync() : channel.ReadPacket());
            channel.CompleteResponse();
            var events = capture.Events.Where(x => x.Key.EndsWith(".Frame", StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(events);
            Assert.Contains(events, x => Equals(x.Value["Layer"], layer) && Equals(x.Value["Direction"], "write"));
            Assert.Contains(events, x => Equals(x.Value["Layer"], layer) && Equals(x.Value["Direction"], "read"));
            Assert.Single(events.Select(x => x.Value["ChannelId"]).Distinct());
            Assert.Contains(capture.Events, x => x.Key.EndsWith(".Boundary", StringComparison.Ordinal) && Equals(x.Value["Stage"], "complete"));
            foreach (var item in capture.Events)
                foreach (var value in item.Value.Values)
                {
                    Assert.True(value is null or string or int or long or uint);
                    var text = value?.ToString() ?? "";
                    Assert.DoesNotContain("secret", text); Assert.DoesNotContain("private", text);
                }
            if (asynchronous) { Assert.Equal(0, wire.SyncReads); Assert.Equal(0, wire.SyncWrites); }
        }

        public static IEnumerable<object[]> Faults()
        {
            foreach (var asynchronous in new[] { false, true })
                foreach (var layer in new[] { "mysql", "compressed", "ob20" })
                    foreach (var fault in new[] { "sequence", "truncated", "unread", "write", "cancel" })
                        yield return new object[] { asynchronous, layer, fault };
        }

        [Theory]
        [MemberData(nameof(Faults))]
        public async Task FaultEventsPreserveOriginalFailureAndCloseBehavior(bool asynchronous, string layer, string fault)
        {
            using var capture = new Capture();
            var frame = MySqlCompressionTests.Frame(fault == "sequence" && layer == "mysql" ? 9 : 1, new byte[] { 10, 11 });
            var input = layer == "compressed" ? MySqlCompressionTests.Block(fault == "sequence" ? 9 : 1, frame)
                : layer == "ob20" ? Ob20Packet.Build(7, 0, (byte)(fault == "sequence" ? 9 : 1), frame) : frame;
            if (fault == "truncated") input = input[..^1];
            using var wire = new FaultWire(input) { FailWrite = fault == "write", CancelRead = fault == "cancel" };
            var channel = new PacketChannel(wire);
            if (layer == "compressed") channel.EnableCompression();
            if (layer == "ob20") channel.EnableOb20(7);
            channel.BeginCommand();
            var failure = await Record.ExceptionAsync(async () =>
            {
                if (asynchronous) await channel.WritePacketAsync(new byte[] { 3 }); else channel.WritePacket(new byte[] { 3 });
                if (fault == "unread")
                {
                    using var payload = asynchronous ? await channel.OpenPayloadAsync() : channel.OpenPayload();
                }
                else if (asynchronous) await channel.ReadPacketAsync(); else channel.ReadPacket();
            });
            if (fault == "unread") Assert.Null(failure);
            else if (fault == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(failure);
            else if (fault == "write") Assert.IsType<IOException>(failure);
            else Assert.IsType<ProtocolException>(failure);
            Assert.Contains(capture.Events, x => x.Key.EndsWith(".Error", StringComparison.Ordinal)
                && Equals(x.Value["Failure"], fault == "unread" ? "incomplete" : fault == "cancel" ? "canceled" : fault == "write" ? "io" : "protocol"));
            if (fault == "write" && layer == "mysql") Assert.False(wire.Closed);
            else Assert.True(wire.Closed);
            if (fault == "sequence") Assert.Contains(capture.Events, x => x.Key.EndsWith(".Frame", StringComparison.Ordinal)
                && Equals(x.Value["Layer"], layer) && Equals(x.Value["Direction"], "read")
                && Equals(x.Value["Sequence"], 9) && Equals(x.Value["ExpectedSequence"], 1));
        }

        [Theory]
        [InlineData(false, "filter")]
        [InlineData(true, "filter")]
        [InlineData(false, "write")]
        [InlineData(true, "write")]
        [InlineData(false, "reenter")]
        [InlineData(true, "reenter")]
        public async Task ThrowingOrReentrantObserversCannotChangeTransportOrActivity(bool asynchronous, string kind)
        {
            using var parent = new Activity("application").Start();
            var visits = 0;
            void Callback()
            {
                visits++; Activity.Current = new Activity("observer");
                if (kind != "reenter") throw new InvalidOperationException("private-observer-error");
                using var nested = new MemoryStream(); new PacketChannel(nested).WritePacket(new byte[] { 9 });
            }
            using var capture = new Capture(kind == "filter" ? _ => { Callback(); return true; } : null, kind == "filter" ? null : Callback);
            using var wire = new Ob20StreamTests.Wire(MySqlCompressionTests.Frame(1, new byte[] { 7 }));
            var channel = new PacketChannel(wire); channel.BeginCommand();
            Assert.Same(parent, Activity.Current);
            if (asynchronous) await channel.WritePacketAsync(new byte[] { 3 }); else channel.WritePacket(new byte[] { 3 });
            Assert.Equal(new byte[] { 7 }, asynchronous ? await channel.ReadPacketAsync() : channel.ReadPacket());
            channel.CompleteResponse(); Assert.Same(parent, Activity.Current);
            Assert.InRange(visits, 1, 8); Assert.False(wire.Closed);
        }

        [Fact]
        public void DisabledObserversDoNotAllocatePerFrame()
        {
            using var wire = new MemoryStream(32); var channel = new PacketChannel(wire); var bytes = new byte[] { 3 };
            for (var i = 0; i < 10; i++) { wire.Position = 0; channel.BeginCommand(); channel.WritePacket(bytes); }
            var started = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1024; i++) { wire.Position = 0; channel.BeginCommand(); channel.WritePacket(bytes); }
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - started, 0, 1024);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task BufferedContinuationRetainsTheActualHeaderFailure(bool asynchronous, bool truncated)
        {
            using var capture = new Capture();
            var first = MySqlCompressionTests.Frame(0, new byte[PacketChannel.MaxFramePayload]);
            using var wire = new MemoryStream(first.Concat(truncated ? new byte[] { 0, 0 } : new byte[] { 0, 0, 0, 9 }).ToArray());
            var channel = new PacketChannel(wire);
            await Assert.ThrowsAsync<ProtocolException>(() => asynchronous ? channel.ReadPacketAsync() : Task.FromResult(channel.ReadPacket()));
            Assert.Contains(capture.Events, x => x.Key.EndsWith(".Error", StringComparison.Ordinal) && Equals(x.Value["Failure"], "protocol"));
            Assert.DoesNotContain(capture.Events, x => x.Key.EndsWith(".Error", StringComparison.Ordinal) && Equals(x.Value["Failure"], "incomplete"));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task PublicCancellationOrCloseStillReportsTheInterruptedRead(bool asynchronous, bool close)
        {
            using var capture = new Capture();
            object? clientId = null;
            await TextCommandTests.ProtocolCancellationObservation(asynchronous, close, () =>
            {
                clientId = capture.Events.First(x => x.Key.EndsWith(".Frame", StringComparison.Ordinal)
                    && Equals(x.Value["Direction"], "read") && Equals(x.Value["Sequence"], 0)).Value["ChannelId"];
                capture.Events.Clear();
            });
            Assert.Contains(capture.Events, x => x.Key.EndsWith(".Error", StringComparison.Ordinal) && Equals(x.Value["ChannelId"], clientId));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RejectedResponseBoundaryIsObservedWithoutClosingItsPayload(bool asynchronous)
        {
            using var capture = new Capture(); using var wire = new FaultWire(MySqlCompressionTests.Frame(0, new byte[] { 7 }));
            var channel = new PacketChannel(wire);
            using var payload = asynchronous ? await channel.OpenPayloadAsync() : channel.OpenPayload();
            Assert.Throws<InvalidOperationException>(() => channel.CompleteResponse()); Assert.False(wire.Closed);
            Assert.Contains(capture.Events, x => x.Key.EndsWith(".Error", StringComparison.Ordinal) && Equals(x.Value["Direction"], "boundary"));
            Assert.DoesNotContain(capture.Events, x => x.Key.EndsWith(".Boundary", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData(false, "mysql")]
        [InlineData(true, "mysql")]
        [InlineData(false, "compressed")]
        [InlineData(true, "compressed")]
        [InlineData(false, "ob20")]
        [InlineData(true, "ob20")]
        public async Task ClosedOuterStreamsStillReportTheirCaughtFailure(bool asynchronous, string layer)
        {
            using var capture = new Capture(); var frame = MySqlCompressionTests.Frame(1, new byte[] { 7 });
            var input = layer == "compressed" ? MySqlCompressionTests.Block(1, frame) : layer == "ob20" ? Ob20Packet.Build(7, 0, 1, frame) : frame;
            using var wire = new FaultWire(input); var channel = new PacketChannel(wire);
            if (layer == "compressed") channel.EnableCompression(); if (layer == "ob20") channel.EnableOb20(7);
            channel.BeginCommand(); if (asynchronous) await channel.WritePacketAsync(new byte[] { 3 }); else channel.WritePacket(new byte[] { 3 });
            wire.BeforeRead = channel.Close;
            await Assert.ThrowsAnyAsync<Exception>(() => asynchronous ? channel.ReadPacketAsync() : Task.FromResult(channel.ReadPacket()));
            Assert.Single(capture.Events, x => x.Key.EndsWith(".Error", StringComparison.Ordinal) && Equals(x.Value["Layer"], layer));
        }

        [Theory]
        [InlineData(false, "compressed")]
        [InlineData(true, "compressed")]
        [InlineData(false, "ob20")]
        [InlineData(true, "ob20")]
        public async Task ChecksumFailuresHaveNoExceptionOrPayloadInDiagnosticEvents(bool asynchronous, string layer)
        {
            using var capture = new Capture(); var frame = MySqlCompressionTests.Frame(1, new byte[4096]);
            var input = layer == "compressed" ? MySqlCompressionTests.Block(1, frame) : Ob20Packet.Build(7, 0, 1, frame); input[^1] ^= 1;
            using var wire = new FaultWire(input); var channel = new PacketChannel(wire);
            if (layer == "compressed") channel.EnableCompression(); else channel.EnableOb20(7);
            channel.BeginCommand(); if (asynchronous) await channel.WritePacketAsync(new byte[] { 3 }); else channel.WritePacket(new byte[] { 3 });
            await Assert.ThrowsAsync<ProtocolException>(() => asynchronous ? channel.ReadPacketAsync() : Task.FromResult(channel.ReadPacket()));
            Assert.Contains(capture.Events, x => x.Key.EndsWith(".Error", StringComparison.Ordinal) && Equals(x.Value["Layer"], layer) && Equals(x.Value["Failure"], "protocol"));
            Assert.All(capture.Events.SelectMany(x => x.Value.Values), x => Assert.True(x is null or string or int or long or uint));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SequenceRolloverRemainsObservableWithoutAnError(bool asynchronous)
        {
            using var capture = new Capture(); using var wire = new MemoryStream();
            for (var i = 0; i < 258; i++) wire.Write(MySqlCompressionTests.Frame(i, new byte[] { 7 }));
            wire.Position = 0; var channel = new PacketChannel(wire);
            for (var i = 0; i < 258; i++) Assert.Equal(new byte[] { 7 }, asynchronous ? await channel.ReadPacketAsync() : channel.ReadPacket());
            Assert.Equal(2, capture.Events.Count(x => x.Key.EndsWith(".Frame", StringComparison.Ordinal) && Equals(x.Value["Sequence"], 0)));
            Assert.DoesNotContain(capture.Events, x => x.Key.EndsWith(".Error", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ExactMultipleReportsTheRequiredEmptyTerminator(bool asynchronous)
        {
            using var capture = new Capture(); using var wire = new FaultWire(Array.Empty<byte>()); var channel = new PacketChannel(wire);
            var data = new byte[PacketChannel.MaxFramePayload];
            if (asynchronous) await channel.WritePacketAsync(data); else channel.WritePacket(data);
            var frames = capture.Events.Where(x => x.Key.EndsWith(".Frame", StringComparison.Ordinal)).ToArray();
            Assert.Equal(2, frames.Length); Assert.Equal((long)PacketChannel.MaxFramePayload, frames[0].Value["PayloadLength"]);
            Assert.Equal(0L, frames[1].Value["PayloadLength"]); Assert.Equal(1, frames[1].Value["Sequence"]);
        }

        private sealed class FaultWire(byte[] input) : Stream
        {
            private readonly MemoryStream _input = new(input);
            public bool FailWrite { get; init; }
            public bool CancelRead { get; init; }
            public bool Closed { get; private set; }
            public Action? BeforeRead { get; set; }
            public override int Read(byte[] buffer, int offset, int count)
            { BeforeRead?.Invoke(); if (CancelRead) throw new OperationCanceledException(); return _input.Read(buffer, offset, count); }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => Task.FromResult(Read(buffer, offset, count));
            public override void Write(byte[] buffer, int offset, int count) { if (FailWrite) throw new IOException("private-wire-error"); }
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) { Write(buffer, offset, count); return Task.CompletedTask; }
            protected override void Dispose(bool disposing) { Closed = true; _input.Dispose(); base.Dispose(disposing); }
            public override void Flush() { }
            public override bool CanRead => !Closed;
            public override bool CanWrite => !Closed;
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        private sealed class Capture : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
        {
            private readonly IDisposable _all;
            private readonly List<IDisposable> _subscriptions = new();
            private readonly Func<string, bool>? _filter;
            private readonly Action? _callback;
            public ConcurrentQueue<KeyValuePair<string, IReadOnlyDictionary<string, object?>>> Events { get; } = new();
            public Capture(Func<string, bool>? filter = null, Action? callback = null)
            { _filter = filter; _callback = callback; _all = DiagnosticListener.AllListeners.Subscribe(this); }
            public void OnNext(DiagnosticListener value)
            { if (value.Name == ListenerName) _subscriptions.Add(value.Subscribe(this, (name, _, _) => _filter?.Invoke(name) ?? true)); }
            public void OnNext(KeyValuePair<string, object?> value)
            { _callback?.Invoke(); Events.Enqueue(new(value.Key, Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(value.Value))); }
            public void OnCompleted() { }
            public void OnError(Exception error) { }
            public void Dispose() { _all.Dispose(); foreach (var subscription in _subscriptions) subscription.Dispose(); }
        }
    }
}
