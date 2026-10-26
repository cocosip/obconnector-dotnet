using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Values;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class ComplexValueCodecTests
    {
        private static readonly ComplexType Number = new ComplexType(0);
        private static readonly ComplexType Text = new ComplexType(253);
        private static readonly ComplexType Raw = new ComplexType(203);
        private static readonly ComplexType Date = new ComplexType(12);
        private static readonly ComplexType Timestamp = new ComplexType(202);
        private static ComplexType Object(params ComplexType[] fields) => new ComplexType(false, fields);
        private static ComplexType Array(ComplexType element) => new ComplexType(true, element);
        private static byte[] Encode(ComplexType type, params object?[] values) => ComplexValueCodec.EncodeParameter(type, values, OceanBaseCompatibilityMode.Oracle);
        private static object?[] Decode(ComplexType type, string hex) => ComplexValueCodec.DecodeResult(new ByteCursor(Convert.FromHexString(hex)), type, OceanBaseCompatibilityMode.Oracle);

        [Fact]
        public void ObjectDirectionsUseDifferentNullOffsetsAndKeepNextField()
        {
            var type = Object(Number, Text, Raw);
            Assert.Equal(Convert.FromHexString("020531322E35300200FF"), Encode(type, DecimalCodec.Parse("12.50"), null, new byte[] { 0, 255 }));
            var cursor = new ByteCursor(Convert.FromHexString("080531322E35300200FF7B"));
            var value = ComplexValueCodec.DecodeResult(cursor, type, OceanBaseCompatibilityMode.Oracle);
            Assert.Equal("12.50", Assert.IsType<OceanBaseDecimal>(value[0]).ToString()); Assert.Null(value[1]);
            Assert.Equal(new byte[] { 0, 255 }, Assert.IsType<byte[]>(value[2])); Assert.Equal(123, cursor.ReadUInt8()); Assert.False(cursor.HasRemaining);
        }

        [Fact]
        public void NestedArraysAndObjectsUseTheirOwnDirectionalBitmaps()
        {
            var type = Object(Number, Array(Object(Text, Raw)), Date);
            var day = new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Unspecified);
            object?[] values = { DecimalCodec.Parse("42"), new object?[] { new object?[] { "患者😀", new byte[] { 0, 255 } }, null }, day };
            Assert.Equal(Convert.FromHexString("000234320202000AE682A3E88085F09F98800200FF07E8070203040506"), Encode(type, values));
            var result = Decode(type, "000234320208000AE682A3E88085F09F98800200FF07E8070203040506");
            var array = Assert.IsType<object?[]>(result[1]); Assert.Null(array[1]);
            var child = Assert.IsType<object?[]>(array[0]); Assert.Equal("患者😀", child[0]); Assert.Equal(new byte[] { 0, 255 }, Assert.IsType<byte[]>(child[1]));
            Assert.Equal(day, result[2]);
        }

        [Theory]
        [InlineData(0)] [InlineData(1)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(14)] [InlineData(15)] [InlineData(16)]
        public void NullBitmapBoundariesDoNotConsumeFollowingData(int count)
        {
            var type = Array(Number); var bytes = new byte[1 + (count + 9) / 8 + 1]; bytes[0] = (byte)count; bytes[bytes.Length - 1] = 123;
            for (var i = 0; i < count; i++) bytes[1 + (i + 2) / 8] |= (byte)(1 << ((i + 2) % 8));
            var cursor = new ByteCursor(bytes); var result = ComplexValueCodec.DecodeResult(cursor, type, OceanBaseCompatibilityMode.Oracle);
            Assert.Equal(count, result.Length); Assert.All(result, Assert.Null); Assert.Equal(123, cursor.ReadUInt8());
            var encoded = Encode(type, new object?[count]); Assert.Equal(1 + (count + 7) / 8, encoded.Length); Assert.Equal(count, encoded[0]);
            for (var i = 0; i < count; i++) Assert.NotEqual(0, encoded[1 + i / 8] & 1 << (i % 8));
        }

        [Fact]
        public void EmptyObjectAndEmptyCollectionAreValues()
        {
            Assert.Empty(Encode(Object())); Assert.Empty(Decode(Object(), "00"));
            Assert.Equal(new byte[] { 0 }, Encode(Array(Text))); Assert.Empty(Decode(Array(Text), "0000"));
            var result = Decode(Array(Text), "0300000361626300"); Assert.Equal(new object?[] { "", "abc", "" }, result);
        }

        [Theory]
        [InlineData(250)] [InlineData(251)] [InlineData(65535)]
        public void CollectionCountsUseLengthEncodingAtTheBoundary(int count)
        {
            var result = new byte[(count < 251 ? 1 : 3) + (count + 9) / 8 + 1];
            var offset = 1; result[0] = (byte)count;
            if (count >= 251) { result[0] = 252; result[1] = (byte)count; result[2] = (byte)(count >> 8); offset = 3; }
            for (var i = 0; i < count; i++) result[offset + (i + 2) / 8] |= (byte)(1 << ((i + 2) % 8));
            result[result.Length - 1] = 123;
            var cursor = new ByteCursor(result); var values = ComplexValueCodec.DecodeResult(cursor, Array(Number), OceanBaseCompatibilityMode.Oracle);
            Assert.Equal(count, values.Length); Assert.All(values, Assert.Null); Assert.Equal(123, cursor.ReadUInt8());
            var encoded = Encode(Array(Number), values); var header = new ByteCursor(encoded);
            Assert.Equal(count, header.ReadLengthEncodedInteger()); Assert.Equal((count + 7) / 8, header.Remaining);
            if (count >= 251) Assert.Equal(new byte[] { 252, (byte)count, (byte)(count >> 8) }, encoded.Take(3));
        }

        [Fact]
        public void NullObjectIsDistinctFromAnObjectContainingNullMembers()
        {
            var type = Object(Object(Number));
            Assert.Equal(new byte[] { 1 }, Encode(type, new object?[] { DBNull.Value }));
            Assert.Equal(new byte[] { 0, 1 }, Encode(type, new object?[] { new object?[] { null } }));
            Assert.Null(Decode(type, "04")[0]); Assert.Null(Assert.IsType<object?[]>(Decode(type, "0004")[0])[0]);
        }

        [Theory]
        [InlineData(0)] [InlineData(3)] [InlineData(6)] [InlineData(9)]
        public void EmbeddedTimestampScaleIsKeptWithoutRounding(byte scale)
        {
            var stamp = new OceanBaseTimestamp(new DateTime(2024, 2, 3, 4, 5, 6), scale == 0 ? 0 : scale == 3 ? 123000000 : scale == 6 ? 123456000 : 123456789, scale);
            var result = Assert.IsType<OceanBaseTimestamp>(ComplexValueCodec.DecodeResult(new ByteCursor(Encode(Object(Timestamp), stamp)), Object(Timestamp), OceanBaseCompatibilityMode.Oracle)[0]);
            Assert.Equal(stamp.Nanosecond, result.Nanosecond); Assert.Equal(scale, result.Scale);
        }

        [Fact]
        public void ExactIntegralAndDecimalInputsUseTheOracleNumberRange()
        {
            var encoded = Encode(Array(Number), long.MinValue, ulong.MaxValue, 1.25m);
            Assert.Equal(Convert.FromHexString("0300142D39323233333732303336383534373735383038" + "143138343436373434303733373039353531363135" + "04312E3235"), encoded);
            Assert.Throws<OverflowException>(() => Encode(Object(Number), DecimalCodec.Parse("1E126")));
            Assert.Throws<OverflowException>(() => Decode(Object(Number), "00053145313236"));
            Assert.Throws<ArgumentException>(() => Encode(Object(Number), 1.5d));
        }

        [Fact]
        public void ExcessiveCountAndScalarLengthDoNotRequireAdvertisedBodies()
        {
            Assert.Throws<NotSupportedException>(() => Encode(Array(Number), new object?[65536]));
            Assert.Throws<ProtocolException>(() => Decode(Object(Number), "00FC0102"));
            Assert.Throws<ProtocolException>(() => Decode(Object(Raw), "00FE0000000001000000"));
            Assert.Throws<ArgumentException>(() => Encode(Object(Array(Number)), new object?[] { 42 }));
        }

        [Fact]
        public void ScalarFamiliesKeepPrecisionAndBinaryData()
        {
            var type = Object(Number, Text, new ComplexType(254), Raw, Timestamp);
            const string number = "12345678901234567890123456789012345678";
            var stamp = new OceanBaseTimestamp(new DateTime(2024, 2, 3, 4, 5, 6), 123456789);
            var encoded = Encode(type, DecimalCodec.Parse(number), "\uFEFF患者😀", " x ", new byte[] { 0, 255 }, stamp);
            // All fields are non-NULL, so the one-byte bitmap is identical in both directions.
            var result = ComplexValueCodec.DecodeResult(new ByteCursor(encoded), type, OceanBaseCompatibilityMode.Oracle);
            Assert.Equal(number, Assert.IsType<OceanBaseDecimal>(result[0]).ToString()); Assert.Equal("\uFEFF患者😀", result[1]); Assert.Equal(" x ", result[2]);
            Assert.Equal(new byte[] { 0, 255 }, Assert.IsType<byte[]>(result[3])); Assert.Equal(stamp.ToString(), Assert.IsType<OceanBaseTimestamp>(result[4]).ToString());
        }

        public static IEnumerable<object[]> MalformedResults()
        {
            yield return new object[] { Array(Text), "" };
            yield return new object[] { Array(Text), "FB" };
            yield return new object[] { Array(Text), "FC" };
            yield return new object[] { Array(Text), "FEFFFFFFFFFFFFFFFF" };
            yield return new object[] { Array(Text), "FD000001" };
            yield return new object[] { Array(Text), "0100FF" };
            yield return new object[] { Array(Text), "0100FB" };
            yield return new object[] { Array(Text), "0100FCFFFF" };
            yield return new object[] { Object(Date), "0007E80702030405" };
            yield return new object[] { Object(Timestamp), "000B" + new string('0', 22) };
        }

        [Theory]
        [MemberData(nameof(MalformedResults))]
        public void MalformedLengthsAndCountsAreRejectedBeforeAllocation(object layout, string hex)
            => Assert.Throws<ProtocolException>(() => Decode((ComplexType)layout, hex));

        [Theory]
        [InlineData("0001FF")] [InlineData("0002C080")]
        public void InvalidUtf8IsNotReplaced(string hex)
            => Assert.Throws<DecoderFallbackException>(() => Decode(Object(Text), hex));

        [Theory]
        [InlineData(1)] [InlineData(160)] [InlineData(163)] [InlineData(255)]
        public void UnsupportedScalarLayoutsAreRejected(byte code) => Assert.Throws<NotSupportedException>(() => new ComplexType(code));

        [Fact]
        public void TypeLayoutsAreSnapshotsAndCollectionsRequireExactlyOneElementType()
        {
            var fields = new[] { Number }; var type = Object(fields); fields[0] = Text;
            Assert.Same(Number, type.Members[0]);
            Assert.Throws<ArgumentException>(() => new ComplexType(true)); Assert.Throws<ArgumentException>(() => new ComplexType(true, Number, Text));
            Assert.Throws<ArgumentException>(() => Object(new ComplexType[] { null! }));
        }

        [Theory]
        [InlineData("number")] [InlineData("text")] [InlineData("raw")] [InlineData("date")]
        [InlineData("timestamp")] [InlineData("count")] [InlineData("unicode")] [InlineData("fraction")]
        public void InvalidParametersAreNotCoercedOrTruncated(string fault)
        {
            var type = fault == "number" ? Number : fault == "raw" ? Raw : fault == "date" || fault == "fraction" ? Date : fault == "timestamp" ? Timestamp : Text;
            object value = fault == "number" ? "12" : fault == "raw" ? "FF" : fault == "date" ? DateTime.UtcNow
                : fault == "timestamp" ? "2024-01-01" : fault == "text" ? 123 : fault == "fraction" ? new DateTime(2024, 1, 1).AddTicks(1) : "\uD800";
            Assert.ThrowsAny<ArgumentException>(() => Encode(Object(type), fault == "count" ? System.Array.Empty<object?>() : new[] { value }));
        }

        [Fact]
        public void MySqlIsRejectedWithoutConsumingTheField()
        {
            var cursor = new ByteCursor(new byte[] { 0 });
            Assert.Throws<NotSupportedException>(() => ComplexValueCodec.DecodeResult(cursor, Object(), OceanBaseCompatibilityMode.MySql)); Assert.Equal(1, cursor.Remaining);
            Assert.Throws<NotSupportedException>(() => ComplexValueCodec.EncodeParameter(Object(), System.Array.Empty<object?>(), OceanBaseCompatibilityMode.MySql));
        }

        [Fact]
        public void DeepNestingAndAggregateCountsAreBounded()
        {
            var type = Object(Number); object?[] values = { DecimalCodec.Parse("1") };
            for (var i = 0; i < 33; i++) { type = Array(type); values = new object?[] { values }; }
            Assert.Throws<NotSupportedException>(() => Encode(type, values));
            Assert.Throws<ProtocolException>(() => Decode(type, string.Concat(Enumerable.Repeat("0100", 33)) + "000131"));
            var many = new object?[40000];
            Assert.Throws<NotSupportedException>(() => Encode(Array(Array(Number)), many, many));
            var nulls = "FC409CFC" + new string('F', 9998) + "03";
            Assert.Throws<ProtocolException>(() => Decode(Array(Array(Number)), "0200" + nulls + nulls));
        }

        [Fact]
        public void HugeParameterTextIsRejectedBeforeUtf8Allocation()
            => Assert.Throws<ArgumentException>(() => Encode(Object(Text), new string('x', (int)PacketChannel.DefaultMaxPacketSize)));

        [Fact]
        public void NineByteLengthPrefixIsIncludedInTheParameterBudget()
            => Assert.Throws<ArgumentException>(() => Encode(Object(Raw), new byte[PacketChannel.DefaultMaxPacketSize - 15]));

        [Theory]
        [InlineData(0xFFFFFF, 4)] [InlineData(0x1000000, 9)] [InlineData((int)PacketChannel.DefaultMaxPacketSize - 20, 9)]
        public void LargeRawValuesRespectPrefixTransitionsAndMaximumTotalLength(int count, int prefix)
        {
            var encoded = Encode(Object(Raw), new byte[count]); Assert.Equal(1 + prefix + count, encoded.Length);
            var cursor = new ByteCursor(encoded); Assert.Equal(0, cursor.ReadUInt8()); Assert.Equal(count, cursor.ReadLengthEncodedInteger()); Assert.Equal(count, cursor.Remaining);
        }

        [Fact]
        public void OneByteBeyondMaximumTotalLengthIsRejected()
            => Assert.Throws<ArgumentException>(() => Encode(Object(Raw), new byte[PacketChannel.DefaultMaxPacketSize - 19]));

        [Theory]
        [InlineData(12)] [InlineData(202)]
        public void InvalidTemporalLengthIsRejectedBeforeRequestingTheAdvertisedBody(byte code)
        {
            var error = Assert.Throws<ProtocolException>(() => Decode(Object(new ComplexType(code)), "00FDFFFFFF"));
            Assert.Contains("temporal field length", error.Message);
        }
    }
}
