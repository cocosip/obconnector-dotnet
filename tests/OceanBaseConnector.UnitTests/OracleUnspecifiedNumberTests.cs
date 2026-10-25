using System;
using System.Globalization;
using System.Numerics;
using System.Text;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(255, "7", "7", 0)]
        [InlineData(255, "7.25", "725", 2)]
        [InlineData(255, "40", "40", 0)]
        [InlineData(255, "-7.2500", "-72500", 4)]
        [InlineData(2, "1.2300", "12300", 4)]
        [InlineData(255, "1E125", "1", -125)]
        public void UnknownPrecisionOracleResultsPreserveExactServerValues(byte scale, string text, string coefficient, int valueScale)
        {
            var column = ValueColumn(246, OceanBaseCompatibilityMode.Oracle, precision: 0, scale: scale);
            var bytes = Encoding.ASCII.GetBytes(text);
            foreach (var binary in new[] { false, true })
            {
                var value = Assert.IsType<OceanBaseDecimal>(binary ? column.DecodeBinary(bytes) : column.Decode(bytes));
                Assert.Equal(BigInteger.Parse(coefficient, CultureInfo.InvariantCulture), value.Coefficient);
                Assert.Equal(valueScale, value.Scale);
                Assert.Equal(value, column.DecodeDecimal(bytes, binary));
            }
            var schema = new ResultDbColumn(column, 0);
            Assert.Null(schema.NumericPrecision);
            Assert.Equal(unchecked((sbyte)scale), schema.NumericScale);
        }

        [Theory]
        [InlineData("1E126")]
        [InlineData("1E-131")]
        [InlineData("123456789012345678901234567890123456789")]
        public void UnknownPrecisionOracleResultsStillEnforceTheNumberRange(string text)
        {
            var column = ValueColumn(246, OceanBaseCompatibilityMode.Oracle, precision: 0, scale: 255);
            foreach (var binary in new[] { false, true })
                Assert.Throws<OverflowException>(() => column.DecodeDecimal(Encoding.ASCII.GetBytes(text), binary));
        }
    }
}
