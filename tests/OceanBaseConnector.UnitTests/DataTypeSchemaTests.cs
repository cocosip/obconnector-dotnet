using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false, false, false)]
        [InlineData(false, false, true)]
        [InlineData(false, true, false)]
        [InlineData(false, true, true)]
        [InlineData(true, false, false)]
        [InlineData(true, false, true)]
        [InlineData(true, true, false)]
        [InlineData(true, true, true)]
        public async Task DataTypesAreFreshModeAwareStaticMetadata(bool mysql, bool asynchronous, bool tinyBoolean)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            var builder = new OceanBaseConnectionStringBuilder(Configuration(server.Port)) { TreatTinyAsBoolean = tinyBoolean };
            using var connection = new OceanBaseConnection(builder.ConnectionString);
            Assert.Throws<InvalidOperationException>(() => connection.GetSchema("DataTypes"));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                var transport = new ProtocolFaultStream(Array.Empty<byte>()); InstallFaultStream(connection, transport);
                using var table = asynchronous ? await connection.GetSchemaAsync("datatypes") : connection.GetSchema("datatypes");
                Assert.Equal("DataTypes", table.TableName); Assert.Equal(CultureInfo.InvariantCulture, table.Locale);
                Assert.Equal(typeof(int), table.Columns["ProviderDbType"]!.DataType);
                Assert.Equal(typeof(long), table.Columns["ColumnSize"]!.DataType);
                Assert.Equal(typeof(short), table.Columns["MinimumScale"]!.DataType);
                Assert.Equal(typeof(string), table.Columns["DataType"]!.DataType);
                Assert.Equal(typeof(string), table.Columns["ProviderSpecificDataType"]!.DataType);
                Assert.All(table.Rows.Cast<DataRow>(), row =>
                {
                    Assert.True((bool)row["IsNullable"]);
                    Assert.True((bool)row["IsSupportedForParameters"] || !mysql && (string)row["TypeName"] == "ROWID" || mysql && (string)row["TypeName"] == "GEOMETRY");
                    Assert.True(Enum.IsDefined(typeof(OceanBaseDbType), row["ProviderDbType"]));
                    Assert.NotNull(typeof(OceanBaseConnection).Assembly.GetType((string)row["ProviderSpecificDataType"]) ?? Type.GetType((string)row["ProviderSpecificDataType"]));
                });
                var decimalName = mysql ? "DECIMAL" : "NUMBER";
                var narrow = DataTypeRow(table, decimalName, typeof(decimal));
                var wide = DataTypeRow(table, decimalName, typeof(OceanBaseDecimal));
                Assert.Equal(28L, narrow["ColumnSize"]); Assert.Equal((short)0, narrow["MinimumScale"]); Assert.Equal((short)28, narrow["MaximumScale"]);
                Assert.Equal(mysql ? 65L : 38L, wide["ColumnSize"]); Assert.Equal((short)(mysql ? 0 : -84), wide["MinimumScale"]); Assert.Equal((short)(mysql ? 30 : 127), wide["MaximumScale"]);
                Assert.Equal("OceanBaseConnector.OceanBaseDecimal", narrow["ProviderSpecificDataType"]);
                Assert.Equal("1 <= precision <= 28 AND 0 <= scale <= precision", narrow["ResultTypeCondition"]);
                Assert.Equal("precision unknown OR precision > 28 OR scale < 0 OR scale > precision", wide["ResultTypeCondition"]);
                var text = DataTypeRow(table, mysql ? "VARCHAR" : "VARCHAR2", typeof(string));
                Assert.Contains("UTF-8", (string)text["RequiredFeatures"]); Assert.Equal(DBNull.Value, text["ColumnSize"]);
                Assert.Equal(DBNull.Value, text["IsCaseSensitive"]); Assert.Equal(DBNull.Value, text["IsSearchable"]);
                var names = table.Rows.Cast<DataRow>().Select(row => (string)row["TypeName"]).ToArray();
                if (mysql)
                {
                    Assert.Contains("JSON", names); Assert.Contains("BIT", names); Assert.Contains("YEAR", names);
                    Assert.DoesNotContain("NUMBER", names); Assert.DoesNotContain("REF CURSOR", names);
                    Assert.Equal(tinyBoolean, names.Contains("TINYINT(1)"));
                    Assert.Equal((int)OceanBaseDbType.Binary, DataTypeRow(table, "BLOB", typeof(byte[]))["ProviderDbType"]);
                    foreach (var name in new[] { "ENUM", "SET" })
                    {
                        var labels = DataTypeRow(table, name, typeof(string));
                        Assert.Equal(name + "({0})", labels["CreateFormat"]); Assert.Equal("labels", labels["CreateParameters"]);
                        Assert.Equal((int)OceanBaseDbType.String, labels["ProviderDbType"]); Assert.False((bool)labels["IsLong"]);
                    }
                    var geometry = DataTypeRow(table, "GEOMETRY", typeof(byte[]));
                    Assert.False((bool)geometry["IsSupportedForParameters"]); Assert.True((bool)geometry["IsSupportedForResults"]);
                    Assert.False((bool)geometry["IsLiteralSupported"]); Assert.Contains("255", (string)geometry["RequiredFeatures"]);
                    foreach (var prefix in new[] { "TINY", "MEDIUM", "LONG" })
                        foreach (var raw in new[] { false, true })
                        {
                            var family = DataTypeRow(table, prefix + (raw ? "BLOB" : "TEXT"), raw ? typeof(byte[]) : typeof(string));
                            Assert.Equal((int)(raw ? OceanBaseDbType.Binary : OceanBaseDbType.String), family["ProviderDbType"]);
                            Assert.True((bool)family["IsLong"]); Assert.True((bool)family["IsSupportedForResults"]);
                        }
                }
                else
                {
                    Assert.Contains("NVARCHAR2", names); Assert.Contains("ARRAY", names); Assert.Contains("OBJECT", names);
                    Assert.DoesNotContain("JSON", names); Assert.DoesNotContain("BIT", names);
                    Assert.DoesNotContain("ENUM", names); Assert.DoesNotContain("SET", names); Assert.DoesNotContain("GEOMETRY", names);
                    var rowid = DataTypeRow(table, "ROWID", typeof(string));
                    Assert.False((bool)rowid["IsSupportedForParameters"]); Assert.True((bool)rowid["IsSupportedForResults"]);
                    Assert.False((bool)rowid["IsLiteralSupported"]); Assert.Contains("209", (string)rowid["RequiredFeatures"]);
                    var urowid = DataTypeRow(table, "UROWID", typeof(string));
                    Assert.Equal((int)OceanBaseDbType.Urowid, urowid["ProviderDbType"]);
                    Assert.True((bool)urowid["IsSupportedForParameters"]); Assert.True((bool)urowid["IsSupportedForResults"]);
                    Assert.False((bool)urowid["IsLiteralSupported"]); Assert.Contains("209", (string)urowid["RequiredFeatures"]);
                    var cursor = DataTypeRow(table, "REF CURSOR", typeof(OceanBaseRefCursor));
                    Assert.False((bool)cursor["IsSupportedForResults"]); Assert.Equal("Output,ReturnValue", cursor["ParameterDirections"]);
                    Assert.Equal(DBNull.Value, cursor["CreateFormat"]); Assert.Equal(DBNull.Value, cursor["LiteralPrefix"]);
                    Assert.True((bool)DataTypeRow(table, "OBJECT", typeof(OceanBaseComplexValue))["RequiresTypeName"]);
                    Assert.False((bool)DataTypeRow(table, "ARRAY", typeof(OceanBaseComplexValue))["RequiresTypeName"]);
                    Assert.Contains("Use Lob Locators", (string)DataTypeRow(table, "BLOB", typeof(OceanBaseLob))["RequiredFeatures"]);
                }
                using var meta = connection.GetSchema();
                var descriptor = Assert.Single(meta.Rows.Cast<DataRow>(), row => (string)row[0] == "DataTypes");
                Assert.Equal(0, descriptor[1]); Assert.Equal(0, descriptor[2]);
                table.Rows.Clear(); using var fresh = connection.GetSchema("DataTypes"); Assert.NotEmpty(fresh.Rows.Cast<DataRow>());
                Assert.Throws<ArgumentException>(() => connection.GetSchema("DataTypes", new string[1]));
                using var canceled = new CancellationTokenSource(); canceled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.GetSchemaAsync("DataTypes", canceled.Token));
                Assert.Empty(transport.Written); Assert.Equal(0, transport.SyncCalls + transport.AsyncCalls);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static DataRow DataTypeRow(DataTable table, string name, Type type) => Assert.Single(table.Rows.Cast<DataRow>(),
            row => (string)row["TypeName"] == name && (string)row["DataType"] == type.FullName);

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task DataTypesParameterIdentitiesBindActualScalarValues(bool mysql)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                using var table = connection.GetSchema("DataTypes");
                var values = new List<(string Name, object Value, ushort Code)>
                {
                    (mysql ? "DECIMAL" : "NUMBER", 12.34m, 246),
                    (mysql ? "DECIMAL" : "NUMBER", new OceanBaseDecimal(System.Numerics.BigInteger.Pow(10, 37) + 1, mysql ? 30 : -2), 246),
                    (mysql ? "FLOAT" : "BINARY_FLOAT", 1.5f, 4),
                    (mysql ? "DOUBLE" : "BINARY_DOUBLE", 1.5d, 5),
                    (mysql ? "VARCHAR" : "VARCHAR2", "目标", 253),
                    (mysql ? "VARBINARY" : "RAW", new byte[] { 0, 255 }, mysql ? (ushort)252 : (ushort)203),
                    ("DATE", new DateTime(2026, 10, 25), mysql ? (ushort)10 : (ushort)12)
                };
                if (mysql) values.AddRange(new (string, object, ushort)[] {
                    ("TINYINT", (sbyte)-1, 1), ("TINYINT UNSIGNED", byte.MaxValue, 0x8001),
                    ("SMALLINT", (short)-1, 2), ("SMALLINT UNSIGNED", ushort.MaxValue, 0x8002),
                    ("MEDIUMINT", -1, 3), ("MEDIUMINT UNSIGNED", 16777215U, 0x8003),
                    ("INT", -1, 3), ("INT UNSIGNED", uint.MaxValue, 0x8003),
                    ("BIGINT", -1L, 8), ("BIGINT UNSIGNED", ulong.MaxValue, 0x8008),
                    ("TINYINT(1)", true, 1), ("BIT", ulong.MaxValue, 0x8008), ("YEAR", (ushort)2026, 0x8002),
                    ("JSON", "{}", 253), ("TIME", TimeSpan.FromHours(1), 11), ("DATETIME", new DateTime(2026, 10, 25), 12) });
                else
                {
                    var timestamp = new OceanBaseTimestamp(new DateTime(2026, 10, 25), 123456789);
                    values.AddRange(new (string, object, ushort)[] {
                        ("NCHAR", "目标", 208), ("NVARCHAR2", "目标", 207),
                        ("TIMESTAMP", new DateTime(2026, 10, 25), 202), ("TIMESTAMP", timestamp, 202),
                        ("TIMESTAMP WITH TIME ZONE", new OceanBaseTimestampWithTimeZone(timestamp, TimeSpan.FromHours(8)), 200),
                        ("TIMESTAMP WITH LOCAL TIME ZONE", new OceanBaseTimestampLocalTimeZone(timestamp, "+08:00"), 201),
                        ("INTERVAL YEAR TO MONTH", new OceanBaseIntervalYearToMonth(false, 12, 3), 204),
                        ("INTERVAL DAY TO SECOND", new OceanBaseIntervalDayToSecond(false, 1, 2, 3, 4), 205) });
                }
                foreach (var item in values)
                {
                    var row = DataTypeRow(table, item.Name, item.Value.GetType());
                    var parameter = new OceanBaseParameter { OceanBaseDbType = (OceanBaseDbType)(int)row["ProviderDbType"], Value = item.Value };
                    var mode = mysql ? OceanBaseCompatibilityMode.MySql : OceanBaseCompatibilityMode.Oracle;
                    var encoded = PreparedParameters.Encode(new[] { parameter }, mode, new NationalCharacterSet("utf16", "utf16", "utf8mb4"));
                    Assert.Equal(item.Code, (ushort)(encoded[2] | encoded[3] << 8));
                    if (parameter.OceanBaseDbType == OceanBaseDbType.NChar || parameter.OceanBaseDbType == OceanBaseDbType.NVarchar2)
                    {
                        Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }, mode));
                        Assert.Equal(new byte[] { 0, 1, (byte)item.Code, 0, 4, 0x76, 0xee, 0x68, 0x07 }, encoded);
                    }
                }
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public static IEnumerable<object[]> DataTypeCodecSamples()
        {
            yield return new object[] { true, "TINYINT", (byte)1, 4U, (byte)0, (byte)0, (ushort)0, (ushort)45, typeof(sbyte) };
            yield return new object[] { true, "TINYINT UNSIGNED", (byte)1, 3U, (byte)0, (byte)0, (ushort)32, (ushort)45, typeof(byte) };
            yield return new object[] { true, "INT", (byte)3, 11U, (byte)0, (byte)0, (ushort)0, (ushort)45, typeof(int) };
            yield return new object[] { true, "BIGINT UNSIGNED", (byte)8, 20U, (byte)0, (byte)0, (ushort)32, (ushort)45, typeof(ulong) };
            yield return new object[] { true, "BIT", (byte)16, 64U, (byte)0, (byte)0, (ushort)0, (ushort)63, typeof(ulong) };
            yield return new object[] { true, "YEAR", (byte)13, 4U, (byte)0, (byte)0, (ushort)0, (ushort)45, typeof(ushort) };
            yield return new object[] { true, "JSON", (byte)245, 1024U, (byte)0, (byte)0, (ushort)0, (ushort)63, typeof(string) };
            yield return new object[] { true, "GEOMETRY", (byte)255, 120U, (byte)0, (byte)0, (ushort)0, (ushort)45, typeof(byte[]) };
            yield return new object[] { true, "ENUM", (byte)247, 120U, (byte)0, (byte)0, (ushort)0, (ushort)45, typeof(string) };
            yield return new object[] { true, "SET", (byte)248, 120U, (byte)0, (byte)0, (ushort)0, (ushort)45, typeof(string) };
            yield return new object[] { true, "DECIMAL", (byte)246, 30U, (byte)28, (byte)4, (ushort)0, (ushort)45, typeof(decimal) };
            yield return new object[] { true, "DECIMAL", (byte)246, 67U, (byte)65, (byte)30, (ushort)0, (ushort)45, typeof(OceanBaseDecimal) };
            yield return new object[] { true, "DATE", (byte)10, 10U, (byte)0, (byte)0, (ushort)0, (ushort)45, typeof(DateTime) };
            yield return new object[] { true, "BLOB", (byte)252, 1024U, (byte)0, (byte)0, (ushort)0, (ushort)63, typeof(byte[]) };
            yield return new object[] { false, "NUMBER", (byte)246, 40U, (byte)28, (byte)4, (ushort)0, (ushort)45, typeof(decimal) };
            yield return new object[] { false, "NUMBER", (byte)246, 40U, (byte)38, unchecked((byte)-2), (ushort)0, (ushort)45, typeof(OceanBaseDecimal) };
            yield return new object[] { false, "TIMESTAMP", (byte)202, 32U, (byte)0, (byte)7, (ushort)0, (ushort)45, typeof(DateTime) };
            yield return new object[] { false, "TIMESTAMP", (byte)202, 32U, (byte)0, (byte)9, (ushort)0, (ushort)45, typeof(OceanBaseTimestamp) };
            yield return new object[] { false, "DATE", (byte)12, 19U, (byte)0, (byte)0, (ushort)0, (ushort)45, typeof(DateTime) };
            yield return new object[] { false, "NVARCHAR2", (byte)207, 100U, (byte)0, (byte)0, (ushort)0, (ushort)45, typeof(string) };
            yield return new object[] { false, "RAW", (byte)203, 100U, (byte)0, (byte)0, (ushort)0, (ushort)63, typeof(byte[]) };
            yield return new object[] { false, "ROWID", (byte)209, 120U, (byte)0, (byte)0, (ushort)0, (ushort)45, typeof(string) };
        }

        [Theory]
        [MemberData(nameof(DataTypeCodecSamples))]
        public async Task DataTypesAgreeWithActualResultColumnMappings(bool mysql, string name, byte code, uint length, byte precision, byte scale, ushort flags, ushort charset, Type expected)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                using var table = connection.GetSchema("DataTypes");
                var actual = ResultColumn.Parse(SchemaColumn("v", code, length, precision, scale, flags: flags, charset: charset), mysql ? OceanBaseCompatibilityMode.MySql : OceanBaseCompatibilityMode.Oracle);
                Assert.Equal(expected, actual.FieldType);
                var row = DataTypeRow(table, name, expected);
                Assert.Equal((int)actual.ProviderType, row["ProviderDbType"]);
                Assert.Equal(actual.ProviderSpecificFieldType.FullName, row["ProviderSpecificDataType"]);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
