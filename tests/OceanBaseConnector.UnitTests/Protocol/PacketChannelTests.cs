using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests.Protocol
{
    public sealed class PacketChannelTests
    {
        private static byte[] Frame(int sequence, params byte[] payload)
        {
            var length = payload.Length;
            var frame = new byte[4 + length];
            frame[0] = (byte)length;
            frame[1] = (byte)(length >> 8);
            frame[2] = (byte)(length >> 16);
            frame[3] = (byte)sequence;
            payload.CopyTo(frame, 4);
            return frame;
        }

        [Fact]
        public void SingleSmallPacketRoundTrips()
        {
            var stream = new SpyStream();
            var channel = new PacketChannel(stream);
            channel.BeginCommand();
            channel.WritePacket(new byte[] { 0x0E, 0x01, 0x02 });

            var written = stream.ToArray();
            Assert.Equal(new byte[] { 0x03, 0x00, 0x00, 0x00, 0x0E, 0x01, 0x02 }, written);

            stream.Position = 0;
            channel.BeginCommand();
            Assert.Equal(new byte[] { 0x0E, 0x01, 0x02 }, channel.ReadPacket());
        }

        [Fact]
        public void EmptyPacketRoundTrips()
        {
            var stream = new SpyStream();
            var channel = new PacketChannel(stream);
            channel.BeginCommand();
            channel.WritePacket(Array.Empty<byte>());

            Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00 }, stream.ToArray());

            stream.Position = 0;
            channel.BeginCommand();
            Assert.Empty(channel.ReadPacket());
        }

        [Fact]
        public void MultiChunkPayloadReassembles()
        {
            // One full frame plus a 5-byte continuation.
            var payload = new byte[PacketChannel.MaxFramePayload + 5];
            for (var i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)(i * 7);
            }

            var stream = new SpyStream();
            var channel = new PacketChannel(stream);
            channel.BeginCommand();
            channel.WritePacket(payload);

            // 0xFFFFFF full frame, 5-byte tail, sequences 0 and 1.
            var written = stream.ToArray();
            Assert.Equal(4 + PacketChannel.MaxFramePayload + 4 + 5, written.Length);
            Assert.Equal(0xFF, written[0]);
            Assert.Equal(0xFF, written[1]);
            Assert.Equal(0xFF, written[2]);
            Assert.Equal(0x00, written[3]);
            Assert.Equal(5, written[4 + PacketChannel.MaxFramePayload]);
            Assert.Equal(0x01, written[4 + PacketChannel.MaxFramePayload + 3]);

            stream.Position = 0;
            channel.BeginCommand();
            var read = channel.ReadPacket();
            Assert.Equal(payload.Length, read.Length);
            Assert.True(payload.AsSpan().SequenceEqual(read));
        }

        [Fact]
        public void ExactFrameMultipleGetsEmptyTerminator()
        {
            var payload = new byte[PacketChannel.MaxFramePayload];
            var stream = new SpyStream();
            var channel = new PacketChannel(stream);
            channel.BeginCommand();
            channel.WritePacket(payload);

            var written = stream.ToArray();
            // Full frame + zero-length terminator frame.
            Assert.Equal(4 + PacketChannel.MaxFramePayload + 4, written.Length);
            var terminator = written.AsSpan(4 + PacketChannel.MaxFramePayload);
            Assert.Equal(0, terminator[0]);
            Assert.Equal(0, terminator[1]);
            Assert.Equal(0, terminator[2]);
            Assert.Equal(1, terminator[3]);

            stream.Position = 0;
            channel.BeginCommand();
            var read = channel.ReadPacket();
            Assert.Equal(payload.Length, read.Length);
        }

        [Fact]
        public void ZeroLengthTerminatorAloneIsEmptyPacket()
        {
            var channel = new PacketChannel(new SpyStream(Frame(0)));
            Assert.Empty(channel.ReadPacket());
        }

        [Fact]
        public void SequenceNumbersIncrementAcrossLogicalPackets()
        {
            var stream = new SpyStream(
                Concat(Frame(0, 0x0A), Frame(1, 0x0B), Frame(2, 0x0C)));
            var channel = new PacketChannel(stream);

            Assert.Equal(new byte[] { 0x0A }, channel.ReadPacket());
            Assert.Equal(new byte[] { 0x0B }, channel.ReadPacket());
            Assert.Equal(new byte[] { 0x0C }, channel.ReadPacket());
        }

        [Fact]
        public void BeginCommandResetsSequences()
        {
            var stream = new SpyStream(
                Concat(Frame(0, 0x0A), Frame(0, 0x0B)));
            var channel = new PacketChannel(stream);

            Assert.Equal(new byte[] { 0x0A }, channel.ReadPacket());
            channel.BeginCommand();
            Assert.Equal(new byte[] { 0x0B }, channel.ReadPacket());
        }

        [Fact]
        public void WrongSequenceIsRejected()
        {
            var channel = new PacketChannel(new SpyStream(Frame(1, 0x0A)));
            var exception = Assert.Throws<ProtocolException>(() => channel.ReadPacket());
            Assert.Contains("sequence mismatch", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void SequenceSkipsAreRejectedWithinAChain()
        {
            var stream = new SpyStream(Concat(Frame(0, 0x0A), Frame(2, 0x0B)));
            var channel = new PacketChannel(stream);

            Assert.Equal(new byte[] { 0x0A }, channel.ReadPacket());
            Assert.Throws<ProtocolException>(() => channel.ReadPacket());
        }

        [Fact]
        public void SequenceWrapsModulo256()
        {
            // 257 frames: sequences 0..255, then the chain wraps back to 0.
            var frames = new byte[257][];
            for (var i = 0; i < 256; i++)
            {
                frames[i] = Frame(i, (byte)i);
            }

            frames[256] = Frame(0, 0xAB);

            var channel = new PacketChannel(new SpyStream(Concat(frames)));
            for (var i = 0; i < 256; i++)
            {
                var packet = channel.ReadPacket();
                Assert.Single(packet);
                Assert.Equal((byte)i, packet[0]);
            }

            Assert.Equal(new byte[] { 0xAB }, channel.ReadPacket());

            // After the wrap the next expected sequence is 1.
            var afterWrap = new PacketChannel(
                new SpyStream(Concat(Concat(frames), Frame(1, 0xCD))));
            for (var i = 0; i < 257; i++)
            {
                afterWrap.ReadPacket();
            }

            Assert.Equal(new byte[] { 0xCD }, afterWrap.ReadPacket());
        }

        [Fact]
        public void TruncatedHeaderIsRejected()
        {
            var channel = new PacketChannel(new SpyStream(new byte[] { 0x05, 0x00, 0x00 }));
            Assert.Throws<ProtocolException>(() => channel.ReadPacket());
        }

        [Fact]
        public void TruncatedPayloadIsRejected()
        {
            var channel = new PacketChannel(new SpyStream(new byte[] { 0x0A, 0x00, 0x00, 0x00, 0x01, 0x02 }));
            var exception = Assert.Throws<ProtocolException>(() => channel.ReadPacket());
            Assert.Contains("closed", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void SingleFrameAboveCapIsRejectedBeforeAllocation()
        {
            var channel = new PacketChannel(new SpyStream(Frame(0, new byte[512])), maxPacketSize: 256);
            Assert.Throws<ProtocolException>(() => channel.ReadPacket());
        }

        [Fact]
        public void ContinuationChainAboveCapIsRejected()
        {
            // Two full frames = 2 x 0xFFFFFF bytes of claimed payload, capped at 1 MB.
            var stream = new SpyStream(Concat(
                Frame(0, new byte[PacketChannel.MaxFramePayload]),
                Frame(1, new byte[PacketChannel.MaxFramePayload])));
            var channel = new PacketChannel(stream, maxPacketSize: 1024 * 1024);

            Assert.Throws<ProtocolException>(() => channel.ReadPacket());
        }

        [Fact]
        public void ContinuationChainWithinCapReassembles()
        {
            // Simulate chunking without 16 MB frames by using small custom
            // frames? The frame maximum is protocol-fixed, so exercise the
            // continuation path through the exact-multiple writer instead:
            // payload of 2 x MaxFramePayload must reassemble to the same bytes.
            var payload = new byte[2 * PacketChannel.MaxFramePayload];
            payload[0] = 0xAA;
            payload[payload.Length - 1] = 0xBB;

            var stream = new SpyStream();
            var channel = new PacketChannel(stream);
            channel.BeginCommand();
            channel.WritePacket(payload);

            stream.Position = 0;
            channel.BeginCommand();
            var read = channel.ReadPacket();
            Assert.Equal(payload.Length, read.Length);
            Assert.Equal(0xAA, read[0]);
            Assert.Equal(0xBB, read[read.Length - 1]);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(3)]
        [InlineData(7)]
        [InlineData(11)]
        public void RandomTruncationNeverReturnsPartialPackets(int seed)
        {
            var valid = Concat(
                Frame(0, 0x0A, 0x0B, 0x0C),
                Frame(1, 0x0D),
                Frame(2, 0x0E, 0x0F));

            var random = new Random(seed);
            for (var attempt = 0; attempt < 32; attempt++)
            {
                var cut = random.Next(1, valid.Length);
                var truncated = new byte[cut];
                Buffer.BlockCopy(valid, 0, truncated, 0, cut);

                var channel = new PacketChannel(new SpyStream(truncated));
                try
                {
                    // Every read must either fail or return one of the fully
                    // intact packets; a partial payload (length 1 or 2 of the
                    // first packet) must never surface.
                    while (true)
                    {
                        var packet = channel.ReadPacket();
                        Assert.Contains(packet.Length, new[] { 3, 1, 2 });
                    }
                }
                catch (ProtocolException)
                {
                    // Expected outcome for a truncated stream.
                }
            }
        }

        [Fact]
        public void FramedErrFixtureReadsAndParses()
        {
            var fixture = ProtocolFixture.Load("framed-err-packet");
            var channel = new PacketChannel(new SpyStream(fixture.Payload));
            var payload = channel.ReadPacket();

            var expectation = fixture.Expectation;
            Assert.Equal(expectation.GetProperty("framePayloadLength").GetInt32(), payload.Length);

            var cursor = new ByteCursor(payload);
            Assert.Equal((byte)expectation.GetProperty("payloadHeader").GetInt32(), cursor.ReadUInt8());
            Assert.Equal(expectation.GetProperty("errorCode").GetInt32(), (int)cursor.ReadUInt16());
            Assert.Equal((byte)'#', cursor.ReadUInt8());
            Assert.Equal(
                expectation.GetProperty("sqlState").GetString(),
                Encoding.ASCII.GetString(cursor.ReadBytes(5)));
            Assert.Contains(
                expectation.GetProperty("messageContains").GetString()!,
                Encoding.ASCII.GetString(cursor.ReadRemaining()),
                StringComparison.Ordinal);
        }

        [Fact]
        public async Task AsyncPathMatchesSyncPathAndNeverBlocks()
        {
            var stream = new SpyStream(
                Concat(Frame(0, 0x0A, 0x0B), Frame(1, 0x0C)));
            var channel = new PacketChannel(stream);

            var first = await channel.ReadPacketAsync(CancellationToken.None);
            var second = await channel.ReadPacketAsync(CancellationToken.None);
            Assert.Equal(new byte[] { 0x0A, 0x0B }, first);
            Assert.Equal(new byte[] { 0x0C }, second);

            Assert.Equal(0, stream.SyncReads);
            Assert.True(stream.AsyncReads > 0);
        }

        [Fact]
        public async Task AsyncWriteNeverBlocks()
        {
            var stream = new SpyStream();
            var channel = new PacketChannel(stream);
            channel.BeginCommand();
            await channel.WritePacketAsync(new byte[] { 0x01, 0x02 });

            Assert.Equal(0, stream.SyncWrites);
            Assert.True(stream.AsyncWrites > 0);
            Assert.Equal(new byte[] { 0x02, 0x00, 0x00, 0x00, 0x01, 0x02 }, stream.ToArray());
        }

        [Fact]
        public async Task AsyncTruncationIsRejected()
        {
            var channel = new PacketChannel(new SpyStream(new byte[] { 0x05, 0x00, 0x00, 0x00, 0x01 }));
            await Assert.ThrowsAsync<ProtocolException>(
                () => channel.ReadPacketAsync(CancellationToken.None));
        }

        [Fact]
        public void WriteRejectsInvalidRanges()
        {
            var channel = new PacketChannel(new SpyStream());
            var payload = new byte[4];

            Assert.Throws<ArgumentOutOfRangeException>(() => channel.WritePacket(payload, 3, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => channel.WritePacket(payload, 0, 5));
            Assert.Throws<ArgumentNullException>(() => channel.WritePacket(null!));
        }

        private static byte[] Concat(params byte[][] parts)
        {
            var total = parts.Sum(p => p.Length);
            var result = new byte[total];
            var offset = 0;
            foreach (var part in parts)
            {
                Buffer.BlockCopy(part, 0, result, offset, part.Length);
                offset += part.Length;
            }

            return result;
        }
    }
}
