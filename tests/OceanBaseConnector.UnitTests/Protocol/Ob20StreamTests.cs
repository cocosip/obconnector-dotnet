using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests.Protocol
{
    public sealed class Ob20StreamTests
    {
        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task Ob20StreamWritesRequestAndReadsWithoutPrefetch(bool async)
        {
            using var wire = new Wire(Ob20Packet.Build(7, 0, 1, new byte[] { 10, 11, 12, 13 }));
            using var stream = new Ob20Stream(wire, 7);
            stream.BeginRequest();
            await Write(stream, new byte[] { 1, 0, 0, 0, 3 }, async);
            await Finish(stream, async);
            Assert.Equal(Ob20Packet.Build(7, 0, 0, new byte[] { 1, 0, 0, 0, 3 }), wire.Output.ToArray());
            var bytes = new byte[2];
            Assert.Equal(2, await Read(stream, bytes, async));
            Assert.Equal(new byte[] { 10, 11 }, bytes);
            Assert.Equal(33, wire.BytesRead);
            Assert.Equal(2, await Read(stream, bytes, async));
            Assert.Equal(new byte[] { 12, 13 }, bytes);
            Assert.Equal(39, wire.BytesRead);
            stream.CompleteResponse();
            if (async) { Assert.Equal(0, wire.SyncReads); Assert.Equal(0, wire.SyncWrites); }
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task Ob20StreamCrossesFramesSkipsExtraAndWrapsSequence(bool async)
        {
            using var input = new MemoryStream();
            for (var i = 0; i < 258; i++)
                input.Write(Ob20Packet.Build(7, 0, (byte)(i + 1), new byte[] { (byte)i }, new byte[17], flags: i == 257 ? 10u : 8u));
            using var wire = new Wire(input.ToArray()); using var stream = new Ob20Stream(wire, 7);
            stream.BeginRequest(); await Write(stream, new byte[] { 0 }, async); await Finish(stream, async);
            var buffer = new byte[4096]; var total = 0; int count;
            while (total < 258)
            {
                count = await Read(stream, buffer, async);
                Assert.Equal(1, count); Assert.Equal((byte)total, buffer[0]); total += count;
            }
            stream.CompleteResponse(); Assert.Equal(input.Length, wire.BytesRead);
            if (async) Assert.Equal(0, wire.SyncReads);
        }

        [Theory]
        [InlineData(false, "crc")] [InlineData(true, "crc")]
        [InlineData(false, "last")] [InlineData(true, "last")]
        [InlineData(false, "trailing")] [InlineData(true, "trailing")]
        [InlineData(false, "request")] [InlineData(true, "request")]
        [InlineData(false, "reroute")] [InlineData(true, "reroute")]
        [InlineData(false, "truncated")] [InlineData(true, "truncated")]
        public async Task Ob20StreamRejectsInvalidResponseAndClosesTransport(bool async, string kind)
        {
            var data = Ob20Packet.Build(7, kind == "request" ? 1u : 0u, 1, new byte[] { 42, 43 },
                flags: kind == "last" ? 0u : kind == "reroute" ? 6u : 2u);
            if (kind == "crc") data[data.Length - 1] ^= 1;
            if (kind == "truncated") data = data.AsSpan(0, data.Length - 1).ToArray();
            using var wire = new Wire(data); using var stream = new Ob20Stream(wire, 7);
            stream.BeginRequest(); await Write(stream, new byte[] { 3 }, async); await Finish(stream, async);
            await Assert.ThrowsAsync<ProtocolException>(async () =>
            {
                var buffer = new byte[kind == "trailing" ? 1 : 2];
                await Read(stream, buffer, async); stream.CompleteResponse();
            });
            Assert.True(wire.Closed);
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task Ob20StreamWritesBoundedFramesAndNoResponseCommandsAdvanceRequest(bool async)
        {
            using var wire = new Wire(Array.Empty<byte>()); using var stream = new Ob20Stream(wire, 7);
            var data = Enumerable.Repeat((byte)42, 128 * 1024).ToArray();
            stream.BeginRequest(); await Write(stream, data, async); await Finish(stream, async);
            stream.CompleteResponse(expectsResponse: false);
            stream.BeginRequest(); await Write(stream, new byte[] { 25 }, async); await Finish(stream, async);
            stream.CompleteResponse(expectsResponse: false);
            var bytes = wire.Output.ToArray(); var first = bytes.AsSpan(0, 65536 + 35).ToArray();
            var second = bytes.AsSpan(first.Length, first.Length).ToArray();
            Assert.Equal(0u, Ob20Packet.Parse(first, 7, 0, 0).Header.Flags);
            Assert.Equal(2u, Ob20Packet.Parse(second, 7, 0, 1).Header.Flags);
            Assert.Equal(new byte[] { 25 }, Ob20Packet.Parse(bytes.AsSpan(first.Length * 2).ToArray(), 7, 1, 0).Payload.ToArray());
        }

        [Fact]
        public void Ob20StreamRejectsSendingBeforePreviousResponseIsConsumed()
        {
            using var wire = new Wire(Array.Empty<byte>()); using var stream = new Ob20Stream(wire, 7);
            stream.BeginRequest(); stream.Write(new byte[] { 3 }, 0, 1); stream.FinishRequest();
            Assert.Throws<InvalidOperationException>(() => stream.BeginRequest());
        }

        [Fact]
        public async Task Ob20StreamHonorsCanceledReadsWithoutConsumingBytes()
        {
            using var wire = new Wire(Array.Empty<byte>()); using var stream = new Ob20Stream(wire, 7);
            stream.BeginRequest(); stream.Write(new byte[] { 3 }, 0, 1); stream.FinishRequest();
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.ReadAsync(new byte[1], 0, 1, cancellation.Token));
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.Equal(0, wire.BytesRead); Assert.True(wire.Closed);
        }

        [Fact]
        public void Ob20OptionsFreezeRoundTripAndFollowEveryEndpoint()
        {
            var builder = new OceanBaseConnectionStringBuilder("Server=host1,host2;User ID=u;Password=hidden;uSe Ob20=true");
            Assert.True(builder.UseOb20);
            var options = builder.Build(); builder.UseOb20 = false;
            Assert.True(options.UseOb20);
            Assert.All(options.Endpoints, endpoint => Assert.True(options.ForEndpoint(endpoint).UseOb20));
            Assert.True(new OceanBaseConnectionStringBuilder(options.SanitizedConnectionString).UseOb20);
            Assert.DoesNotContain("hidden", options.SanitizedConnectionString);
            Assert.False(new OceanBaseConnectionStringBuilder().UseOb20);
            Assert.Throws<ArgumentException>(() => new OceanBaseConnectionStringBuilder("Use OB20=yes"));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task Ob20PacketChannelSplitsInnerHeadersAndValidatesFinalBoundary(bool async)
        {
            var inner = new byte[] { 3, 0, 0, 1, 40, 41, 42 };
            var first = Ob20Packet.Build(7, 0, 1, inner.AsSpan(0, 2).ToArray(), flags: 0);
            var second = Ob20Packet.Build(7, 0, 2, inner.AsSpan(2).ToArray());
            using var wire = new Wire(first.Concat(second).ToArray());
            var channel = new PacketChannel(wire); channel.EnableOb20(7); channel.BeginCommand();
            if (async) await channel.WritePacketAsync(new byte[] { 3 }); else channel.WritePacket(new byte[] { 3 });
            Assert.Equal(new byte[] { 40, 41, 42 }, async ? await channel.ReadPacketAsync() : channel.ReadPacket());
            channel.CompleteResponse();
            Assert.Throws<InvalidOperationException>(() => channel.EnableOb20(7));
            Assert.Throws<InvalidOperationException>(() => channel.ReplaceUnderlyingStream(wire));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task Ob20PreservesExactMySqlMaximumTerminatorAndSharedSequence(bool async)
        {
            using var wire = new Wire(Ob20Packet.Build(7, 0, 1, new byte[] { 1, 0, 0, 2, 42 }));
            var channel = new PacketChannel(wire); channel.EnableOb20(7); channel.BeginCommand();
            var payload = Enumerable.Repeat((byte)43, PacketChannel.MaxFramePayload).ToArray();
            if (async) await channel.WritePacketAsync(payload); else channel.WritePacket(payload);
            using var inner = new MemoryStream(); var frames = wire.Output.ToArray(); var offset = 0; var sequence = 0;
            while (offset < frames.Length)
            {
                var header = Ob20Packet.ParseHeader(frames.AsSpan(offset, 31).ToArray(), 7, 0, (byte)sequence);
                var size = header.PayloadLength + 35;
                var frame = Ob20Packet.Parse(frames.AsSpan(offset, size).ToArray(), 7, 0, (byte)sequence);
                Assert.Equal(offset + size == frames.Length ? 2u : 0u, frame.Header.Flags);
                inner.Write(frame.Payload.Array!, frame.Payload.Offset, frame.Payload.Count);
                sequence++; offset += size;
            }
            Assert.Equal(257, sequence);
            var bytes = inner.ToArray(); Assert.Equal(payload.Length + 8, bytes.Length);
            Assert.Equal(new byte[] { 255, 255, 255, 0 }, bytes.AsSpan(0, 4).ToArray());
            Assert.Equal(payload, bytes.AsSpan(4, payload.Length).ToArray());
            Assert.Equal(new byte[] { 0, 0, 0, 1 }, bytes.AsSpan(bytes.Length - 4).ToArray());
            Assert.Equal(new byte[] { 42 }, async ? await channel.ReadPacketAsync() : channel.ReadPacket());
            channel.CompleteResponse();
        }

        [Fact]
        public void Ob20StreamingThirtyTwoMiBDoesNotAllocateWholeFrames()
        {
            using var input = new MemoryStream(); var data = new byte[64 * 1024];
            for (var i = 0; i < 512; i++) input.Write(Ob20Packet.Build(7, 0, (byte)(i + 1), data, flags: i == 511 ? 2u : 0u));
            using var wire = new Wire(input.ToArray()); using var stream = new Ob20Stream(wire, 7);
            stream.BeginRequest(); stream.Write(new byte[] { 3 }, 0, 1); stream.FinishRequest();
            var buffer = new byte[8192]; var allocated = GC.GetAllocatedBytesForCurrentThread();
            var total = 0;
            while (total < 32 * 1024 * 1024) total += stream.Read(buffer, 0, buffer.Length);
            stream.CompleteResponse();
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Assert.Equal(32 * 1024 * 1024, total); Assert.InRange(allocated, 0, 256 * 1024);
        }

        private static Task<int> Read(Stream stream, byte[] bytes, bool async) => async
            ? stream.ReadAsync(bytes, 0, bytes.Length) : Task.FromResult(stream.Read(bytes, 0, bytes.Length));
        private static Task Write(Stream stream, byte[] bytes, bool async)
        {
            if (async) return stream.WriteAsync(bytes, 0, bytes.Length);
            stream.Write(bytes, 0, bytes.Length); return Task.CompletedTask;
        }
        private static Task Finish(Ob20Stream stream, bool async)
        { if (async) return stream.FinishRequestAsync(CancellationToken.None); stream.FinishRequest(); return Task.CompletedTask; }

        internal sealed class Wire : Stream
        {
            private readonly MemoryStream _input;
            public MemoryStream Output { get; } = new MemoryStream();
            public Wire(byte[] input) => _input = new MemoryStream(input);
            public int SyncReads { get; private set; }
            public int SyncWrites { get; private set; }
            public int BytesRead { get; private set; }
            public bool Closed { get; private set; }
            public override int Read(byte[] buffer, int offset, int count) { SyncReads++; return Take(buffer, offset, count); }
            private int Take(byte[] buffer, int offset, int count) { var read = _input.Read(buffer, offset, Math.Min(count, 13)); BytesRead += read; return read; }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            { token.ThrowIfCancellationRequested(); return Task.FromResult(Take(buffer, offset, count)); }
            public override void Write(byte[] buffer, int offset, int count) { SyncWrites++; Output.Write(buffer, offset, count); }
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
            { token.ThrowIfCancellationRequested(); Output.Write(buffer, offset, count); return Task.CompletedTask; }
            protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
            public override bool CanRead => !Closed; public override bool CanWrite => !Closed; public override bool CanSeek => false;
            public override void Flush() { }
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
