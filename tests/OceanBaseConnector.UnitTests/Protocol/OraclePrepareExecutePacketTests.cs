using System;
using System.Data;
using System.Linq;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests.Protocol
{
    public sealed class OraclePrepareExecutePacketTests
    {
        // Field order follows fixed ComStmtPrepareExecute; CRCs were emitted by its original Java CRC class.
        [Theory]
        [InlineData(0u, "begin p; end;", "a10000000000010000000d626567696e20703b20656e643b000000000000000000000000be5b825b00000000")]
        [InlineData(0x89abcdefu, "begin p('患者'); end;", "a1efcdab89000100000017626567696e20702827e682a3e8808527293b20656e643b00000000000000000000000011c6398200000000")]
        public void OraclePrepareExecuteMatchesIndependentRequest(uint id, string sql, string hex)
            => Assert.Equal(Convert.FromHexString(hex), OraclePrepareExecutePacket.Build(id, sql, 0, Array.Empty<byte>()));

        [Fact]
        public void OraclePrepareExecuteReusesExistingParameterEncodingWithoutAddingDirections()
        {
            var parameters = PreparedParameters.Encode(new[]
            {
                new OceanBaseParameter { DbType = DbType.Int64, Value = 42L },
                new OceanBaseParameter { DbType = DbType.String, Value = DBNull.Value },
            }, OceanBaseCompatibilityMode.Oracle);
            var packet = OraclePrepareExecutePacket.Build(0x12345678, "begin p(?, ?); end;", 2, parameters);
            Assert.Equal(Convert.FromHexString("a178563412000100000013626567696e2070283f2c203f293b20656e643b020000000201080006002a00000000000000000000000000000004f6286500000000"), packet);
        }

        [Theory]
        [InlineData(0, "00")] [InlineData(250, "FA")] [InlineData(251, "FCFB00")]
        [InlineData(65535, "FCFFFF")] [InlineData(65536, "FD000001")]
        [InlineData(16777216, "FE0000000100000000")]
        public void OraclePrepareExecuteUsesByteLengthPrefixesAndKeepsFixedTrailer(int length, string prefix)
        {
            var sql = new string('x', length); var packet = OraclePrepareExecutePacket.Build(uint.MaxValue, sql, 0, Array.Empty<byte>());
            Assert.Equal(Convert.FromHexString(prefix), packet.AsSpan(10, prefix.Length / 2).ToArray());
            var reader = new ByteCursor(packet); Assert.Equal(0xa1, reader.ReadUInt8()); Assert.Equal(uint.MaxValue, reader.ReadUInt32());
            Assert.Equal(0, reader.ReadUInt8()); Assert.Equal(1u, reader.ReadUInt32());
            Assert.Equal(length, reader.ReadLengthEncodedInteger()); reader.Skip(length);
            Assert.Equal(0u, reader.ReadUInt32()); Assert.Equal(0u, reader.ReadUInt32()); Assert.Equal(0u, reader.ReadUInt32());
            reader.ReadUInt32(); Assert.Equal(0u, reader.ReadUInt32()); Assert.False(reader.HasRemaining);
        }

        [Fact]
        public void OraclePrepareExecuteValidatesCountsPayloadDimensionsAndSizeBeforeAllocation()
        {
            Assert.Throws<ArgumentNullException>(() => OraclePrepareExecutePacket.Build(0, null!, 0, Array.Empty<byte>()));
            Assert.Throws<ArgumentNullException>(() => OraclePrepareExecutePacket.Build(0, "x", 0, null!));
            foreach (var count in new[] { -1, 65536, int.MaxValue })
                Assert.Throws<ArgumentOutOfRangeException>(() => OraclePrepareExecutePacket.Build(0, "x", count, Array.Empty<byte>()));
            Assert.Throws<ArgumentException>(() => OraclePrepareExecutePacket.Build(0, "x", 0, new byte[1]));
            Assert.Throws<ArgumentException>(() => OraclePrepareExecutePacket.Build(0, "x", 2, new byte[5]));
            var excessive = new byte[64 * 1024 * 1024];
            Assert.Throws<ArgumentException>(() => OraclePrepareExecutePacket.Build(0, "x", 1, excessive));
            Assert.Throws<ArgumentException>(() => OraclePrepareExecutePacket.Build(0, new string('患', 64 * 1024 * 1024 / 3), 0, Array.Empty<byte>()));
            Assert.Throws<System.Text.EncoderFallbackException>(() => OraclePrepareExecutePacket.Build(0, "\ud800", 0, Array.Empty<byte>()));
        }

        private static byte[] Header => Convert.FromHexString("00efcdab89020003000007000400000001");

        [Fact]
        public void OraclePrepareExecuteReadsExtendedHeaderWithoutConsumingTailOrAllocatingMetadata()
        {
            var header = OraclePrepareExecutePacket.ParseHeader(Header);
            Assert.Equal(0x89abcdefu, header.StatementId); Assert.Equal(2, header.ColumnCount); Assert.Equal(3, header.ParameterCount);
            Assert.Equal(7, header.WarningCount); Assert.Equal(4u, header.ExtendFlags); Assert.True(header.HasResultSet);
            var limits = Header; limits[5] = 0; limits[6] = 16; limits[7] = 255; limits[8] = 255; limits[15] = 128;
            header = OraclePrepareExecutePacket.ParseHeader(limits);
            Assert.Equal(4096, header.ColumnCount); Assert.Equal(65535, header.ParameterCount); Assert.Equal(0x80000004u, header.ExtendFlags);
        }

        [Fact]
        public void OraclePrepareExecuteRejectsEveryTruncatedHeaderAndTrailingBytes()
        {
            for (var length = 0; length < Header.Length; length++)
                Assert.Throws<ProtocolException>(() => OraclePrepareExecutePacket.ParseHeader(Header.AsSpan(0, length).ToArray()));
            Assert.Throws<ProtocolException>(() => OraclePrepareExecutePacket.ParseHeader(Header.Concat(new byte[] { 0 }).ToArray()));
            Assert.Throws<ProtocolException>(() => OraclePrepareExecutePacket.ParseHeader(new byte[] { 0, 0, 0, 2, 0, 0, 0 }));
        }

        [Theory]
        [InlineData(0, 1)] [InlineData(0, 254)] [InlineData(0, 255)]
        [InlineData(9, 1)] [InlineData(9, 255)] [InlineData(16, 2)] [InlineData(16, 255)]
        [InlineData(6, 17)]
        public void OraclePrepareExecuteRejectsMalformedHeaderFields(int offset, byte value)
        {
            var bytes = Header; bytes[offset] = value;
            Assert.Throws<ProtocolException>(() => OraclePrepareExecutePacket.ParseHeader(bytes));
        }

        [Fact]
        public void OraclePrepareExecutePreservesNoResultHeaderAndOpaqueFlags()
        {
            var bytes = Header; bytes[16] = 0;
            var header = OraclePrepareExecutePacket.ParseHeader(bytes);
            Assert.False(header.HasResultSet); Assert.Equal(2, header.ColumnCount); Assert.Equal(4u, header.ExtendFlags);
        }
    }
}
