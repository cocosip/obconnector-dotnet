using System;
using System.Collections.Generic;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Values;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class ComplexIntervalTextTests
    {
        public static IEnumerable<object[]> Precisions()
        {
            for (byte leading = 0; leading <= 9; leading++)
            {
                yield return new object[] { false, leading, (byte)0 };
                for (byte scale = 0; scale <= 9; scale++) yield return new object[] { true, leading, scale };
            }
        }

        private static OceanBaseComplexType Type(bool daySecond, string declaration)
            => new OceanBaseComplexType(null, "Owner", "Thing", false, new[]
            {
                new OceanBaseComplexMember("V", null, declaration, null, new ComplexType(daySecond ? (byte)205 : (byte)204)),
                new OceanBaseComplexMember("N", null, "NUMBER", null, new ComplexType(0)),
            });

        private static object?[] Decode(OceanBaseComplexType type, string text)
            => ComplexValueCodec.DecodeTextResult(ResultColumn.Utf8.GetBytes(text), type, OceanBaseCompatibilityMode.Oracle);

        [Theory]
        [MemberData(nameof(Precisions))]
        public void DeclaredPrecisionsAndSignsSurviveNestedTextResults(bool daySecond, byte leading, byte scale)
        {
            var declaration = daySecond ? $"INTERVAL DAY({leading}) TO SECOND({scale})" : $"INTERVAL YEAR({leading}) TO MONTH";
            var child = Type(daySecond, declaration);
            var root = new OceanBaseComplexType(null, "Owner", "Root", false, new[]
            {
                new OceanBaseComplexMember("CHILD", "Owner", "Thing", child, child.Layout),
                new OceanBaseComplexMember("S", null, "VARCHAR2", null, new ComplexType(253)),
            });
            foreach (var negative in new[] { false, true })
            {
                var component = leading == 0 ? 0 : leading == 9 ? 999999999 : 1;
                var nanos = scale == 0 ? 0 : (int)Math.Pow(10, 9 - scale);
                object expected = daySecond ? (object)new OceanBaseIntervalDayToSecond(negative, component, 23, 59, 58, nanos, leading, scale)
                    : new OceanBaseIntervalYearToMonth(negative, component, 11, leading);
                var result = Decode(root, "Root(Thing('" + expected + "',29),'after, (value)')");
                var values = Assert.IsType<object?[]>(result[0]);
                Assert.Equal(expected, values[0]); Assert.Equal("29", values[1]!.ToString()); Assert.Equal("after, (value)", result[1]);
            }
            Assert.Null(Decode(child, "Thing(NULL,29)")[0]);
        }

        [Theory]
        [InlineData(false, "12-11")]
        [InlineData(false, "+12-12")]
        [InlineData(false, "+012-11")]
        [InlineData(false, "+12-1")]
        [InlineData(false, "+１２-11")]
        [InlineData(false, " +12-11")]
        [InlineData(false, "+12-11 ")]
        [InlineData(true, "+12 24:00:00.123")]
        [InlineData(true, "+12 00:60:00.123")]
        [InlineData(true, "+12 00:00:60.123")]
        [InlineData(true, "+12 00:00:00.1234")]
        [InlineData(true, "+12 00:00:00.12")]
        [InlineData(true, "+12 00:00:00")]
        [InlineData(true, "+12 0:00:00.123")]
        [InlineData(true, "+12T00:00:00.123")]
        [InlineData(true, "+12 00:00:00.１２３")]
        public void MalformedIntervalMembersNeverBecomeValues(bool daySecond, string value)
        {
            var type = Type(daySecond, daySecond ? "INTERVAL DAY(2) TO SECOND(3)" : "INTERVAL YEAR(2) TO MONTH");
            Assert.Throws<ProtocolException>(() => Decode(type, "Thing('" + value + "',29)"));
        }

        [Theory]
        [InlineData(false, "INTERVAL")]
        [InlineData(false, "INTERVAL YEAR(10) TO MONTH")]
        [InlineData(false, "INTERVAL DAY(2) TO SECOND(3)")]
        [InlineData(true, "INTERVAL DAY(2) TO SECOND(255)")]
        [InlineData(true, "INTERVAL DAY(-1) TO SECOND(3)")]
        [InlineData(true, "INTERVAL YEAR(2) TO MONTH")]
        public void UnknownOrMismatchedIntervalDeclarationsStayRejectedEvenForNull(bool daySecond, string declaration)
            => Assert.Throws<NotSupportedException>(() => Decode(Type(daySecond, declaration), "Thing(NULL,29)"));

        [Fact]
        public void NulInsideAnIntervalComponentIsNotAnIntegerTerminator()
            => Assert.Throws<ProtocolException>(() => Decode(Type(false, "INTERVAL YEAR(2) TO MONTH"), "Thing('+12-0\0',29)"));

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void DefaultDeclarationsKeepOraclePrecisions(bool daySecond)
        {
            var type = Type(daySecond, daySecond ? "INTERVAL DAY TO SECOND" : "INTERVAL YEAR TO MONTH");
            object expected = daySecond ? (object)new OceanBaseIntervalDayToSecond(false, 12, 1, 2, 3, 123456000, 2, 6)
                : new OceanBaseIntervalYearToMonth(false, 12, 11, 2);
            Assert.Equal(expected, Decode(type, "Thing('" + expected + "',29)")[0]);
        }
    }
}
