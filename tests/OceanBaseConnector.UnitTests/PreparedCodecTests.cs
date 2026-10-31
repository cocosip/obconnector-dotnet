using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        public static IEnumerable<object[]> PrimitiveWireValues()
        {
            yield return new object[] { (sbyte)-2, (ushort)1, new byte[] { 254 } };
            yield return new object[] { byte.MaxValue, (ushort)0x8001, new byte[] { 255 } };
            yield return new object[] { short.MinValue, (ushort)2, new byte[] { 0, 128 } };
            yield return new object[] { ushort.MaxValue, (ushort)0x8002, new byte[] { 255, 255 } };
            yield return new object[] { int.MinValue, (ushort)3, new byte[] { 0, 0, 0, 128 } };
            yield return new object[] { uint.MaxValue, (ushort)0x8003, new byte[] { 255, 255, 255, 255 } };
            yield return new object[] { long.MinValue, (ushort)8, new byte[] { 0, 0, 0, 0, 0, 0, 0, 128 } };
            yield return new object[] { ulong.MaxValue, (ushort)0x8008, new byte[] { 255, 255, 255, 255, 255, 255, 255, 255 } };
            yield return new object[] { -1.5f, (ushort)4, new byte[] { 0, 0, 192, 191 } };
            yield return new object[] { -1.5d, (ushort)5, new byte[] { 0, 0, 0, 0, 0, 0, 248, 191 } };
        }

        [Theory]
        [MemberData(nameof(PrimitiveWireValues))]
        public void PrimitiveParametersAndResultsPreserveWireValues(object value, ushort type, byte[] wire)
        {
            var encoded = PreparedParameters.Encode(new[] { new OceanBaseParameter { Value = value } });
            Assert.Equal(new byte[] { 0, 1, (byte)type, (byte)(type >> 8) }.Concat(wire), encoded);
            var column = ResultColumn.Parse(Column("v", (byte)type, flags: (ushort)((type & 0x8000) != 0 ? 32 : 0)), OceanBaseCompatibilityMode.MySql);
            var cursor = new ByteCursor(wire);
            Assert.Equal(value, column.DecodeBinary(column.ReadBinaryField(cursor)));
            Assert.False(cursor.HasRemaining);
            Assert.Equal(DBNull.Value, column.DecodeBinary(null));
        }

        [Fact]
        public void NullBitmapCrossesByteBoundaryAndTypesRefreshAfterValueChanges()
        {
            var values = Enumerable.Range(0, 10).Select(i => new OceanBaseParameter { Value = i }).ToArray();
            values[0].Value = null;
            values[8].Value = DBNull.Value;
            values[9].Value = DBNull.Value;
            var encoded = PreparedParameters.Encode(values);
            Assert.Equal(new byte[] { 1, 3, 1, 6, 0, 3, 0 }, encoded.Take(7));
            Assert.Equal(new byte[] { 6, 0, 6, 0 }, encoded.Skip(19).Take(4));
            Assert.Equal(23 + 7 * 4, encoded.Length);
            values[0].Value = true;
            values[8].Value = false;
            values[9].Value = (byte)255;
            encoded = PreparedParameters.Encode(values);
            Assert.Equal(new byte[] { 0, 0, 1, 1, 0 }, encoded.Take(5));
            Assert.Equal(new byte[] { 1, 0, 1, 128 }, encoded.Skip(19).Take(4));
            Assert.Equal(1, encoded[23]);
            Assert.Equal(new byte[] { 0, 255 }, encoded.TakeLast(2));
        }

        [Theory]
        [InlineData(250, new byte[] { 250 })]
        [InlineData(251, new byte[] { 252, 251, 0 })]
        [InlineData(65536, new byte[] { 253, 0, 0, 1 })]
        public void BinaryParametersUseCanonicalLengthPrefixes(int length, byte[] prefix)
        {
            var bytes = Enumerable.Repeat((byte)123, length).ToArray();
            var encoded = PreparedParameters.Encode(new[] { new OceanBaseParameter { Value = bytes } });
            Assert.Equal(new byte[] { 0, 1, 252, 0 }.Concat(prefix).Concat(bytes), encoded);
        }

        [Fact]
        public void ParameterInferenceCanBeOverriddenAndResetWithoutConvertingValues()
        {
            var parameter = new OceanBaseParameter { Value = 42 };
            Assert.Equal(DbType.Int32, parameter.DbType);
            parameter.DbType = DbType.Int64;
            Assert.Throws<ArgumentException>(() => PreparedParameters.Encode(new[] { parameter }));
            parameter.Value = 42L;
            Assert.Equal(DbType.Int64, parameter.DbType);
            parameter.Value = "秘密";
            Assert.Equal(DbType.Int64, parameter.DbType);
            parameter.ResetDbType();
            Assert.Equal(DbType.String, parameter.DbType);
            Assert.Equal(new byte[] { 0, 1, 253, 0, 6, 231, 167, 152, 229, 175, 134 }, PreparedParameters.Encode(new[] { parameter }));
            parameter.Value = new object();
            Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }));
        }

        [Theory]
        [InlineData(DbType.Currency)]
        [InlineData(DbType.DateTimeOffset)]
        [InlineData(DbType.Guid)]
        [InlineData(DbType.Object)]
        public void UnsupportedTypedNullFailsBeforeWireEncoding(DbType type)
        {
            Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { new OceanBaseParameter { DbType = type, Value = DBNull.Value } }));
        }

        [Fact]
        public void XmlParametersDoNotGuessStringOrBinaryWire()
        {
            foreach (var value in new object?[] { "<root />", DBNull.Value })
            {
                var parameter = new OceanBaseParameter { DbType = DbType.Xml, Value = value };
                Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }));
            }
        }

        [Fact]
        public void OracleNumberUsesDecimalPublicTypeWithItsFixedWidthWireValue()
        {
            var column = ResultColumn.Parse(Column("n", 3), OceanBaseCompatibilityMode.Oracle);
            Assert.Equal(typeof(decimal), column.FieldType);
            Assert.Equal(0m, column.DecodeBinary(column.ReadBinaryField(new ByteCursor(new byte[4]))));
        }
    }
}
