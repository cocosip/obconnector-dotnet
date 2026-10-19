using System;
using System.Linq;
using System.Text;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests.Protocol
{
    public sealed class Ob20PacketTests
    {
        // Independently emitted using the fixed Java baseline's CRC implementations.
        private static byte[] Fixture => Convert.FromHexString("290000ff000000ab201400efcdab89563412ff0d000000020000000000d4f3090000000353454c45435420310f430a36");
        private static byte[] Basic => Convert.FromHexString("090000000353454c4543542031");

        [Fact]
        public void Ob20MatchesIndependentFrameAndRetainsPayloadView()
        {
            var bytes = Fixture;
            var frame = Ob20Packet.Parse(bytes, 0x89abcdef, 0x123456, 255);
            Assert.Equal(Basic, frame.Payload.ToArray());
            Assert.Same(bytes, frame.Payload.Array);
            Assert.Equal(31, frame.Payload.Offset);
            Assert.Equal(0, frame.ExtraInfo.Count);
            Assert.Equal(2u, frame.Header.Flags);
            Assert.Equal((byte)255, frame.Header.CompressedSequence);
            Assert.Equal(Fixture, Ob20Packet.Build(0x89abcdef, 0x123456, 255, Basic));
        }

        [Fact]
        public void Ob20ChecksumsUseProtocolInitialAndFinalValuesAndSupportChunks()
        {
            var bytes = Encoding.ASCII.GetBytes("!123456789?");
            Assert.Equal((ushort)0xbb3d, Ob20Checksum.Header(bytes, 1, 9));
            Assert.Equal(0x58e3fa20u, Ob20Checksum.Payload(0, bytes, 1, 9));
            var state = Ob20Checksum.Payload(0, bytes, 1, 4);
            Assert.Equal(0x58e3fa20u, Ob20Checksum.Payload(state, bytes, 5, 5));
            Assert.Equal(123u, Ob20Checksum.Payload(123, bytes, 3, 0));
            Assert.Equal((ushort)0, Ob20Checksum.Header(Array.Empty<byte>(), 0, 0));
        }

        [Theory]
        [InlineData(0)] [InlineData(1)] [InlineData(3)] [InlineData(7)]
        [InlineData(9)] [InlineData(11)] [InlineData(15)] [InlineData(18)]
        [InlineData(19)] [InlineData(23)] [InlineData(27)] [InlineData(29)]
        [InlineData(31)] [InlineData(40)] [InlineData(47)]
        public void Ob20RejectsCorruptionBeforeExposingPayload(int offset)
        {
            var bytes = Fixture; bytes[offset] ^= 1;
            Assert.Throws<ProtocolException>(() => Ob20Packet.Parse(bytes, 0x89abcdef, 0x123456, 255));
        }

        [Fact]
        public void Ob20RejectsEveryTruncationAndTrailingBytes()
        {
            var bytes = Fixture;
            for (var length = 0; length < bytes.Length; length++)
                Assert.Throws<ProtocolException>(() => Ob20Packet.Parse(bytes.AsSpan(0, length).ToArray(), 0x89abcdef, 0x123456, 255));
            Assert.Throws<ProtocolException>(() => Ob20Packet.Parse(bytes.Concat(new byte[1]).ToArray(), 0x89abcdef, 0x123456, 255));
        }

        [Fact]
        public void Ob20ChecksIdentityAndPayloadLimitBeforeRequiringBody()
        {
            var bytes = Fixture.AsSpan(0, 31).ToArray();
            Assert.Equal(13, Ob20Packet.ParseHeader(bytes, 0x89abcdef, 0x123456, 255, 13).PayloadLength);
            Assert.Throws<ProtocolException>(() => Ob20Packet.ParseHeader(bytes, 0x89abcdef, 0x123456, 255, 12));
            Assert.Throws<ProtocolException>(() => Ob20Packet.ParseHeader(bytes, 7, 0x123456, 255));
            Assert.Throws<ProtocolException>(() => Ob20Packet.ParseHeader(bytes, 0x89abcdef, 7, 255));
            Assert.Throws<ProtocolException>(() => Ob20Packet.ParseHeader(bytes, 0x89abcdef, 0x123456, 0));
        }

        [Theory]
        [InlineData(0, 1)] // outer length mismatch
        [InlineData(4, 1)] // actual compression is unsupported
        [InlineData(7, 1)] // magic
        [InlineData(9, 1)] // version
        [InlineData(19, 255)] // uint32 payload cannot fit a 24-bit outer frame
        [InlineData(22, 255)]
        public void Ob20StructuralValidationRemainsWhenChecksumsAreDisabled(int offset, byte value)
        {
            var bytes = Fixture; DisableChecksums(bytes); bytes[offset] = value;
            Assert.Throws<ProtocolException>(() => Ob20Packet.Parse(bytes, 0x89abcdef, 0x123456, 255));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public void Ob20SplitsExtraFromBasicAndPreservesUnknownFlags(bool checksum)
        {
            var extra = new byte[] { 0, 1, 255, 10 };
            var flags = 0x8000000bu;
            var bytes = Ob20Packet.Build(uint.MaxValue, 0xffffff, 0, Basic, extra, flags, checksum);
            var frame = Ob20Packet.Parse(bytes, uint.MaxValue, 0xffffff, 0);
            Assert.Equal(flags, frame.Header.Flags);
            Assert.Equal(extra, frame.ExtraInfo.ToArray());
            Assert.Equal(Basic, frame.Payload.ToArray());
            Assert.Same(bytes, frame.ExtraInfo.Array);
            Assert.Same(bytes, frame.Payload.Array);
            if (!checksum)
            {
                Assert.Equal(new byte[2], bytes.AsSpan(29, 2).ToArray());
                Assert.Equal(new byte[4], bytes.AsSpan(bytes.Length - 4).ToArray());
            }
        }

        [Fact]
        public void Ob20RejectsInvalidExtraLengthEvenWithoutChecksums()
        {
            var bytes = Ob20Packet.Build(1, 2, 3, Basic, new byte[] { 1 }, checksum: false);
            for (var i = 31; i < 35; i++) bytes[i] = 255;
            Assert.Throws<ProtocolException>(() => Ob20Packet.Parse(bytes, 1, 2, 3));
            bytes = Ob20Packet.Build(1, 2, 3, Array.Empty<byte>(), checksum: false);
            bytes[23] |= 1;
            Assert.Throws<ProtocolException>(() => Ob20Packet.Parse(bytes, 1, 2, 3));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public void Ob20AllowsEmptyPayloadAndEmptyPresentExtra(bool present)
        {
            var bytes = Ob20Packet.Build(0, 0, 0, Array.Empty<byte>(), present ? Array.Empty<byte>() : null);
            var frame = Ob20Packet.Parse(bytes, 0, 0, 0);
            Assert.Equal(0, frame.Payload.Count);
            Assert.Equal(present, (frame.Header.Flags & 1) != 0);
        }

        [Fact]
        public void Ob20PayloadCapIncludesExtraPrefixAndBytes()
        {
            var bytes = Ob20Packet.Build(1, 0, 1, Basic, new byte[7]);
            Assert.Equal(24, Ob20Packet.ParseHeader(bytes, 1, 0, 1, 24).PayloadLength);
            Assert.Throws<ProtocolException>(() => Ob20Packet.ParseHeader(bytes, 1, 0, 1, 23));
            Assert.Throws<ArgumentOutOfRangeException>(() => Ob20Packet.Build(1, 0x1000000, 1, Basic));
            Assert.Throws<ArgumentOutOfRangeException>(() => Ob20Packet.ParseHeader(bytes, 1, 0, 1, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => Ob20Packet.ParseHeader(bytes, 1, 0x1000000, 1));
        }

        [Fact]
        public void Ob20RejectsOutgoingFrameAboveOuterLengthLimit()
        {
            var maximum = new byte[0xffffff - 28];
            var bytes = Ob20Packet.Build(1, 0, 0, maximum, checksum: false);
            Assert.Equal(0xffffff + 7, bytes.Length);
            Assert.Equal(maximum.Length, Ob20Packet.ParseHeader(bytes, 1, 0, 0).PayloadLength);
            Assert.Throws<ArgumentException>(() => Ob20Packet.Build(1, 0, 0, maximum, Array.Empty<byte>(), checksum: false));
            Assert.Throws<ArgumentException>(() => Ob20Packet.Build(1, 0, 0, new byte[maximum.Length + 1], checksum: false));
        }

        [Theory]
        [InlineData(true)] [InlineData(false)]
        public void Ob20EachNonzeroChecksumIsEnforcedIndependently(bool headerEnabled)
        {
            var bytes = Fixture;
            if (headerEnabled) Array.Clear(bytes, bytes.Length - 4, 4);
            else bytes[29] = bytes[30] = 0;
            Assert.Equal(Basic, Ob20Packet.Parse(bytes, 0x89abcdef, 0x123456, 255).Payload.ToArray());
            bytes[headerEnabled ? 27 : 31] ^= 1;
            Assert.Throws<ProtocolException>(() => Ob20Packet.Parse(bytes, 0x89abcdef, 0x123456, 255));
        }

        [Fact]
        public void Ob20PreservesReservedAndOuterSequenceWithoutAssigningThemInnerSemantics()
        {
            var bytes = Fixture; DisableChecksums(bytes);
            bytes[3] = 19; bytes[27] = 0xcd; bytes[28] = 0xab;
            var header = Ob20Packet.ParseHeader(bytes, 0x89abcdef, 0x123456, 255);
            Assert.Equal((byte)19, header.CompressedSequence);
            Assert.Equal((ushort)0xabcd, header.Reserved);
        }

        [Theory]
        [InlineData(-1, 1)] [InlineData(0, -1)] [InlineData(1, 1)]
        [InlineData(int.MaxValue, int.MaxValue)]
        public void Ob20ChecksumRejectsOutOfBoundsRanges(int offset, int count)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Ob20Checksum.Header(new byte[1], offset, count));
            Assert.Throws<ArgumentOutOfRangeException>(() => Ob20Checksum.Payload(0, new byte[1], offset, count));
        }

        [Fact]
        public void Ob20RejectsInvalidApiArguments()
        {
            Assert.Throws<ArgumentNullException>(() => Ob20Packet.Build(0, 0, 0, null!));
            Assert.Throws<ArgumentNullException>(() => Ob20Packet.Parse(null!, 0, 0, 0));
            Assert.Throws<ArgumentNullException>(() => Ob20Checksum.Header(null!, 0, 0));
            Assert.Throws<ArgumentNullException>(() => Ob20Checksum.Payload(0, null!, 0, 0));
            Assert.Throws<ArgumentException>(() => Ob20Packet.Build(0, 0, 0, Basic, flags: 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => Ob20Packet.ParseHeader(Fixture, 0, 0, 0, int.MaxValue));
        }

        private static void DisableChecksums(byte[] bytes)
        {
            bytes[29] = bytes[30] = 0;
            Array.Clear(bytes, bytes.Length - 4, 4);
        }
    }
}
