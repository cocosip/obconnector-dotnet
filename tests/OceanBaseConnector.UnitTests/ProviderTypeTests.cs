using System;
using System.Collections.Generic;
using System.Data;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false, 0, 0, 45, "Decimal")]
        [InlineData(false, 246, 0, 45, "Decimal")]
        [InlineData(true, 3, 0, 45, "Decimal")]
        [InlineData(true, 246, 0, 45, "Decimal")]
        [InlineData(false, 1, 0, 45, "SByte")]
        [InlineData(false, 1, 32, 45, "Byte")]
        [InlineData(false, 2, 0, 45, "Int16")]
        [InlineData(false, 2, 32, 45, "UInt16")]
        [InlineData(false, 3, 0, 45, "Int32")]
        [InlineData(false, 3, 32, 45, "UInt32")]
        [InlineData(false, 9, 0, 45, "Int32")]
        [InlineData(false, 9, 32, 45, "UInt32")]
        [InlineData(false, 8, 0, 45, "Int64")]
        [InlineData(false, 8, 32, 45, "UInt64")]
        [InlineData(false, 4, 0, 45, "Single")]
        [InlineData(true, 5, 0, 45, "Double")]
        [InlineData(false, 6, 0, 45, "Null")]
        [InlineData(false, 7, 0, 45, "Timestamp")]
        [InlineData(false, 10, 0, 45, "Date")]
        [InlineData(false, 11, 0, 45, "Time")]
        [InlineData(false, 12, 0, 45, "DateTime")]
        [InlineData(true, 12, 0, 45, "OracleDate")]
        [InlineData(true, 202, 0, 45, "Timestamp")]
        [InlineData(true, 203, 0, 63, "Binary")]
        [InlineData(false, 15, 0, 45, "String")]
        [InlineData(true, 253, 0, 45, "String")]
        [InlineData(false, 254, 0, 45, "String")]
        [InlineData(false, 252, 0, 45, "String")]
        [InlineData(false, 15, 0, 63, "Binary")]
        [InlineData(false, 253, 0, 63, "Binary")]
        [InlineData(false, 254, 0, 63, "Binary")]
        [InlineData(false, 252, 0, 63, "Binary")]
        public void SchemaProviderTypeUsesPublicScalarIdentity(bool oracle, byte wireType, ushort flags, ushort charset, string expected)
        {
            var column = new ResultDbColumn(ResultColumn.Parse(SchemaColumn("v", wireType, 12, 8, charset: charset, flags: flags),
                oracle ? OceanBaseCompatibilityMode.Oracle : OceanBaseCompatibilityMode.MySql), 0);
            var identity = Assert.IsType<int>(column["ProviderType"]);
            Assert.Equal(expected, Enum.GetName(typeof(OceanBaseDbType), identity));
        }

        public static IEnumerable<object[]> ScalarProviderParameters()
        {
            yield return new object[] { "SByte", DbType.SByte, (sbyte)-1 };
            yield return new object[] { "Byte", DbType.Byte, byte.MaxValue };
            yield return new object[] { "Int16", DbType.Int16, short.MinValue };
            yield return new object[] { "UInt16", DbType.UInt16, ushort.MaxValue };
            yield return new object[] { "Int32", DbType.Int32, int.MinValue };
            yield return new object[] { "UInt32", DbType.UInt32, uint.MaxValue };
            yield return new object[] { "Int64", DbType.Int64, long.MinValue };
            yield return new object[] { "UInt64", DbType.UInt64, ulong.MaxValue };
            yield return new object[] { "Single", DbType.Single, 1.25f };
            yield return new object[] { "Double", DbType.Double, 1.25d };
            yield return new object[] { "Boolean", DbType.Boolean, true };
            yield return new object[] { "String", DbType.String, "中文" };
            yield return new object[] { "Binary", DbType.Binary, new byte[] { 0, 128, 255 } };
            yield return new object[] { "Date", DbType.Date, new DateTime(2026, 10, 19) };
            yield return new object[] { "DateTime", DbType.DateTime, new DateTime(2026, 10, 19, 12, 34, 56) };
            yield return new object[] { "Time", DbType.Time, TimeSpan.FromMinutes(-30) };
        }

        [Theory]
        [MemberData(nameof(ScalarProviderParameters))]
        public void ScalarProviderOverrideReusesStrictDbTypeEncoding(string name, DbType dbType, object value)
        {
            var identity = (OceanBaseDbType)Enum.Parse(typeof(OceanBaseDbType), name);
            var parameter = new OceanBaseParameter { Value = value, OceanBaseDbType = identity };
            Assert.Equal(dbType, parameter.DbType);
            var expected = PreparedParameters.Encode(new[] { new OceanBaseParameter { Value = value, DbType = dbType } });
            Assert.Equal(expected, PreparedParameters.Encode(new[] { parameter }));
            parameter.Value = DBNull.Value;
            Assert.Equal(new byte[] { 1, 1, 6, 0 }, PreparedParameters.Encode(new[] { parameter }));
            parameter.Value = new object();
            Assert.Throws<ArgumentException>(() => PreparedParameters.Encode(new[] { parameter }));
            parameter.DbType = DbType.String;
            Assert.Null(parameter.OceanBaseDbType);
            parameter.OceanBaseDbType = identity; parameter.ResetDbType();
            Assert.Null(parameter.OceanBaseDbType); Assert.Equal(DbType.Object, parameter.DbType);
        }

        [Fact]
        public void NullProviderTypeOnlyAcceptsNullValuesAndExistingEnumValuesStayStable()
        {
            var names = new[] { "Decimal", "OracleDate", "Timestamp", "NChar", "NVarchar2", "IntervalYearToMonth", "IntervalDayToSecond", "Json", "Bit", "Year" };
            for (var i = 0; i < names.Length; i++) Assert.Equal(i, (int)Enum.Parse(typeof(OceanBaseDbType), names[i]));
            var parameter = new OceanBaseParameter { OceanBaseDbType = (OceanBaseDbType)Enum.Parse(typeof(OceanBaseDbType), "Null") };
            Assert.Equal(DbType.Object, parameter.DbType);
            Assert.Equal(new byte[] { 1, 1, 6, 0 }, PreparedParameters.Encode(new[] { parameter }));
            parameter.Value = DBNull.Value;
            Assert.Equal(new byte[] { 1, 1, 6, 0 }, PreparedParameters.Encode(new[] { parameter }));
            parameter.Value = 42;
            Assert.Throws<ArgumentException>(() => PreparedParameters.Encode(new[] { parameter }));
        }

        [Theory]
        [MemberData(nameof(ScalarProviderParameters))]
        public void ScalarProviderOverridesRetainOracleModeRules(string name, DbType dbType, object value)
        {
            var parameter = new OceanBaseParameter { Value = value, OceanBaseDbType = (OceanBaseDbType)Enum.Parse(typeof(OceanBaseDbType), name) };
            if (dbType == DbType.Time)
            {
                Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
                parameter.Value = DBNull.Value;
                Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
                parameter.Value = null;
                Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
            }
            else
                Assert.Equal(PreparedParameters.Encode(new[] { new OceanBaseParameter { Value = value, DbType = dbType } }, OceanBaseCompatibilityMode.Oracle),
                    PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
        }
    }
}
