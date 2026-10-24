using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests.Protocol
{
    public sealed class MySqlCompressionTests
    {
        private static void Enable(PacketChannel channel)
        {
            var method = typeof(PacketChannel).GetMethod("EnableCompression", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            method.Invoke(channel, null);
        }

        internal static byte[] Frame(int sequence, byte[] payload)
        {
            var frame = new byte[payload.Length + 4];
            WriteLength(frame, 0, payload.Length); frame[3] = (byte)sequence;
            payload.CopyTo(frame, 4); return frame;
        }

        internal static byte[] Block(int sequence, byte[] data, bool compress = true)
        {
            var body = data;
            if (compress)
            {
                using var output = new MemoryStream();
                using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(data);
                body = output.ToArray();
            }
            var result = new byte[7 + body.Length];
            WriteLength(result, 0, body.Length); result[3] = (byte)sequence;
            WriteLength(result, 4, compress ? data.Length : 0);
            body.CopyTo(result, 7); return result;
        }

        internal static void WriteLength(byte[] bytes, int offset, int length)
        { bytes[offset] = (byte)length; bytes[offset + 1] = (byte)(length >> 8); bytes[offset + 2] = (byte)(length >> 16); }

        internal static int LengthAt(byte[] bytes, int offset) => bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16;

        private static Task<byte[]> Read(PacketChannel channel, bool asynchronous) => asynchronous
            ? channel.ReadPacketAsync() : Task.FromResult(channel.ReadPacket());
        private static Task Write(PacketChannel channel, byte[] data, bool asynchronous) => asynchronous
            ? channel.WritePacketAsync(data) : Run(() => channel.WritePacket(data));
        private static Task Run(Action action) { action(); return Task.CompletedTask; }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ReadsCoalescedPacketsAndSplitHeadersAcrossMixedBlocks(bool asynchronous)
        {
            var data = Frame(0, new byte[] { 10, 11 }).Concat(Frame(1, new byte[1500])).ToArray();
            var wire = Block(0, data[..2], false).Concat(Block(1, data[2..8])).Concat(Block(2, data[8..], false)).ToArray();
            using var stream = new MemoryStream(wire);
            var channel = new PacketChannel(stream); Enable(channel);
            Assert.Equal(new byte[] { 10, 11 }, await Read(channel, asynchronous));
            Assert.Equal(new byte[1500], await Read(channel, asynchronous));
            channel.CompleteResponse();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AcceptsInnerSequenceRenumberingWhenServerFlushesCompressedBlocks(bool asynchronous)
        {
            // MySQL may reset inner sequence numbers to the compressed counter at a flush.
            var first = Frame(0, new byte[] { 1 }).Concat(Frame(1, new byte[] { 2 })).ToArray();
            using var stream = new MemoryStream(Block(0, first).Concat(Block(1, Frame(1, new byte[] { 3 }))).ToArray());
            var channel = new PacketChannel(stream); Enable(channel);
            for (byte value = 1; value <= 3; value++) Assert.Equal(new[] { value }, await Read(channel, asynchronous));
            channel.CompleteResponse();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task WritesBlocksReadableByFrameworkZlibAndKeepsPayloadsBounded(bool asynchronous)
        {
            using var stream = new MemoryStream();
            var channel = new PacketChannel(stream); Enable(channel); channel.BeginCommand();
            var payload = Enumerable.Repeat((byte)42, 200_000).ToArray();
            await Write(channel, payload, asynchronous);
            var wire = stream.ToArray();
            using var decoded = new MemoryStream();
            var sequence = 0;
            for (var offset = 0; offset < wire.Length;)
            {
                var length = LengthAt(wire, offset); var original = LengthAt(wire, offset + 4);
                Assert.Equal((byte)sequence++, wire[offset + 3]);
                Assert.InRange(original, 1, 64 * 1024);
                using var source = new MemoryStream(wire, offset + 7, length);
                using var inflater = new ZLibStream(source, CompressionMode.Decompress);
                using var block = new MemoryStream(); inflater.CopyTo(block);
                Assert.Equal(original, block.Length); block.Position = 0; block.CopyTo(decoded);
                offset += 7 + length;
            }
            Assert.Equal(Frame(0, payload), decoded.ToArray());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task WritesSmallOrIncompressiblePacketsVerbatim(bool asynchronous)
        {
            foreach (var size in new[] { 0, 1, 5000 })
            {
                using var stream = new MemoryStream();
                var channel = new PacketChannel(stream); Enable(channel);
                var data = new byte[size]; new Random(718).NextBytes(data);
                await Write(channel, data, asynchronous);
                var wire = stream.ToArray();
                Assert.Equal(0, LengthAt(wire, 4));
                Assert.Equal(Frame(0, data), wire[7..]);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OuterSequenceWrapsAndResetsAtNewCommands(bool asynchronous)
        {
            var blocks = Enumerable.Range(0, 258).SelectMany(i => Block(i, Frame(i, new byte[] { 7 }), false));
            using var stream = new MemoryStream(blocks.Concat(Block(0, Frame(0, new byte[] { 8 }))).ToArray());
            var channel = new PacketChannel(stream); Enable(channel);
            for (var i = 0; i < 258; i++) Assert.Equal(new byte[] { 7 }, await Read(channel, asynchronous));
            channel.CompleteResponse(); channel.BeginCommand();
            Assert.Equal(new byte[] { 8 }, await Read(channel, asynchronous));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RejectsCorruptLengthsChecksumSequencesAndExtraZlibInput(bool asynchronous)
        {
            var good = Block(0, Frame(0, new byte[100]));
            var checksum = (byte[])good.Clone(); checksum[^1] ^= 1;
            var shortOutput = (byte[])good.Clone(); WriteLength(shortOutput, 4, 103);
            var longOutput = (byte[])good.Clone(); WriteLength(longOutput, 4, 105);
            var sequence = (byte[])good.Clone(); sequence[3] = 1;
            var trailing = good.Concat(new byte[] { 0 }).ToArray(); WriteLength(trailing, 0, good.Length - 6);
            var joined = good.Concat(good[7..]).ToArray(); WriteLength(joined, 0, joined.Length - 7);
            var dictionary = new byte[] { 6, 0, 0, 0, 100, 0, 0, 0x78, 0x20, 0, 0, 0, 1 };
            var missingEnd = new byte[7 + 2 + 5 + 104 + 4];
            WriteLength(missingEnd, 0, missingEnd.Length - 7); WriteLength(missingEnd, 4, 104);
            missingEnd[7] = 0x78; missingEnd[8] = 1; missingEnd[9] = 0; // non-final stored block, correct bytes and checksum
            missingEnd[10] = 104; missingEnd[12] = 151; missingEnd[13] = 255;
            Frame(0, new byte[100]).CopyTo(missingEnd, 14); good[^4..].CopyTo(missingEnd, missingEnd.Length - 4);
            foreach (var wire in new[] { checksum, shortOutput, longOutput, sequence, trailing, joined, dictionary, missingEnd, new byte[7], good[..6], good[..^1] })
            {
                using var stream = new MemoryStream(wire);
                var channel = new PacketChannel(stream); Enable(channel);
                await Assert.ThrowsAsync<ProtocolException>(async () => await Read(channel, asynchronous));
                Assert.Throws<ObjectDisposedException>(() => channel.BeginCommand());
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ExistingLogicalPacketLimitStillRejectsCompressedPayload(bool asynchronous)
        {
            using var stream = new MemoryStream(Block(0, Frame(0, new byte[100])));
            var channel = new PacketChannel(stream, 99); Enable(channel);
            await Assert.ThrowsAsync<ProtocolException>(async () => await Read(channel, asynchronous));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CompletionCannotDiscardBufferedDataOrCarryItIntoNextCommand(bool asynchronous)
        {
            foreach (var begin in new[] { false, true })
            {
                using var stream = new MemoryStream(Block(0, Frame(0, new byte[] { 1 }).Concat(new byte[] { 9 }).ToArray()));
                var channel = new PacketChannel(stream); Enable(channel);
                Assert.Equal(new byte[] { 1 }, await Read(channel, asynchronous));
                Assert.Throws<ProtocolException>(() => { if (begin) channel.BeginCommand(); else channel.CompleteResponse(); });
                Assert.False(stream.CanRead);
            }
        }

        [Fact]
        public void CompressionOptionRoundTripsToEndpointAndRejectsOb20Combination()
        {
            var builder = new OceanBaseConnectionStringBuilder("Server=localhost;User ID=u;Use Compression=true");
            var options = builder.Build().ForEndpoint(OceanBaseConnector.Internal.OceanBaseEndpoint.Parse("elsewhere", 2881)[0]);
            Assert.Equal(true, options.GetType().GetProperty("UseCompression")?.GetValue(options));
            Assert.Contains("Use Compression", builder.ToSanitizedConnectionString(false));
            builder.UseOb20 = true;
            Assert.Throws<ArgumentException>(() => builder.Build());
            Assert.Throws<ArgumentException>(() => new OceanBaseConnectionStringBuilder("Use Compression=maybe"));
        }

        [Theory]
        [InlineData(false, 0)]
        [InlineData(true, 0)]
        [InlineData(false, 17)]
        [InlineData(true, 17)]
        public async Task LargeLogicalPacketsSpanCompressionBlocksAndPreserveEmptyTerminators(bool asynchronous, int extra)
        {
            using var stream = new MemoryStream();
            var channel = new PacketChannel(stream); Enable(channel);
            var data = new byte[PacketChannel.MaxFramePayload + extra];
            data[0] = 42; data[^1] = 99;
            await Write(channel, data, asynchronous);
            stream.Position = 0; channel.BeginCommand();
            Assert.Equal(data, await Read(channel, asynchronous)); channel.CompleteResponse();
        }

        [Fact]
        public async Task CanceledAsyncIoPoisonsTheCompressedTransport()
        {
            using var stream = new MemoryStream(Block(0, Frame(0, new byte[20])));
            var channel = new PacketChannel(stream); Enable(channel);
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => channel.WritePacketAsync(new byte[] { 3 }, cancel.Token));
            Assert.False(stream.CanRead);
        }

        [Fact]
        public async Task StreamedAsyncPayloadUsesOnlyAsyncIoAcrossShortNetworkReads()
        {
            using var wire = new AsyncOnlyStream();
            var channel = new PacketChannel(wire); Enable(channel);
            var expected = Enumerable.Repeat((byte)73, 150_000).ToArray();
            await channel.WritePacketAsync(expected);
            channel.CompleteResponse(false); wire.Position = 0; channel.BeginCommand();
            using var payload = await channel.OpenPayloadAsync();
            using var result = new MemoryStream();
            var buffer = new byte[97];
            int read;
            while ((read = await payload.ReadAsync(buffer, 0, buffer.Length)) != 0) result.Write(buffer, 0, read);
            Assert.Equal(expected, result.ToArray()); channel.CompleteResponse();
        }

        private sealed class AsyncOnlyStream : MemoryStream
        {
            public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous network read.");
            public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous network write.");
            public override void Flush() => throw new InvalidOperationException("Synchronous network flush.");
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(base.Read(buffer, offset, Math.Min(count, 3))); }
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            { cancellationToken.ThrowIfCancellationRequested(); base.Write(buffer, offset, count); return Task.CompletedTask; }
            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }
    }
}
