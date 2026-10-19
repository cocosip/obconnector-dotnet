using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests.Protocol
{
    public sealed class LobLocatorTests
    {
        // Published V1 example in fixed Java Lob.java; includes a 17-byte rowid before "abc".
        private static byte[] V1 => Convert.FromHexString("21424F4C0100000089AED4CB3FAF05007BC3000000E90300110000000000000011000000030000002A4141494B41514141414141414141413D616263");

        [Fact]
        public void V1ReadsPublishedLocatorWithoutCopyingOrExposingItsRowId()
        {
            var bytes = V1; var value = LobLocator.Parse(bytes);
            Assert.Equal(1, value.Version); Assert.Equal(3, value.ByteLength);
            Assert.True(value.HasInlineData); Assert.Same(bytes, value.InlineData.Array);
            Assert.Equal(57, value.InlineData.Offset); Assert.Equal("abc", System.Text.Encoding.ASCII.GetString(value.InlineData.ToArray()));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public void V1AcceptsEmptyAndCompatibilityLocators(bool empty)
        {
            var bytes = V1.Take(empty ? 40 : V1.Length).ToArray();
            Write32(bytes, 32, 0); Write32(bytes, 36, empty ? 0u : (uint)(bytes.Length - 40));
            bytes[28] = 1; Array.Clear(bytes, 8, 20);
            var value = LobLocator.Parse(bytes);
            Assert.True(value.HasInlineData); Assert.Equal(bytes.Length - 40, value.ByteLength);
            Assert.Equal(40, value.InlineData.Offset); Assert.Same(bytes, value.InlineData.Array);
        }

        public static IEnumerable<object[]> V2Layouts()
        {
            // Memory header: ObLobCommon version=1, in_row/use_big_endian/is_mem_loc; version=2/type=1.
            yield return new object[] { "0184010002A10000616263", true, 8, 3L }; // simple
            yield return new object[] { "0184010002A10000", true, 8, 0L };
            yield return new object[] { "018401000221000001840000616263", true, 12, 3L }; // uninitialized disk
            yield return new object[] { "018401000221000001840000", true, 12, 0L };
            yield return new object[] { Disk(true, true, false, 3), true, 36, 3L };
            yield return new object[] { Disk(true, false, false, 3), true, 92, 3L }; // outrow disk + inline payload
            yield return new object[] { Disk(false, false, false, 0x100000003), false, 0, 0x100000003L };
            yield return new object[] { Disk(false, false, true, long.MaxValue), false, 0, long.MaxValue };
            yield return new object[] { Disk(true, true, true, 3), true, 36 + 41, 3L };
            yield return new object[] { Disk(true, false, true, 3), true, 92 + 41, 3L };
        }

        [Theory]
        [MemberData(nameof(V2Layouts))]
        public void V2DistinguishesInlineBytesFromDiskMetadataAndLargeExternalLengths(string hex, bool inline, int offset, long length)
        {
            var bytes = Convert.FromHexString(hex); var value = LobLocator.Parse(bytes);
            Assert.Equal(2, value.Version); Assert.Equal(length, value.ByteLength); Assert.Equal(inline, value.HasInlineData);
            if (inline)
            {
                Assert.Same(bytes, value.InlineData.Array); Assert.Equal(offset, value.InlineData.Offset);
                Assert.Equal(length, value.InlineData.Count);
                Assert.Equal(length == 0 ? Array.Empty<byte>() : new byte[] { 97, 98, 99 }, value.InlineData.ToArray());
            }
            else { Assert.Null(value.InlineData.Array); Assert.Equal(0, value.InlineData.Count); }
        }

        [Fact]
        public void EveryTruncatedFixedLengthLocatorIsRejected()
        {
            foreach (var bytes in new[] { V1, Convert.FromHexString(Disk(true, true, false, 3)),
                Convert.FromHexString(Disk(true, false, true, 3)), Convert.FromHexString(Disk(false, false, true, 10)) })
                for (var length = 0; length < bytes.Length; length++)
                    Assert.Throws<ProtocolException>(() => LobLocator.Parse(bytes.Take(length).ToArray()));
        }

        [Theory]
        [InlineData(32, uint.MaxValue)] [InlineData(36, uint.MaxValue)]
        [InlineData(32, 18u)] [InlineData(36, 2u)]
        public void V1RejectsOffsetsOverflowTruncationAndTrailingData(int position, uint number)
        {
            var bytes = V1; Write32(bytes, position, number);
            Assert.Throws<ProtocolException>(() => LobLocator.Parse(bytes));
        }

        [Fact]
        public void V1RejectsInvalidIdentityVersionAndOptions()
        {
            var bytes = V1; bytes[0] = 0;
            Assert.Throws<ProtocolException>(() => LobLocator.Parse(bytes));
            bytes = V1; bytes[15] |= 128;
            Assert.Throws<ProtocolException>(() => LobLocator.Parse(bytes));
            bytes = V1; bytes[4] = 3;
            Assert.Throws<NotSupportedException>(() => LobLocator.Parse(bytes));
            bytes = V1; bytes[30] = 1;
            Assert.Throws<NotSupportedException>(() => LobLocator.Parse(bytes));
            Assert.Throws<ArgumentNullException>(() => LobLocator.Parse(null!));
        }

        [Theory]
        [InlineData(4, 0x8102u)] // simple without inline bytes
        [InlineData(4, 0x1A102u)] // simple + extern
        [InlineData(4, 0x2A102u)] // freed
        [InlineData(4, 0xA002u)] // invalid type
        [InlineData(0, 0x8401u)] // disk header without memory marker
        [InlineData(0, 0x80018401u)] // reserved common bit
        public void V2RejectsContradictoryMemoryHeaders(int offset, uint header)
        {
            var bytes = Convert.FromHexString("0184010002A10000616263"); Write32(bytes, offset, header);
            Assert.Throws<ProtocolException>(() => LobLocator.Parse(bytes));
        }

        [Theory]
        [InlineData(32, uint.MaxValue)] [InlineData(36, uint.MaxValue)]
        [InlineData(32, 0u)] [InlineData(36, 2u)]
        [InlineData(30, 0xffffu)] [InlineData(40, 0xffffu)]
        public void V2RejectsExternalHeaderBoundsAndPayloadOverlap(int position, uint number)
        {
            var bytes = Convert.FromHexString(Disk(true, false, true, 3)); Write32(bytes, position, number);
            Assert.Throws<ProtocolException>(() => LobLocator.Parse(bytes));
        }

        [Fact]
        public void V2RejectsInconsistentDiskStateAndUnrepresentableLength()
        {
            var bytes = Convert.FromHexString(Disk(false, false, false, 3));
            bytes[9] &= 0xfe; Assert.Throws<ProtocolException>(() => LobLocator.Parse(bytes));
            bytes = Convert.FromHexString(Disk(false, false, false, 3)); bytes[9] |= 4;
            Assert.Throws<ProtocolException>(() => LobLocator.Parse(bytes));
            bytes = Convert.FromHexString(Disk(true, true, false, 3)); Write32(bytes, 28, 4);
            Assert.Throws<ProtocolException>(() => LobLocator.Parse(bytes));
            bytes = Convert.FromHexString(Disk(false, false, false, 3));
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(28), ulong.MaxValue);
            Assert.Throws<ProtocolException>(() => LobLocator.Parse(bytes));
        }

        [Fact]
        public void V2PreservesOpaqueExtensionsAndReadOnlyFlagsWithoutDecodingThem()
        {
            var bytes = Convert.FromHexString(Disk(true, true, true, 3));
            bytes[5] |= 0x50; // read-only + open
            Array.Fill(bytes, (byte)0xfe, 42, 5); // opaque extension and rowkey
            Assert.Equal(new byte[] { 97, 98, 99 }, LobLocator.Parse(bytes).InlineData.ToArray());
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)] [InlineData(true, true)]
        public void V2ExternalUninitializedDiskHandlesPayloadAndBothEmptyOffsets(bool empty, bool syntheticOffset)
        {
            var bytes = new byte[53 + (empty ? 0 : 3)];
            Write32(bytes, 0, 0x18401); Write32(bytes, 4, 0x12102);
            bytes[30] = 2; bytes[40] = 5; // opaque extension5 and rowkey2
            Write32(bytes, 32, syntheticOffset ? 9u : 13u); Write32(bytes, 36, empty ? 0u : 3u);
            Write32(bytes, 49, 0x8401);
            if (!empty) Array.Copy(new byte[] { 97, 98, 99 }, 0, bytes, 53, 3);
            var value = LobLocator.Parse(bytes);
            Assert.True(value.HasInlineData); Assert.Equal(empty ? 0 : 3, value.ByteLength);
            Assert.Equal(53, value.InlineData.Offset); Assert.Same(bytes, value.InlineData.Array);
        }

        [Fact]
        public void V2TemporaryFullIsSupportedButDeltaAndUnknownVersionsAreExplicitlyRejected()
        {
            var bytes = Convert.FromHexString("0184010002A20000616263");
            Assert.Equal(3, LobLocator.Parse(bytes).ByteLength);
            bytes[5] = 0xa3;
            Assert.Throws<NotSupportedException>(() => LobLocator.Parse(bytes));
            bytes[4] = 3;
            Assert.Throws<NotSupportedException>(() => LobLocator.Parse(bytes));
        }

        private static void Write32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);

        private static string Disk(bool inline, bool diskInline, bool external, long length)
        {
            // Independent C++ layout: common4 + lob id16 + byte_size8 + optional outrow ctx48/charlen8.
            var start = external ? 49 : 8; var payload = start + (diskInline ? 28 : 84);
            var bytes = new byte[payload + (inline ? checked((int)length) : 0)];
            Write32(bytes, 0, 0x18401); Write32(bytes, 4, 0x102u | (inline ? 0x2000u : 0) | (external ? 0x10000u : 0));
            if (external)
            {
                bytes[30] = 2; bytes[40] = 5; // rowkey2 after opaque extension5
                Write32(bytes, 32, (uint)(payload - 40)); Write32(bytes, 36, inline ? (uint)length : 0);
            }
            Write32(bytes, start, diskInline ? 0x8501u : 0x8101u);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(start + 20), length);
            if (inline) { bytes[payload] = 97; bytes[payload + 1] = 98; bytes[payload + 2] = 99; }
            return Convert.ToHexString(bytes);
        }
    }
}
