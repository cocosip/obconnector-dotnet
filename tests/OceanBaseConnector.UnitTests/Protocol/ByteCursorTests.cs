using System;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests.Protocol
{
    public sealed class ByteCursorTests
    {
        [Fact]
        public void ReadsLittleEndianPrimitives()
        {
            var data = new byte[]
            {
                0x01,
                0x34, 0x12,
                0x56, 0x34, 0x12,
                0x78, 0x56, 0x34, 0x12,
                0xEF, 0xCD, 0xAB, 0x89, 0x67, 0x45, 0x23, 0x01,
            };
            var cursor = new ByteCursor(data);

            Assert.Equal(0x01, cursor.ReadUInt8());
            Assert.Equal(0x1234, cursor.ReadUInt16());
            Assert.Equal(0x123456u, cursor.ReadUInt24());
            Assert.Equal(0x12345678u, cursor.ReadUInt32());
            Assert.Equal(0x0123456789ABCDEFul, cursor.ReadUInt64());
            Assert.Equal(0, cursor.Remaining);
        }

        [Fact]
        public void MaxUInt24RoundTrips()
        {
            var data = new byte[] { 0xFF, 0xFF, 0xFF };
            Assert.Equal(0xFFFFFFu, new ByteCursor(data).ReadUInt24());
        }

        [Fact]
        public void UnderrunIsRejectedForEveryPrimitive()
        {
            Assert.Throws<ProtocolException>(() => new ByteCursor(new byte[0]).ReadUInt8());
            Assert.Throws<ProtocolException>(() => new ByteCursor(new byte[1]).ReadUInt16());
            Assert.Throws<ProtocolException>(() => new ByteCursor(new byte[2]).ReadUInt24());
            Assert.Throws<ProtocolException>(() => new ByteCursor(new byte[3]).ReadUInt32());
            Assert.Throws<ProtocolException>(() => new ByteCursor(new byte[7]).ReadUInt64());
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(5)]
        public void ReadBytesBeyondRemainingIsRejected(int available)
        {
            var cursor = new ByteCursor(new byte[available]);
            Assert.Throws<ProtocolException>(() => cursor.ReadBytes(available + 1));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(int.MinValue)]
        public void NegativeCountsAreRejectedWithoutUnderflow(int count)
        {
            var cursor = new ByteCursor(new byte[8]);
            Assert.Throws<ProtocolException>(() => cursor.ReadBytes(count));
            Assert.Throws<ProtocolException>(() => cursor.Skip(count));
        }

        [Fact]
        public void SkipAndReadRemaining()
        {
            var cursor = new ByteCursor(new byte[] { 1, 2, 3, 4, 5 });
            cursor.Skip(2);
            Assert.Equal(new byte[] { 3, 4, 5 }, cursor.ReadRemaining());
            Assert.Equal(0, cursor.Remaining);
        }

        [Fact]
        public void ConstructorValidatesRange()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ByteCursor(new byte[4], 2, 3));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ByteCursor(new byte[4], 5, 0));
            Assert.Throws<ArgumentNullException>(() => new ByteCursor(null!));
        }

        [Fact]
        public void SegmentConstructionReadsOnlyWithinRange()
        {
            var data = new byte[] { 0xFF, 0x2A, 0x00, 0x99 };
            var cursor = new ByteCursor(data, 1, 2);
            Assert.Equal(0x2A, cursor.ReadUInt8());
            Assert.Equal(0x00, cursor.ReadUInt8());
            Assert.Throws<ProtocolException>(() => cursor.ReadUInt8());
        }
    }
}
